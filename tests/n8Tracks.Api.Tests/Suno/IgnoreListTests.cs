using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The ignore list (#143): a Don't copy choice puts the record on it at the commit; later syncs class it
/// <c>ignored</c> and default it to Don't copy; importing it takes it off. The list is read and items
/// are removed through <c>GET /api/v1/suno/ignored</c> and <c>POST /api/v1/suno/ignored/remove</c>,
/// session-only. Removing imports nothing; Suno's changes, its Trash included, never remove an item;
/// a deleted clip is never listed. The invariant 3 guard (<c>ImportNeverOverwritesGuardTests</c>) holds
/// the list to the confirmed choices.
/// </summary>
public sealed class IgnoreListTests
{
    private static readonly Uri Ignored = new("/api/v1/suno/ignored", UriKind.Relative);
    private static readonly Uri Remove = new("/api/v1/suno/ignored/remove", UriKind.Relative);

    [Fact]
    public async Task ConfirmingDontCopyPutsEachRecordOnTheListWithItsTitleWorkspaceAndDate()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("studio", "Studio"));
        var at = ProposalApi.At;
        var (id, _) = await ProposalApi.ExportAsync(
            client,
            token,
            ProposalApi.Clip("no-1", "studio", at, 0, "First words", "Not this one"),
            ProposalApi.Clip("no-2", null, at.AddHours(1), 0, "Second words", "Nor this"),
            ProposalApi.Clip("kept", null, at.AddHours(2), 0, "Kept words", "Kept"));
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(new JsonObject { ["action"] = "ignore" }, "no-1", "no-2"));
        await ProposalApi.ChangedAsync(client, id, 2, ProposalApi.Change(new JsonObject { ["action"] = "skip" }, "kept"));
        Assert.Empty((await ListAsync(client)).GetProperty("items").EnumerateArray());

        var before = DateTime.UtcNow.AddSeconds(-1);
        var result = await ImportCommitApi.CommitAsync(client, id);

        var outcomes = ImportCommitApi.Records(result);
        Assert.Equal(["ignored", "ignored", "skipped"], new[] { "no-1", "no-2", "kept" }.Select(sunoId => ImportCommitApi.Outcome(outcomes[sunoId])));
        Assert.Equal(0, ImportCommitApi.GenerationCount(factory, "no-1") + ImportCommitApi.GenerationCount(factory, "no-2"));

        var list = await ListAsync(client);
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var items = Items(list);
        Assert.Equal(["no-1", "no-2"], items.Keys.Order(StringComparer.Ordinal));
        var first = items["no-1"];
        Assert.Equal(("Not this one", "studio", "Studio"), (Text(first, "title"), Text(first, "workspaceId"), Text(first, "workspaceName")));
        Assert.Equal(("present", "2026-10-06T12:00:00Z"), (Text(first, "status"), first.GetProperty("lastSeenAt").GetDateTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        Assert.InRange(first.GetProperty("ignoredAt").GetDateTime(), before, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(JsonValueKind.Null, items["no-2"].GetProperty("workspaceId").ValueKind);
        var workspace = Assert.Single(list.GetProperty("workspaces").EnumerateArray());
        Assert.Equal(("studio", "Studio", 1), (Text(workspace, "id"), Text(workspace, "name"), workspace.GetProperty("count").GetInt32()));
    }

    /// <summary>
    /// The next sync lists an ignored record as Ignored, proposed Skip (shown as Don't copy) on the basis
    /// of the list; choosing Import imports it and takes it off the list, in the same transaction.
    /// </summary>
    [Fact]
    public async Task ALaterSyncListsItIgnoredAndImportingItTakesItOffTheList()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var clip = ProposalApi.Clip("again-1", null, ProposalApi.At, 0, "Again words", "Again");
        await IgnoreThroughACommitAsync(client, token, clip);

        var (id, records) = await ProposalApi.ExportAsync(client, token, clip);
        Assert.Equal("ignored", Text(records["again-1"], "class"));
        Assert.Equal("skip", Text(ProposalApi.Proposed(records["again-1"]), "action"));
        Assert.Equal("ignored", Text(records["again-1"].GetProperty("proposal"), "basis"));

        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(ProposalApi.Import(new JsonObject { ["kind"] = "newSong", ["key"] = "new:1", ["title"] = "Wanted after all" }), "again-1"));
        var result = await ImportCommitApi.CommitAsync(client, id);

        Assert.Equal("created", ImportCommitApi.Outcome(ImportCommitApi.Records(result)["again-1"]));
        Assert.Equal(1, ImportCommitApi.GenerationCount(factory, "again-1"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_ignored_items;"));
    }

    /// <summary>Skip on an ignored record keeps it on the list, as Don't copy does; adding it again changes nothing (idempotent).</summary>
    [Fact]
    public async Task SkipOrDontCopyAgainKeepsTheEntryAsItWas()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var first = ProposalApi.Clip("stay-1", null, ProposalApi.At, 0, "Stay words", "Stay");
        var second = ProposalApi.Clip("stay-2", null, ProposalApi.At.AddHours(1), 0, "Other words", "Stay too");
        await IgnoreThroughACommitAsync(client, token, first, second);
        var ignoredAt = TestDatabase.Rows(factory.DataPath, "SELECT ignored_utc FROM suno_ignored_items ORDER BY suno_id;");

        var (id, _) = await ProposalApi.ExportAsync(client, token, first, second);
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(new JsonObject { ["action"] = "ignore" }, "stay-2"));
        var result = await ImportCommitApi.CommitAsync(client, id);

        var outcomes = ImportCommitApi.Records(result);
        Assert.Equal(("skipped", "ignored"), (ImportCommitApi.Outcome(outcomes["stay-1"]), ImportCommitApi.Outcome(outcomes["stay-2"])));
        Assert.Equal(ignoredAt, TestDatabase.Rows(factory.DataPath, "SELECT ignored_utc FROM suno_ignored_items ORDER BY suno_id;"));
    }

    [Fact]
    public async Task TheListIsPagedAtFiftyNewestFirstAndSearchedAndFiltered()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var number = 1; number <= 55; number++)
        {
            Insert(factory, $"clip-{number:D2}", $"Song number {number}", number % 2 == 0 ? "even" : "odd", start.AddMinutes(number), number <= 3 ? "trashed" : "present");
        }

        Insert(factory, "abc-special", "Ünïcode Morning Light", null, start.AddDays(10), "not_seen");

        var page = await ListAsync(client);
        Assert.Equal((56, 50, 1), (page.GetProperty("total").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("page").GetInt32()));
        var ids = page.GetProperty("items").EnumerateArray().Select(static item => Text(item, "sunoId")).ToList();
        Assert.Equal(50, ids.Count);
        Assert.Equal(["abc-special", "clip-55", "clip-54"], ids.Take(3));
        var last = (await ListAsync(client, "?page=2")).GetProperty("items").EnumerateArray().Select(static item => Text(item, "sunoId")).ToList();
        Assert.Equal(["clip-06", "clip-05", "clip-04", "clip-03", "clip-02", "clip-01"], last);

        // A case-insensitive substring of the title, or the start of the Suno ID.
        Assert.Equal(["abc-special"], Ids(await ListAsync(client, "?q=MORNING")));
        Assert.Equal(["abc-special"], Ids(await ListAsync(client, "?q=ABC")));
        Assert.Empty(Ids(await ListAsync(client, "?q=special")));
        Assert.Equal(["clip-15"], Ids(await ListAsync(client, "?q=number%2015")));
        Assert.Equal(["clip-51"], Ids(await ListAsync(client, "?q=CLIP-51")));

        // Filters by workspace and status, combined with each other and the search.
        var odd = await ListAsync(client, "?workspace=odd");
        Assert.Equal(28, odd.GetProperty("total").GetInt32());
        Assert.Equal(["clip-03", "clip-01"], Ids(await ListAsync(client, "?workspace=odd&status=trashed")));
        Assert.Equal(["abc-special"], Ids(await ListAsync(client, "?status=not_seen")));
        Assert.Empty(Ids(await ListAsync(client, "?status=missing")));
        Assert.Equal(["clip-02"], Ids(await ListAsync(client, "?workspace=even&status=trashed&q=number")));

        // The workspaces of the whole list, whatever the filters.
        var workspaces = odd.GetProperty("workspaces").EnumerateArray().Select(static workspace => (Text(workspace, "id"), workspace.GetProperty("count").GetInt32())).ToList();
        Assert.Equal([("even", 27), ("odd", 28)], workspaces);

        foreach (var bad in new[] { "?status=gone", "?page=0", "?page=x", "?sort=title", "?q=a&q=b" })
        {
            using var response = await client.GetAsync(new Uri(Ignored + bad, UriKind.Relative));
            await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }
    }

    /// <summary>Removal takes the items named off the list, skips the rest with a count, and imports nothing: the next sync lists the clip as New.</summary>
    [Fact]
    public async Task RemovingAnItemOnlyMakesItEligibleAgain()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var back = ProposalApi.Clip("back-1", null, ProposalApi.At, 0, "Back words", "Back");
        var still = ProposalApi.Clip("still-1", null, ProposalApi.At.AddHours(1), 0, "Still words", "Still");
        await IgnoreThroughACommitAsync(client, token, back, still);
        var generations = TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;");

        var removal = await RemoveAsync(client, "back-1", "never-ignored", "back-1");

        Assert.Equal((1, 1), (removal.GetProperty("removed").GetInt32(), removal.GetProperty("unknown").GetInt32()));
        Assert.Equal(["still-1"], Ids(await ListAsync(client)));
        Assert.Equal(generations, TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;"));
        Assert.Equal(0, ImportCommitApi.GenerationCount(factory, "back-1"));

        var (_, records) = await ProposalApi.ExportAsync(client, token, back, still);
        Assert.Equal(("new", "ignored"), (Text(records["back-1"], "class"), Text(records["still-1"], "class")));
    }

    /// <summary>Removing an item while an export waits for review lists that export's record as New at once.</summary>
    [Fact]
    public async Task RemovingAnItemReclassifiesTheExportWaitingForReview()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var clip = ProposalApi.Clip("waiting-1", null, ProposalApi.At, 0, "Waiting words", "Waiting");
        await IgnoreThroughACommitAsync(client, token, clip);
        var (id, records) = await ProposalApi.ExportAsync(client, token, clip);
        Assert.Equal("ignored", Text(records["waiting-1"], "class"));

        await RemoveAsync(client, "waiting-1");

        var record = (await SunoExportApi.RecordsByIdAsync(client, id))["waiting-1"];
        Assert.Equal("new", Text(record, "class"));
        Assert.Equal("skip", Text(record.GetProperty("choice"), "action"));
        Assert.Equal(1, SunoExportApi.Count(await SunoExportApi.GetAsync(client, null, id), "new"));
    }

    [Fact]
    public async Task ARemovalThatIsNotAListOfSunoIdsOrTooLongRemovesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        Insert(factory, "kept-1", "Kept", null, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "present");

        foreach (var body in new[] { "{}", """{"sunoIds":[]}""", """{"sunoIds":"kept-1"}""", """{"sunoIds":["kept-1",3]}""", """{"sunoIds":[""]}""" })
        {
            using var response = await SunoExportApi.SendAsync(client, HttpMethod.Post, Remove, null, body);
            await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        var tooMany = new JsonObject { ["sunoIds"] = new JsonArray([.. Enumerable.Range(0, 1001).Select(static number => (JsonNode)$"id-{number}")]) };
        using (var response = await SunoExportApi.SendAsync(client, HttpMethod.Post, Remove, null, tooMany.ToJsonString()))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "too_many_items");
            Assert.Equal((1000, 1001), (problem.GetProperty("limit").GetInt32(), problem.GetProperty("count").GetInt32()));
        }

        Assert.Equal(["kept-1"], Ids(await ListAsync(client)));
    }

    [Fact]
    public async Task ABearerTokenCannotReadOrChangeTheList()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        Insert(factory, "kept-1", "Kept", null, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "present");

        using (var listed = await SunoExportApi.SendAsync(client, HttpMethod.Get, Ignored, token))
        {
            await SetupApi.ProblemAsync(listed, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        using (var removed = await SunoExportApi.SendAsync(client, HttpMethod.Post, Remove, token, """{"sunoIds":["kept-1"]}"""))
        {
            await SetupApi.ProblemAsync(removed, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        Assert.Equal(["kept-1"], Ids(await ListAsync(client)));
    }

    /// <summary>
    /// A change to the clip's data in Suno, or a trip to the Trash and back, never removes it: it stays
    /// ignored, and its title and status here change only when a sync is confirmed.
    /// </summary>
    [Fact]
    public async Task SunoChangesNeverRemoveAnItemAndAConfirmedSyncRefreshesIt()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var clip = ProposalApi.Clip("moving-1", null, ProposalApi.At, 0, "Moving words", "Old title");
        await IgnoreThroughACommitAsync(client, token, clip);

        // Retitled and moved to Suno's Trash: still ignored; nothing changes here until the commit.
        var retitled = ProposalApi.Clip("moving-1", "studio", ProposalApi.At, 0, "Changed words", "New title");
        var (trashedId, export) = await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, trashed: [retitled]));
        Assert.Equal("ready", export.GetProperty("state").GetString());
        Assert.Equal("ignored", Text((await SunoExportApi.RecordsByIdAsync(client, trashedId))["moving-1"], "class"));
        var item = Items(await ListAsync(client))["moving-1"];
        Assert.Equal(("Old title", "present"), (Text(item, "title"), Text(item, "status")));

        await ImportCommitApi.CommitAsync(client, trashedId);
        item = Items(await ListAsync(client))["moving-1"];
        Assert.Equal(("New title", "studio", "trashed"), (Text(item, "title"), Text(item, "workspaceId"), Text(item, "status")));

        // Restored from the Trash: still ignored, present again.
        var (restoredId, records) = await ProposalApi.ExportAsync(client, token, retitled);
        Assert.Equal("ignored", Text(records["moving-1"], "class"));
        await ImportCommitApi.CommitAsync(client, restoredId);
        Assert.Equal("present", Text(Items(await ListAsync(client))["moving-1"], "status"));
    }

    /// <summary>
    /// A whole-library sync without the item marks it missing when the Trash was read to the end too, and
    /// not seen otherwise, keeping when it was last seen; a sync of some workspaces says nothing about it.
    /// </summary>
    [Fact]
    public async Task OnlyAWholeLibrarySyncMarksAnItemMissingOrNotSeen()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await IgnoreThroughACommitAsync(client, token, ProposalApi.Clip("absent-1", null, ProposalApi.At, 0, "Absent words", "Absent"));
        var other = ProposalApi.Clip("other-1", null, ProposalApi.At.AddHours(1), 0, "Other words", "Other");

        async Task<string?> StatusAfterAsync(JsonObject header)
        {
            var (id, _) = await SunoExportApi.UploadAsync(client, token, header, SunoExportApi.Part(1, [other]));
            await ImportCommitApi.CommitAsync(client, id);
            var item = Items(await ListAsync(client))["absent-1"];
            Assert.Equal("2026-10-06T12:00:00Z", item.GetProperty("lastSeenAt").GetDateTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            return Text(item, "status");
        }

        Assert.Equal("present", await StatusAfterAsync(SunoExportApi.Header(scope: "workspaces")));
        Assert.Equal("present", await StatusAfterAsync(SunoExportApi.Header(libraryComplete: false)));
        Assert.Equal("not_seen", await StatusAfterAsync(SunoExportApi.Header(trashedComplete: false)));
        Assert.Equal("missing", await StatusAfterAsync(SunoExportApi.Header()));
    }

    /// <summary>
    /// An ignored item is never a deleted one: Don't copy is refused for a clip deleted in n8Tracks, and
    /// a tombstoned Suno ID is never listed even if an entry for it is stored.
    /// </summary>
    [Fact]
    public async Task ADeletedClipIsNeverIgnoredNorListed()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SongApi.CreateAsync(client, "Deleted one");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("gone-1"));
        await ProposalApi.DeleteGenerationAsync(client, "n8-1-v1-g1");

        var (id, records) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("gone-1", null, ProposalApi.At, 0, "Gone words", "Gone"));
        Assert.Equal("deleted", Text(records["gone-1"], "class"));
        var refused = await ProposalApi.RefusedAsync(client, id, 1, ProposalApi.Change(new JsonObject { ["action"] = "ignore" }, "gone-1"));
        Assert.Equal(["tombstoned"], refused["gone-1"]);

        Insert(factory, "gone-1", "Gone", null, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "present");
        Insert(factory, "listed-1", "Listed", null, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), "present");
        var list = await ListAsync(client);
        Assert.Equal(["listed-1"], Ids(list));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Empty(Ids(await ListAsync(client, "?q=gone")));
    }

    [Theory]
    [InlineData(null, true, false, false, false, SunoIgnoredStatus.Present)]
    [InlineData(SunoIgnoredStatus.Missing, true, true, true, true, SunoIgnoredStatus.Trashed)]
    [InlineData(SunoIgnoredStatus.Trashed, false, false, true, true, SunoIgnoredStatus.Missing)]
    [InlineData(SunoIgnoredStatus.Present, false, false, true, false, SunoIgnoredStatus.NotSeen)]
    [InlineData(SunoIgnoredStatus.Trashed, false, false, false, true, SunoIgnoredStatus.Trashed)]
    [InlineData(null, false, false, false, false, null)]
    public void TheStatusFollowsTheSyncInTrashPresentMissingNotSeenOrder(SunoIgnoredStatus? current, bool included, bool trashed, bool wholeLibrary, bool trashComplete, SunoIgnoredStatus? expected) =>
        Assert.Equal(expected, SunoIgnoreListRules.StatusAfter(current, included, trashed, wholeLibrary, trashComplete));

    [Fact]
    public void OnlyALibraryScopeReadToItsEndIsAWholeLibrarySync()
    {
        static SunoExportHeader Header(string scope, bool complete) =>
            new("0.1.0", "1", DateTimeOffset.UnixEpoch, scope, [], complete, true, false, "[]", "[]");

        Assert.True(SunoIgnoreListRules.IsWholeLibrary(Header("library", true)));
        Assert.False(SunoIgnoreListRules.IsWholeLibrary(Header("library", false)));
        Assert.False(SunoIgnoreListRules.IsWholeLibrary(Header("workspaces", true)));
        Assert.False(SunoIgnoreListRules.IsWholeLibrary(Header("playlists", true)));
    }

    /// <summary>Uploads <paramref name="clips"/>, sets them all to Don't copy, and commits.</summary>
    internal static async Task IgnoreThroughACommitAsync(HttpClient client, string token, params JsonNode[] clips)
    {
        var (id, _) = await ProposalApi.ExportAsync(client, token, clips);
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(new JsonObject { ["action"] = "ignore" }, [.. clips.Select(SunoExportApi.IdOf)]));
        var result = await ImportCommitApi.CommitAsync(client, id);
        Assert.All(ImportCommitApi.Records(result).Values, static record => Assert.Equal("ignored", ImportCommitApi.Outcome(record)));
    }

    /// <summary>A page of the list as the signed-in user, asserting 200.</summary>
    internal static async Task<JsonElement> ListAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync(new Uri(Ignored + query, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Removes <paramref name="sunoIds"/> as the signed-in user, asserting 200, and returns the counts.</summary>
    internal static async Task<JsonElement> RemoveAsync(HttpClient client, params string[] sunoIds)
    {
        var json = new JsonObject { ["sunoIds"] = new JsonArray([.. sunoIds.Select(static id => (JsonNode)id)]) }.ToJsonString();
        using var response = await SunoExportApi.SendAsync(client, HttpMethod.Post, Remove, null, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Stores an entry directly, as a commit would have.</summary>
    private static void Insert(N8TracksApiFactory factory, string sunoId, string title, string? workspace, DateTime ignored, string status) =>
        TestDatabase.Execute(
            factory.DataPath,
            string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT INTO suno_ignored_items (suno_id, title, workspace_id, ignored_utc, last_status, last_seen_utc) VALUES ('{sunoId}', '{title}', {(workspace is null ? "NULL" : $"'{workspace}'")}, '{ignored:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}', '{status}', '{ignored:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}');"));

    private static Dictionary<string, JsonElement> Items(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().ToDictionary(static item => Text(item, "sunoId")!, StringComparer.Ordinal);

    private static List<string?> Ids(JsonElement list) => [.. list.GetProperty("items").EnumerateArray().Select(static item => Text(item, "sunoId"))];

    private static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();
}
