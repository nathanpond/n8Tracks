using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Invariants;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The import commit (#140): <c>POST /api/v1/suno/exports/{id}/commit</c> and the job it queues. Each
/// choice kind is applied, each target as a unit (a target failing midway leaves nothing), a record
/// linked since the review is never duplicated, an existing Version is attached to only while it holds
/// the clip's inputs, a Reimport restores the deleted Generation while it is retained, references are
/// resolved, and an interrupted commit returns the export to review. The invariant guards are
/// <c>ImportNeverOverwritesGuardTests</c> (3) and <c>VersionImmutabilityGuardTests</c> (1).
/// </summary>
public sealed class ImportCommitTests
{
    [Fact]
    public async Task ACommitAppliesEachChoiceAndListsWhatItDidPerRecord()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("studio", "Studio"));
        var at = ProposalApi.At;
        var first = ProposalApi.Clip("pair-1", "studio", at, 0, "Pair words", "Pair title");
        var second = ProposalApi.Clip("pair-2", "studio", at, 1, "Pair words", "Pair title, take two");
        var (id, records) = await ProposalApi.ExportAsync(client, token, first, second, ProposalApi.Clip("skip-me", null, at.AddHours(1), 0, "Other words"));
        await ImportCommitApi.StageImageAsync(client, token, id, "pair-1");
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(new JsonObject { ["action"] = "skip" }, "skip-me"));

        var result = await ImportCommitApi.CommitAsync(client, id);

        var outcomes = ImportCommitApi.Records(result);
        Assert.Equal(("created", "created", "skipped"), (ImportCommitApi.Outcome(outcomes["pair-1"]), ImportCommitApi.Outcome(outcomes["pair-2"]), ImportCommitApi.Outcome(outcomes["skip-me"])));
        Assert.Equal(0, ImportCommitApi.GenerationCount(factory, "skip-me"));
        var created = result.GetProperty("created");
        Assert.Equal((1, 1, 2), (created.GetProperty("songs").GetInt32(), created.GetProperty("versions").GetInt32(), created.GetProperty("generations").GetInt32()));

        // The new Song: titled as proposed, in the clips' workspace, credited to no Artist, its Version 1
        // frozen holding the clips' inputs, with two Generations in batch order showing Suno's titles.
        var link = Assert.Single(result.GetProperty("songs").EnumerateArray());
        Assert.Equal(("Pair title", true), (link.GetProperty("title").GetString(), link.GetProperty("created").GetBoolean()));
        var song = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/songs/" + link.GetProperty("shortcode").GetString(), UriKind.Relative)));
        Assert.Equal("studio", song.GetProperty("sunoWorkspace").GetProperty("id").GetString());
        var shortcode = song.GetProperty("shortcode").GetString()!;
        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/versions/{shortcode}-v1", UriKind.Relative)));
        Assert.True(version.GetProperty("isFrozen").GetBoolean());
        Assert.Equal("Pair words", version.GetProperty("lyrics").GetString());
        var generations = (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/songs/{shortcode}/generations", UriKind.Relative)))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["pair-1", "pair-2"], generations.Select(static generation => generation.GetProperty("sunoId").GetString()));
        Assert.Equal($"{shortcode}-v1-g1", outcomes["pair-1"].GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal("Pair title, take two", generations[1].GetProperty("title").GetString());

        // The staged image went to its Generation; the group became one Generation Event of two.
        Assert.NotEqual(JsonValueKind.Null, generations[0].GetProperty("artwork").ValueKind);
        Assert.Equal(JsonValueKind.Null, generations[1].GetProperty("artwork").ValueKind);
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT batch_size FROM generation_events;"));
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generation_event_links;"));
    }

    [Fact]
    public async Task ANewSongIsCreditedToNoArtistEvenWithADefaultArtist()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        using (var artist = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/artists", UriKind.Relative), """{"name":"The default","confirmDuplicate":true}"""))
        {
            var artistId = (await SetupApi.JsonAsync(artist)).GetProperty("id").GetString();
            var settings = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/settings/catalog", UriKind.Relative)));
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("/api/v1/settings/catalog", UriKind.Relative))
            {
                Content = new StringContent($$"""{"defaultArtistId":"{{artistId}}"}""", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(settings.GetProperty("revision").GetInt32())));
            using var set = await client.SendAsync(request);
            Assert.True(set.StatusCode == HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        }

        // A Song made by hand gets the default Artist; one made by the import gets none (Carry 2).
        var manual = await SongApi.CreateAsync(client, "Made by hand");
        var (id, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("credited-1", null, ProposalApi.At, 0, "Credit words", "Imported Song"));
        var result = await ImportCommitApi.CommitAsync(client, id);

        var imported = Assert.Single(result.GetProperty("songs").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM song_artist_credits WHERE song_id = '{Upper(manual.GetProperty("id").GetGuid())}';"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM song_artist_credits WHERE song_id = '{Upper(imported)}';"));
    }

    [Fact]
    public async Task ATargetThatFailsMidwayLeavesNoHalfCreatedSongAndUndoesNothingElse()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        // A Mashup that names one source: its Song and Version are written, then the attach refuses it.
        var mashup = new JsonObject
        {
            ["id"] = "mashup-1",
            ["status"] = "complete",
            ["title"] = "Half a mashup",
            ["metadata"] = new JsonObject { ["task"] = "mashup_condition", ["mashup_clip_ids"] = new JsonArray("not-imported-source") },
        };
        var (id, records) = await ProposalApi.ExportAsync(client, token, mashup, ProposalApi.Clip("whole-1", null, ProposalApi.At, 0, "Whole words", "Whole"));
        Assert.Equal("newSong", ProposalApi.Text(ProposalApi.Target(records["mashup-1"]), "kind"));

        var result = await ImportCommitApi.CommitAsync(client, id);

        var outcomes = ImportCommitApi.Records(result);
        Assert.Equal(("failed", "invalid_clip"), (ImportCommitApi.Outcome(outcomes["mashup-1"]), ImportCommitApi.Reason(outcomes["mashup-1"])));
        Assert.Equal("created", ImportCommitApi.Outcome(outcomes["whole-1"]));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs WHERE title = 'Half a mashup';"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM external_suno_references WHERE suno_id = 'not-imported-source';"));
        Assert.Equal("committed", (await SunoExportApi.GetAsync(client, null, id)).GetProperty("state").GetString());

        // The failed Song's shortcode number went back: the other Song took the first.
        Assert.Equal("n8-1", Assert.Single(result.GetProperty("songs").EnumerateArray()).GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task ARecordLinkedSinceTheReviewIsSkippedNotDuplicated()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var clip = ProposalApi.Clip("race-1", null, ProposalApi.At, 0, "Race words", "Raced");
        var (id, _) = await ProposalApi.ExportAsync(client, token, clip);

        // Linked elsewhere between the review and the commit.
        await SongApi.CreateAsync(client, "Got there first");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", clip.ToJsonString());

        var result = await ImportCommitApi.CommitAsync(client, id);

        var record = ImportCommitApi.Records(result)["race-1"];
        Assert.Equal(("linked", "already_linked"), (ImportCommitApi.Outcome(record), ImportCommitApi.Reason(record)));
        Assert.Equal(1, ImportCommitApi.GenerationCount(factory, "race-1"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs WHERE title = 'Raced';"));
    }

    [Fact]
    public async Task AnExistingVersionEditedSinceTheChoiceFailsItsTargetAndKeepsItsInputs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Edited meanwhile");
        var held = ProposalApi.Clip("held-1", null, ProposalApi.At, 0, "Held words");
        var versionId = await ImportedVersions.AddAsync(factory, song.GetProperty("id").GetGuid(), "2", held);
        var (id, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("held-2", null, ProposalApi.At.AddHours(1), 0, "Held words"));
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(ProposalApi.Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v2" }), "held-2"));

        // The Version is edited after the choice: the review would now refuse it, and so does the commit.
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/versions/{versionId}", UriKind.Relative)))).GetProperty("revision").GetInt32();
        using (var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/versions/{versionId}", UriKind.Relative))
        {
            Content = new StringContent("""{"lyrics":"Changed after the choice"}""", System.Text.Encoding.UTF8, "application/json"),
        })
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        var before = VersionImmutabilityGuardTests.Stored(factory, versionId);
        var result = await ImportCommitApi.CommitAsync(client, id);

        var record = ImportCommitApi.Records(result)["held-2"];
        Assert.Equal(("failed", "inputs_differ"), (ImportCommitApi.Outcome(record), ImportCommitApi.Reason(record)));
        Assert.Equal(before, VersionImmutabilityGuardTests.Stored(factory, versionId));
        Assert.Equal(0, ImportCommitApi.GenerationCount(factory, "held-2"));
    }

    [Fact]
    public async Task ASameInputsClipFreezesAMutableVersionWithItsInputsByteIdentical()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Frozen by the import");
        var versionId = await ImportedVersions.AddAsync(factory, song.GetProperty("id").GetGuid(), "2", ProposalApi.Clip("same-1", null, ProposalApi.At, 0, "Same words"));
        var (id, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("same-2", null, ProposalApi.At.AddHours(1), 0, "Same words"));
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(ProposalApi.Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v2" }), "same-2"));
        var before = VersionImmutabilityGuardTests.Stored(factory, versionId);

        var result = await ImportCommitApi.CommitAsync(client, id);

        Assert.Equal("created", ImportCommitApi.Outcome(ImportCommitApi.Records(result)["same-2"]));
        Assert.Equal(before, VersionImmutabilityGuardTests.Stored(factory, versionId));
        Assert.True((await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/versions/{versionId}", UriKind.Relative)))).GetProperty("isFrozen").GetBoolean());
        Assert.Equal(0, result.GetProperty("created").GetProperty("versions").GetInt32());
    }

    [Fact]
    public async Task EachImportedClipsRawJsonReadsBackIdenticalThroughTheProviderRecord()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var clips = SunoExportApi.LibraryClips().Take(3).ToList();
        var (id, _) = await ProposalApi.ExportAsync(client, token, [.. clips]);

        var result = await ImportCommitApi.CommitAsync(client, id);

        foreach (var clip in clips)
        {
            var record = ImportCommitApi.Records(result)[SunoExportApi.IdOf(clip)];
            Assert.Equal("created", ImportCommitApi.Outcome(record));
            using var response = await client.GetAsync(new Uri($"/api/v1/generations/{record.GetProperty("generation").GetProperty("shortcode").GetString()}/provider-record", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(clip.ToJsonString(), await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task ATakenNumberIsReplacedByTheNextValidOneAndReported()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Numbered");
        var (id, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("numbered-1", null, ProposalApi.At, 0, "Numbered words"));
        await ProposalApi.ChangedAsync(
            client,
            id,
            1,
            ProposalApi.Change(ProposalApi.Import(new JsonObject { ["kind"] = "newVersion", ["key"] = "new:9", ["song"] = "n8-1", ["parentVersion"] = null, ["number"] = "2" }), "numbered-1"));

        // Version 2 is made by hand meanwhile.
        await ImportedVersions.AddAsync(factory, song.GetProperty("id").GetGuid(), "2", ProposalApi.Clip("other", null, ProposalApi.At, 0, "Other words"));
        var result = await ImportCommitApi.CommitAsync(client, id);

        var record = ImportCommitApi.Records(result)["numbered-1"];
        Assert.Equal(("created", "number_taken"), (ImportCommitApi.Outcome(record), ImportCommitApi.Reason(record)));
        Assert.StartsWith("n8-1-v3-", record.GetProperty("generation").GetProperty("shortcode").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReimportRestoresTheDeletedGenerationWithItsRatingCommentsArtworkAndShortcode()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SongApi.CreateAsync(client, "Deleted from");
        var clip = ProposalApi.Clip("back-1", null, ProposalApi.At, 0, "Back words");
        var original = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", clip.ToJsonString());
        await ImportCommitApi.RateAndCommentAsync(client, "n8-1-v1-g1", 4, "Keep the piano");
        await ImportCommitApi.UploadImageAsync(client, "n8-1-v1-g1");
        await ProposalApi.DeleteGenerationAsync(client, "n8-1-v1-g1");

        var (id, records) = await ProposalApi.ExportAsync(client, token, clip);
        Assert.Equal("deleted", records["back-1"].GetProperty("class").GetString());
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(ProposalApi.Import(new JsonObject { ["kind"] = "newSong", ["key"] = "new:5", ["title"] = "Back again" }), "back-1"));

        var result = await ImportCommitApi.CommitAsync(client, id);

        var record = ImportCommitApi.Records(result)["back-1"];
        Assert.True(record.GetProperty("restored").GetBoolean());
        Assert.Equal(original.Generation.Id, record.GetProperty("generation").GetProperty("id").GetGuid());
        var generation = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/generations/n8-1-v1-g1", UriKind.Relative)));
        Assert.Equal(4, generation.GetProperty("rating").GetInt32());
        Assert.Equal("Keep the piano", Assert.Single(generation.GetProperty("comments").EnumerateArray()).GetProperty("text").GetString());
        Assert.NotEqual(JsonValueKind.Null, generation.GetProperty("artwork").ValueKind);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM provider_tombstones;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs WHERE title = 'Back again';"));
    }

    [Fact]
    public async Task AReimportPastItsRetentionImportsTheClipAfresh()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SongApi.CreateAsync(client, "Deleted long ago");
        var clip = ProposalApi.Clip("gone-1", null, ProposalApi.At, 0, "Gone words");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", clip.ToJsonString());
        await ProposalApi.DeleteGenerationAsync(client, "n8-1-v1-g1");

        // Its 30 days are over (the prune has not run yet).
        TestDatabase.Execute(factory.DataPath, "UPDATE retention_groups SET prune_after_utc = '2000-01-01T00:00:00.000Z';");
        var (id, _) = await ProposalApi.ExportAsync(client, token, clip);
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(ProposalApi.Import(new JsonObject { ["kind"] = "newSong", ["key"] = "new:5", ["title"] = "Imported afresh" }), "gone-1"));

        var result = await ImportCommitApi.CommitAsync(client, id);

        var record = ImportCommitApi.Records(result)["gone-1"];
        Assert.Equal("created", ImportCommitApi.Outcome(record));
        Assert.False(record.TryGetProperty("restored", out _));
        Assert.Equal("n8-2-v1-g1", record.GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM provider_tombstones;"));
    }

    [Fact]
    public async Task EachNewVersionHoldsItsClipsSourcesResolvedToClipsImportedInTheSameCommit()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        const string parentId = "00000000-0000-4000-8000-0000000000d1";
        var parent = new JsonObject { ["id"] = parentId, ["status"] = "complete", ["title"] = "The parent" };
        var cover = new JsonObject
        {
            ["id"] = "00000000-0000-4000-8000-0000000000d2",
            ["status"] = "complete",
            ["title"] = "The cover",
            ["metadata"] = new JsonObject { ["task"] = "cover", ["cover_clip_id"] = parentId, ["edited_clip_id"] = parentId },
        };
        var (id, records) = await ProposalApi.ExportAsync(client, token, cover, parent);

        var result = await ImportCommitApi.CommitAsync(client, id);

        var outcomes = ImportCommitApi.Records(result);
        Assert.All(outcomes.Values, static record => Assert.Equal("created", ImportCommitApi.Outcome(record)));
        var coverVersion = await SetupApi.JsonAsync(await client.GetAsync(new Uri(
            "/api/v1/versions/" + outcomes[cover["id"]!.GetValue<string>()].GetProperty("generation").GetProperty("shortcode").GetString()![..^3],
            UriKind.Relative)));
        var source = Assert.Single(coverVersion.GetProperty("inputs").GetProperty("sources").EnumerateArray());
        Assert.Equal("ok", source.GetProperty("availability").GetString());
        Assert.Equal(outcomes[parentId].GetProperty("generation").GetProperty("id").GetGuid().ToString(), source.GetProperty("generation").GetProperty("id").GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_relationships;"));
    }

    [Fact]
    public async Task TheCommitIsRefusedUnlessTheExportIsReadyAtTheRevisionSentAndIsSessionOnly()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var (id, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("once-1", null, ProposalApi.At, 0, "Once words"));

        using (var bearer = await ImportCommitApi.SendAsync(client, id, "\"1\"", token))
        {
            await SetupApi.ProblemAsync(bearer, HttpStatusCode.Forbidden, "session_required");
        }

        using (var missing = await ImportCommitApi.SendAsync(client, id, null))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        using (var stale = await ImportCommitApi.SendAsync(client, id, "\"7\""))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        using (var unknown = await ImportCommitApi.SendAsync(client, Guid.CreateVersion7(), "\"1\""))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        await ImportCommitApi.CommitAsync(client, id);

        // An export is committed once.
        using (var again = await ImportCommitApi.SendAsync(client, id, "\"1\""))
        {
            await SetupApi.ProblemAsync(again, HttpStatusCode.Conflict, ImportCommitService.CommittedCode);
        }

        // A discarded export cannot be committed.
        var (discarded, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("never-1", null, ProposalApi.At, 0, "Never words"));
        using (await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(discarded, "/discard"), token))
        {
        }

        using (var notReady = await ImportCommitApi.SendAsync(client, discarded, "\"1\""))
        {
            await SetupApi.ProblemAsync(notReady, HttpStatusCode.Conflict, "export_not_ready");
        }

        // While one is being committed, another commit, and a new export's completion, wait.
        var (waiting, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("later-1", null, ProposalApi.At, 0, "Later words"));
        TestDatabase.Execute(factory.DataPath, $"UPDATE suno_exports SET state = 'committing' WHERE id = '{Upper(waiting)}';");
        using (var inProgress = await ImportCommitApi.SendAsync(client, waiting, "\"1\""))
        {
            await SetupApi.ProblemAsync(inProgress, HttpStatusCode.Conflict, "import_in_progress");
        }
    }

    [Fact]
    public async Task AnInterruptedCommitReturnsTheExportToReviewWithWhatItImportedLinked()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var done = ProposalApi.Clip("done-1", null, ProposalApi.At, 0, "Done words");
        var (id, _) = await ProposalApi.ExportAsync(client, token, done, ProposalApi.Clip("todo-1", null, ProposalApi.At.AddHours(1), 0, "Todo words"));

        // The process stopped mid-commit: one record was imported, and the export is still committing.
        await ImportedVersions.AttachAsync(factory, await SongApi.CreateAsync(client, "Imported before the stop"), "2", done);
        TestDatabase.Execute(factory.DataPath, $"UPDATE suno_exports SET state = 'committing', job_id = NULL WHERE id = '{Upper(id)}';");

        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ImportCommitService>().RecoverInterruptedAsync());
        }

        Assert.Equal("ready", (await SunoExportApi.GetAsync(client, null, id)).GetProperty("state").GetString());
        var records = await SunoExportApi.RecordsByIdAsync(client, id);
        Assert.Equal(("linked", "skip"), (records["done-1"].GetProperty("class").GetString(), records["done-1"].GetProperty("choice").GetProperty("action").GetString()));
        Assert.Equal("import", records["todo-1"].GetProperty("choice").GetProperty("action").GetString());

        // The user confirms again; nothing is duplicated.
        var result = await ImportCommitApi.CommitAsync(client, id);
        Assert.Equal("created", ImportCommitApi.Outcome(ImportCommitApi.Records(result)["todo-1"]));
        Assert.Equal(1, ImportCommitApi.GenerationCount(factory, "done-1"));
    }

    [Fact]
    public async Task TheAnswerNamesTheJobItStartedEvenWhenTheJobEndsBeforeTheAnswer()
    {
        // The commit job ends before the commit request answers: the queue hands back the job's ID
        // only once the worker has finished it, as a quick (all Skip) job can on a busy machine.
        using var factory = new N8TracksApiFactory { TestServices = FinishFirstQueue.Register };
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var (id, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("quick-1", null, ProposalApi.At, 0, "Quick words"));

        using var response = await ImportCommitApi.SendAsync(client, id, await ImportCommitApi.IfMatchAsync(client, id));

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var started = await SetupApi.JsonAsync(response);
        Assert.Equal(JsonValueKind.String, started.GetProperty("jobId").ValueKind);
        var jobId = started.GetProperty("jobId").GetGuid();
        Assert.Equal($"/api/v1/jobs/{jobId}", response.Headers.Location?.OriginalString);
        Assert.Equal("committed", started.GetProperty("state").GetString());
        Assert.Equal("succeeded", (await Jobs.TestJobs.GetAsync(client, jobId)).GetProperty("status").GetString());

        // The export keeps naming its job, and so does the refusal of a second commit.
        Assert.Equal(jobId, (await SunoExportApi.GetAsync(client, null, id)).GetProperty("jobId").GetGuid());
        using var again = await ImportCommitApi.SendAsync(client, id, await ImportCommitApi.IfMatchAsync(client, id));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(jobId, (await SetupApi.JsonAsync(again)).GetProperty("jobId").GetGuid());
    }

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    /// <summary>A queue that returns a commit job's ID only after the worker has finished the job.</summary>
    private sealed class FinishFirstQueue(Application.Jobs.JobQueue inner, IServiceScopeFactory scopes) : Application.Jobs.IJobQueue
    {
        public static void Register(IServiceCollection services)
        {
            services.AddSingleton<Application.Jobs.IJobQueue>(static provider =>
                new FinishFirstQueue(provider.GetRequiredService<Application.Jobs.JobQueue>(), provider.GetRequiredService<IServiceScopeFactory>()));
        }

        public async Task<Guid> EnqueueAsync(string type, JsonElement? payload, CancellationToken cancellationToken)
        {
            var id = await inner.EnqueueAsync(type, payload, cancellationToken);
            if (type != ImportCommitService.JobType)
            {
                return id;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (true)
            {
                var scope = scopes.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var job = await scope.ServiceProvider.GetRequiredService<Application.Jobs.JobService>().FindAsync(id, cancellationToken);
                    if (job is { Status: Application.Jobs.JobStatus.Succeeded or Application.Jobs.JobStatus.Failed })
                    {
                        return id;
                    }
                }

                Assert.True(DateTime.UtcNow < deadline, $"Commit job {id} never ended.");
                await Task.Delay(25, cancellationToken);
            }
        }
    }
}
