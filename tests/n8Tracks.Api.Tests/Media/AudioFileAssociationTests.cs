using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The user's associations (#210) through the API, over real scans of the host's temporary media
/// folder: associate a file with a Song alone or with one of its Generations, change it, remove it,
/// and match it by Suno ID again; the refusals; the user's decision against later scans; and the
/// media folder, which no association ever writes (invariant 2).
/// </summary>
public sealed class AudioFileAssociationTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string B = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";
    private const string Nobody = "ffffeeee-dddd-4ccc-bbbb-aaaa99998888";

    [Fact]
    public async Task AFileIsAssociatedWithASongAloneThenWithAGenerationThenChangedAndRemovedLeavingTheMediaFolderAsItWas()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, "Album/My Song.wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var before = MediaApi.Listing(factory.MediaPath);
        var file = await FileAsync(client, "Album/My Song.wav");
        Assert.Equal(1, Revision(file));
        Assert.False(file.GetProperty("autoMatchBlocked").GetBoolean());

        // A Song alone, by its shortcode: the file leaves the unmatched list at once.
        using (var response = await AssociateAsync(client, file, 1, """{"song":"n8-1"}"""))
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
            var body = await SetupApi.JsonAsync(response);
            Assert.Equal(catalog.First.SongId, body.GetProperty("song").GetProperty("id").GetGuid());
            Assert.Equal("n8-1", body.GetProperty("song").GetProperty("shortcode").GetString());
            Assert.Equal("Origin", body.GetProperty("song").GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("generation").ValueKind);
            Assert.Equal("user", body.GetProperty("associationOrigin").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("unmatchedReason").ValueKind);
            Assert.Equal(2, Revision(body));
        }

        Assert.Empty((await MediaApi.ListAsync(client, "?association=none")).Items);
        Assert.Single((await MediaApi.ListAsync(client, "?association=associated")).Items);

        // Then one of the Song's Generations, by its ID.
        using (var response = await AssociateAsync(client, file, 2, $$"""{"song":"{{catalog.First.SongId}}","generation":"{{catalog.First.GenerationId}}"}"""))
        {
            var body = await SetupApi.JsonAsync(response);
            Assert.Equal(catalog.First.GenerationId, body.GetProperty("generation").GetProperty("id").GetGuid());
            Assert.Equal("n8-1-v1-g1", body.GetProperty("generation").GetProperty("shortcode").GetString());
            Assert.Equal(3, Revision(body));
        }

        // Changed to another Song's Generation, then back to that Song alone: a name with no UUID is never blocked.
        using (var response = await AssociateAsync(client, file, 3, """{"song":"n8-2","generation":"n8-2-v1-g1"}"""))
        {
            var body = await SetupApi.JsonAsync(response);
            Assert.Equal(catalog.Other.SongId, body.GetProperty("song").GetProperty("id").GetGuid());
            Assert.Equal(catalog.Other.GenerationId, body.GetProperty("generation").GetProperty("id").GetGuid());
            Assert.False(body.GetProperty("autoMatchBlocked").GetBoolean());
        }

        using (var response = await AssociateAsync(client, file, 4, """{"song":"n8-2","generation":null}"""))
        {
            var body = await SetupApi.JsonAsync(response);
            Assert.Equal(catalog.Other.SongId, body.GetProperty("song").GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("generation").ValueKind);
            Assert.Equal(5, Revision(body));
        }

        // Removed: back among the unmatched, unassociated by the user.
        using (var removed = await SendAsync(client, HttpMethod.Delete, $"audio-files/{Id(file)}/association", 5))
        {
            Assert.True(removed.StatusCode == HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync());
            Assert.Equal("\"6\"", removed.Headers.ETag?.Tag);
        }

        var unmatched = Assert.Single((await MediaApi.ListAsync(client, "?association=none")).Items);
        Assert.Equal(JsonValueKind.Null, unmatched.GetProperty("song").ValueKind);
        Assert.Equal(JsonValueKind.Null, unmatched.GetProperty("associationOrigin").ValueKind);
        Assert.Equal("unassociated_by_user", unmatched.GetProperty("unmatchedReason").GetString());
        Assert.Equal(6, Revision(unmatched));

        // Nothing in the media folder changed (invariant 2), and no Song or Generation was touched.
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
        Assert.Equal(1, (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")))).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task NamingTheAssociationAFileAlreadyHasStoresNothingAndKeepsItsOrigin()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        MediaApi.Place(factory, $"matched (suno-{A}).wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var file = await FileAsync(client, $"matched (suno-{A}).wav");
        Assert.Equal("suno-id", file.GetProperty("associationOrigin").GetString());
        Assert.Equal(2, Revision(file));

        using var response = await AssociateAsync(client, file, 2, """{"song":"n8-1","generation":"n8-1-v1-g1"}""");
        var body = await SetupApi.JsonAsync(response);
        Assert.Equal("suno-id", body.GetProperty("associationOrigin").GetString());
        Assert.Equal(2, Revision(body));
        Assert.False(body.GetProperty("autoMatchBlocked").GetBoolean());

        // A stale revision is still a conflict, even when nothing would change.
        using var stale = await AssociateAsync(client, file, 1, """{"song":"n8-1","generation":"n8-1-v1-g1"}""");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Removing the association of a file with none stores nothing.
        MediaApi.Place(factory, "plain.wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var plain = await FileAsync(client, "plain.wav");
        using (var removed = await SendAsync(client, HttpMethod.Delete, $"audio-files/{Id(plain)}/association", 1))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        var after = await FileAsync(client, "plain.wav");
        Assert.Equal(1, Revision(after));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("unmatchedReason").ValueKind);
    }

    [Fact]
    public async Task TheRefusals()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, "refused.wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var file = await FileAsync(client, "refused.wav");

        // A Generation of another Song.
        using (var other = await AssociateAsync(client, file, 1, """{"song":"n8-1","generation":"n8-2-v1-g1"}"""))
        {
            var problem = await ProblemAsync(other, HttpStatusCode.UnprocessableEntity, "generation_not_in_song");
            Assert.Equal(catalog.Other.GenerationId, problem.GetProperty("generationId").GetGuid());
        }

        // A stale revision: 409 with the file as it is now.
        using (var stale = await AssociateAsync(client, file, 7, """{"song":"n8-1"}"""))
        {
            var problem = await ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(Id(file), problem.GetProperty("current").GetProperty("id").GetGuid());
            Assert.Equal(1, Revision(problem.GetProperty("current")));
        }

        using (var stale = await SendAsync(client, HttpMethod.Delete, $"audio-files/{Id(file)}/association", 7))
        {
            await ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        // Unknown Song, Generation, and file.
        using (var unknown = await AssociateAsync(client, file, 1, """{"song":"n8-99"}"""))
        {
            await ProblemAsync(unknown, HttpStatusCode.NotFound, "not_found");
        }

        using (var unknown = await AssociateAsync(client, file, 1, $$"""{"song":"n8-1","generation":"{{Guid.CreateVersion7()}}"}"""))
        {
            await ProblemAsync(unknown, HttpStatusCode.NotFound, "not_found");
        }

        using (var unknown = await SendAsync(client, HttpMethod.Put, $"audio-files/{Guid.CreateVersion7()}/association", 1, """{"song":"n8-1"}"""))
        {
            await ProblemAsync(unknown, HttpStatusCode.NotFound, "not_found");
        }

        // No Song, or a field that is not text.
        using (var missing = await AssociateAsync(client, file, 1, """{"generation":"n8-1-v1-g1"}"""))
        {
            var problem = await ProblemAsync(missing, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("song", out _));
        }

        using (var wrong = await AssociateAsync(client, file, 1, """{"song":12,"generation":true}"""))
        {
            var errors = (await ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "validation_failed")).GetProperty("errors");
            Assert.True(errors.TryGetProperty("song", out _));
            Assert.True(errors.TryGetProperty("generation", out _));
        }

        // No revision, and a malformed one.
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/audio-files/{Id(file)}/association", UriKind.Relative)))
        {
            request.Content = new StringContent("""{"song":"n8-1"}""", Encoding.UTF8, "application/json");
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            using var unrevised = await client.SendAsync(request);
            Assert.Equal((HttpStatusCode)428, unrevised.StatusCode);
        }

        // Nothing was written by any refusal.
        var after = await FileAsync(client, "refused.wav");
        Assert.Equal(1, Revision(after));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("song").ValueKind);
    }

    [Fact]
    public async Task ADeletedSongOrGenerationIsNotFoundButAnArchivedSongIsAllowed()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, "target.wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var file = await FileAsync(client, "target.wav");

        await DeleteGenerationAsync(client, "n8-1-v1-g2");
        using (var deleted = await AssociateAsync(client, file, 1, $$"""{"song":"n8-1","generation":"{{catalog.Second.GenerationId}}"}"""))
        {
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }

        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-2")));
        using (var gone = await SendAsync(client, HttpMethod.Delete, "songs/n8-2", Revision(song), """{"confirmTitle":"Other"}"""))
        {
            Assert.True(gone.StatusCode == HttpStatusCode.NoContent, await gone.Content.ReadAsStringAsync());
        }

        using (var deleted = await AssociateAsync(client, file, 1, $$"""{"song":"{{catalog.Other.SongId}}"}"""))
        {
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }

        TestDatabase.Execute(factory.DataPath, $"UPDATE songs SET workflow_state_id = '{DefaultWorkflowStates.Archived.Id.ToString().ToUpperInvariant()}' WHERE shortcode_number = 1;");
        using var archived = await AssociateAsync(client, file, 1, """{"song":"n8-1","generation":"n8-1-v1-g1"}""");
        Assert.True(archived.StatusCode == HttpStatusCode.OK, await archived.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AssociatingNeedsSongsWrite()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        MediaApi.Place(factory, "scoped.wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var file = await FileAsync(client, "scoped.wav");
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead, CredentialScopes.GenerationsEvaluate);
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using var anonymous = factory.CreateClient();

        foreach (var (method, path, json) in new[]
        {
            (HttpMethod.Put, "association", """{"song":"n8-1"}"""),
            (HttpMethod.Delete, "association", (string?)null),
            (HttpMethod.Post, "rematch", null),
        })
        {
            using var refused = await TokenSendAsync(anonymous, reader, method, $"audio-files/{Id(file)}/{path}", 1, json);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal(1, Revision(await FileAsync(client, "scoped.wav")));
        using var allowed = await TokenSendAsync(anonymous, writer, HttpMethod.Put, $"audio-files/{Id(file)}/association", 1, """{"song":"n8-1"}""");
        Assert.True(allowed.StatusCode == HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ARemovedSunoIdMatchStaysUnmatchedScanAfterScanAndAHandMadeAssociationIsNeverChangedByAScan()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, $"live (suno-{A}).wav", "wav");
        MediaApi.Place(factory, $"moved (suno-{B}).wav", "wav");
        Assert.Equal(2, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("associated").GetInt32());
        var live = await FileAsync(client, $"live (suno-{A}).wav");
        var moved = await FileAsync(client, $"moved (suno-{B}).wav");

        // The user removes the first file's automatic association: two scans leave it unmatched.
        using (var removed = await SendAsync(client, HttpMethod.Delete, $"audio-files/{Id(live)}/association", Revision(live)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        // The user moves the second to another Song by hand: blocked, since it replaced a Suno ID match.
        using (var replaced = await AssociateAsync(client, moved, Revision(moved), """{"song":"n8-2"}"""))
        {
            var body = await SetupApi.JsonAsync(replaced);
            Assert.Equal("user", body.GetProperty("associationOrigin").GetString());
            Assert.True(body.GetProperty("autoMatchBlocked").GetBoolean());
        }

        for (var scan = 0; scan < 2; scan++)
        {
            var result = MediaApi.Result(await MediaApi.ScanAsync(client));
            Assert.Equal(0, result.GetProperty("associated").GetInt32());

            var file = await FileAsync(client, $"live (suno-{A}).wav");
            Assert.Equal(JsonValueKind.Null, file.GetProperty("song").ValueKind);
            Assert.Equal("unassociated_by_user", file.GetProperty("unmatchedReason").GetString());
            Assert.True(file.GetProperty("autoMatchBlocked").GetBoolean());

            var other = await FileAsync(client, $"moved (suno-{B}).wav");
            Assert.Equal(catalog.Other.SongId, other.GetProperty("song").GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, other.GetProperty("generation").ValueKind);
            Assert.Equal("user", other.GetProperty("associationOrigin").GetString());
        }

        // Re-associated by hand with a different Song, then scanned: unchanged.
        live = await FileAsync(client, $"live (suno-{A}).wav");
        using (await AssociateAsync(client, live, Revision(live), """{"song":"n8-2","generation":"n8-2-v1-g1"}"""))
        {
        }

        MediaApi.Result(await MediaApi.ScanAsync(client));
        var kept = await FileAsync(client, $"live (suno-{A}).wav");
        Assert.Equal(catalog.Other.GenerationId, kept.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal("user", kept.GetProperty("associationOrigin").GetString());

        // The block outlives the user's association: once removed again, the matcher still leaves it be.
        using (await SendAsync(client, HttpMethod.Delete, $"audio-files/{Id(kept)}/association", Revision(kept)))
        {
        }

        TestDatabase.Execute(factory.DataPath, $"UPDATE audio_files SET unmatched_reason = 'song_deleted' WHERE file_name = 'live (suno-{A}).wav';");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(JsonValueKind.Null, (await FileAsync(client, $"live (suno-{A}).wav")).GetProperty("song").ValueKind);
    }

    /// <summary>
    /// A file whose bytes and time change on disk is read again by the next scan (#203 AC4) and keeps
    /// what it is associated with, how, and its owner's choice of it (#385): one associated by hand
    /// with a Song alone (no Suno ID in its name), and one attached to a Generation by its Suno ID.
    /// </summary>
    [Fact]
    public async Task AChangedFileKeepsItsAssociationItsOriginAndItsPreferenceOnTheNextScan()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        const string Loose = "Loose/second master.wav";
        var bySunoId = $"take (suno-{A}).mp3";
        MediaApi.Place(factory, Loose, "wav");
        MediaApi.Place(factory, bySunoId, "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        var loose = await FileAsync(client, Loose);
        using (var associated = await AssociateAsync(client, loose, Revision(loose), """{"song":"n8-2"}"""))
        {
            Assert.True(associated.StatusCode == HttpStatusCode.OK, await associated.Content.ReadAsStringAsync());
        }

        var expected = new[] { $"{catalog.Other.SongId}|null|user|False|null", $"{catalog.First.SongId}|{catalog.First.GenerationId}|suno-id|False|null" };
        Assert.Equal(expected, new[] { Association(await FileAsync(client, Loose)), Association(await FileAsync(client, bySunoId)) });

        // New bytes and a new time on disk, as an editor's re-export would leave them: read again, kept.
        await ChangeAsync(factory, Loose, "wav", 2048);
        await ChangeAsync(factory, bySunoId, "mp3", 2048);
        Assert.Equal(2, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("changed").GetInt32());
        Assert.Equal(expected, new[] { Association(await FileAsync(client, Loose)), Association(await FileAsync(client, bySunoId)) });

        // Each its owner's preferred file, then changed again: the choice stands as well.
        await PreferAsync(client, "songs/n8-2", await FileAsync(client, Loose));
        await PreferAsync(client, "generations/n8-1-v1-g1", await FileAsync(client, bySunoId));
        expected = [.. expected.Select(static association => association.Replace("|False|", "|True|", StringComparison.Ordinal))];
        await ChangeAsync(factory, Loose, "wav", 4096);
        await ChangeAsync(factory, bySunoId, "mp3", 4096);
        Assert.Equal(2, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("changed").GetInt32());
        Assert.Equal(expected, new[] { Association(await FileAsync(client, Loose)), Association(await FileAsync(client, bySunoId)) });
        Assert.Equal(new FileInfo(MediaApi.Fixture("wav")).Length + 4096, (await FileAsync(client, Loose)).GetProperty("sizeBytes").GetInt64());
    }

    /// <summary>Rewrites the file at <paramref name="path"/> as the fixture plus <paramref name="padding"/> zero bytes, modified an hour per KiB of padding from now.</summary>
    private static async Task ChangeAsync(N8TracksApiFactory factory, string path, string format, int padding)
    {
        var full = MediaApi.Write(factory, path, [.. await File.ReadAllBytesAsync(MediaApi.Fixture(format)), .. new byte[padding]]);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddHours(padding / 1024));
    }

    [Fact]
    public async Task MatchBySunoIdAgainClearsTheBlockAndMatchesAtOnce()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, $"again (suno-{A}).wav", "wav");
        MediaApi.Place(factory, $"nobody (suno-{Nobody}).wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var again = await FileAsync(client, $"again (suno-{A}).wav");

        // An associated file has nothing to match.
        using (var associated = await SendAsync(client, HttpMethod.Post, $"audio-files/{Id(again)}/rematch", Revision(again)))
        {
            await ProblemAsync(associated, HttpStatusCode.UnprocessableEntity, "audio_file_associated");
        }

        using (await SendAsync(client, HttpMethod.Delete, $"audio-files/{Id(again)}/association", Revision(again)))
        {
        }

        again = await FileAsync(client, $"again (suno-{A}).wav");
        Assert.True(again.GetProperty("autoMatchBlocked").GetBoolean());
        using (var stale = await SendAsync(client, HttpMethod.Post, $"audio-files/{Id(again)}/rematch", Revision(again) - 1))
        {
            await ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        using (var rematched = await SendAsync(client, HttpMethod.Post, $"audio-files/{Id(again)}/rematch", Revision(again)))
        {
            Assert.True(rematched.StatusCode == HttpStatusCode.OK, await rematched.Content.ReadAsStringAsync());
            var body = await SetupApi.JsonAsync(rematched);
            Assert.Equal(catalog.First.GenerationId, body.GetProperty("generation").GetProperty("id").GetGuid());
            Assert.Equal("suno-id", body.GetProperty("associationOrigin").GetString());
            Assert.False(body.GetProperty("autoMatchBlocked").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("unmatchedReason").ValueKind);
        }

        // A file whose UUID names nothing stays unmatched, with no reason.
        var nobody = await FileAsync(client, $"nobody (suno-{Nobody}).wav");
        using (await AssociateAsync(client, nobody, Revision(nobody), """{"song":"n8-1"}"""))
        {
        }

        using (await SendAsync(client, HttpMethod.Delete, $"audio-files/{Id(nobody)}/association", Revision(nobody) + 1))
        {
        }

        nobody = await FileAsync(client, $"nobody (suno-{Nobody}).wav");
        Assert.Equal("unassociated_by_user", nobody.GetProperty("unmatchedReason").GetString());
        Assert.True(nobody.GetProperty("autoMatchBlocked").GetBoolean());
        using var unmatched = await SendAsync(client, HttpMethod.Post, $"audio-files/{Id(nobody)}/rematch", Revision(nobody));
        var still = await SetupApi.JsonAsync(unmatched);
        Assert.Equal(JsonValueKind.Null, still.GetProperty("song").ValueKind);
        Assert.Equal(JsonValueKind.Null, still.GetProperty("unmatchedReason").ValueKind);
        Assert.False(still.GetProperty("autoMatchBlocked").GetBoolean());
    }

    [Fact]
    public async Task AMissingFileIsAssociatedChangedAndUnassociatedLikeAnyOther()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        var path = MediaApi.Place(factory, "gone.flac", "flac");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        File.Delete(path);
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var file = await FileAsync(client, "gone.flac");
        Assert.Equal("missing", file.GetProperty("status").GetString());

        using (var associated = await AssociateAsync(client, file, 1, """{"song":"n8-1"}"""))
        {
            Assert.Equal("missing", (await SetupApi.JsonAsync(associated)).GetProperty("status").GetString());
        }

        using (var changed = await AssociateAsync(client, file, 2, """{"song":"n8-2","generation":"n8-2-v1-g1"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        using (var removed = await SendAsync(client, HttpMethod.Delete, $"audio-files/{Id(file)}/association", 3))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        var after = await FileAsync(client, "gone.flac");
        Assert.Equal("missing", after.GetProperty("status").GetString());
        Assert.Equal("unassociated_by_user", after.GetProperty("unmatchedReason").GetString());
    }

    private sealed record Attached(Guid SongId, Guid GenerationId);

    private sealed record Catalog(Attached First, Attached Second, Attached Other);

    /// <summary>Two Songs: "Origin" (n8-1) with Generations of Suno IDs A and B, and "Other" (n8-2) with one of a third.</summary>
    private static async Task<Catalog> CatalogAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.CreateAsync(client, "Other");
        var first = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(A));
        var second = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(B));
        var other = await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d"));
        return new Catalog(
            new Attached(first.Generation.SongId, first.Generation.Id),
            new Attached(second.Generation.SongId, second.Generation.Id),
            new Attached(other.Generation.SongId, other.Generation.Id));
    }

    private static async Task<JsonElement> FileAsync(HttpClient client, string path) =>
        MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, path);

    private static Guid Id(JsonElement file) => file.GetProperty("id").GetGuid();

    /// <summary>Song ID, Generation ID, origin, whether it is its owner's preferred file, and its unmatched reason.</summary>
    private static string Association(JsonElement file) => string.Join(
        '|',
        file.GetProperty("song") is { ValueKind: JsonValueKind.Object } song ? song.GetProperty("id").GetString()! : "null",
        file.GetProperty("generation") is { ValueKind: JsonValueKind.Object } generation ? generation.GetProperty("id").GetString()! : "null",
        file.GetProperty("associationOrigin").GetString() ?? "null",
        file.GetProperty("isPreferred").GetBoolean() ? "True" : "False",
        file.GetProperty("unmatchedReason").GetString() ?? "null");

    /// <summary>Makes <paramref name="file"/> the preferred file of <paramref name="owner"/> (<c>songs/x</c> or <c>generations/x</c>).</summary>
    private static async Task PreferAsync(HttpClient client, string owner, JsonElement file)
    {
        var current = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/{owner}", UriKind.Relative)));
        using var response = await SendAsync(client, HttpMethod.Put, $"{owner}/preferred-audio-file", Revision(current), $$"""{"audioFile":"{{Id(file)}}"}""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static int Revision(JsonElement record) => record.GetProperty("revision").GetInt32();

    private static Task<HttpResponseMessage> AssociateAsync(HttpClient client, JsonElement file, int revision, string json) =>
        SendAsync(client, HttpMethod.Put, $"audio-files/{Id(file)}/association", revision, json);

    private static async Task DeleteGenerationAsync(HttpClient client, string shortcode)
    {
        var generation = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative)));
        using var response = await SendAsync(client, HttpMethod.Delete, $"generations/{shortcode}", Revision(generation));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, body);
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri($"/api/v1/{path}", UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> TokenSendAsync(HttpClient client, string token, HttpMethod method, string path, int revision, string? json)
    {
        using var request = new HttpRequestMessage(method, new Uri($"/api/v1/{path}", UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }
}
