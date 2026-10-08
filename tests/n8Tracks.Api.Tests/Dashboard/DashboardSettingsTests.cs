using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Dashboard;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Dashboard;

/// <summary>
/// #230: the dashboard's arrangement (<c>GET</c>, <c>PUT</c>, <c>DELETE /api/v1/settings/dashboard</c>)
/// and the Song opened last (<c>GET</c>, <c>PUT /api/v1/settings/last-song</c>): saved, read, reset,
/// unknown keys ignored, missing ones appended shown, the revision check, and session only.
/// </summary>
public sealed class DashboardSettingsTests
{
    private static readonly Uri Layout = new("/api/v1/settings/dashboard", UriKind.Relative);
    private static readonly Uri LastSong = new("/api/v1/settings/last-song", UriKind.Relative);

    private static readonly string[] DefaultOrder =
        ["recentlyEdited", "unmatchedFiles", "sunoReviews", "sunoProblems", "workflowStates", "withoutSelection"];

    [Fact]
    public async Task BeforeAnySaveTheDefaultArrangementShowsEverySectionAtRevisionZero()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(Layout);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"0\"", response.Headers.ETag?.Tag);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var layout = await SetupApi.JsonAsync(response);

        Assert.Equal(0, layout.GetProperty("revision").GetInt32());
        Assert.False(layout.GetProperty("customized").GetBoolean());
        Assert.Equal(DefaultOrder, Keys(layout));
        Assert.All(layout.GetProperty("sections").EnumerateArray(), static section => Assert.False(section.GetProperty("hidden").GetBoolean()));
        Assert.Equal(DefaultOrder, layout.GetProperty("defaultOrder").EnumerateArray().Select(static key => key.GetString()));
        Assert.Equal(DashboardSectionKeys.DefaultOrder, DefaultOrder);
    }

    [Fact]
    public async Task ASavedArrangementIsReadBackAndAStaleSaveIsAConflict()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // The Demo: By workflow state hidden, Unmatched Files at the top.
        var arrangement = Sections(
            ("unmatchedFiles", false),
            ("recentlyEdited", false),
            ("sunoReviews", false),
            ("sunoProblems", false),
            ("workflowStates", true),
            ("withoutSelection", false));
        using (var saved = await SendAsync(client, HttpMethod.Put, Layout, "\"0\"", arrangement))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal("\"1\"", saved.Headers.ETag?.Tag);
        }

        var read = await GetAsync(client, Layout);
        Assert.Equal(1, read.GetProperty("revision").GetInt32());
        Assert.True(read.GetProperty("customized").GetBoolean());
        Assert.Equal(["unmatchedFiles", "recentlyEdited", "sunoReviews", "sunoProblems", "workflowStates", "withoutSelection"], Keys(read));
        Assert.Equal(["workflowStates"], Hidden(read));

        // Another browser read revision 0 before the save: its save is refused with what is stored now.
        using (var stale = await SendAsync(client, HttpMethod.Put, Layout, "\"0\"", Sections(("recentlyEdited", true))))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(1, problem.GetProperty("current").GetProperty("revision").GetInt32());
            Assert.Equal(["workflowStates"], Hidden(problem.GetProperty("current")));
        }

        using (var missing = await SendAsync(client, HttpMethod.Put, Layout, null, arrangement))
        {
            await SetupApi.ProblemAsync(missing, (HttpStatusCode)428, "revision_required");
        }

        using (var malformed = await SendAsync(client, HttpMethod.Put, Layout, "1", arrangement))
        {
            await SetupApi.ProblemAsync(malformed, HttpStatusCode.BadRequest, "invalid_revision");
        }
    }

    [Fact]
    public async Task AnUnknownKeyIsIgnoredARepeatedOneTakesItsFirstPlaceAndAMissingOneIsAppendedShown()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var sent = Sections(
            ("sunoProblems", true),
            ("notASection", true),
            ("withoutSelection", false),
            ("sunoProblems", false));
        using var response = await SendAsync(client, HttpMethod.Put, Layout, "\"0\"", sent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await SetupApi.JsonAsync(response);

        Assert.Equal(["sunoProblems", "withoutSelection", "recentlyEdited", "unmatchedFiles", "sunoReviews", "workflowStates"], Keys(saved));
        Assert.Equal(["sunoProblems"], Hidden(saved));
        Assert.Equal(Keys(saved), Keys(await GetAsync(client, Layout)));
    }

    [Fact]
    public async Task ASectionALaterVersionAddsAppearsAtTheEndShownAndAStoredUnknownKeyIsDropped()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // Saved by a version without Suno problems, and by one that had a section this one has not.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDashboardLayoutStore>().WriteAsync(
                new StoredDashboardLayout(
                    [
                        new("withoutSelection", true),
                        new("retiredSection", false),
                        new("recentlyEdited", false),
                        new("workflowStates", false),
                        new("sunoReviews", true),
                        new("unmatchedFiles", false),
                    ],
                    3),
                CancellationToken.None);
        }

        var read = await GetAsync(client, Layout);
        Assert.Equal(3, read.GetProperty("revision").GetInt32());
        Assert.Equal(["withoutSelection", "recentlyEdited", "workflowStates", "sunoReviews", "unmatchedFiles", "sunoProblems"], Keys(read));
        Assert.Equal(["withoutSelection", "sunoReviews"], Hidden(read));
    }

    [Fact]
    public async Task ResetClearsTheArrangementSoTheDefaultAppliesAndTheRevisionKeepsCounting()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var saved = await SendAsync(client, HttpMethod.Put, Layout, "\"0\"", Sections(("withoutSelection", true))))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using (var reset = await SendAsync(client, HttpMethod.Delete, Layout, "\"1\"", body: null))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            Assert.Equal("\"2\"", reset.Headers.ETag?.Tag);
            var answer = await SetupApi.JsonAsync(reset);
            Assert.False(answer.GetProperty("customized").GetBoolean());
            Assert.Equal(DefaultOrder, Keys(answer));
            Assert.Empty(Hidden(answer));
        }

        var read = await GetAsync(client, Layout);
        Assert.Equal(2, read.GetProperty("revision").GetInt32());
        Assert.False(read.GetProperty("customized").GetBoolean());
        Assert.Equal(DefaultOrder, Keys(read));

        // A save based on a revision from before the reset, the first included, is stale.
        foreach (var revision in new[] { "\"0\"", "\"1\"" })
        {
            using var stale = await SendAsync(client, HttpMethod.Put, Layout, revision, Sections(("recentlyEdited", true)));
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        using (var staleReset = await SendAsync(client, HttpMethod.Delete, Layout, "\"1\"", body: null))
        {
            await SetupApi.ProblemAsync(staleReset, HttpStatusCode.Conflict, "revision_conflict");
        }

        using (var missing = await SendAsync(client, HttpMethod.Delete, Layout, null, body: null))
        {
            await SetupApi.ProblemAsync(missing, (HttpStatusCode)428, "revision_required");
        }
    }

    [Theory]
    [InlineData("""{}""", "sections")]
    [InlineData("""{"sections":null}""", "sections")]
    [InlineData("""{"sections":{"key":"recentlyEdited","hidden":true}}""", "sections")]
    [InlineData("""{"sections":["recentlyEdited"]}""", "sections[0]")]
    [InlineData("""{"sections":[{"key":"recentlyEdited"}]}""", "sections[0]")]
    [InlineData("""{"sections":[{"key":"recentlyEdited","hidden":true},{"key":3,"hidden":false}]}""", "sections[1]")]
    [InlineData("""{"sections":[{"key":"recentlyEdited","hidden":"yes"}]}""", "sections[0]")]
    public async Task AMalformedArrangementIsRefusedAndNothingIsSaved(string json, string field)
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await SendAsync(client, HttpMethod.Put, Layout, "\"0\"", json);
        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());

        Assert.Equal(0, (await GetAsync(client, Layout)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task TooManyPlacementsAreRefused()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var many = Sections([.. Enumerable.Range(0, DashboardLayout.MaximumPlacements + 1).Select(static index => ($"key{index}", false))]);
        using var response = await SendAsync(client, HttpMethod.Put, Layout, "\"0\"", many);
        await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
    }

    [Fact]
    public async Task TheLastSongIsNoneUntilRecordedThenTheSongOpenedLastAndLastWriteWins()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var first = await SongApi.CreateAsync(client, "First Song");
        var second = await SongApi.CreateAsync(client, "Second Song");

        var none = await GetAsync(client, LastSong);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("song").ValueKind);
        Assert.False(none.GetProperty("deleted").GetBoolean());

        // Reading a Song records nothing: only the explicit call does.
        using (var opened = await client.GetAsync(SongApi.Song("n8-1")))
        {
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        }

        Assert.Equal(JsonValueKind.Null, (await GetAsync(client, LastSong)).GetProperty("song").ValueKind);

        await RememberAsync(client, first.GetProperty("id").GetGuid());
        var last = (await GetAsync(client, LastSong)).GetProperty("song");
        Assert.Equal(first.GetProperty("id").GetGuid(), last.GetProperty("id").GetGuid());
        Assert.Equal("n8-1", last.GetProperty("shortcode").GetString());
        Assert.Equal("First Song", last.GetProperty("title").GetString());

        // No revision: any write replaces it, an If-Match sent or not.
        await RememberAsync(client, second.GetProperty("id").GetGuid());
        await RememberAsync(client, first.GetProperty("id").GetGuid(), ifMatch: "\"7\"");
        await RememberAsync(client, second.GetProperty("id").GetGuid());
        Assert.Equal("n8-2", (await GetAsync(client, LastSong)).GetProperty("song").GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task AnArchivedLastSongStillOpensAndADeletedOneIsAnsweredAsDeleted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Gone Soon");
        await RememberAsync(client, song.GetProperty("id").GetGuid());

        TestDatabase.Execute(
            factory.DataPath,
            string.Create(CultureInfo.InvariantCulture, $"UPDATE songs SET workflow_state_id = '{DefaultWorkflowStates.Archived.Id.ToString().ToUpperInvariant()}' WHERE shortcode_number = 1;"));
        Assert.Equal("n8-1", (await GetAsync(client, LastSong)).GetProperty("song").GetProperty("shortcode").GetString());

        // Deleted within its retention period (restorable): deleted.
        await DeleteSongAsync(client, "n8-1");
        var deleted = await GetAsync(client, LastSong);
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("song").ValueKind);
        Assert.True(deleted.GetProperty("deleted").GetBoolean());

        // Gone for good: still deleted.
        var other = await SongApi.CreateAsync(client, "Gone For Good");
        await RememberAsync(client, other.GetProperty("id").GetGuid());
        SongApi.RemoveDirectly(factory.DataPath, 2);
        Assert.True((await GetAsync(client, LastSong)).GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task RecordingASongThatIsNotThereOrIsNotAnIdIsRefused()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Kept");

        using (var missing = await SendAsync(client, HttpMethod.Put, LastSong, null, JsonSerializer.Serialize(new { song = Guid.CreateVersion7() })))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }

        foreach (var json in new[] { """{}""", """{"song":null}""", """{"song":"n8-1"}""", """{"song":12}""" })
        {
            using var response = await SendAsync(client, HttpMethod.Put, LastSong, null, json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("song", out _), json);
        }

        Assert.Equal(JsonValueKind.Null, (await GetAsync(client, LastSong)).GetProperty("song").ValueKind);
    }

    [Fact]
    public async Task ABearerTokenIsRefusedEveryDashboardSettingWhateverItsScopes()
    {
        using var factory = SongApi.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        foreach (var (method, uri) in new[] { (HttpMethod.Get, Layout), (HttpMethod.Put, Layout), (HttpMethod.Delete, Layout), (HttpMethod.Get, LastSong), (HttpMethod.Put, LastSong) })
        {
            using var response = await CredentialApi.SendAsync(client, method, uri, token);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, "session_required");
        }

        // Signed out: 401.
        using var anonymous = await client.GetAsync(Layout);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static string Sections(params (string Key, bool Hidden)[] sections) =>
        JsonSerializer.Serialize(new { sections = sections.Select(static section => new { key = section.Key, hidden = section.Hidden }) });

    private static List<string> Keys(JsonElement layout) =>
        [.. layout.GetProperty("sections").EnumerateArray().Select(static section => section.GetProperty("key").GetString()!)];

    private static List<string> Hidden(JsonElement layout) =>
        [.. layout.GetProperty("sections").EnumerateArray().Where(static section => section.GetProperty("hidden").GetBoolean()).Select(static section => section.GetProperty("key").GetString()!)];

    private static async Task<JsonElement> GetAsync(HttpClient client, Uri uri)
    {
        using var response = await client.GetAsync(uri);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task RememberAsync(HttpClient client, Guid song, string? ifMatch = null)
    {
        using var response = await SendAsync(client, HttpMethod.Put, LastSong, ifMatch, JsonSerializer.Serialize(new { song }));
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static async Task DeleteSongAsync(HttpClient client, string reference)
    {
        var song = await GetAsync(client, SongApi.Song(reference));
        using var response = await SendAsync(
            client,
            HttpMethod.Delete,
            SongApi.Song(reference),
            SongApi.Quoted(song.GetProperty("revision").GetInt32()),
            JsonSerializer.Serialize(new { confirmTitle = song.GetProperty("title").GetString() }));
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Sends <paramref name="body"/> (JSON text, or none) with the anti-forgery header and <paramref name="ifMatch"/> when given.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string? ifMatch, string? body)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }
}
