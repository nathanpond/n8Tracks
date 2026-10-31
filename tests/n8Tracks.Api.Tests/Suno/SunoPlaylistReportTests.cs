using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The user's Suno playlists as a sync reads them (#449): <c>PUT /api/v1/suno/playlists/discovered</c>
/// (<c>suno.sync</c>) keeps each playlist of the playlist list in <c>suno_playlists</c> by its Suno ID,
/// with its name, trashed flag, and song count and nothing else of Suno's object; only a complete list
/// changes availability; nothing is deleted; a playlist's members never change; the sync panel reads the
/// known list without members (<c>GET .../discovered</c>, <c>suno.sync</c>); and the Sources editor's list
/// (<c>GET /api/v1/suno/playlists</c>) answers each with its state and song count.
/// </summary>
public sealed class SunoPlaylistReportTests
{
    private static readonly Uri Discovered = new("/api/v1/suno/playlists/discovered", UriKind.Relative);
    private static readonly Uri Playlists = new("/api/v1/suno/playlists", UriKind.Relative);

    /// <summary>What a playlist list's owner fields hold here: never stored, answered, or logged.</summary>
    private const string HandleSentinel = "handle-sentinel-9c1d";
    private const string DisplayNameSentinel = "display-sentinel-9c1d";

    [Fact]
    public async Task ACapturedPlaylistListIsKeptByIdWithOnlyTheFieldsReadAndOfferedAsInspiration()
    {
        using var factory = new LoggingApiFactory("Debug");
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var captured = JsonNode.Parse(Clips.Fixture("playlist-me.response.json"))!["playlists"]!.AsArray();
        JsonNode[] playlists = [.. captured.Select(static playlist => playlist!.DeepClone())];
        foreach (var playlist in playlists)
        {
            playlist["user_handle"] = HandleSentinel;
            playlist["user_display_name"] = DisplayNameSentinel;
        }

        var answer = await ReportAsync(client, token, complete: true, playlists);

        Assert.Equal(playlists.Select(static playlist => (string?)playlist["id"]), answer.GetProperty("added").EnumerateArray().Select(static id => id.GetString()));
        // The panel's list: ID, name, state, and song count, never a member.
        var known = await KnownAsync(client, token);
        Assert.Equal(answer.GetProperty("items").EnumerateArray().Select(static item => item.GetRawText()), known.Select(static item => item.GetRawText()));
        Assert.All(known, static item => Assert.Equal(["id", "name", "songCount", "state"], item.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal)));
        var first = known.Single(item => item.GetProperty("id").GetString() == (string?)captured[0]!["id"]);
        Assert.Equal((int)captured[0]!["song_count"]!, first.GetProperty("songCount").GetInt32());
        Assert.Equal("available", first.GetProperty("state").GetString());

        // Inspo's list (catalog.read) has them too, Available, with no members recorded yet.
        var listed = await ListAsync(client);
        Assert.Equal(captured.Count, listed.Length);
        Assert.All(listed, static item =>
        {
            Assert.Equal("available", item.GetProperty("state").GetString());
            Assert.Equal(0, item.GetProperty("memberCount").GetInt32());
            Assert.Empty(item.GetProperty("clipIds").EnumerateArray());
        });

        // Nothing else of Suno's playlist is kept: no owner's handle or display name, image, or description.
        var rows = string.Join('\n', TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || name || '|' || clip_ids || '|' || state || '|' || coalesce(song_count, 'null') || '|' || last_seen_utc FROM suno_playlists;"));
        Assert.DoesNotContain(HandleSentinel, rows, StringComparison.Ordinal);
        Assert.DoesNotContain(DisplayNameSentinel, rows, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn2.suno.ai", rows, StringComparison.Ordinal);
        Assert.Equal(
            ["clip_ids", "last_seen_utc", "name", "song_count", "state", "suno_id"],
            TestDatabase.Rows(factory.DataPath, "SELECT name FROM pragma_table_info('suno_playlists');").Order(StringComparer.Ordinal));

        // Invariant 6: the report is logged by its counts only.
        Assert.Contains("Suno playlists reported: 2 named, complete True", factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(HandleSentinel, factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(DisplayNameSentinel, factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain((string)captured[0]!["name"]!, factory.CapturedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportingAndTheKnownListNeedSunoSyncAndASignedOutCallerIsRefused()
    {
        using var factory = SongApi.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.SunoSync)]);
        var sync = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var body = Body(complete: false, Playlist("pl-1", "Night Drive"));

        using (var refused = await SendAsync(client, others, body))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SunoSync, problem.GetProperty("requiredScope").GetString());
        }

        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Discovered, others))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
        }

        using (var signedOut = await client.PutAsync(Discovered, new StringContent(body, System.Text.Encoding.UTF8, "application/json")))
        {
            await SetupApi.ProblemAsync(signedOut, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        using (var signedOut = await client.GetAsync(Discovered))
        {
            await SetupApi.ProblemAsync(signedOut, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_playlists;"));

        // Complement: suno.sync reports and reads; it is refused the Sources editor's list, which holds the members.
        using var reported = await SendAsync(client, sync, body);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        Assert.Equal(["pl-1"], (await KnownAsync(client, sync)).Select(static item => item.GetProperty("id").GetString()));
        using var members = await CredentialApi.SendAsync(client, HttpMethod.Get, Playlists, sync);
        await SetupApi.ProblemAsync(members, HttpStatusCode.Forbidden, "insufficient_scope");
    }

    [Fact]
    public async Task AKnownPlaylistTakesItsNewNameAndCountAndKeepsTheMembersAnImportRecorded()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        // A playlist an import recorded (#153): its name and members.
        TestDatabase.Execute(factory.DataPath, """INSERT INTO suno_playlists (suno_id, name, clip_ids, last_seen_utc) VALUES ('pl-1', 'From an import', '["c1","c2"]', '2026-09-01T00:00:00.000Z');""");

        clock.Advance(TimeSpan.FromMinutes(5));
        var answer = await ReportAsync(client, token, complete: false, Playlist("pl-1", "Harbour Lights", songCount: 3));

        Assert.Empty(answer.GetProperty("added").EnumerateArray());
        Assert.Equal(["pl-1"], answer.GetProperty("renamed").EnumerateArray().Select(static id => id.GetString()));
        var one = (await ListAsync(client)).Single();
        Assert.Equal("Harbour Lights", one.GetProperty("name").GetString());
        Assert.Equal(3, one.GetProperty("songCount").GetInt32());
        Assert.Equal(["c1", "c2"], one.GetProperty("clipIds").EnumerateArray().Select(static id => id.GetString()));
        Assert.NotEqual("2026-09-01T00:00:00.000Z", TestDatabase.Scalar(factory.DataPath, "SELECT last_seen_utc FROM suno_playlists WHERE suno_id = 'pl-1';"));

        // A blank name and no count keep what is known; num_total_results stands in for song_count.
        await ReportAsync(client, token, complete: false, Playlist("pl-1", "  "));
        Assert.Equal("Harbour Lights|3", TestDatabase.Scalar(factory.DataPath, "SELECT name || '|' || song_count FROM suno_playlists WHERE suno_id = 'pl-1';"));
        var counted = Playlist("pl-2", "Tidal Hymns");
        counted["num_total_results"] = 7;
        await ReportAsync(client, token, complete: false, counted);
        Assert.Equal("7", TestDatabase.Scalar(factory.DataPath, "SELECT song_count FROM suno_playlists WHERE suno_id = 'pl-2';"));
    }

    [Fact]
    public async Task OnlyACompleteListMarksAPlaylistUnavailableNothingIsDeletedAndItComesBack()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        TestDatabase.Execute(factory.DataPath, """INSERT INTO suno_playlists (suno_id, name, clip_ids, last_seen_utc) VALUES ('pl-import', 'Seen in an import', '["c9"]', '2026-09-01T00:00:00.000Z');""");
        await ReportAsync(client, token, complete: true, Playlist("pl-1", "Going"), Playlist("pl-2", "Staying"), Playlist("pl-import", "Seen in an import"));

        // An incomplete list changes no state, even for one it names trashed.
        var incomplete = await ReportAsync(client, token, complete: false, Playlist("pl-2", "Staying", trashed: true));
        Assert.Empty(incomplete.GetProperty("becameUnavailable").EnumerateArray());

        var answer = await ReportAsync(client, token, complete: true, Playlist("pl-2", "Staying", trashed: true));

        Assert.Equal(["pl-1", "pl-2", "pl-import"], answer.GetProperty("becameUnavailable").EnumerateArray().Select(static id => id.GetString()).Order(StringComparer.Ordinal));
        Assert.All(await ListAsync(client), static item => Assert.Equal("unavailable", item.GetProperty("state").GetString()));
        Assert.Equal("""["c9"]""", TestDatabase.Scalar(factory.DataPath, "SELECT clip_ids FROM suno_playlists WHERE suno_id = 'pl-import';"));
        Assert.Equal("3", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_playlists;"));

        var back = await ReportAsync(client, token, complete: true, Playlist("pl-1", "Going"), Playlist("pl-2", "Staying"), Playlist("pl-import", "Seen in an import"));
        Assert.Equal(["pl-1", "pl-2", "pl-import"], back.GetProperty("becameAvailable").EnumerateArray().Select(static id => id.GetString()));
    }

    [Theory]
    [InlineData("""{"complete":true,"playlists":[{"id":"pl-1","name":"One"},{"name":"No ID"}]}""", "playlists[1].id")]
    [InlineData("""{"complete":true,"playlists":[{"id":""}]}""", "playlists[0].id")]
    [InlineData("""{"complete":true,"playlists":["pl-1"]}""", "playlists[0]")]
    [InlineData("""{"complete":true,"playlists":{"id":"pl-1"}}""", "playlists")]
    [InlineData("""{"complete":"yes","playlists":[{"id":"pl-1"}]}""", "complete")]
    [InlineData("""{"playlists":[{"id":"pl-1"}]}""", "complete")]
    public async Task AReportThatCannotBeReadStoresNothing(string body, string field)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await ReportAsync(client, token, complete: true, Playlist("pl-0", "Known"));
        var before = TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || name || '|' || state || '|' || last_seen_utc FROM suno_playlists ORDER BY suno_id;");

        using var response = await SendAsync(client, token, body);

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.GetRawText());
        Assert.Equal(before, TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || name || '|' || state || '|' || last_seen_utc FROM suno_playlists ORDER BY suno_id;"));
    }

    /// <summary>A playlist object as Suno's playlist list sends it, with only the fields given (null leaves one out).</summary>
    private static JsonObject Playlist(string id, string? name = null, bool? trashed = null, int? songCount = null)
    {
        var playlist = new JsonObject { ["id"] = id, ["image_url"] = "https://cdn2.suno.ai/image.jpeg", ["user_handle"] = HandleSentinel, ["user_display_name"] = DisplayNameSentinel };
        if (name is not null)
        {
            playlist["name"] = name;
        }

        if (trashed is not null)
        {
            playlist["is_trashed"] = trashed;
        }

        if (songCount is not null)
        {
            playlist["song_count"] = songCount;
        }

        return playlist;
    }

    private static string Body(bool complete, params JsonNode[] playlists) =>
        new JsonObject { ["complete"] = complete, ["playlists"] = new JsonArray([.. playlists.Select(static playlist => playlist.DeepClone())]) }.ToJsonString();

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Discovered)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReportAsync(HttpClient client, string token, bool complete, params JsonNode[] playlists)
    {
        using var response = await SendAsync(client, token, Body(complete, playlists));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement[]> KnownAsync(HttpClient client, string token)
    {
        using var response = await CredentialApi.SendAsync(client, HttpMethod.Get, Discovered, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Playlists);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }
}
