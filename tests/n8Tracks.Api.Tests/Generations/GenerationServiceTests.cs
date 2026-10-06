using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Retention;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Retention;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Generations;

/// <summary>
/// Attaching Generations and recording Generation Events (#117) through <see cref="GenerationService"/>,
/// the one way a Generation is made: what is stored, what is refused (nothing stored then), the Suno
/// ID's uniqueness among live Generations, and retention of the provider record with its Generation.
/// </summary>
public sealed class GenerationServiceTests
{
    [Fact]
    public async Task AttachingAClipFreezesTheVersionAndKeepsItsFieldsAndItsRawText()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Attached");
        var raw = Clips.FixtureClip("generate-v2-web.response.json");

        var attached = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", raw);

        // One place sets the ordinal and the freeze, as #69 requires.
        Assert.Equal(1, attached.Generation.Ordinal);
        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        Assert.True(version.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(2, version.GetProperty("revision").GetInt32());

        // A new Generation is active and present, at revision 1, with the clip's fields.
        var generation = attached.Generation;
        Assert.Equal(GenerationState.Active, generation.State);
        Assert.Equal(GenerationRemoteState.Present, generation.RemoteState);
        Assert.Equal(1, generation.Revision);
        Assert.Equal("00000000-0000-4000-8000-000000000002", generation.SunoId);
        Assert.Equal("submitted", generation.ProviderStatus);
        Assert.Null(generation.Clip!.DurationSeconds);
        Assert.Null(generation.EventId);

        // The raw clip is stored as the text received.
        Assert.Equal(raw, TestDatabase.Scalar(factory.DataPath, "SELECT payload FROM provider_records;"));
        Assert.Equal(
            $"{Upper(generation.Id)}|00000000-0000-4000-8000-000000000002|clip|",
            TestDatabase.Scalar(factory.DataPath, "SELECT generation_id || '|' || suno_id || '|' || kind || '|' || coalesce(export_id, '') FROM provider_records;"));
    }

    [Fact]
    public async Task ASunoIdALiveGenerationHoldsIsRefusedAndNothingIsWritten()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "First");
        await SongApi.CreateAsync(client, "Second");
        var first = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("shared-clip"));
        var before = Snapshot(factory);

        // The same clip again, on the same Version or another Song's, by the same ID or with other fields.
        foreach (var (version, raw) in new[] { ("n8-1-v1", Clips.Minimal("shared-clip")), ("n8-2-v1", Clips.Minimal("shared-clip", "error")) })
        {
            var refused = Assert.IsType<GenerationAttachOutcome.SunoIdExists>(await SongApi.AttachAsync(factory, version, raw));
            Assert.Equal("n8-1-v1-g1", refused.Existing.Shortcode);
            Assert.Equal(first.Generation.Id, refused.Existing.Generation.Id);
        }

        Assert.Equal(before, Snapshot(factory));
        var second = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-2-v1", UriKind.Relative)));
        Assert.False(second.GetProperty("isFrozen").GetBoolean());

        // The database refuses a duplicate too (the partial unique index), whatever wrote it.
        var duplicate = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO generations (id, version_id, song_id, ordinal, created_utc, suno_id) SELECT '{Upper(Guid.CreateVersion7())}', version_id, song_id, 99, created_utc, 'shared-clip' FROM generations LIMIT 1;"));
        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.Ordinal);

        // Complement: Generations without Suno data are many, and other Suno IDs are free.
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null);
        await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal("another-clip"));
        Assert.Equal("4", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;"));
    }

    [Fact]
    public async Task TheSunoIdIsUniqueAmongLiveGenerationsOnly()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Deleted");
        var raw = Clips.Handwritten("retained-clip", "kept-with-the-generation");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", raw);
        await NewVersionAsync(client, "2");

        // Deleting the Version retains its Generation, provider record included, out of the live table.
        var v1 = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        using (var deleted = await DeleteAsync(client, "versions/n8-1-v1", v1.GetProperty("revision").GetInt32()))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM provider_records;"));
        Assert.Equal(
            ["generation", "provider-record"],
            TestDatabase.Rows(factory.DataPath, "SELECT record_type FROM retention_records WHERE record_type IN ('generation', 'provider-record') ORDER BY position;"));

        // A retained Generation does not hold its Suno ID: it can be attached again (reimporting the
        // original is the deletion story's).
        var again = await SongApi.AttachGenerationAsync(factory, "n8-1-v2", Clips.Minimal("retained-clip"));
        Assert.Equal("retained-clip", again.Generation.SunoId);

        // Restoring the deleted Version while the Suno ID is live clashes, and changes nothing.
        var group = await WithServiceAsync(factory, service => service.FindByShortcodeAsync("n8-1-v1", CancellationToken.None));
        Assert.IsType<RetentionRestoreOutcome.Clash>(await RestoreAsync(factory, group!.Id));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM provider_records;"));
    }

    [Fact]
    public async Task ADeletedGenerationsProviderRecordAndEventLinkComeBackWithItUnchanged()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Restored");
        var raw = Clips.Handwritten("restored-clip", "kept-with-the-generation");
        var attached = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", raw);
        Assert.IsType<GenerationEventOutcome.Recorded>(await RecordAsync(factory, Request([attached.Generation.Id])));
        await NewVersionAsync(client, "2");
        var generationBefore = TestDatabase.Scalar(factory.DataPath, GenerationRow);
        var linksBefore = Dump(factory, "generation_event_links");

        var v1 = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        using (var deleted = await DeleteAsync(client, "versions/n8-1-v1", v1.GetProperty("revision").GetInt32()))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        Assert.Empty(Dump(factory, "generation_event_links"));
        Assert.Single(Dump(factory, "generation_events"));

        var group = await WithServiceAsync(factory, service => service.FindByShortcodeAsync("n8-1-v1", CancellationToken.None));
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group!.Id));

        // Back as it was: the row (its revision raised by the restore, as every restored row's), the
        // raw text byte for byte, and the link.
        Assert.Equal(raw, TestDatabase.Scalar(factory.DataPath, "SELECT payload FROM provider_records;"));
        Assert.Equal(linksBefore, Dump(factory, "generation_event_links"));
        Assert.Equal(generationBefore, TestDatabase.Scalar(factory.DataPath, GenerationRow));
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT revision FROM generations;"));
        using var record = await client.GetAsync(new Uri("/api/v1/generations/n8-1-v1-g1/provider-record", UriKind.Relative));
        Assert.Equal(raw, await record.Content.ReadAsStringAsync());
    }

    [Fact]
    public void AGenerationRetainedBeforeProviderDataRestoresAsActivePresentAndWithout()
    {
        var shape1 = new JsonObject
        {
            ["id"] = "A",
            ["version_id"] = "B",
            ["song_id"] = "C",
            ["ordinal"] = 3,
            ["created_utc"] = "2026-10-05T00:00:00.000Z",
        };

        var shape2 = RetainedTypes.GenerationShape1To2(shape1);

        Assert.Equal("active", (string?)shape2["state"]);
        Assert.Equal("present", (string?)shape2["remote_state"]);
        Assert.Equal(1, (int?)shape2["revision"]);
        Assert.Null(shape2["suno_id"]);
        Assert.Equal(25, shape2.Count);
        Assert.True(RetainedTypes.Generation.Upgraders.ContainsKey(1));

        // Shape 3 (#119) adds the rating: an earlier Generation restores unrated.
        var shape3 = RetainedTypes.GenerationShape2To3(shape2);
        Assert.True(shape3.ContainsKey("rating"));
        Assert.Null(shape3["rating"]);
        Assert.Equal(26, shape3.Count);
        Assert.Equal(3, RetainedTypes.Generation.ShapeVersion);
        Assert.True(RetainedTypes.Generation.Upgraders.ContainsKey(2));
    }

    [Fact]
    public async Task AGenerationRetainedUnderShape1IsRestoredThroughTheUpgrader()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Old shape");
        var version = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        var songId = song.GetProperty("id").GetGuid();
        var generation = Guid.CreateVersion7();
        var group = Guid.CreateVersion7();
        TestDatabase.Execute(
            factory.DataPath,
            $$"""
            UPDATE versions SET is_frozen = 1, last_generation_ordinal = 4 WHERE id = '{{Upper(version)}}';
            INSERT INTO retention_groups (id, kind, label, shortcode, deleted_utc, prune_after_utc, files) VALUES ('{{Upper(group)}}', 'generation', 'Old', NULL, '2026-10-05T00:00:00.000Z', '2099-01-01T00:00:00.000Z', '[]');
            INSERT INTO retention_records (group_id, position, record_type, original_id, shape_version, document) VALUES ('{{Upper(group)}}', 0, 'generation', '{{Upper(generation)}}', 1,
                '{"id":"{{Upper(generation)}}","version_id":"{{Upper(version)}}","song_id":"{{Upper(songId)}}","ordinal":4,"created_utc":"2026-10-05T00:00:00.000Z"}');
            """);

        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group));

        var read = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/generations/n8-1-v1-g4", UriKind.Relative)));
        Assert.Equal("active", read.GetProperty("state").GetString());
        Assert.Equal("present", read.GetProperty("remoteState").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, read.GetProperty("sunoId").ValueKind);
    }

    [Fact]
    public async Task AnInvalidClipOrAMissingEventIsRefusedAndNothingIsWritten()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Refused");
        var before = Snapshot(factory);

        foreach (var raw in new[] { "{}", "[]", "not json", "{\"id\":3}", string.Empty })
        {
            var invalid = Assert.IsType<GenerationAttachOutcome.InvalidClip>(await SongApi.AttachAsync(factory, "n8-1-v1", raw));
            Assert.False(string.IsNullOrWhiteSpace(invalid.Reason));
        }

        Assert.IsType<GenerationAttachOutcome.EventNotFound>(
            await SongApi.AttachAsync(factory, "n8-1-v1", Clips.Minimal("evented"), new GenerationAttachOptions(EventId: Guid.CreateVersion7())));
        Assert.IsType<GenerationAttachOutcome.VersionNotFound>(await SongApi.AttachAsync(factory, "n8-1-v9", Clips.Minimal("nowhere")));

        Assert.Equal(before, Snapshot(factory));
        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        Assert.False(version.GetProperty("isFrozen").GetBoolean());
    }

    [Fact]
    public async Task AnEventIsRecordedAtomicallyAndAGenerationHasAtMostOne()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Events");
        var one = (await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("batch-0"))).Generation.Id;
        var two = (await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("batch-1"))).Generation.Id;
        var three = (await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null)).Generation.Id;

        // A Generation may have none.
        Assert.Null((await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null)).Generation.EventId);

        // Refused, with nothing stored: invalid fields, an unknown Generation, no Generation at all.
        Assert.IsType<GenerationEventOutcome.Invalid>(await RecordAsync(factory, Request([one]) with { BatchSize = 0 }));
        Assert.IsType<GenerationEventOutcome.Invalid>(await RecordAsync(factory, Request([one]) with { ProviderRequestId = new string('r', 201) }));
        Assert.IsType<GenerationEventOutcome.Invalid>(await RecordAsync(factory, Request([])));
        var unknown = Guid.CreateVersion7();
        Assert.Equal([unknown], Assert.IsType<GenerationEventOutcome.GenerationsNotFound>(await RecordAsync(factory, Request([one, unknown]))).GenerationIds);
        Assert.Empty(Dump(factory, "generation_events"));
        Assert.Empty(Dump(factory, "generation_event_links"));

        // One event, many Generations, with every field of the design.
        var recorded = Assert.IsType<GenerationEventOutcome.Recorded>(await RecordAsync(factory, Request([one, two]) with { ProviderRequestId = "req-1" }));
        Assert.Equal(
            $"{Upper(recorded.Event.Id)}|req-1|observed|high|2|1970-01-01T00:00:00.000Z",
            TestDatabase.Scalar(factory.DataPath, "SELECT id || '|' || provider_request_id || '|' || source || '|' || confidence || '|' || batch_size || '|' || occurred_utc FROM generation_events;"));
        Assert.Equal(recorded.Event.Id, (await FindAsync(factory, "n8-1-v1-g1"))!.Generation.EventId);
        Assert.Equal(recorded.Event.Id, (await FindAsync(factory, "n8-1-v1-g2"))!.Generation.EventId);

        // At most one each: an event naming a linked Generation is refused whole.
        var linked = Assert.IsType<GenerationEventOutcome.AlreadyLinked>(await RecordAsync(factory, Request([three, two])));
        Assert.Equal([two], linked.GenerationIds);
        Assert.Single(Dump(factory, "generation_events"));
        Assert.Null((await FindAsync(factory, "n8-1-v1-g3"))!.Generation.EventId);

        // Attaching with an existing event links the new Generation to it; the request ID is optional.
        var inferred = Assert.IsType<GenerationEventOutcome.Recorded>(await RecordAsync(factory, Request([three]) with { Source = GenerationEventSource.Inferred, Confidence = GenerationEventConfidence.Medium }));
        var joined = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null);
        Assert.Null(joined.Generation.EventId);
        var linkedOnAttach = Assert.IsType<GenerationAttachOutcome.Attached>(await SongApi.AttachAsync(factory, "n8-1-v1", Clips.Minimal("batch-2"), new GenerationAttachOptions(EventId: inferred.Event.Id)));
        Assert.Equal(inferred.Event.Id, linkedOnAttach.Generation.Generation.EventId);
        Assert.Equal("|inferred|medium", TestDatabase.Scalar(factory.DataPath, $"SELECT coalesce(provider_request_id, '') || '|' || source || '|' || confidence FROM generation_events WHERE id = '{Upper(inferred.Event.Id)}';"));
    }

    [Fact]
    public async Task AnArchivedGenerationResolvesAsArchived()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Archived");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null);
        Assert.Equal("active", (await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/resolve/n8-1-v1-g1", UriKind.Relative)))).GetProperty("status").GetString());

        // No story archives one yet; the column is the user-facing state.
        TestDatabase.Execute(factory.DataPath, "UPDATE generations SET state = 'archived';");

        Assert.Equal("archived", (await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/resolve/n8-1-v1-g1", UriKind.Relative)))).GetProperty("status").GetString());
        Assert.Equal("archived", (await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/generations/n8-1-v1-g1", UriKind.Relative)))).GetProperty("state").GetString());
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(factory.DataPath, "UPDATE generations SET state = 'hidden';"));
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(factory.DataPath, "UPDATE generations SET remote_state = 'gone';"));
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(factory.DataPath, "UPDATE generations SET ordinal = 5;"));
    }

    /// <summary>Every column of the one Generation but its revision, as stored.</summary>
    private const string GenerationRow =
        "SELECT quote(id) || quote(version_id) || quote(song_id) || quote(ordinal) || quote(created_utc) || quote(state) || quote(remote_state) || quote(suno_id) "
        + "|| quote(provider_status) || quote(suno_title) || quote(duration_seconds) || quote(model_version) || quote(model_name) || quote(model_label) || quote(style_tags) "
        + "|| quote(minimum_bpm) || quote(maximum_bpm) || quote(average_bpm) || quote(musical_key) || quote(suno_created_utc) || quote(audio_url) || quote(image_url) "
        + "|| quote(workspace_id) || quote(batch_index) FROM generations;";

    /// <summary>Records <paramref name="request"/> through the service.</summary>
    internal static async Task<GenerationEventOutcome> RecordAsync(N8TracksApiFactory factory, GenerationEventRequest request)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<GenerationService>().RecordEventAsync(request, CancellationToken.None);
        }
    }

    private static async Task<Application.Songs.GenerationSummary?> FindAsync(N8TracksApiFactory factory, string reference)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<GenerationService>().FindAsync(Application.References.CatalogReference.Parse(reference), CancellationToken.None);
        }
    }

    private static GenerationEventRequest Request(IReadOnlyList<Guid> generations) =>
        new(null, GenerationEventSource.Observed, GenerationEventConfidence.High, 2, DateTimeOffset.UnixEpoch, generations);

    /// <summary>Every row of the tables an attach or an event writes.</summary>
    private static string Snapshot(N8TracksApiFactory factory) =>
        string.Join(
            Environment.NewLine,
            new[] { "generations", "provider_records", "generation_events", "generation_event_links", "versions" }.SelectMany(table => Dump(factory, table)));

    private static async Task NewVersionAsync(HttpClient client, string number)
    {
        using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative), $$"""{"sourceVersionId":"n8-1-v1","number":"{{number}}"}""");
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path, int revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/" + path, UriKind.Relative));
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }
}
