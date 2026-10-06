using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Retention;
using n8Tracks.Infrastructure.Retention;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Generations;

/// <summary>
/// Archiving Generations and the Song's Selected Generation (#120). <c>PATCH /api/v1/generations/{reference}</c>
/// takes <c>state</c> (archive or reactivate) under the Generation's revision; <c>PUT</c> and
/// <c>DELETE /api/v1/songs/{reference}/selected-generation</c> choose and clear the Song's one chosen
/// output under the Song's revision. All need <c>generations.evaluate</c>. Selecting changes nothing
/// but the Song's selection, revision, and updated time.
/// </summary>
public sealed class GenerationSelectionEndpointTests
{
    [Fact]
    public async Task AGenerationOfAnyVersionIsSelectedReplacedAndClearedEachRaisingTheSongsRevision()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        var song = await SongAsync(client);
        Assert.False(song.GetProperty("hasSelectedGeneration").GetBoolean());
        Assert.Equal(JsonValueKind.Null, song.GetProperty("selectedGeneration").ValueKind);
        var revision = song.GetProperty("revision").GetInt32();

        // Select a Generation of Version 1 by shortcode: the Song names it, and only it is marked.
        clock.Advance(TimeSpan.FromMinutes(5));
        var selected = await SelectAsync(client, revision, "n8-1-v1-g1", HttpStatusCode.OK);
        Assert.Equal(revision + 1, selected.GetProperty("revision").GetInt32());
        Assert.True(selected.GetProperty("hasSelectedGeneration").GetBoolean());
        Assert.Equal("n8-1-v1-g1", selected.GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
        Assert.Equal("active", selected.GetProperty("selectedGeneration").GetProperty("state").GetString());
        Assert.Equal("present", selected.GetProperty("selectedGeneration").GetProperty("remoteState").GetString());
        Assert.Equal(clock.GetUtcNow().UtcDateTime, selected.GetProperty("updatedAt").GetDateTime());
        Assert.Equal(["n8-1-v1-g1"], await MarkedAsync(client));

        // Replace it with one of Version 2, named by ID: the first is no longer marked.
        var v2g1 = await GenerationAsync(client, "n8-1-v2-g1");
        var replaced = await SelectAsync(client, revision + 1, v2g1.GetProperty("id").GetString()!, HttpStatusCode.OK);
        Assert.Equal(revision + 2, replaced.GetProperty("revision").GetInt32());
        Assert.Equal(v2g1.GetProperty("id").GetString(), replaced.GetProperty("selectedGeneration").GetProperty("id").GetString());
        Assert.Equal(["n8-1-v2-g1"], await MarkedAsync(client));
        Assert.True((await GenerationAsync(client, "n8-1-v2-g1")).GetProperty("isSelected").GetBoolean());
        Assert.False((await GenerationAsync(client, "n8-1-v1-g1")).GetProperty("isSelected").GetBoolean());

        // Choosing it again stores nothing: the revision stays.
        Assert.Equal(revision + 2, (await SelectAsync(client, revision + 2, "n8-1-v2-g1", HttpStatusCode.OK)).GetProperty("revision").GetInt32());

        // The Songs list says it too.
        var row = (await SongApi.ListAsync(client)).GetProperty("items")[0];
        Assert.True(row.GetProperty("hasSelectedGeneration").GetBoolean());
        Assert.Equal("n8-1-v2-g1", row.GetProperty("selectedGeneration").GetProperty("shortcode").GetString());

        // Clear: the Song has none; clearing again stores nothing.
        using (var cleared = await SendAsync(client, HttpMethod.Delete, "songs/n8-1/selected-generation", SongApi.Quoted(revision + 2)))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            Assert.Equal(SongApi.Quoted(revision + 3), cleared.Headers.ETag?.Tag);
            var body = await SetupApi.JsonAsync(cleared);
            Assert.False(body.GetProperty("hasSelectedGeneration").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("selectedGeneration").ValueKind);
        }

        using (var again = await SendAsync(client, HttpMethod.Delete, "songs/n8-1/selected-generation", SongApi.Quoted(revision + 3)))
        {
            Assert.Equal(revision + 3, (await SetupApi.JsonAsync(again)).GetProperty("revision").GetInt32());
        }

        Assert.Empty(await MarkedAsync(client));
        Assert.Equal("NULL", TestDatabase.Scalar(factory.DataPath, "SELECT quote(selected_generation_id) FROM songs;"));
    }

    [Fact]
    public async Task SelectingChangesNothingButTheSongsSelectionRevisionAndUpdatedTime()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        await RateAsync(client, "n8-1-v1-g2", 1, """{"rating":4}""");
        var song = await SongAsync(client);
        var before = Snapshot(factory);

        clock.Advance(TimeSpan.FromMinutes(5));
        var selected = await SelectAsync(client, song.GetProperty("revision").GetInt32(), "n8-1-v1-g1", HttpStatusCode.OK);
        Assert.Equal(before, Snapshot(factory));

        clock.Advance(TimeSpan.FromMinutes(5));
        await SelectAsync(client, selected.GetProperty("revision").GetInt32(), "n8-1-v2-g1", HttpStatusCode.OK);
        Assert.Equal(before, Snapshot(factory));

        // What does change: the selection, the revision, the updated time.
        Assert.Equal(
            $"{Upper((await GenerationAsync(client, "n8-1-v2-g1")).GetProperty("id").GetGuid())}|{song.GetProperty("revision").GetInt32() + 2}|{UtcText(clock)}",
            TestDatabase.Scalar(factory.DataPath, "SELECT selected_generation_id || '|' || revision || '|' || updated_utc FROM songs;"));
    }

    [Fact]
    public async Task AnotherSongsGenerationAnUnknownOneAStaleRevisionAndAMissingNameAreRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        await SongApi.CreateAsync(client, "Other");
        await SongApi.AttachGenerationAsync(factory, "n8-2-v1", null);
        var revision = (await SongAsync(client)).GetProperty("revision").GetInt32();
        var other = await GenerationAsync(client, "n8-2-v1-g1");
        var before = Snapshot(factory, includeSongs: true);

        // Another Song's Generation, by shortcode or ID: 422, naming it.
        foreach (var reference in new[] { "n8-2-v1-g1", other.GetProperty("id").GetString()! })
        {
            using var response = await SendAsync(client, HttpMethod.Put, "songs/n8-1/selected-generation", SongApi.Quoted(revision), Body(reference));
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "generation_not_in_song");
            Assert.Equal("n8-2-v1-g1", problem.GetProperty("shortcode").GetString());
        }

        // An unknown Generation, a Version's shortcode, and nonsense: 404.
        foreach (var reference in new[] { "n8-1-v1-g9", "n8-1-v1", Guid.NewGuid().ToString(), "nonsense" })
        {
            using var response = await SendAsync(client, HttpMethod.Put, "songs/n8-1/selected-generation", SongApi.Quoted(revision), Body(reference));
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // No Generation named, or not as text: 422 on the field.
        foreach (var json in new[] { "{}", """{"generation":null}""", """{"generation":"  "}""", """{"generation":7}""", """{"generation":["n8-1-v1-g1"]}""" })
        {
            using var response = await SendAsync(client, HttpMethod.Put, "songs/n8-1/selected-generation", SongApi.Quoted(revision), json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("generation", out _), json);
        }

        // An unknown Song, no If-Match, a malformed one.
        using (var unknown = await SendAsync(client, HttpMethod.Put, "songs/n8-99/selected-generation", SongApi.Quoted(1), Body("n8-1-v1-g1")))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using (var missing = await SendAsync(client, HttpMethod.Put, "songs/n8-1/selected-generation", null, Body("n8-1-v1-g1")))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, Revisions.RequiredCode);
        }

        using (var malformed = await SendAsync(client, HttpMethod.Delete, "songs/n8-1/selected-generation", "1"))
        {
            await SetupApi.ProblemAsync(malformed, HttpStatusCode.BadRequest, Revisions.InvalidCode);
        }

        Assert.Equal(before, Snapshot(factory, includeSongs: true));

        // A stale Song revision is 409 with the Song as it is now, for selecting and clearing alike.
        var selected = await SelectAsync(client, revision, "n8-1-v1-g1", HttpStatusCode.OK);
        foreach (var (method, json) in new[] { (HttpMethod.Put, Body("n8-1-v2-g1")), (HttpMethod.Delete, null) })
        {
            using var stale = await SendAsync(client, method, "songs/n8-1/selected-generation", SongApi.Quoted(revision), json);
            var current = (await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode)).GetProperty("current");
            Assert.Equal(selected.GetProperty("revision").GetInt32(), current.GetProperty("revision").GetInt32());
            Assert.Equal("n8-1-v1-g1", current.GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
        }

        Assert.Equal(["n8-1-v1-g1"], await MarkedAsync(client));
    }

    [Fact]
    public async Task AGenerationIsArchivedAndReactivatedIndependentlyOfItsVersionUnderItsOwnRevision()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        var songBefore = await SongAsync(client);
        var version = await VersionAsync(client, "n8-1-v1");

        clock.Advance(TimeSpan.FromMinutes(5));
        var archived = await PatchGenerationAsync(client, "n8-1-v1-g1", 1, """{"state":"archived"}""", HttpStatusCode.OK);
        Assert.Equal("archived", archived.GetProperty("state").GetString());
        Assert.Equal(2, archived.GetProperty("revision").GetInt32());

        // The Version stays active, its sibling Generation active; the Song's updated time moved, not its revision.
        Assert.False((await VersionAsync(client, "n8-1-v1")).GetProperty("archived").GetBoolean());
        Assert.Equal(version.GetProperty("revision").GetInt32(), (await VersionAsync(client, "n8-1-v1")).GetProperty("revision").GetInt32());
        Assert.Equal("active", (await GenerationAsync(client, "n8-1-v1-g2")).GetProperty("state").GetString());
        var songAfter = await SongAsync(client);
        Assert.Equal(songBefore.GetProperty("revision").GetInt32(), songAfter.GetProperty("revision").GetInt32());
        Assert.Equal(clock.GetUtcNow().UtcDateTime, songAfter.GetProperty("updatedAt").GetDateTime());

        // The state it already has stores nothing; state and rating go together under one revision.
        Assert.Equal(2, (await PatchGenerationAsync(client, "n8-1-v1-g1", 2, """{"state":"archived"}""", HttpStatusCode.OK)).GetProperty("revision").GetInt32());
        var both = await PatchGenerationAsync(client, "n8-1-v1-g1", 2, """{"state":"active","rating":5}""", HttpStatusCode.OK);
        Assert.Equal("active", both.GetProperty("state").GetString());
        Assert.Equal(5, both.GetProperty("rating").GetInt32());
        Assert.Equal(3, both.GetProperty("revision").GetInt32());

        // An archived Version's Generation stays active until the user archives it.
        using (var archiveVersion = await SendAsync(client, HttpMethod.Patch, "versions/n8-1-v1", SongApi.Quoted(version.GetProperty("revision").GetInt32()), """{"archived":true}"""))
        {
            Assert.Equal(HttpStatusCode.OK, archiveVersion.StatusCode);
        }

        Assert.Equal("active", (await GenerationAsync(client, "n8-1-v1-g1")).GetProperty("state").GetString());
        Assert.Equal("archived", (await PatchGenerationAsync(client, "n8-1-v1-g1", 3, """{"state":"archived"}""", HttpStatusCode.OK)).GetProperty("state").GetString());

        // A state that is not active or archived is 422, and a stale revision 409, changing nothing.
        foreach (var json in new[] { """{"state":"trashed"}""", """{"state":"Active"}""", """{"state":null}""", """{"state":1}""" })
        {
            using var invalid = await SendAsync(client, HttpMethod.Patch, "generations/n8-1-v1-g1", SongApi.Quoted(4), json);
            var problem = await SetupApi.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("state", out _), json);
        }

        using (var stale = await SendAsync(client, HttpMethod.Patch, "generations/n8-1-v1-g1", SongApi.Quoted(3), """{"state":"active"}"""))
        {
            var current = (await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode)).GetProperty("current");
            Assert.Equal("archived", current.GetProperty("state").GetString());
            Assert.Equal(4, current.GetProperty("revision").GetInt32());
        }

        Assert.Equal("archived|4", TestDatabase.Scalar(factory.DataPath, "SELECT state || '|' || revision FROM generations WHERE ordinal = 1 AND version_id = (SELECT id FROM versions WHERE number = '1');"));
    }

    [Fact]
    public async Task AnArchivedTrashedOrMissingGenerationCanBeSelectedAndStaysSelectedWhenArchivedShowingItsState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        TestDatabase.Execute(factory.DataPath, "UPDATE generations SET remote_state = 'missing' WHERE ordinal = 2;");
        await PatchGenerationAsync(client, "n8-1-v2-g1", 1, """{"state":"archived"}""", HttpStatusCode.OK);

        // An archived one is chosen and shown with its state.
        var revision = (await SongAsync(client)).GetProperty("revision").GetInt32();
        var song = await SelectAsync(client, revision, "n8-1-v2-g1", HttpStatusCode.OK);
        Assert.Equal("archived", song.GetProperty("selectedGeneration").GetProperty("state").GetString());

        // A Remote Missing one too: the choice is the user's.
        song = await SelectAsync(client, revision + 1, "n8-1-v1-g2", HttpStatusCode.OK);
        Assert.Equal("missing", song.GetProperty("selectedGeneration").GetProperty("remoteState").GetString());

        // Archiving the selected one leaves it selected; the Song's revision is unchanged by it.
        var archived = await PatchGenerationAsync(client, "n8-1-v1-g2", 1, """{"state":"archived"}""", HttpStatusCode.OK);
        Assert.True(archived.GetProperty("isSelected").GetBoolean());
        var after = await SongAsync(client);
        Assert.Equal(revision + 2, after.GetProperty("revision").GetInt32());
        Assert.Equal("n8-1-v1-g2", after.GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
        Assert.Equal("archived", after.GetProperty("selectedGeneration").GetProperty("state").GetString());

        // A trashed one reads as such.
        TestDatabase.Execute(factory.DataPath, "UPDATE generations SET remote_state = 'trashed' WHERE ordinal = 2;");
        Assert.Equal("trashed", (await SongAsync(client)).GetProperty("selectedGeneration").GetProperty("remoteState").GetString());
    }

    [Fact]
    public async Task AlbumAndPlaylistTracksSayWhetherTheirSongHasASelectedGeneration()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        await SongApi.CreateAsync(client, "Without");
        var album = await CreateCollectionAsync(client, "albums", "Chosen EP");
        var playlist = await CreateCollectionAsync(client, "playlists", "Chosen mix");
        var revision = 1;
        foreach (var song in new[] { "n8-1", "n8-2" })
        {
            using var track = await SendAsync(client, HttpMethod.Post, $"albums/{album}/tracks", SongApi.Quoted(revision), JsonSerializer.Serialize(new { songId = song }));
            Assert.Equal(HttpStatusCode.OK, track.StatusCode);
            using var entry = await SendAsync(client, HttpMethod.Post, $"playlists/{playlist}/songs", SongApi.Quoted(revision), JsonSerializer.Serialize(new { songId = song }));
            Assert.Equal(HttpStatusCode.OK, entry.StatusCode);
            revision++;
        }

        Assert.Equal([false, false], await FlagsAsync(client, $"albums/{album}", "tracks"));
        Assert.Equal([false, false], await FlagsAsync(client, $"playlists/{playlist}", "songs"));

        await SelectAsync(client, (await SongAsync(client)).GetProperty("revision").GetInt32(), "n8-1-v1-g1", HttpStatusCode.OK);

        Assert.Equal([true, false], await FlagsAsync(client, $"albums/{album}", "tracks"));
        Assert.Equal([true, false], await FlagsAsync(client, $"playlists/{playlist}", "songs"));
        var listed = (await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/albums", UriKind.Relative)))).GetProperty("items")[0];
        Assert.True(listed.GetProperty("tracks")[0].GetProperty("hasSelectedGeneration").GetBoolean());
    }

    [Fact]
    public async Task ArchivingAndSelectingNeedGenerationsEvaluate()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        using var tool = factory.CreateClient();
        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.GenerationsEvaluate)]);
        var evaluator = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.GenerationsEvaluate);
        var before = Snapshot(factory, includeSongs: true);

        foreach (var (method, path) in new[] { (HttpMethod.Put, "songs/n8-1/selected-generation"), (HttpMethod.Delete, "songs/n8-1/selected-generation"), (HttpMethod.Patch, "generations/n8-1-v1-g1") })
        {
            using var refused = await CredentialApi.SendAsync(tool, method, new Uri("/api/v1/" + path, UriKind.Relative), others);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.GenerationsEvaluate, problem.GetProperty("requiredScope").GetString());
        }

        Assert.Equal(before, Snapshot(factory, includeSongs: true));

        // A token holding only generations.evaluate archives, selects, and clears.
        var revision = (await SongAsync(client)).GetProperty("revision").GetInt32();
        using (var archive = await TokenSendAsync(tool, evaluator, HttpMethod.Patch, "generations/n8-1-v1-g1", "\"1\"", """{"state":"archived"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        }

        using (var select = await TokenSendAsync(tool, evaluator, HttpMethod.Put, "songs/n8-1/selected-generation", SongApi.Quoted(revision), Body("n8-1-v1-g1")))
        {
            Assert.Equal(HttpStatusCode.OK, select.StatusCode);
        }

        using (var clear = await TokenSendAsync(tool, evaluator, HttpMethod.Delete, "songs/n8-1/selected-generation", SongApi.Quoted(revision + 1), null))
        {
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        }
    }

    [Fact]
    public async Task DeletingTheSelectedGenerationsVersionClearsTheSelectionAndRestoringItPutsItBack()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        var selected = await SelectAsync(client, (await SongAsync(client)).GetProperty("revision").GetInt32(), "n8-1-v1-g2", HttpStatusCode.OK);

        var version = await VersionAsync(client, "n8-1-v1");
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v1", SongApi.Quoted(version.GetProperty("revision").GetInt32())))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        var song = await SongAsync(client);
        Assert.False(song.GetProperty("hasSelectedGeneration").GetBoolean());
        Assert.True(song.GetProperty("revision").GetInt32() > selected.GetProperty("revision").GetInt32());
        Assert.Equal(["selected-generation"], TestDatabase.Rows(factory.DataPath, "SELECT record_type FROM retention_records WHERE record_type = 'selected-generation';"));

        var group = await WithServiceAsync(factory, service => service.FindByShortcodeAsync("n8-1-v1", CancellationToken.None));
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group!.Id));
        Assert.Empty(restored.Notes);
        var back = await SongAsync(client);
        Assert.Equal("n8-1-v1-g2", back.GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
        Assert.True(back.GetProperty("revision").GetInt32() > song.GetProperty("revision").GetInt32());
        Assert.True((await GenerationAsync(client, "n8-1-v1-g2")).GetProperty("isSelected").GetBoolean());
    }

    [Fact]
    public async Task ASelectionMadeAfterTheVersionWasDeletedIsKeptWhenTheVersionIsRestored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        await SelectAsync(client, (await SongAsync(client)).GetProperty("revision").GetInt32(), "n8-1-v1-g1", HttpStatusCode.OK);
        var version = await VersionAsync(client, "n8-1-v1");
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v1", SongApi.Quoted(version.GetProperty("revision").GetInt32())))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        // Meanwhile the user chose another: the restore does not take it away, and says so.
        await SelectAsync(client, (await SongAsync(client)).GetProperty("revision").GetInt32(), "n8-1-v2-g1", HttpStatusCode.OK);
        var group = await WithServiceAsync(factory, service => service.FindByShortcodeAsync("n8-1-v1", CancellationToken.None));
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group!.Id));
        Assert.Contains(restored.Notes, static note => note.Contains("Selected Generation", StringComparison.Ordinal));
        Assert.Equal("n8-1-v2-g1", (await SongAsync(client)).GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task ASongDeletedWithASelectedGenerationIsRestoredWithIt()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        var selected = await SelectAsync(client, (await SongAsync(client)).GetProperty("revision").GetInt32(), "n8-1-v2-g1", HttpStatusCode.OK);
        var selectedId = TestDatabase.Scalar(factory.DataPath, "SELECT selected_generation_id FROM songs;");

        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-1", SongApi.Quoted(selected.GetProperty("revision").GetInt32()), """{"confirmTitle":"Chosen"}"""))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;"));

        var group = await WithServiceAsync(factory, service => service.FindByShortcodeAsync("n8-1", CancellationToken.None));
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group!.Id));
        Assert.Empty(restored.Notes);
        Assert.Equal(selectedId, TestDatabase.Scalar(factory.DataPath, "SELECT selected_generation_id FROM songs;"));
        Assert.Equal("n8-1-v2-g1", (await SongAsync(client)).GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
        Assert.Equal(["n8-1-v2-g1"], await MarkedAsync(client));
    }

    [Fact]
    public void ASongRetainedBeforeTheSelectionRestoresWithNone()
    {
        var shape1 = new JsonObject { ["id"] = "A", ["title"] = "Old" };

        var shape2 = RetainedTypes.SongShape1To2(shape1);

        Assert.True(shape2.ContainsKey("selected_generation_id"));
        Assert.Null(shape2["selected_generation_id"]);
        Assert.Equal(2, RetainedTypes.Song.ShapeVersion);
        Assert.True(RetainedTypes.Song.Upgraders.ContainsKey(1));
    }

    [Fact]
    public async Task TheDatabaseRefusesASelectedGenerationThatDoesNotExistAndTheRemovalOfOneThatIsSelected()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await TwoVersionSongAsync(factory, client);
        await SelectAsync(client, (await SongAsync(client)).GetProperty("revision").GetInt32(), "n8-1-v1-g1", HttpStatusCode.OK);

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(factory.DataPath, $"UPDATE songs SET selected_generation_id = '{Upper(Guid.NewGuid())}';"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(factory.DataPath, "DELETE FROM generations WHERE id = (SELECT selected_generation_id FROM songs);"));
    }

    /// <summary>Song n8-1 ("Chosen") with Version 1 (Generations g1, g2) and Version 2 (g1).</summary>
    private static async Task TwoVersionSongAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await SongApi.CreateAsync(client, "Chosen");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("chosen-1"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("chosen-2"));
        using (var branched = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative), """{"sourceVersionId":"n8-1-v1","number":"2"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, branched.StatusCode);
        }

        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", Clips.Minimal("chosen-3"));
    }

    /// <summary>
    /// Every Version, Generation, and comment row, and every Song's columns: all of them when
    /// <paramref name="includeSongs"/>, otherwise all but the selection, revision, and updated time
    /// (what selecting may change).
    /// </summary>
    private static string Snapshot(N8TracksApiFactory factory, bool includeSongs = false)
    {
        var songColumns = TestDatabase.Rows(factory.DataPath, "SELECT name FROM pragma_table_info('songs') ORDER BY cid;")
            .Where(column => includeSongs || column is not ("selected_generation_id" or "revision" or "updated_utc"))
            .Select(static column => $"quote({column})");
        return string.Join(
            Environment.NewLine,
            TestDatabase.Rows(factory.DataPath, $"SELECT {string.Join(" || '|' || ", songColumns)} FROM songs ORDER BY shortcode_number;")
                .Concat(Table(factory, "versions"))
                .Concat(Table(factory, "generations"))
                .Concat(Table(factory, "generation_comments")));
    }

    private static IEnumerable<string> Table(N8TracksApiFactory factory, string table)
    {
        var columns = TestDatabase.Rows(factory.DataPath, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;").Select(static column => $"quote({column})");
        return TestDatabase.Rows(factory.DataPath, $"SELECT {string.Join(" || '|' || ", columns)} FROM {table} ORDER BY id;");
    }

    private static string UtcText(TestClock clock) => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string Body(string generation) => JsonSerializer.Serialize(new { generation });

    private static async Task<JsonElement> SongAsync(HttpClient client) => await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));

    private static async Task<JsonElement> VersionAsync(HttpClient client, string reference) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/versions/{reference}", UriKind.Relative)));

    private static async Task<JsonElement> GenerationAsync(HttpClient client, string reference) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/generations/{reference}", UriKind.Relative)));

    /// <summary>The shortcodes of the Song's Generations marked selected, as its Generation list answers them.</summary>
    private static async Task<List<string>> MarkedAsync(HttpClient client) =>
        [.. (await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/songs/n8-1/generations", UriKind.Relative)))).GetProperty("items").EnumerateArray()
            .Where(static generation => generation.GetProperty("isSelected").GetBoolean())
            .Select(static generation => generation.GetProperty("shortcode").GetString()!)];

    private static async Task<List<bool>> FlagsAsync(HttpClient client, string path, string list) =>
        [.. (await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/" + path, UriKind.Relative)))).GetProperty(list).EnumerateArray()
            .Select(static entry => entry.GetProperty("hasSelectedGeneration").GetBoolean())];

    private static async Task<string> CreateCollectionAsync(HttpClient client, string path, string title)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/" + path, UriKind.Relative), JsonSerializer.Serialize(new { title }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> SelectAsync(HttpClient client, int revision, string generation, HttpStatusCode status)
    {
        using var response = await SendAsync(client, HttpMethod.Put, "songs/n8-1/selected-generation", SongApi.Quoted(revision), Body(generation));
        Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
        var body = await SetupApi.JsonAsync(response);
        Assert.Equal(SongApi.Quoted(body.GetProperty("revision").GetInt32()), response.Headers.ETag?.Tag);
        return body;
    }

    private static async Task<JsonElement> PatchGenerationAsync(HttpClient client, string reference, int revision, string json, HttpStatusCode status)
    {
        using var response = await SendAsync(client, HttpMethod.Patch, $"generations/{reference}", SongApi.Quoted(revision), json);
        Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static Task<JsonElement> RateAsync(HttpClient client, string reference, int revision, string json) =>
        PatchGenerationAsync(client, reference, revision, json, HttpStatusCode.OK);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? ifMatch, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri("/api/v1/" + path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> TokenSendAsync(HttpClient client, string token, HttpMethod method, string path, string? ifMatch, string? json)
    {
        using var request = new HttpRequestMessage(method, new Uri("/api/v1/" + path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }
}
