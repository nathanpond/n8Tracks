using System.Net;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// Deleting one entry of a Version's history (<c>DELETE /api/v1/versions/{reference}/snapshots/{id}</c>):
/// session only, no revision, frozen Versions included. The snapshot becomes a one-record retention
/// group labelled with the Version's shortcode and the entry's time; entries the 50-entry cap removes
/// are not retained.
/// </summary>
public sealed class HistoryEntryDeletionEndpointTests
{
    [Fact]
    public async Task DeletingAnEntryAnswers204RemovesItFromTheHistoryAndRetainsItAsOneLabelledGroup()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, shortcode, snapshots) = await VersionWithSnapshotsAsync(client, "Pruned by hand", "First words");
        clock.Advance(TimeSpan.FromMinutes(5));
        snapshots.Add(await SnapshotAsync(client, version, "Second words"));
        clock.Advance(TimeSpan.FromMinutes(5));
        snapshots.Add(await SnapshotAsync(client, version, "Third words"));
        var revisionBefore = TestDatabase.Scalar(factory.DataPath, $"SELECT revision || '|' || updated_utc FROM versions WHERE id = '{Upper(version)}';");

        // By the Version's shortcode, as the discretion line asks.
        using (var response = await DeleteAsync(client, shortcode, snapshots[1]))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal([snapshots[2], snapshots[0]], await HistoryAsync(client, version));
        Assert.Equal(HttpStatusCode.NotFound, await ReadSnapshotAsync(client, version, snapshots[1]));

        var group = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        Assert.Equal(RetainedRecordTypes.EditorSnapshot, group.Kind);
        Assert.Equal($"History entry of {shortcode} at 2026-10-01T09:05:00Z", group.Label);
        Assert.Null(group.Shortcode);
        Assert.Empty(group.Files);
        Assert.Equal([new RetainedRecord(RetainedRecordTypes.EditorSnapshot, Upper(snapshots[1]), 1)], group.Records);
        Assert.Equal(clock.GetUtcNow(), group.DeletedUtc);
        Assert.Equal(clock.GetUtcNow() + RetentionService.RetentionPeriod, group.PruneAfterUtc);

        // The Version is not touched, and its shortcode still resolves to it, not to the group.
        Assert.Equal(revisionBefore, TestDatabase.Scalar(factory.DataPath, $"SELECT revision || '|' || updated_utc FROM versions WHERE id = '{Upper(version)}';"));
        Assert.Null(await WithServiceAsync(factory, service => service.FindByShortcodeAsync(shortcode, CancellationToken.None)));

        // Deleting it again finds nothing, and retains nothing more.
        using (var again = await DeleteAsync(client, version.ToString(), snapshots[1]))
        {
            await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));

        // Complement: the retained entry restores to the same place in the history.
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        Assert.Equal([snapshots[2], snapshots[1], snapshots[0]], await HistoryAsync(client, version));
    }

    [Fact]
    public async Task AnEntryOfAFrozenVersionIsDeletedAndTheVersionsInputsAreUnchanged()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, shortcode, snapshots) = await VersionWithSnapshotsAsync(client, "Frozen history", "Before the Generation", "Later draft");
        await SongApi.AttachGenerationAsync(factory, shortcode);
        var stored = TestDatabase.Scalar(factory.DataPath, $"SELECT lyrics || '|' || styles || '|' || inputs || '|' || revision FROM versions WHERE id = '{Upper(version)}';");

        using (var response = await DeleteAsync(client, version.ToString(), snapshots[0]))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal([snapshots[1]], await HistoryAsync(client, version));
        Assert.Equal(stored, TestDatabase.Scalar(factory.DataPath, $"SELECT lyrics || '|' || styles || '|' || inputs || '|' || revision FROM versions WHERE id = '{Upper(version)}';"));
    }

    [Fact]
    public async Task EntriesTheCapRemovesCreateNoRetentionGroup()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, _, oldest) = await VersionWithSnapshotsAsync(client, "Capped history", "Oldest");
        for (var index = 0; index < EditorRevisionService.MaximumKept + 2; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await SnapshotAsync(client, version, $"Newer {index}");
        }

        var history = await HistoryAsync(client, version);
        Assert.Equal(EditorRevisionService.MaximumKept, history.Count);
        Assert.DoesNotContain(oldest[0], history);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_records;"));

        // Complement: the user's own deletion of one of them does create one.
        using (var response = await DeleteAsync(client, version.ToString(), history[^1]))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
    }

    [Fact]
    public async Task ABearerTokenWithEveryScopeIs403SessionRequiredAndNothingIsDeleted()
    {
        using var factory = SongApi.Host();
        using var session = await SessionApi.SignedInClientAsync(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(session, "Session only", "Kept words");
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        using var tool = factory.CreateClient();

        using (var refused = await CredentialApi.SendAsync(tool, HttpMethod.Delete, Entry(version.ToString(), snapshots[0]), token))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        Assert.Equal(snapshots, await HistoryAsync(session, version));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));

        // Without a session or a token at all, it is 401.
        using var anonymous = await SessionApi.SendAsync(tool, HttpMethod.Delete, Entry(version.ToString(), snapshots[0]));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task AnEntryOfAnotherVersionOrOfNoVersionIs404AndNothingIsDeleted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (mine, _, _) = await VersionWithSnapshotsAsync(client, "Mine", "My words");
        var (theirs, theirShortcode, theirSnapshots) = await VersionWithSnapshotsAsync(client, "Theirs", "Their words");

        foreach (var (reference, snapshot) in new[]
        {
            (mine.ToString(), theirSnapshots[0]),
            ("n8-1-v1", theirSnapshots[0]),
            (mine.ToString(), Guid.CreateVersion7()),
            (Guid.CreateVersion7().ToString(), theirSnapshots[0]),
            ("n8-9-v1", theirSnapshots[0]),
            (theirShortcode.Replace("-v1", string.Empty, StringComparison.Ordinal), theirSnapshots[0]),
        })
        {
            using var response = await DeleteAsync(client, reference, snapshot);
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal(theirSnapshots, await HistoryAsync(client, theirs));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));

        // Complement: through its own Version it is deleted.
        using var own = await DeleteAsync(client, theirShortcode, theirSnapshots[0]);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
    }

    private static Uri Entry(string versionReference, Guid snapshot) => new($"/api/v1/versions/{versionReference}/snapshots/{snapshot}", UriKind.Relative);

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string versionReference, Guid snapshot) =>
        SessionApi.SendAsync(client, HttpMethod.Delete, Entry(versionReference, snapshot));
}
