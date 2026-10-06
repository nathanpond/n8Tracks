using System.Net;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Retention;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Scheduling;
using n8Tracks.Infrastructure.Assets;
using SkiaSharp;
using static n8Tracks.Api.Tests.Assets.ArtworkApi;
using static n8Tracks.Api.Tests.Assets.ArtworkImages;

namespace n8Tracks.Api.Tests.Assets;

/// <summary>
/// What happens to artwork over time (#97): an upload nothing attaches within 24 hours of its latest
/// upload is removed with its files; one attached before then is kept; artwork attached to nothing
/// live is not served; artwork a retention group still lists is kept until the group is pruned, and
/// then its files go unless something live still attaches it. The sweep runs hourly on the shared
/// scheduler.
/// </summary>
public sealed class ArtworkSweepTests
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly TimeSpan Millisecond = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task AnUploadUnattachedAtTwentyFourHoursIsRemovedAndOneAttachedBeforeThenIsKept()
    {
        var clock = new TestClock();
        var attachments = new TestAttachments();
        using var factory = Host(clock, attachments);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var unattachedImage = Halves(SKEncodedImageFormat.Png, 400, 200);
        var unattached = await StoreAsync(client, unattachedImage);
        var attached = await StoreAsync(client, Halves(SKEncodedImageFormat.Jpeg, 400, 200));
        clock.Advance(TimeSpan.FromHours(23));
        attachments.Attach(IdOf(attached));

        // Just short of a day: nothing goes, and the fresh upload is still served.
        clock.Advance(TimeSpan.FromHours(1) - Millisecond);
        Assert.Equal(new ArtworkSweepSummary(0, 0), await SweepAsync(factory));
        using (var fresh = await GetAsync(client, UrlOf(unattached, "96"), reader))
        {
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }

        // At a day: the unattached upload goes, row and files and folder; the attached one stays.
        clock.Advance(Millisecond);
        Assert.Equal(new ArtworkSweepSummary(1, 0), await SweepAsync(factory));
        Assert.Equal(1, AssetRows(factory));
        var hash = Hash(unattachedImage);
        Assert.DoesNotContain(StoredFiles(factory), file => file.Contains(hash, StringComparison.Ordinal));
        Assert.False(Directory.Exists(FullPath(factory, $"artwork/{hash[..2]}/{hash}")));
        using (var gone = await GetAsync(client, UrlOf(unattached, "original"), reader))
        {
            await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, "not_found");
        }

        // An attached asset is never removed, however long it stays.
        clock.Advance(TimeSpan.FromDays(60));
        Assert.Equal(new ArtworkSweepSummary(0, 0), await SweepAsync(factory));
        using var kept = await GetAsync(client, UrlOf(attached, "320"), reader);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(3, StoredFiles(factory).Count);
    }

    [Fact]
    public async Task UploadingTheSameBytesAgainRestartsTheClock()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var image = Halves(SKEncodedImageFormat.Webp, 400, 200);
        var first = await StoreAsync(client, image);

        clock.Advance(TimeSpan.FromHours(20));
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.ArtworkWrite);
        var again = await StoreAsync(client, image, token, HttpStatusCode.OK);
        Assert.Equal(IdOf(first), IdOf(again));

        // A day after the first upload, but not after the second: kept.
        clock.Advance(TimeSpan.FromHours(4));
        Assert.Equal(new ArtworkSweepSummary(0, 0), await SweepAsync(factory));

        // A day after the second: removed.
        clock.Advance(TimeSpan.FromHours(20));
        Assert.Equal(new ArtworkSweepSummary(1, 0), await SweepAsync(factory));
        Assert.Equal(0, AssetRows(factory));
        Assert.Empty(StoredFiles(factory));

        // Uploading the bytes after that stores them afresh, as a new asset.
        var later = await StoreAsync(client, image, token);
        Assert.NotEqual(IdOf(first), IdOf(later));
        Assert.Equal(3, StoredFiles(factory).Count);
    }

    [Fact]
    public async Task ArtworkAttachedToNothingLiveIsNotFound()
    {
        var clock = new TestClock();
        var attachments = new TestAttachments();
        using var factory = Host(clock, attachments);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        attachments.Attach(IdOf(asset));
        clock.Advance(TimeSpan.FromDays(2));

        using (var attached = await GetAsync(client, UrlOf(asset, "original"), reader))
        {
            Assert.Equal(HttpStatusCode.OK, attached.StatusCode);
        }

        // Detached, past its upload's day: not served, though its row and files are still there.
        attachments.Detach(IdOf(asset));
        foreach (var key in new[] { "original", "96", "1024" })
        {
            using var detached = await GetAsync(client, UrlOf(asset, key), reader);
            await SetupApi.ProblemAsync(detached, HttpStatusCode.NotFound, "not_found");
            Assert.Equal("no-store", detached.Headers.CacheControl?.ToString());
        }

        Assert.Equal(1, AssetRows(factory));
    }

    [Fact]
    public async Task AnAssetOnlyARetentionGroupListsIsKeptUntilTheGroupIsPrunedThenItsFilesGoButAnAttachedOnesNever()
    {
        var clock = new TestClock();
        var attachments = new TestAttachments();
        using var factory = Host(clock, attachments);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        RetentionApi.CreateTestTables(factory);
        var (version, _, _) = await RetentionApi.VersionWithSnapshotsAsync(client, "Cover story");

        var replacedImage = Halves(SKEncodedImageFormat.Png, 400, 200);
        var stillAttachedImage = Halves(SKEncodedImageFormat.Jpeg, 400, 200);
        var replaced = await StoreAsync(client, replacedImage);
        var stillAttached = await StoreAsync(client, stillAttachedImage);
        attachments.Attach(IdOf(replaced));
        attachments.Attach(IdOf(stillAttached));
        var replacedFiles = FilesOf(factory, replacedImage);
        var stillAttachedFiles = FilesOf(factory, stillAttachedImage);
        Assert.Equal(3, replacedFiles.Count);

        // Each was attached through a (test) record that is now deleted into retention, listing its files;
        // the first asset is attached to nothing else, the second still is.
        attachments.Detach(IdOf(replaced));
        await RetainAttachmentAsync(factory, version, replacedFiles);
        await RetainAttachmentAsync(factory, version, stillAttachedFiles);

        // Past its upload's day, but retained: kept (rows and files), though not served.
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(new ArtworkSweepSummary(0, 0), await SweepAsync(factory));
        Assert.Equal(2, AssetRows(factory));
        Assert.All(replacedFiles, file => Assert.True(File.Exists(FullPath(factory, file)), file));
        using (var hidden = await GetAsync(client, UrlOf(replaced, "original"), reader))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        // The groups are pruned: the retained-only asset's files are deleted, the attached one's kept.
        clock.Advance(RetentionService.RetentionPeriod);
        var pruned = await RetentionApi.PruneAsync(factory);
        Assert.Equal(2, pruned.GroupsPruned);
        Assert.All(replacedFiles, file => Assert.False(File.Exists(FullPath(factory, file)), file));
        Assert.All(stillAttachedFiles, file => Assert.True(File.Exists(FullPath(factory, file)), file));

        // And the next sweep removes its row; the attached asset is served as before.
        Assert.Equal(new ArtworkSweepSummary(1, 0), await SweepAsync(factory));
        Assert.Equal(1, AssetRows(factory));
        Assert.Equal(stillAttachedFiles.Order(StringComparer.Ordinal), StoredFiles(factory));
        using var served = await GetAsync(client, UrlOf(stillAttached, "320"), reader);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    public async Task TheSweepClearsStagingFilesLeftByAWriteThatNeverFinished()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var staging = Directory.CreateDirectory(Path.Combine(factory.DataPath, ManagedAssetStore.StagingFolderName)).FullName;
        var stale = Path.Combine(staging, "stale.tmp");
        var recent = Path.Combine(staging, "recent.tmp");
        await File.WriteAllTextAsync(stale, "half an image");
        await File.WriteAllTextAsync(recent, "being written");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(2));

        Assert.Equal(new ArtworkSweepSummary(0, 0), await SweepAsync(factory));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task TheSweepRunsOnTheSharedSchedulerAtTheFirstLookThenHourly()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.ArtworkWrite);
        await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));

        // The first look after a start runs it (nothing is a day old yet).
        Assert.Null(await TickAsync(factory));
        clock.Advance(TimeSpan.FromMinutes(30));
        await StoreAsync(client, Halves(SKEncodedImageFormat.Jpeg, 400, 200), writer);

        // A day after the first upload, a look runs it: that upload goes, the later one is not a day old.
        clock.Advance(Day - TimeSpan.FromMinutes(30));
        Assert.Equal("removed 1 unattached artwork uploads", await TickAsync(factory));
        Assert.Equal(1, AssetRows(factory));

        // Half an hour on, the later upload is a day old, but the sweep waits for its hour.
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Null(await TickAsync(factory));
        Assert.Equal(1, AssetRows(factory));

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("removed 1 unattached artwork uploads", await TickAsync(factory));
        Assert.Equal(0, AssetRows(factory));
    }

    /// <summary>The files stored for <paramref name="image"/>, relative to the managed-assets folder.</summary>
    private static List<string> FilesOf(N8TracksApiFactory factory, byte[] image)
    {
        var hash = Hash(image);
        return [.. StoredFiles(factory).Where(file => file.StartsWith($"artwork/{hash[..2]}/{hash}/", StringComparison.Ordinal))];
    }

    /// <summary>A test record of <paramref name="version"/> using the files, deleted into retention with them.</summary>
    private static async Task RetainAttachmentAsync(N8TracksApiFactory factory, Guid version, List<string> files)
    {
        var row = RetentionApi.AddTestArtwork(factory, version, files[^1]);
        await RetentionApi.RetainAsync(
            factory,
            new RetentionRequest("test-artwork", "Replaced artwork", null, [new RetainedRoot("test-artwork", row)], files));
    }

    /// <summary>One look by the sweep's scheduled task, as the scheduler takes it.</summary>
    private static async Task<string?> TickAsync(N8TracksApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var task = scope.ServiceProvider.GetServices<IDailyTask>().OfType<ArtworkSweepTask>().Single();
        Assert.Equal("artwork sweep", task.Name);
        return await task.TickAsync(CancellationToken.None);
    }
}
