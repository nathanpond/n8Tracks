using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Invariants;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// A sync with nothing to change (#450): completing an export answers <c>nothingToReview</c>, decided in
/// the service layer (<see cref="ImportReviewService.NothingToReviewAsync"/>, invariant 5). It is true only
/// when the export is ready, every record is linked, and Confirm would follow no Suno state, give no
/// Generation Suno's final status, and record no playlist's songs other than n8Tracks has. The extension
/// then discards it through the discard endpoint, which writes no catalog row (invariant 3).
/// </summary>
public sealed class NothingToReviewTests
{
    [Fact]
    public async Task ASyncOfSongsN8TracksAlreadyHasHasNothingToReviewAndItsDiscardWritesNoCatalogRow()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await PlaylistMembershipTests.ImportAsync(client, token, "c-1", "c-2");

        var (id, export) = await SunoExportApi.UploadAsync(client, token, RemoteStateApi.Header(), SunoExportApi.Part(1, [Clip("c-1"), Clip("c-2")]));

        Assert.Equal(("ready", 2, 2), (export.GetProperty("state").GetString(), SunoExportApi.Count(export, "linked"), SunoExportApi.Count(export, "total")));
        Assert.True(export.GetProperty("nothingToReview").GetBoolean());

        // Only completing answers it.
        Assert.False((await SunoExportApi.GetAsync(client, token, id)).TryGetProperty("nothingToReview", out _));

        // The extension's discard: the export is discarded, and no catalog row changes.
        var before = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        using (var discarded = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/discard"), token))
        {
            Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
            Assert.Equal("discarded", (await SetupApi.JsonAsync(discarded)).GetProperty("state").GetString());
        }

        var after = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (table, rows) in before)
        {
            Assert.Equal(rows.ToDictionary(static pair => pair.Key, static pair => pair.Value.Text), after[table].ToDictionary(static pair => pair.Key, static pair => pair.Value.Text));
        }

        Assert.Equal(false, await NothingToReviewAsync(factory, id));
        Assert.Null(await NothingToReviewAsync(factory, Guid.NewGuid()));
    }

    [Fact]
    public async Task AnExportWithARecordToChooseOnGoesToReview()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await PlaylistMembershipTests.ImportAsync(client, token, "c-1");
        SunoExportApi.Ignore(factory, "c-ignored");

        // A new record.
        var (_, withNew) = await SunoExportApi.UploadAsync(client, token, RemoteStateApi.Header(), SunoExportApi.Part(1, [Clip("c-1"), Clip("c-new")]));
        Assert.Equal(1, SunoExportApi.Count(withNew, "new"));
        Assert.False(withNew.GetProperty("nothingToReview").GetBoolean());

        // A record on the ignore list: the review may still import it.
        var (_, withIgnored) = await SunoExportApi.UploadAsync(client, token, RemoteStateApi.Header(), SunoExportApi.Part(1, [Clip("c-1"), Clip("c-ignored")]));
        Assert.Equal(1, SunoExportApi.Count(withIgnored, "ignored"));
        Assert.False(withIgnored.GetProperty("nothingToReview").GetBoolean());

        // A linked clip in Suno's Trash: Confirm would follow it (#142).
        var (_, trashed) = await SunoExportApi.UploadAsync(client, token, RemoteStateApi.Header(), SunoExportApi.Part(1, [], [Clip("c-1")]));
        Assert.Equal(1, SunoExportApi.Count(trashed, "linked"));
        Assert.False(trashed.GetProperty("nothingToReview").GetBoolean());
    }

    [Fact]
    public async Task APlaylistSyncHasNothingToReviewOnlyWhenEveryPlaylistHoldsTheSongsN8TracksRecorded()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await PlaylistMembershipTests.ImportAsync(client, token, "c-1", "c-2");

        // Not recorded yet: the review records it.
        var first = await PlaylistMembershipTests.PlaylistExportAsync(client, token, [], PlaylistMembershipTests.Membership("pl-1", "Night Drive", 2, "c-1", "c-2"));
        Assert.Equal(false, await NothingToReviewAsync(factory, first));
        await ImportCommitApi.CommitAsync(client, first);

        // The same songs: nothing to review.
        var same = await PlaylistMembershipTests.PlaylistExportAsync(client, token, [], PlaylistMembershipTests.Membership("pl-1", "Night Drive", 2, "c-1", "c-2"));
        Assert.Equal(true, await NothingToReviewAsync(factory, same));

        // A song dropped: the review has a membership change to record.
        var changed = await PlaylistMembershipTests.PlaylistExportAsync(client, token, [], PlaylistMembershipTests.Membership("pl-1", "Night Drive", 1, "c-1"));
        Assert.Equal(false, await NothingToReviewAsync(factory, changed));
    }

    private static JsonNode Clip(string sunoId) => RemoteStateApi.Clip(sunoId, "Song " + sunoId);

    /// <summary>Asks the review service directly, in a scope of its own.</summary>
    private static async Task<bool?> NothingToReviewAsync(N8TracksApiFactory factory, Guid exportId)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<ImportReviewService>().NothingToReviewAsync(exportId);
        }
    }
}
