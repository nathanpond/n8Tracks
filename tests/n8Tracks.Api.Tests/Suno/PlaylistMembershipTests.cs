using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The membership of the playlists a playlist sync read (#449): the review lists each playlist the sync
/// chose (<c>GET .../exports/{id}/playlists</c>) with its songs as read compared with what n8Tracks
/// recorded, which songs n8Tracks has, which the export holds, and which are not imported (by Suno ID,
/// never made up); the user chooses whether each is recorded (<c>PATCH</c>, session-only); and only the
/// commit records a membership the user left to record, in <c>suno_playlists</c>, and nothing else of it.
/// </summary>
public sealed class PlaylistMembershipTests
{
    [Fact]
    public async Task APlaylistSyncsSongsAreListedAgainstTheCatalogAndRecordedAtConfirm()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await ImportAsync(client, token, "c-have");

        // The sync read three songs of three: one n8Tracks has, one this export holds, one neither.
        var exportId = await PlaylistExportAsync(client, token, [RemoteStateApi.Clip("c-staged", "Paper Lanterns")], Membership("pl-1", "Night Drive", 3, "c-have", "c-staged", "c-none"));

        var row = Assert.Single(await RowsAsync(client, exportId));
        Assert.Equal("pl-1", row.GetProperty("sunoId").GetString());
        Assert.Equal("Night Drive", row.GetProperty("name").GetString());
        Assert.Equal(3, row.GetProperty("songCount").GetInt32());
        Assert.Equal(3, row.GetProperty("read").GetInt32());
        Assert.True(row.GetProperty("complete").GetBoolean());
        Assert.Equal("new", row.GetProperty("change").GetString());
        Assert.Equal((3, 0), (row.GetProperty("added").GetInt32(), row.GetProperty("dropped").GetInt32()));
        Assert.Equal(1, row.GetProperty("inCatalog").GetInt32());
        Assert.Equal(1, row.GetProperty("inExport").GetInt32());
        Assert.Equal(["c-none"], row.GetProperty("notImported").EnumerateArray().Select(static id => id.GetString()));
        Assert.True(row.GetProperty("record").GetBoolean());
        Assert.False(row.GetProperty("chosen").GetBoolean());

        // Listing changes nothing; Confirm records the songs as read, in order. A song not imported is
        // named only by its Suno ID: no Song or Generation is made for it.
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_playlists;"));
        var result = await ImportCommitApi.CommitAsync(client, exportId);
        Assert.Equal("""[{"sunoId":"pl-1","outcome":"applied"}]""", result.GetProperty("playlists").GetRawText());
        Assert.Equal("""Night Drive|["c-have","c-staged","c-none"]|available""", TestDatabase.Scalar(factory.DataPath, "SELECT name || '|' || clip_ids || '|' || state FROM suno_playlists WHERE suno_id = 'pl-1';"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations WHERE suno_id = 'c-none';"));

        // The same songs again: unchanged. The one that export imported is in the catalog now; the one
        // never imported is still listed as not imported.
        var again = await PlaylistExportAsync(client, token, [], Membership("pl-1", "Night Drive", 3, "c-have", "c-staged", "c-none"));
        var same = Assert.Single(await RowsAsync(client, again));
        Assert.Equal("same", same.GetProperty("change").GetString());
        Assert.Equal((2, 0), (same.GetProperty("inCatalog").GetInt32(), same.GetProperty("inExport").GetInt32()));
        Assert.Equal(["c-none"], same.GetProperty("notImported").EnumerateArray().Select(static id => id.GetString()));

        // A song dropped and one added: changed, with the difference counted.
        var changed = await PlaylistExportAsync(client, token, [], Membership("pl-1", "Night Drive", 3, "c-have", "c-none", "c-new"));
        var differs = Assert.Single(await RowsAsync(client, changed));
        Assert.Equal(("changed", 1, 1), (differs.GetProperty("change").GetString(), differs.GetProperty("added").GetInt32(), differs.GetProperty("dropped").GetInt32()));
    }

    [Fact]
    public async Task AMembershipTheUserChoseNotToRecordIsLeftAsItWasEvenWhenItHoldsAClipImported()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        TestDatabase.Execute(factory.DataPath, """INSERT INTO suno_playlists (suno_id, name, clip_ids, last_seen_utc) VALUES ('pl-1', 'Before', '["c-old"]', '2026-09-01T00:00:00.000Z');""");
        var exportId = await PlaylistExportAsync(client, token, [RemoteStateApi.Clip("c-staged", "Paper Lanterns")], Membership("pl-1", "After", 1, "c-staged"));

        await SetAsync(client, exportId, record: false, "pl-1");

        var row = Assert.Single(await RowsAsync(client, exportId));
        Assert.False(row.GetProperty("record").GetBoolean());
        Assert.True(row.GetProperty("chosen").GetBoolean());
        var before = TestDatabase.Scalar(factory.DataPath, "SELECT name || '|' || clip_ids || '|' || last_seen_utc FROM suno_playlists;");
        var result = await ImportCommitApi.CommitAsync(client, exportId);

        // The clip is imported (its own choice), but the playlist holding it is not recorded for it.
        Assert.Equal("created", ImportCommitApi.Outcome(ImportCommitApi.Records(result)["c-staged"]));
        Assert.Equal("""[{"sunoId":"pl-1","outcome":"skipped"}]""", result.GetProperty("playlists").GetRawText());
        Assert.Equal(before, TestDatabase.Scalar(factory.DataPath, "SELECT name || '|' || clip_ids || '|' || last_seen_utc FROM suno_playlists;"));
    }

    [Fact]
    public async Task APlaylistReadInPartIsShownSoAndRecordedOnlyWhenTheUserChooses()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var partial = await PlaylistExportAsync(client, token, [], Membership("pl-1", "Long Mix", 5, "c-1", "c-2", "c-3"));

        var row = Assert.Single(await RowsAsync(client, partial));
        Assert.False(row.GetProperty("complete").GetBoolean());
        Assert.Equal((5, 3), (row.GetProperty("songCount").GetInt32(), row.GetProperty("read").GetInt32()));
        Assert.False(row.GetProperty("record").GetBoolean());
        await ImportCommitApi.CommitAsync(client, partial);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_playlists;"));

        // Complement: chosen, the songs read are recorded.
        var chosen = await PlaylistExportAsync(client, token, [], Membership("pl-1", "Long Mix", 5, "c-1", "c-2", "c-3"));
        await SetAsync(client, chosen, record: true, "pl-1");
        await ImportCommitApi.CommitAsync(client, chosen);
        Assert.Equal("""["c-1","c-2","c-3"]""", TestDatabase.Scalar(factory.DataPath, "SELECT clip_ids FROM suno_playlists WHERE suno_id = 'pl-1';"));
    }

    [Fact]
    public async Task OnlyAPlaylistSyncHasRowsAndAChoiceMustNameOneOfThemAtTheExportsRevision()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        // A library sync that lists a playlist has no rows: its playlists follow the clips imported (#153).
        var library = await RemoteStateApi.ExportAsync(client, token, [RemoteStateApi.Clip("c-1", "Paper Lanterns")], [], SunoExportApi.Header(playlists: [SunoExportApi.Playlist("pl-1", "Night Drive", "c-1")]));
        Assert.Empty(await RowsAsync(client, library));

        var exportId = await PlaylistExportAsync(client, token, [], Membership("pl-1", "Night Drive", 1, "c-1"));
        using (var unknown = await PatchAsync(client, exportId, await ImportCommitApi.IfMatchAsync(client, exportId), """{"sunoIds":["pl-other"],"record":false}"""))
        {
            var problem = await SetupApi.ProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, "unknown_rows");
            Assert.Equal(["pl-other"], problem.GetProperty("sunoIds").EnumerateArray().Select(static id => id.GetString()));
        }

        using (var malformed = await PatchAsync(client, exportId, await ImportCommitApi.IfMatchAsync(client, exportId), """{"sunoIds":[],"record":"no"}"""))
        {
            var problem = await SetupApi.ProblemAsync(malformed, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("sunoIds", out _));
            Assert.True(problem.GetProperty("errors").TryGetProperty("record", out _));
        }

        using (var stale = await PatchAsync(client, exportId, "\"999\"", """{"sunoIds":["pl-1"],"record":false}"""))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        using (var noRevision = await PatchAsync(client, exportId, null, """{"sunoIds":["pl-1"],"record":false}"""))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, noRevision.StatusCode);
        }

        // None of these changed the choice; the export's own credential cannot list or choose (session only).
        Assert.False(Assert.Single(await RowsAsync(client, exportId)).GetProperty("chosen").GetBoolean());
        using var byToken = await SunoExportApi.SendAsync(client, HttpMethod.Get, SunoExportApi.Export(exportId, "/playlists"), token);
        Assert.Equal(HttpStatusCode.Forbidden, byToken.StatusCode);
    }

    /// <summary>A playlist as a playlist sync's part names it: its songs as read, and the count its page gave.</summary>
    internal static JsonObject Membership(string id, string name, int songCount, params string[] clipIds)
    {
        var playlist = SunoExportApi.Playlist(id, name, clipIds);
        playlist["songCount"] = songCount;
        return playlist;
    }

    /// <summary>Imports <paramref name="sunoIds"/> as new Songs by a library sync and its commit (each record's default).</summary>
    internal static async Task ImportAsync(HttpClient client, string token, params string[] sunoIds)
    {
        var exportId = await RemoteStateApi.ExportAsync(client, token, [.. sunoIds.Select(static id => RemoteStateApi.Clip(id, "Song " + id))], []);
        await ImportCommitApi.CommitAsync(client, exportId);
    }

    /// <summary>Uploads a playlist sync of <paramref name="playlists"/> (its scope names each) with <paramref name="clips"/>, and returns it once ready.</summary>
    internal static async Task<Guid> PlaylistExportAsync(HttpClient client, string token, IEnumerable<JsonNode> clips, params JsonObject[] playlists)
    {
        var header = RemoteStateApi.Header(scope: "playlists", libraryComplete: false, trashedComplete: false);
        header["scope"]!["ids"] = new JsonArray([.. playlists.Select(static playlist => (JsonNode)(string)playlist["id"]!)]);
        var (id, export) = await SunoExportApi.UploadAsync(client, token, header, SunoExportApi.Part(1, clips, null, new JsonArray([.. playlists.Select(static playlist => playlist.DeepClone())])));
        Assert.Equal("ready", export.GetProperty("state").GetString());
        return id;
    }

    internal static async Task<JsonElement[]> RowsAsync(HttpClient client, Guid exportId)
    {
        using var response = await client.GetAsync(SunoExportApi.Export(exportId, "/playlists"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    internal static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid exportId, string? ifMatch, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, SunoExportApi.Export(exportId, "/playlists"))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    internal static async Task SetAsync(HttpClient client, Guid exportId, bool record, params string[] sunoIds)
    {
        using var response = await PatchAsync(client, exportId, await ImportCommitApi.IfMatchAsync(client, exportId), JsonSerializer.Serialize(new { sunoIds, record }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
