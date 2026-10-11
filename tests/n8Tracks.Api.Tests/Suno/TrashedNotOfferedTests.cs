using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Songs;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// A clip in Suno's Trash that was never imported is not offered in the review (#418). Every sync reads the
/// whole Trash so that linked clips can archive their Generations; a trashed record that classifies
/// <c>new</c> is left out at classification: it is not listed, counted, proposed, or committed, and the
/// export says how many were left out (<c>trashedNotOffered</c>). A trashed record that is linked,
/// deleted, or ignored keeps its class, so archiving and the ignore list's status still follow Suno.
/// </summary>
public sealed class TrashedNotOfferedTests
{
    private static readonly JsonNode Linked = RemoteStateApi.Clip("trashed-linked", "Linked");
    private static readonly JsonNode Deleted = RemoteStateApi.Clip("trashed-deleted", "Deleted");
    private static readonly JsonNode Ignored = RemoteStateApi.Clip("trashed-ignored", "Ignored");
    private static readonly JsonNode NeverOne = RemoteStateApi.Clip("trashed-never-1", "Never imported");
    private static readonly JsonNode NeverTwo = RemoteStateApi.Clip("trashed-never-2", "Never imported either");
    private static readonly JsonNode Listed = RemoteStateApi.Clip("listed-new", "In the library");

    [Fact]
    public async Task ATrashedClipNeverImportedIsNeitherListedCountedProposedNorCommitted()
    {
        using var factory = SongApi.Host();
        var (client, token) = await CatalogAsync(factory);

        var (id, export) = await SunoExportApi.UploadAsync(
            client,
            token,
            Header(playlists: [SunoExportApi.Playlist("mix", "Mix", "listed-new", "trashed-never-1", "trashed-linked")]),
            SunoExportApi.Part(1, [Listed], [Linked, Deleted, Ignored, NeverOne, NeverTwo]));

        // Left out and counted; in no class count.
        Assert.Equal("ready", export.GetProperty("state").GetString());
        Assert.Equal(2, export.GetProperty("trashedNotOffered").GetInt32());
        Assert.Equal((1, 1, 1, 1, 4), (SunoExportApi.Count(export, "new"), SunoExportApi.Count(export, "linked"), SunoExportApi.Count(export, "deleted"), SunoExportApi.Count(export, "ignored"), SunoExportApi.Count(export, "total")));
        Assert.Equal(2, (await SunoExportApi.GetAsync(client, token, id)).GetProperty("trashedNotOffered").GetInt32());
        Assert.Equal(2, (await ImportReviewApi.SummaryAsync(client, id)).GetProperty("export").GetProperty("trashedNotOffered").GetInt32());

        // Not listed, by any filter; the trashed records that are linked, deleted, or ignored are, as before.
        var records = await SunoExportApi.RecordsByIdAsync(client, id);
        Assert.Equal(["listed-new", "trashed-deleted", "trashed-ignored", "trashed-linked"], records.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            [("new", false), ("deleted", true), ("ignored", true), ("linked", true)],
            new[] { "listed-new", "trashed-deleted", "trashed-ignored", "trashed-linked" }.Select(sunoId => (records[sunoId].GetProperty("class").GetString(), records[sunoId].GetProperty("trashed").GetBoolean())));
        Assert.Equal(["listed-new"], (await SunoExportApi.RecordsByIdAsync(client, id, "?class=new")).Keys);
        Assert.Equal(["listed-new", "trashed-linked"], (await SunoExportApi.RecordsByIdAsync(client, id, "?playlist=mix")).Keys.Order(StringComparer.Ordinal));

        // Nothing of them stays staged: no record, no playlist membership, so no proposal or choice.
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_export_records WHERE suno_id LIKE 'trashed-never-%';"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_export_record_playlists WHERE suno_id LIKE 'trashed-never-%';"));
        Assert.Equal("import", records["listed-new"].GetProperty("choice").GetProperty("action").GetString());

        // Committed: the library clip comes in, the trashed ones never imported do not; the linked one is
        // archived by sync, and the ignored one is marked trashed on the ignore list.
        var result = ImportCommitApi.Records(await ImportCommitApi.CommitAsync(client, id));
        Assert.Equal("created", ImportCommitApi.Outcome(result["listed-new"]));
        Assert.DoesNotContain("trashed-never-1", result.Keys);
        Assert.DoesNotContain("trashed-never-2", result.Keys);
        Assert.Equal((0, 0), (ImportCommitApi.GenerationCount(factory, "trashed-never-1"), ImportCommitApi.GenerationCount(factory, "trashed-never-2")));
        Assert.Equal(("archived", "trashed", "sync"), await RemoteStateApi.StatesAsync(client, "n8-1-v1-g1"));
        Assert.Equal("trashed", IgnoredStatus(await IgnoreListTests.ListAsync(client), "trashed-ignored"));
        Assert.Equal(2, (await SunoExportApi.GetAsync(client, null, id)).GetProperty("trashedNotOffered").GetInt32());
        client.Dispose();
    }

    /// <summary>
    /// Classified again once ignore entries are removed (or after an interrupted commit), a trashed record
    /// that is now <c>new</c> leaves the review the same way, and the count grows; a library record that
    /// becomes new stays.
    /// </summary>
    [Fact]
    public async Task ATrashedRecordThatBecomesNewWhenClassifiedAgainLeavesTheReview()
    {
        using var factory = SongApi.Host();
        var (client, token) = await CatalogAsync(factory);
        SunoExportApi.Ignore(factory, "listed-new");

        var (id, export) = await SunoExportApi.UploadAsync(client, token, Header(), SunoExportApi.Part(1, [Listed], [Ignored, NeverOne]));
        Assert.Equal((2, 1), (SunoExportApi.Count(export, "ignored"), export.GetProperty("trashedNotOffered").GetInt32()));

        // Chosen for import while it was ignored (an ignored clip in the Trash may be imported, #143).
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(ProposalApi.Import(new JsonObject { ["kind"] = "newSong", ["key"] = "new:1", ["title"] = "Back from the Trash", ["workspaceId"] = null }), "trashed-ignored"));
        await IgnoreListTests.RemoveAsync(client, "trashed-ignored", "listed-new");

        var again = await SunoExportApi.GetAsync(client, null, id);
        Assert.Equal((1, 0, 1, 2), (SunoExportApi.Count(again, "new"), SunoExportApi.Count(again, "ignored"), SunoExportApi.Count(again, "total"), again.GetProperty("trashedNotOffered").GetInt32()));
        Assert.Equal(["listed-new"], (await SunoExportApi.RecordsByIdAsync(client, id)).Keys);

        // Though chosen for import before it left, it is not committed.
        var result = ImportCommitApi.Records(await ImportCommitApi.CommitAsync(client, id));
        Assert.DoesNotContain("trashed-ignored", result.Keys);
        Assert.Equal(0, ImportCommitApi.GenerationCount(factory, "trashed-ignored"));
        client.Dispose();
    }

    /// <summary>An export with every clip offered says so with zero.</summary>
    [Fact]
    public async Task AnExportWithNothingLeftOutCountsZero()
    {
        using var factory = SongApi.Host();
        var (client, token) = await CatalogAsync(factory);

        var (_, export) = await SunoExportApi.UploadAsync(client, token, Header(), SunoExportApi.Part(1, [Listed], [Linked]));
        Assert.Equal((0, 2), (export.GetProperty("trashedNotOffered").GetInt32(), SunoExportApi.Count(export, "total")));
        client.Dispose();
    }

    /// <summary>
    /// A Song whose Generation holds <c>trashed-linked</c> (<c>n8-1-v1-g1</c>), a Generation of
    /// <c>trashed-deleted</c> deleted (tombstoned), and <c>trashed-ignored</c> on the ignore list.
    /// </summary>
    private static async Task<(HttpClient Client, string Token)> CatalogAsync(N8TracksApiFactory factory)
    {
        var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SongApi.CreateAsync(client, "Followed");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Linked.ToJsonString());
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Deleted.ToJsonString());
        await ProposalApi.DeleteGenerationAsync(client, "n8-1-v1-g2");
        SunoExportApi.Ignore(factory, "trashed-ignored");
        return (client, token);
    }

    /// <summary>A whole-library header captured after the catalog was made.</summary>
    private static JsonObject Header(JsonArray? playlists = null)
    {
        var header = SunoExportApi.Header(playlists: playlists);
        header["capturedAt"] = RemoteStateApi.CapturedLater;
        return header;
    }

    private static string? IgnoredStatus(JsonElement list, string sunoId) =>
        list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("sunoId").GetString() == sunoId).GetProperty("status").GetString();
}
