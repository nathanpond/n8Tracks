using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using static n8Tracks.Api.Tests.Suno.RemoteStateApi;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Following Suno's Trash, restores, and missing clips (#142). A sync review lists each linked clip whose
/// remote state would change ("Suno state changes"); every row starts applied and can be set to Skip; at
/// Confirm the rows are worked out again and applied. A clip in Suno's Trash archives its Generation (by
/// sync), a restore reactivates only what sync archived, and a clip a whole-library sync found nowhere is
/// marked missing, nothing else changing. Nothing is deleted, nothing enters retention, and no Selected
/// Generation or workflow state changes.
/// </summary>
public sealed class RemoteStateTests
{
    private static readonly JsonNode Alpha = Clip("clip-a", "Alpha");
    private static readonly JsonNode Beta = Clip("clip-b", "Beta");

    [Fact]
    public async Task ATrashedClipIsArchivedAMissingOneMarkedAndBothComeBackWhenListedAgain()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);

        // A whole-library sync: Alpha in the Trash, Beta in neither list.
        var first = await ExportAsync(client, token, [], [Alpha]);
        var list = await ListAsync(client, first);
        Assert.True(list.GetProperty("missingChecked").GetBoolean());
        Assert.Equal((1, 0, 1, 2, 0), Counts(list));
        var rows = await RowsAsync(client, first);
        Assert.Equal(["clip-a", "clip-b"], rows.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("trashed", true, true), Row(rows["clip-a"]));
        Assert.Equal(("present", "trashed", "active", "archived"), Moves(rows["clip-a"]));
        Assert.Equal("n8-1-v1-g1", rows["clip-a"].GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal("n8-1", rows["clip-a"].GetProperty("generation").GetProperty("songShortcode").GetString());
        Assert.Equal(("missing", false, true), Row(rows["clip-b"]));
        Assert.Equal(("present", "missing", "active", "active"), Moves(rows["clip-b"]));

        // The review lists them and confirming has something to do.
        var summary = await ImportReviewApi.SummaryAsync(client, first);
        Assert.Equal(2, summary.GetProperty("remoteChanges").GetInt32());
        Assert.False(summary.GetProperty("nothingToDo").GetBoolean());

        var result = Results(await ImportCommitApi.CommitAsync(client, first));
        Assert.Equal(("trashed", "applied"), Outcome(result["clip-a"]));
        Assert.Equal(("missing", "applied"), Outcome(result["clip-b"]));
        Assert.Equal(("archived", "trashed", "sync"), await StatesAsync(client, "n8-1-v1-g1"));
        Assert.Equal(("active", "missing", null), await StatesAsync(client, "n8-1-v1-g2"));

        // A later sync finds both in the library: both are present, and Alpha is active again.
        var second = await ExportAsync(client, token, [Alpha, Beta], []);
        rows = await RowsAsync(client, second);
        Assert.Equal(("restored", false, true), Row(rows["clip-a"]));
        Assert.True(rows["clip-a"].GetProperty("reactivates").GetBoolean());
        Assert.Equal(("trashed", "present", "archived", "active"), Moves(rows["clip-a"]));
        Assert.Equal(("restored", false, true), Row(rows["clip-b"]));
        Assert.False(rows["clip-b"].GetProperty("reactivates").GetBoolean());
        await ImportCommitApi.CommitAsync(client, second);
        Assert.Equal(("active", "present", null), await StatesAsync(client, "n8-1-v1-g1"));
        Assert.Equal(("active", "present", null), await StatesAsync(client, "n8-1-v1-g2"));

        // Nothing went anywhere: both Generations are live, and nothing entered retention.
        Assert.Equal(2, ImportCommitApi.GenerationCount(factory, "clip-a") + ImportCommitApi.GenerationCount(factory, "clip-b"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
        client.Dispose();
    }

    [Fact]
    public async Task AMissingClipFoundInTheTrashGoesStraightToTrashedAndASyncArchiveSurvivesGoingMissing()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);

        // Beta goes missing, then turns up in the Trash: missing to trashed directly, and archived by sync.
        await ImportCommitApi.CommitAsync(client, await ExportAsync(client, token, [Alpha], []));
        Assert.Equal(("active", "missing", null), await StatesAsync(client, "n8-1-v1-g2"));
        var trashed = await ExportAsync(client, token, [Alpha], [Beta]);
        Assert.Equal(("missing", "trashed", "active", "archived"), Moves((await RowsAsync(client, trashed))["clip-b"]));
        await ImportCommitApi.CommitAsync(client, trashed);
        Assert.Equal(("archived", "trashed", "sync"), await StatesAsync(client, "n8-1-v1-g2"));

        // Missing again, it keeps sync's archive; listed again, it is reactivated.
        await ImportCommitApi.CommitAsync(client, await ExportAsync(client, token, [Alpha], []));
        Assert.Equal(("archived", "missing", "sync"), await StatesAsync(client, "n8-1-v1-g2"));
        await ImportCommitApi.CommitAsync(client, await ExportAsync(client, token, [Alpha, Beta], []));
        Assert.Equal(("active", "present", null), await StatesAsync(client, "n8-1-v1-g2"));
        client.Dispose();
    }

    [Theory]
    [InlineData("workspaces", true, true)]
    [InlineData("playlists", true, true)]
    [InlineData("library", false, true)]
    [InlineData("library", true, false)]
    public async Task AScopedOrIncompleteSyncNeverMarksAnythingMissing(string scope, bool libraryComplete, bool trashedComplete)
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);

        // Neither clip is in it: still, nothing is missing. A trashed one is still followed.
        var export = await ExportAsync(client, token, [], [Alpha], Header(scope, libraryComplete, trashedComplete));
        var list = await ListAsync(client, export);
        Assert.False(list.GetProperty("missingChecked").GetBoolean());
        Assert.Equal(["clip-a"], (await RowsAsync(client, export)).Keys);

        await ImportCommitApi.CommitAsync(client, export);
        Assert.Equal(("archived", "trashed", "sync"), await StatesAsync(client, "n8-1-v1-g1"));
        Assert.Equal(("active", "present", null), await StatesAsync(client, "n8-1-v1-g2"));
        client.Dispose();
    }

    [Fact]
    public async Task ARowSetToSkipLeavesItsGenerationUnchanged()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);
        var before = await GenerationAsync(client, "n8-1-v1-g1");

        var export = await ExportAsync(client, token, [], [Alpha, Beta]);
        await SetAsync(client, export, false, "clip-a");
        var rows = await RowsAsync(client, export);
        Assert.False(rows["clip-a"].GetProperty("apply").GetBoolean());
        Assert.True(rows["clip-b"].GetProperty("apply").GetBoolean());
        Assert.Equal((2, 0, 0, 1, 1), Counts(await ListAsync(client, export)));
        var summary = await ImportReviewApi.SummaryAsync(client, export);
        Assert.Equal((1, 2), (summary.GetProperty("remoteChanges").GetInt32(), summary.GetProperty("remoteChangesTotal").GetInt32()));

        // Set back to apply and to Skip again: the last choice stands.
        await SetAsync(client, export, true, "clip-a");
        Assert.True((await RowsAsync(client, export))["clip-a"].GetProperty("apply").GetBoolean());
        await SetAsync(client, export, false, "clip-a");

        var result = Results(await ImportCommitApi.CommitAsync(client, export));
        Assert.Equal(("trashed", "skipped"), Outcome(result["clip-a"]));
        Assert.Equal(("trashed", "applied"), Outcome(result["clip-b"]));
        var after = await GenerationAsync(client, "n8-1-v1-g1");
        Assert.Equal(before.GetProperty("revision").GetInt32(), after.GetProperty("revision").GetInt32());
        Assert.Equal(("active", "present", null), await StatesAsync(client, "n8-1-v1-g1"));
        Assert.Equal(("archived", "trashed", "sync"), await StatesAsync(client, "n8-1-v1-g2"));
        client.Dispose();
    }

    [Fact]
    public async Task AUsersOwnArchiveIsNeverUndoneByARestore()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);

        // Alpha archived by hand before it was trashed: the trash changes only its remote state.
        await SetStateAsync(client, "n8-1-v1-g1", "archived");
        Assert.Equal(("archived", "present", "user"), await StatesAsync(client, "n8-1-v1-g1"));
        var trashed = await ExportAsync(client, token, [Beta], [Alpha]);
        var row = (await RowsAsync(client, trashed))["clip-a"];
        Assert.Equal(("present", "trashed", "archived", "archived"), Moves(row));
        Assert.False(row.GetProperty("archives").GetBoolean());
        await ImportCommitApi.CommitAsync(client, trashed);
        Assert.Equal(("archived", "trashed", "user"), await StatesAsync(client, "n8-1-v1-g1"));

        // Restored in Suno: listed as restored, but it stays archived.
        var restored = await ExportAsync(client, token, [Alpha, Beta], []);
        row = (await RowsAsync(client, restored))["clip-a"];
        Assert.Equal(("restored", false, true), Row(row));
        Assert.Equal(("trashed", "present", "archived", "archived"), Moves(row));
        await ImportCommitApi.CommitAsync(client, restored);
        Assert.Equal(("archived", "present", "user"), await StatesAsync(client, "n8-1-v1-g1"));
        client.Dispose();
    }

    [Fact]
    public async Task ArchivingBySyncArchivedGenerationByHandMakesTheArchiveTheUsers()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);
        await ImportCommitApi.CommitAsync(client, await ExportAsync(client, token, [Beta], [Alpha]));
        Assert.Equal(("archived", "trashed", "sync"), await StatesAsync(client, "n8-1-v1-g1"));

        // The user archives it again by hand: the archive is now the user's own, so a restore keeps it.
        await SetStateAsync(client, "n8-1-v1-g1", "archived");
        Assert.Equal(("archived", "trashed", "user"), await StatesAsync(client, "n8-1-v1-g1"));
        await ImportCommitApi.CommitAsync(client, await ExportAsync(client, token, [Alpha, Beta], []));
        Assert.Equal(("archived", "present", "user"), await StatesAsync(client, "n8-1-v1-g1"));

        // Reactivated by hand, it has no archiver.
        await SetStateAsync(client, "n8-1-v1-g1", "active");
        Assert.Equal(("active", "present", null), await StatesAsync(client, "n8-1-v1-g1"));
        client.Dispose();
    }

    [Fact]
    public async Task ASelectedGenerationArchivedBySyncStaysSelectedAndTheSongsStateIsUnchanged()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);
        var revision = (await SongAsync(client)).GetProperty("revision").GetInt32();
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri("/api/v1/songs/n8-1/selected-generation", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { generation = "n8-1-v1-g1" }), System.Text.Encoding.UTF8, "application/json"),
        })
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
            using var selected = await client.SendAsync(request);
            Assert.True(selected.StatusCode == HttpStatusCode.OK, await selected.Content.ReadAsStringAsync());
        }

        var before = await SongAsync(client);
        await ImportCommitApi.CommitAsync(client, await ExportAsync(client, token, [Beta], [Alpha]));

        var after = await SongAsync(client);
        Assert.Equal("n8-1-v1-g1", after.GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
        Assert.Equal("archived", after.GetProperty("selectedGeneration").GetProperty("state").GetString());
        Assert.Equal("trashed", after.GetProperty("selectedGeneration").GetProperty("remoteState").GetString());
        Assert.Equal(before.GetProperty("state").ToString(), after.GetProperty("state").ToString());
        Assert.Equal(before.GetProperty("revision").GetInt32(), after.GetProperty("revision").GetInt32());
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM provider_tombstones;"));
        client.Dispose();
    }

    [Fact]
    public async Task AnIgnoredClipHasNoRowAndStaysIgnoredWhetherTrashedOrRestored()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);
        TestDatabase.Execute(factory.DataPath, "INSERT INTO suno_ignored_items (suno_id, title, workspace_id, ignored_utc) VALUES ('clip-i', 'Ignored', NULL, '2026-10-01T00:00:00.000Z');");
        var ignored = Clip("clip-i", "Ignored");

        foreach (var (listed, trashed) in new[] { (new[] { Alpha, Beta }, new[] { ignored }), (new[] { Alpha, Beta, ignored }, Array.Empty<JsonNode>()) })
        {
            var export = await ExportAsync(client, token, listed, trashed);
            Assert.Equal("ignored", (await SunoExportApi.RecordsByIdAsync(client, export))["clip-i"].GetProperty("class").GetString());
            Assert.Empty(await RowsAsync(client, export));
            Assert.True((await ImportReviewApi.SummaryAsync(client, export)).GetProperty("nothingToDo").GetBoolean());
            await ImportCommitApi.CommitAsync(client, export);
            Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_ignored_items WHERE suno_id = 'clip-i';"));
            Assert.Equal(0, ImportCommitApi.GenerationCount(factory, "clip-i"));
        }

        client.Dispose();
    }

    [Fact]
    public async Task RowsAreWorkedOutAgainAtConfirmAndAGenerationDeletedMeanwhileIsLeftOut()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);
        var export = await ExportAsync(client, token, [], [Alpha, Beta]);
        Assert.Equal(2, (await RowsAsync(client, export)).Count);

        // Beta's Generation is deleted, and Alpha archived by hand, after the review was opened.
        await ProposalApi.DeleteGenerationAsync(client, "n8-1-v1-g2");
        await SetStateAsync(client, "n8-1-v1-g1", "archived");

        var result = Results(await ImportCommitApi.CommitAsync(client, export));
        Assert.Equal(["clip-a"], result.Keys);
        Assert.Equal(("archived", "trashed", "user"), await StatesAsync(client, "n8-1-v1-g1"));
        Assert.Equal(0, ImportCommitApi.GenerationCount(factory, "clip-b"));
        client.Dispose();
    }

    [Fact]
    public async Task AGenerationAttachedAfterTheSyncReadTheLibraryIsNotMissing()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);
        var header = Header();
        header["capturedAt"] = "2026-01-01T00:00:00.000Z";

        var export = await ExportAsync(client, token, [], [], header);
        Assert.True((await ListAsync(client, export)).GetProperty("missingChecked").GetBoolean());
        Assert.Empty(await RowsAsync(client, export));
        client.Dispose();
    }

    [Fact]
    public async Task TheListFiltersByTitleAndPages()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);
        var export = await ExportAsync(client, token, [], []);

        var page = await ListAsync(client, export, "?q=ALP");
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal("clip-a", page.GetProperty("items")[0].GetProperty("sunoId").GetString());
        Assert.Equal((0, 0, 2, 2, 0), Counts(page));

        var second = await ListAsync(client, export, "?pageSize=1&page=2");
        Assert.Equal(2, second.GetProperty("total").GetInt32());
        Assert.Equal("clip-b", Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("sunoId").GetString());

        using (var unknown = await client.GetAsync(RemoteStates(export, "?class=new")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        using (var gone = await client.GetAsync(RemoteStates(Guid.CreateVersion7())))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        client.Dispose();
    }

    [Fact]
    public async Task AChangeOfRowsIsRefusedWithoutTheRevisionForUnknownRowsOrAMalformedBodyAndAfterConfirm()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);
        var export = await ExportAsync(client, token, [], [Alpha]);
        var ifMatch = await ImportCommitApi.IfMatchAsync(client, export);

        using (var none = await PatchAsync(client, export, null, """{"sunoIds":["clip-a"],"apply":false}"""))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, none.StatusCode);
        }

        using (var stale = await PatchAsync(client, export, "\"99\"", """{"sunoIds":["clip-a"],"apply":false}"""))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }

        using (var unknown = await PatchAsync(client, export, ifMatch, """{"sunoIds":["clip-a","not-a-row"],"apply":false}"""))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
            var problem = await SetupApi.JsonAsync(unknown);
            Assert.Equal("unknown_rows", problem.GetProperty("code").GetString());
            Assert.Equal(["not-a-row"], problem.GetProperty("sunoIds").EnumerateArray().Select(static id => id.GetString()));
        }

        foreach (var body in new[] { """{"sunoIds":[],"apply":false}""", """{"sunoIds":["clip-a"]}""", """{"sunoIds":"clip-a","apply":"no"}""", "{}" })
        {
            using var malformed = await PatchAsync(client, export, ifMatch, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
            Assert.Equal("validation_failed", (await SetupApi.JsonAsync(malformed)).GetProperty("code").GetString());
        }

        // Nothing was changed by any refusal: the row is still applied, at the same revision.
        Assert.True((await RowsAsync(client, export))["clip-a"].GetProperty("apply").GetBoolean());
        Assert.Equal(ifMatch, await ImportCommitApi.IfMatchAsync(client, export));

        await ImportCommitApi.CommitAsync(client, export);
        using (var late = await PatchAsync(client, export, await ImportCommitApi.IfMatchAsync(client, export), """{"sunoIds":["clip-a"],"apply":false}"""))
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Equal("export_not_ready", (await SetupApi.JsonAsync(late)).GetProperty("code").GetString());
        }

        client.Dispose();
    }

    [Fact]
    public async Task TheRowsAreSessionOnly()
    {
        using var factory = SongApi.Host();
        var (client, token) = await TwoGenerationsAsync(factory);
        var export = await ExportAsync(client, token, [], [Alpha]);

        using var listed = await SunoExportApi.SendAsync(client, HttpMethod.Get, RemoteStates(export), token);
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        using var changed = await SunoExportApi.SendAsync(client, HttpMethod.Patch, RemoteStates(export), token, """{"sunoIds":["clip-a"],"apply":false}""");
        Assert.Equal(HttpStatusCode.Forbidden, changed.StatusCode);
        client.Dispose();
    }

    /// <summary>A Song (n8-1) with two Generations of its Version 1: Alpha (g1) and Beta (g2), both active and present.</summary>
    private static async Task<(HttpClient Client, string Token)> TwoGenerationsAsync(N8TracksApiFactory factory)
    {
        var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SongApi.CreateAsync(client, "Followed");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Alpha.ToJsonString());
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Beta.ToJsonString());
        return (client, token);
    }

    private static async Task<JsonElement> SongAsync(HttpClient client) => await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));

    private static (int Trashed, int Restored, int Missing, int Applied, int Skipped) Counts(JsonElement list)
    {
        var counts = list.GetProperty("counts");
        return (counts.GetProperty("trashed").GetInt32(), counts.GetProperty("restored").GetInt32(), counts.GetProperty("missing").GetInt32(), counts.GetProperty("applied").GetInt32(), counts.GetProperty("skipped").GetInt32());
    }

    private static (string Change, bool Archives, bool Apply) Row(JsonElement row) =>
        (row.GetProperty("change").GetString()!, row.GetProperty("archives").GetBoolean(), row.GetProperty("apply").GetBoolean());

    private static (string RemoteState, string NewRemoteState, string State, string NewState) Moves(JsonElement row) =>
        (row.GetProperty("remoteState").GetString()!, row.GetProperty("newRemoteState").GetString()!, row.GetProperty("state").GetString()!, row.GetProperty("newState").GetString()!);

    private static (string Change, string Outcome) Outcome(JsonElement row) =>
        (row.GetProperty("change").GetString()!, row.GetProperty("outcome").GetString()!);
}
