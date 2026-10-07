using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using static n8Tracks.Api.Tests.Suno.ImportReviewApi;
using static n8Tracks.Api.Tests.Suno.ProposalApi;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The review page's API (#139): records named for review and found by title, a change of choices by
/// filter, what confirming would do with every choice checked again, where a record may go in a Song, and
/// which export is waiting. All of it is session-only, and none of it writes the catalog (the invariant 3
/// guard runs it too).
/// </summary>
public sealed class ImportReviewEndpointTests
{
    /// <summary>
    /// Each record names its choice's target (a new Song, a Version, a new Version of a Song) and a linked
    /// record its Generation, for the page's headings and links; <c>q</c> finds records by title, ignoring case.
    /// </summary>
    [Fact]
    public async Task RecordsNameTheirTargetsAndGenerationsAndAreFoundByTitle()
    {
        await using var review = await ReviewAsync();

        var records = await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId);
        var morning = records["a"].GetProperty("target");
        Assert.Equal("newSong", Text(morning, "kind"));
        Assert.Equal("Morning Light", Text(morning.GetProperty("song"), "title"));
        Assert.Equal("1", Text(morning, "number"));
        var generation = records["linked"].GetProperty("generation");
        Assert.Equal("n8-1-v3-g1", Text(generation, "shortcode"));
        Assert.Equal("n8-1", Text(generation, "songShortcode"));
        Assert.Equal(JsonValueKind.Null, records["linked"].GetProperty("target").ValueKind);

        await ChangedAsync(review.Client, review.ExportId, 1, Change(Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v2" }), "a"));
        await ChangedAsync(review.Client, review.ExportId, 2, Change(NewVersionOfTarget("new:9", "4"), "b"));
        records = await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId);
        var existing = records["a"].GetProperty("target");
        Assert.Equal("version", Text(existing, "kind"));
        Assert.Equal("n8-1-v2", Text(existing.GetProperty("version"), "shortcode"));
        Assert.Equal("Target", Text(existing.GetProperty("song"), "title"));
        var branch = records["b"].GetProperty("target");
        Assert.Equal("newVersion", Text(branch, "kind"));
        Assert.Equal("n8-1", Text(branch.GetProperty("song"), "shortcode"));
        Assert.Equal("4", Text(branch, "number"));

        Assert.Equal(["a"], (await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId, "?q=LIGHT")).Keys);
        Assert.Empty(await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId, "?q=nowhere"));
        Assert.Equal(["b"], (await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId, "?q=rain&class=new")).Keys);
    }

    /// <summary>
    /// A change by filter takes every new, ignored, or deleted record that matches, but those it leaves out
    /// and never a linked one; one that matches none is refused, and a body may not mix the two forms.
    /// </summary>
    [Fact]
    public async Task AChangeByFilterTakesEveryMatchingRecordTheReviewCanChange()
    {
        await using var review = await ReviewAsync();
        var ignore = new JsonObject { ["action"] = "ignore" };

        var export = await ChangedAsync(review.Client, review.ExportId, 1, ByFilter(new JsonObject { ["workspace"] = "free" }, ignore, "b"));
        Assert.Equal(2, export.GetProperty("revision").GetInt32());
        var records = await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId);
        Assert.Equal("ignore", Text(records["a"].GetProperty("choice"), "action"));
        Assert.Equal("import", Text(records["b"].GetProperty("choice"), "action"));
        Assert.Equal("skip", Text(records["linked"].GetProperty("choice"), "action"));

        // Every record, linked one included, matches an empty filter; only those the review can change are taken.
        await ChangedAsync(review.Client, review.ExportId, 2, ByFilter(new JsonObject(), ignore));
        records = await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId);
        Assert.Equal("ignore", Text(records["b"].GetProperty("choice"), "action"));
        Assert.Equal("skip", Text(records["linked"].GetProperty("choice"), "action"));

        foreach (var (filter, except) in new[] { (new JsonObject { ["class"] = "linked" }, Array.Empty<string>()), (new JsonObject { ["q"] = "rain" }, ["b"]) })
        {
            using var none = await PatchAsync(review.Client, review.ExportId, "\"3\"", ByFilter(filter, ignore, except));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
            var problem = await SetupApi.JsonAsync(none);
            Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
            Assert.True(problem.GetProperty("errors").TryGetProperty("filter", out _));
        }

        foreach (var (body, field) in new[]
        {
            ("""{"filter":{},"sunoIds":["a"],"choice":{"action":"skip"}}""", "sunoIds"),
            ("""{"filter":{"class":"old"},"choice":{"action":"skip"}}""", "filter.class"),
            ("""{"filter":{"title":"x"},"choice":{"action":"skip"}}""", "filter.title"),
            ("""{"filter":[],"choice":{"action":"skip"}}""", "filter"),
            (ByFilter(new JsonObject(), new JsonObject { ["action"] = "skip" }, [.. Enumerable.Range(0, 1_001).Select(static n => $"x{n}")]), "except"),
        })
        {
            using var malformed = await PatchAsync(review.Client, review.ExportId, "\"3\"", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
            Assert.True((await SetupApi.JsonAsync(malformed)).GetProperty("errors").TryGetProperty(field, out _), field);
        }

        Assert.Equal(3, (await SunoExportApi.GetAsync(review.Client, null, review.ExportId)).GetProperty("revision").GetInt32());
    }

    /// <summary>
    /// The summary counts what confirming would create, ignore, and skip, and checks every stored choice
    /// against the catalog as it is now: a Version edited since, or a number taken since, makes the choice
    /// invalid with its reason; with nothing imported or ignored there is nothing to do.
    /// </summary>
    [Fact]
    public async Task TheSummaryCountsWhatConfirmingDoesAndChecksEveryChoiceAgain()
    {
        await using var review = await ReviewAsync();

        var summary = await SummaryAsync(review.Client, review.ExportId);
        Assert.Equal((2, 2, 2, 0, 0, 1), Counts(summary));
        Assert.True(summary.GetProperty("valid").GetBoolean());
        Assert.False(summary.GetProperty("nothingToDo").GetBoolean());
        Assert.Equal("new:3", Text(summary, "nextKey"));
        Assert.Equal(1, summary.GetProperty("revision").GetInt32());
        var workspace = Assert.Single(summary.GetProperty("workspaces").EnumerateArray());
        Assert.Equal("free", Text(workspace, "id"));
        Assert.Equal(3, workspace.GetProperty("count").GetInt32());

        await ChangedAsync(review.Client, review.ExportId, 1, Change(Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v2" }), "a"));
        await ChangedAsync(review.Client, review.ExportId, 2, Change(NewVersionOfTarget("new:9", "4"), "b"));
        summary = await SummaryAsync(review.Client, review.ExportId);
        Assert.Equal((0, 1, 2, 0, 0, 1), Counts(summary));
        Assert.True(summary.GetProperty("valid").GetBoolean());
        Assert.Equal("new:10", Text(summary, "nextKey"));

        // The catalog moves on: Version 2 is edited, and number 4 is taken.
        await EditLyricsAsync(review.Client, "n8-1-v2", "[Verse]\nEdited since");
        SongApi.AddVersionDirectly(review.Factory.DataPath, 1, "4");
        summary = await SummaryAsync(review.Client, review.ExportId);
        Assert.False(summary.GetProperty("valid").GetBoolean());
        Assert.Equal(2, summary.GetProperty("invalidCount").GetInt32());
        Assert.Equal(["inputs_differ"], Reasons(summary, "a"));
        Assert.Equal(["invalid_number"], Reasons(summary, "b"));

        await ChangedAsync(review.Client, review.ExportId, 3, ByFilter(new JsonObject(), new JsonObject { ["action"] = "skip" }));
        summary = await SummaryAsync(review.Client, review.ExportId);
        Assert.Equal((0, 0, 0, 0, 0, 3), Counts(summary));
        Assert.True(summary.GetProperty("nothingToDo").GetBoolean());
        Assert.True(summary.GetProperty("valid").GetBoolean());

        using var response = await review.Client.GetAsync(SunoExportApi.Export(review.ExportId, "/summary"));
        Assert.Equal("\"4\"", response.Headers.ETag?.Tag);
        using var unknown = await review.Client.GetAsync(SunoExportApi.Export(Guid.CreateVersion7(), "/summary"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>
    /// A Reimport (a deleted record set to import) and a Don't copy are counted as such, and a record on the
    /// ignore list already counts as not copied while it is not imported; it alone leaves nothing to do.
    /// </summary>
    [Fact]
    public async Task ReimportsAndIgnoresAreCounted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Was here");
        var gone = Clip("gone", "free", At.AddDays(-1), 0);
        await ImportedVersions.AttachAsync(factory, song, "2", gone);
        await DeleteGenerationAsync(client, "n8-1-v2-g1");
        SunoExportApi.Ignore(factory, "kept");
        var (id, records) = await ExportAsync(
            client,
            token,
            gone,
            Clip("fresh", "free", At, 0, lyrics: "[Verse]\nFresh"),
            Clip("kept", "free", At.AddHours(1), 0, lyrics: "[Verse]\nKept"));
        Assert.Equal("deleted", Text(records["gone"], "class"));
        Assert.Equal("ignored", Text(records["kept"], "class"));
        await ChangedAsync(client, id, 1, ByFilter(new JsonObject { ["class"] = "new" }, new JsonObject { ["action"] = "skip" }));
        var untouched = await SummaryAsync(client, id);
        Assert.Equal((0, 0, 0, 0, 1, 2), Counts(untouched));
        Assert.True(untouched.GetProperty("nothingToDo").GetBoolean());

        await ChangedAsync(client, id, 2, Change(Import(new JsonObject { ["kind"] = "newSong", ["key"] = "new:5", ["title"] = "Back again" }), "gone"));
        await ChangedAsync(client, id, 3, Change(new JsonObject { ["action"] = "ignore" }, "fresh"));

        var summary = await SummaryAsync(client, id);
        Assert.Equal((1, 1, 1, 1, 2, 0), Counts(summary));
        Assert.False(summary.GetProperty("nothingToDo").GetBoolean());
    }

    /// <summary>
    /// Where a record may go in a Song: the Versions holding its inputs, and the numbers a new Version may
    /// take, top-level or under a parent, other records' new Versions counting as used (its own does not).
    /// </summary>
    [Fact]
    public async Task TargetsNameMatchingVersionsAndTheNumbersANewVersionMayTake()
    {
        await using var review = await ReviewAsync();

        var targets = await TargetsAsync(review.Client, review.ExportId, "a", "?song=n8-1");
        Assert.Equal("Target", Text(targets.GetProperty("song"), "title"));
        Assert.Equal(["n8-1-v2"], Shortcodes(targets, "matching"));
        Assert.Equal(["n8-1-v1", "n8-1-v2", "n8-1-v3"], Shortcodes(targets, "versions"));
        Assert.Equal(["4"], Numbers(targets));
        Assert.Equal("topLevel", Text(targets.GetProperty("numbers")[0], "kind"));
        Assert.Empty(Shortcodes(await TargetsAsync(review.Client, review.ExportId, "b", "?song=n8-1"), "matching"));

        targets = await TargetsAsync(review.Client, review.ExportId, "a", "?song=n8-1&parent=n8-1-v2");
        Assert.Equal(["2.1", "4"], Numbers(targets));
        Assert.True(targets.GetProperty("numbers")[0].GetProperty("proposed").GetBoolean());
        Assert.Equal("child", Text(targets.GetProperty("numbers")[0], "kind"));
        Assert.Equal("n8-1-v2", Text(targets.GetProperty("parent"), "shortcode"));

        await ChangedAsync(review.Client, review.ExportId, 1, Change(NewVersionOfTarget("new:9", "4"), "b"));
        Assert.Equal(["5"], Numbers(await TargetsAsync(review.Client, review.ExportId, "a", "?song=n8-1")));
        Assert.Equal(["4"], Numbers(await TargetsAsync(review.Client, review.ExportId, "b", "?song=n8-1")));

        var other = await SongApi.CreateAsync(review.Client, "Other");
        foreach (var (path, status) in new[]
        {
            ("/records/a/targets?song=n8-99", HttpStatusCode.NotFound),
            ("/records/nowhere/targets?song=n8-1", HttpStatusCode.NotFound),
            ($"/records/a/targets?song={Text(other, "shortcode")}&parent=n8-1-v2", HttpStatusCode.UnprocessableEntity),
            ("/records/a/targets", HttpStatusCode.BadRequest),
            ("/records/a/targets?song=n8-1&version=n8-1-v2", HttpStatusCode.BadRequest),
        })
        {
            using var response = await review.Client.GetAsync(SunoExportApi.Export(review.ExportId, path));
            Assert.True(status == response.StatusCode, path);
        }
    }

    /// <summary>The export waiting for review, or once none waits the last one and what became of it.</summary>
    [Fact]
    public async Task TheCurrentExportIsTheOneWaitingOrTheLastOne()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        var current = await CurrentAsync(client);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("waiting").ValueKind);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("last").ValueKind);

        var (id, _) = await ExportAsync(client, token, Clip("one", "free", At, 0));
        current = await CurrentAsync(client);
        Assert.Equal(id, current.GetProperty("waiting").GetProperty("id").GetGuid());
        Assert.Equal(id, current.GetProperty("last").GetProperty("id").GetGuid());

        // A receiving export (a sync under way) does not hide the one waiting.
        var receiving = await SunoExportApi.CreateAsync(client, token);
        Assert.Equal(id, (await CurrentAsync(client)).GetProperty("waiting").GetProperty("id").GetGuid());

        using (var discarded = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/discard"), null))
        {
            Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
        }

        using (var discarded = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(receiving, "/discard"), token))
        {
            Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
        }

        current = await CurrentAsync(client);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("waiting").ValueKind);
        Assert.Equal(receiving, current.GetProperty("last").GetProperty("id").GetGuid());
        Assert.Equal("discarded", Text(current.GetProperty("last"), "state"));
    }

    /// <summary>
    /// Suno's library filters are kept without the members naming the user or a workspace, and the export
    /// says which kinds of clip they left out; anything but an object is refused.
    /// </summary>
    [Fact]
    public async Task LibraryFiltersAreKeptWithoutIdentifiersAndNameWhatWasLeftOut()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        const string UserId = "00000000-0000-4000-8000-000000000103";

        var header = SunoExportApi.Header();
        // As the TS-003 library request sends them (feed-v3.library-page-1.request.json).
        header["libraryFilters"] = JsonNode.Parse(
            $$"""
            {
              "disliked": "False",
              "trashed": "False",
              "fromStudioProject": { "presence": "False" },
              "stem": { "presence": "False" },
              "stemComplement": "False",
              "user": { "presence": "True", "userId": "{{UserId}}" }
            }
            """);
        Assert.Contains(UserId, header.ToJsonString(), StringComparison.Ordinal);
        var id = await SunoExportApi.CreateAsync(client, token, header);

        var export = await SunoExportApi.GetAsync(client, token, id);
        Assert.Equal(
            ["disliked", "fromStudioProject", "stem", "stemComplement"],
            export.GetProperty("libraryExcluded").EnumerateArray().Select(static kind => kind.GetString()!).Order(StringComparer.Ordinal));
        var stored = TestDatabase.Scalar(factory.DataPath, $"SELECT library_filters_json FROM suno_exports WHERE upper(id) = '{id.ToString().ToUpperInvariant()}';");
        Assert.DoesNotContain(UserId, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("trashed", stored, StringComparison.Ordinal);

        Assert.Empty((await SunoExportApi.GetAsync(client, token, await SunoExportApi.CreateAsync(client, token))).GetProperty("libraryExcluded").EnumerateArray());

        header["libraryFilters"] = 5;
        using var refused = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Exports, token, header.ToJsonString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.True((await SetupApi.JsonAsync(refused)).GetProperty("errors").TryGetProperty("libraryFilters", out _));
    }

    /// <summary>The review's reads are the signed-in user's: a token, even one with every scope, is refused.</summary>
    [Fact]
    public async Task TheReviewReadsAreSessionOnly()
    {
        await using var review = await ReviewAsync();
        var token = await CredentialApi.CreateTokenAsync(review.Factory, [.. n8Tracks.Application.Credentials.CredentialScopes.All]);

        foreach (var uri in new[]
        {
            Current,
            SunoExportApi.Export(review.ExportId, "/summary"),
            SunoExportApi.Export(review.ExportId, "/records/a/targets?song=n8-1"),
        })
        {
            using var response = await CredentialApi.SendAsync(review.Client, HttpMethod.Get, uri, token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("session_required", (await SetupApi.JsonAsync(response)).GetProperty("code").GetString());
        }
    }

    private static (int Songs, int Versions, int Generations, int Reimports, int Ignored, int Skipped) Counts(JsonElement summary) =>
        (summary.GetProperty("songs").GetInt32(),
            summary.GetProperty("versions").GetInt32(),
            summary.GetProperty("generations").GetInt32(),
            summary.GetProperty("reimports").GetInt32(),
            summary.GetProperty("ignored").GetInt32(),
            summary.GetProperty("skipped").GetInt32());

    private static string[] Reasons(JsonElement summary, string sunoId) =>
        [.. summary.GetProperty("invalid").GetProperty(sunoId).EnumerateArray().Select(static reason => reason.GetString()!)];

    private static JsonObject NewVersionOfTarget(string key, string number) =>
        Import(new JsonObject { ["kind"] = "newVersion", ["key"] = key, ["song"] = "n8-1", ["parentVersion"] = null, ["number"] = number });

    /// <summary>Edits the lyrics of the mutable Version <paramref name="shortcode"/> as the editor does.</summary>
    private static async Task EditLyricsAsync(HttpClient client, string shortcode, string lyrics)
    {
        var uri = new Uri("/api/v1/versions/" + shortcode, UriKind.Relative);
        var version = await SetupApi.JsonAsync(await client.GetAsync(uri));
        using var request = new HttpRequestMessage(HttpMethod.Patch, uri)
        {
            Content = new StringContent(new JsonObject { ["lyrics"] = lyrics }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", $"\"{version.GetProperty("revision").GetInt32()}\""));
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A ready export of three records in the workspace <c>free</c>: <c>a</c> ("Morning Light", the template
    /// inputs), <c>b</c> ("Evening Rain", other lyrics, a minute later), and <c>linked</c> ("Held", the
    /// Generation of Version 3). The Song <c>n8-1</c> "Target" has Version 1 (its own inputs), Version 2
    /// (mutable, the template inputs), and Version 3 (frozen). Each new record is proposed a new Song.
    /// </summary>
    private static async Task<Review> ReviewAsync()
    {
        var factory = SongApi.Host();
        var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Target");
        await ImportedVersions.AddAsync(factory, song.GetProperty("id").GetGuid(), "2", Clip("template", "free", At, 0));
        var linked = Clip("linked", "free", At.AddDays(-1), 0, lyrics: "[Verse]\nHeld", title: "Held");
        await ImportedVersions.AttachAsync(factory, song, "3", linked);

        var (id, _) = await ExportAsync(
            client,
            token,
            Clip("a", "free", At, 0, title: "Morning Light"),
            Clip("b", "free", At.AddMinutes(1), 0, lyrics: "[Verse]\nOther", title: "Evening Rain"),
            linked);
        return new Review(factory, client, id);
    }

    private sealed record Review(N8TracksApiFactory Factory, HttpClient Client, Guid ExportId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
