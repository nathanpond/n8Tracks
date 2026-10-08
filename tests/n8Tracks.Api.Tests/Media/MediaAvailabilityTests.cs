using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Health;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Missing files, an unavailable media folder, and recovery (#207), through real scans of the host's
/// temporary media folder: a file a completed scan does not find is Missing with its association kept,
/// and Available again when it is back; an unavailable folder marks nothing Missing and makes every file
/// report unavailable until it is readable again, which queues a recovery scan; health, the media
/// status, and every file agree. No scan ever deletes an audio file or an association.
/// </summary>
public sealed class MediaAvailabilityTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";

    private static readonly Uri Status = new("/api/v1/media/status", UriKind.Relative);
    private static readonly Uri Health = new("/health", UriKind.Relative);

    [Fact]
    public async Task AFileThatGoesAwayIsMissingWithItsAssociationAndIsAvailableWhenItIsBack()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await CatalogAsync(factory, client);
        var named = $"Song (suno-{A}).mp3";
        MediaApi.Place(factory, named, "mp3");
        MediaApi.Place(factory, "other.ogg", "ogg");
        Assert.Equal(1, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("associated").GetInt32());
        var before = MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, named);
        var counts = Counts(factory);

        File.Delete(MediaApi.FullPath(factory, named));
        var gone = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(1, gone.GetProperty("missing").GetInt32());
        Assert.Equal(0, gone.GetProperty("restored").GetInt32());
        Assert.Equal(1, gone.GetProperty("seen").GetInt32());

        var missing = MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, named);
        Assert.Equal("missing", missing.GetProperty("status").GetString());
        Assert.Equal("missing", missing.GetProperty("storedStatus").GetString());
        AssertSameAssociation(before, missing, song);
        Assert.Equal(before.GetProperty("lastSeenAt").GetDateTime(), missing.GetProperty("lastSeenAt").GetDateTime());
        Assert.Equal(named, Assert.Single((await MediaApi.ListAsync(client, "?status=missing")).Items).GetProperty("path").GetString());
        Assert.Equal("other.ogg", Assert.Single((await MediaApi.ListAsync(client, "?status=available")).Items).GetProperty("path").GetString());
        Assert.Equal(counts, Counts(factory));

        // A second scan does not count it again.
        Assert.Equal(0, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("missing").GetInt32());

        MediaApi.Place(factory, named, "mp3");
        var back = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(1, back.GetProperty("restored").GetInt32());
        Assert.Equal(0, back.GetProperty("missing").GetInt32());
        var restored = MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, named);
        Assert.Equal("available", restored.GetProperty("status").GetString());
        Assert.Equal("available", restored.GetProperty("storedStatus").GetString());
        AssertSameAssociation(before, restored, song);
        Assert.Equal(counts, Counts(factory));
    }

    [Fact]
    public async Task AMissingFileBackWithOtherContentIsTheSameRecordReadAgain()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "tone.ogg", "ogg");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var before = Assert.Single((await MediaApi.ListAsync(client)).Items);

        File.Delete(MediaApi.FullPath(factory, "tone.ogg"));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        MediaApi.Write(factory, "tone.ogg", [.. File.ReadAllBytes(MediaApi.Fixture("ogg")), .. new byte[4096]]);

        var back = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(1, back.GetProperty("changed").GetInt32());
        Assert.Equal(1, back.GetProperty("restored").GetInt32());
        var after = Assert.Single((await MediaApi.ListAsync(client)).Items);
        Assert.Equal(before.GetProperty("id").GetGuid(), after.GetProperty("id").GetGuid());
        Assert.Equal("available", after.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("sizeBytes").GetInt64() + 4096, after.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(before.GetProperty("revision").GetInt32(), after.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AFileListedButNotLookedAtKeepsItsStatus()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "x.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        File.Delete(MediaApi.FullPath(factory, "x.mp3"));
        Assert.Equal(1, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("missing").GetInt32());

        MediaApi.Place(factory, "x.mp3", "mp3");
        var mount = MediaApi.Mount(factory);
        mount.NullStats["x.mp3"] = true;
        var unread = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(1, unread.GetProperty("unreadable").GetInt32());
        Assert.Equal(0, unread.GetProperty("restored").GetInt32());
        Assert.Equal("missing", Assert.Single((await MediaApi.ListAsync(client)).Items).GetProperty("status").GetString());

        mount.NullStats.Clear();
        Assert.Equal(1, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("restored").GetInt32());
        Assert.Equal("available", Assert.Single((await MediaApi.ListAsync(client)).Items).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnAbsentMediaFolderMarksNothingMissingAndEveryFileReportsUnavailableUntilItIsBack()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        MediaApi.Place(factory, "b/b.ogg", "ogg");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var rows = AudioRows(factory);
        var counts = Counts(factory);
        var mount = await MountAsync(client);
        Assert.Equal("available", mount.GetProperty("state").GetString());
        var since = mount.GetProperty("since").GetDateTime();

        var moved = factory.MediaPath + "-unplugged";
        Directory.Move(factory.MediaPath, moved);
        try
        {
            var job = await MediaApi.ScanAsync(client);
            Assert.Equal("failed", job.GetProperty("status").GetString());
            Assert.Contains(MediaFolderUnavailableException.Text, job.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.Equal(rows, AudioRows(factory));

            var unavailable = await MountAsync(client);
            Assert.Equal("unavailable", unavailable.GetProperty("state").GetString());
            Assert.True(unavailable.GetProperty("since").GetDateTime() >= since);

            var (items, _) = await MediaApi.ListAsync(client);
            Assert.Equal(2, items.Count);
            Assert.All(items, static item =>
            {
                Assert.Equal("unavailable", item.GetProperty("status").GetString());
                Assert.Equal("available", item.GetProperty("storedStatus").GetString());
            });
            Assert.Equal(2, (await MediaApi.ListAsync(client, "?status=unavailable")).Items.Count);
            Assert.Empty((await MediaApi.ListAsync(client, "?status=available")).Items);
            Assert.Empty((await MediaApi.ListAsync(client, "?status=missing")).Items);
            using var one = await client.GetAsync(new Uri($"/api/v1/audio-files/{items[0].GetProperty("id").GetGuid()}", UriKind.Relative));
            Assert.Equal("unavailable", (await SetupApi.JsonAsync(one)).GetProperty("status").GetString());
        }
        finally
        {
            Directory.Move(moved, factory.MediaPath);
        }

        MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal("available", (await MountAsync(client)).GetProperty("state").GetString());
        Assert.All((await MediaApi.ListAsync(client)).Items, static item => Assert.Equal("available", item.GetProperty("status").GetString()));
        Assert.Empty((await MediaApi.ListAsync(client, "?status=unavailable")).Items);
        Assert.Equal(counts, Counts(factory));
    }

    [Fact]
    public async Task AnEmptyReadableFolderIsASuccessfulScanThatMarksEveryFileMissing()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        MediaApi.Place(factory, "deep/er/b.ogg", "ogg");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var counts = Counts(factory);

        Directory.Delete(factory.MediaPath, recursive: true);
        Directory.CreateDirectory(factory.MediaPath);

        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(2, result.GetProperty("missing").GetInt32());
        Assert.Equal("available", (await MountAsync(client)).GetProperty("state").GetString());
        Assert.All((await MediaApi.ListAsync(client)).Items, static item => Assert.Equal("missing", item.GetProperty("status").GetString()));
        Assert.Equal(counts, Counts(factory));
    }

    [Fact]
    public async Task AScanStoppedPartWayMarksNothingMissing()
    {
        using var factory = MediaApi.Host(new MediaScanOptions { BatchSize = 1 });
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "1.mp3", "mp3");
        MediaApi.Place(factory, "2.ogg", "ogg");
        MediaApi.Place(factory, "3.flac", "flac");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        File.Delete(MediaApi.FullPath(factory, "3.flac"));
        var statuses = Statuses(factory);

        // Through the job's own cancellation token: stopped after the first file of the second pass.
        using var stop = new CancellationTokenSource();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var scans = scope.ServiceProvider.GetRequiredService<MediaScanService>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scans.RunAsync(
                Guid.CreateVersion7(),
                MediaScanTrigger.Manual,
                (progress, _) =>
                {
                    if (progress > 0)
                    {
                        stop.Cancel();
                    }
                },
                stop.Token));
        }

        Assert.All((await MediaApi.ListAsync(client)).Items, static item => Assert.Equal("available", item.GetProperty("storedStatus").GetString()));
        Assert.Equal(statuses, Statuses(factory));

        // A failed one too.
        MediaApi.Mount(factory).FailingStats["2.ogg"] = true;
        Assert.Equal("failed", (await MediaApi.ScanAsync(client)).GetProperty("status").GetString());
        Assert.Empty((await MediaApi.ListAsync(client, "?status=missing")).Items);

        MediaApi.Mount(factory).FailingStats.Clear();
        Assert.Equal(1, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("missing").GetInt32());
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task ADirectoryThatCannotBeListedLeavesItsFilesAsTheyWere()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "open/gone.mp3", "mp3");
        MediaApi.Place(factory, "locked/kept.mp3", "mp3");
        MediaApi.Place(factory, "locked/deeper/also.ogg", "ogg");
        MediaApi.Place(factory, "locked/was-missing.flac", "flac");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        File.Delete(MediaApi.FullPath(factory, "locked/was-missing.flac"));
        Assert.Equal(1, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("missing").GetInt32());

        // Back on disk, but under a directory that cannot be listed: it stays Missing.
        MediaApi.Place(factory, "locked/was-missing.flac", "flac");
        File.Delete(MediaApi.FullPath(factory, "open/gone.mp3"));
        var locked = MediaApi.FullPath(factory, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (CanList(locked))
            {
                // A privileged process reads it anyway; the rest of the rule is tested above.
                return;
            }

            var result = MediaApi.Result(await MediaApi.ScanAsync(client));
            Assert.Equal(1, result.GetProperty("unreadableDirectories").GetInt32());
            Assert.Equal(1, result.GetProperty("missing").GetInt32());
            var (items, _) = await MediaApi.ListAsync(client);
            Assert.Equal("missing", MediaApi.ByPath(items, "open/gone.mp3").GetProperty("status").GetString());
            Assert.Equal("available", MediaApi.ByPath(items, "locked/kept.mp3").GetProperty("status").GetString());
            Assert.Equal("available", MediaApi.ByPath(items, "locked/deeper/also.ogg").GetProperty("status").GetString());
            Assert.Equal("missing", MediaApi.ByPath(items, "locked/was-missing.flac").GetProperty("status").GetString());
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task AMovedOrRenamedFileIsMissingAtTheOldPathAndNewAtTheNewWithoutTheAssociation()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await CatalogAsync(factory, client);
        var named = $"Song (suno-{A}).mp3";
        var kept = $"Kept (suno-{A}).ogg";
        MediaApi.Place(factory, named, "mp3");
        MediaApi.Place(factory, kept, "ogg");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var before = MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, named);

        // One renamed to a name without the ID; one moved, its name (and so its ID) unchanged.
        File.Move(MediaApi.FullPath(factory, named), MediaApi.FullPath(factory, "renamed.mp3"));
        Directory.CreateDirectory(MediaApi.FullPath(factory, "moved"));
        File.Move(MediaApi.FullPath(factory, kept), MediaApi.FullPath(factory, $"moved/{kept}"));

        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(2, result.GetProperty("missing").GetInt32());
        Assert.Equal(2, result.GetProperty("new").GetInt32());

        var (items, _) = await MediaApi.ListAsync(client);
        Assert.Equal(4, items.Count);
        var old = MediaApi.ByPath(items, named);
        Assert.Equal("missing", old.GetProperty("status").GetString());
        AssertSameAssociation(before, old, song);
        Assert.Equal("missing", MediaApi.ByPath(items, kept).GetProperty("status").GetString());

        // n8Tracks does not carry the association across: the renamed file has none, and the moved one
        // is matched only because #206's rule applies to its own name.
        var renamed = MediaApi.ByPath(items, "renamed.mp3");
        Assert.Equal("available", renamed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, renamed.GetProperty("song").ValueKind);
        var moved = MediaApi.ByPath(items, $"moved/{kept}");
        Assert.Equal(song, moved.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal("suno-id", moved.GetProperty("associationOrigin").GetString());
    }

    [Fact]
    public async Task HealthTheMediaStatusAndEveryFileAgreeAndTheFolderComingBackQueuesARecoveryScan()
    {
        var clock = new TestClock();
        using var probe = new SwitchableMediaProbe();
        using var factory = MediaApi.Host(services: services =>
        {
            probe.Register(services);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        await AssertAgreeAsync(client, "available");

        // The health probe changes the state at once, whichever way it goes.
        probe.Readable = false;
        Assert.Equal("unavailable", await HealthMediaAsync(client));
        await AssertAgreeAsync(client, "unavailable");
        Assert.Empty(await RecoveryJobsAsync(factory));

        probe.Readable = true;
        Assert.Equal("available", await HealthMediaAsync(client));
        await AssertAgreeAsync(client, "available");
        Assert.Single(await RecoveryJobsAsync(factory));

        // Within five minutes of that one, coming back again queues none; after them, it does.
        probe.Readable = false;
        await HealthMediaAsync(client);
        probe.Readable = true;
        await HealthMediaAsync(client);
        await AssertAgreeAsync(client, "available");
        Assert.Single(await RecoveryJobsAsync(factory));

        clock.Advance(MediaAvailability.RecoveryInterval);
        probe.Readable = false;
        await HealthMediaAsync(client);
        probe.Readable = true;
        await HealthMediaAsync(client);
        Assert.Equal(2, (await RecoveryJobsAsync(factory)).Count);
    }

    [Fact]
    public async Task TheMonitorNeedsTwoFailedProbesInARowButOneReadableProbeAndThenQueuesARecoveryScan()
    {
        using var probe = new SwitchableMediaProbe();
        using var factory = MediaApi.Host(services: probe.Register);
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        probe.Readable = false;
        Assert.Equal(MediaRecoveryAction.None, await CheckAsync(factory));
        Assert.Equal("available", (await MountAsync(client)).GetProperty("state").GetString());

        // A readable probe in between starts the count again.
        probe.Readable = true;
        Assert.Equal(MediaRecoveryAction.None, await CheckAsync(factory));
        probe.Readable = false;
        Assert.Equal(MediaRecoveryAction.None, await CheckAsync(factory));
        Assert.Equal(MediaRecoveryAction.Unavailable, await CheckAsync(factory));
        await AssertAgreeAsync(client, "unavailable", health: false);
        Assert.Equal(MediaRecoveryAction.None, await CheckAsync(factory));

        probe.Readable = true;
        Assert.Equal(MediaRecoveryAction.Recovered, await CheckAsync(factory));
        await AssertAgreeAsync(client, "available", health: false);
        var recovery = Assert.Single(await RecoveryJobsAsync(factory));
        Assert.Equal(Application.Jobs.JobStatus.Succeeded, recovery.Status);
    }

    [Fact]
    public async Task TheMediaStatusNeedsCatalogReadAndSaysAvailableBeforeAnythingWasRecorded()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        using (var anonymous = factory.CreateClient())
        {
            using var refused = await anonymous.GetAsync(Status);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var mount = await MountAsync(client);
        Assert.Equal("available", mount.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, mount.GetProperty("since").ValueKind);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, true)]
    [InlineData(1, 0, 0, 0, 0, false)]
    [InlineData(0, 1, 0, 0, 0, false)]
    [InlineData(0, 0, 1, 0, 0, false)]
    [InlineData(0, 0, 0, 1, 0, false)]
    [InlineData(0, 0, 0, 0, 1, false)]
    public void AScanFoundNothingOnlyWhenNothingWasNewChangedMissingRestoredOrAssociated(int added, int changed, int missing, int restored, int associated, bool nothing)
    {
        var counts = new MediaScanCounts(added + changed, added, changed, 0, 0, 0, 0, Associated: associated, Missing: missing, Restored: restored);
        Assert.Equal(nothing, counts.FoundNothing);
    }

    /// <summary>Song n8-1 with Generation g1 (Suno ID <see cref="A"/>); its ID.</summary>
    private static async Task<Guid> CatalogAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await SongApi.CreateAsync(client, "Origin");
        return (await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(A))).Generation.Id;
    }

    private static void AssertSameAssociation(JsonElement before, JsonElement after, Guid generation)
    {
        Assert.Equal(before.GetProperty("id").GetGuid(), after.GetProperty("id").GetGuid());
        Assert.Equal(generation, after.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal(before.GetProperty("song").GetProperty("id").GetGuid(), after.GetProperty("song").GetProperty("id").GetGuid());
        Assert.Equal("suno-id", after.GetProperty("associationOrigin").GetString());
        Assert.Equal(before.GetProperty("revision").GetInt32(), after.GetProperty("revision").GetInt32());
    }

    /// <summary>The media status, health (when asked), and every file report the folder as <paramref name="state"/>.</summary>
    private static async Task AssertAgreeAsync(HttpClient client, string state, bool health = true)
    {
        Assert.Equal(state, (await MountAsync(client)).GetProperty("state").GetString());
        if (health)
        {
            Assert.Equal(state, await HealthMediaAsync(client));
        }

        var (items, _) = await MediaApi.ListAsync(client);
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal(state, item.GetProperty("status").GetString()));
    }

    private static async Task<JsonElement> MountAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Status);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("mount");
    }

    /// <summary>The detail of the health report's media component: <c>available</c> or <c>unavailable</c>.</summary>
    private static async Task<string?> HealthMediaAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Health);
        var report = await SetupApi.JsonAsync(response);
        return report.GetProperty("components").GetProperty("media").GetProperty("detail").GetString();
    }

    private static async Task<MediaRecoveryAction> CheckAsync(N8TracksApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediaRecoveryService>().CheckAsync(CancellationToken.None);
    }

    /// <summary>The recovery scans, once every scan has finished.</summary>
    private static async Task<List<Application.Jobs.JobSummary>> RecoveryJobsAsync(N8TracksApiFactory factory)
    {
        await MediaScheduleApi.SettleAsync(factory);
        return [.. (await MediaScheduleApi.ScanJobsAsync(factory)).Where(static job => MediaScheduleApi.Trigger(job) == "recovery")];
    }

    /// <summary>How many audio file rows, and how many of them are associated.</summary>
    private static List<string> Counts(N8TracksApiFactory factory) =>
        TestDatabase.Rows(factory.DataPath, "SELECT count(*), count(song_id) FROM audio_files;");

    private static List<string> AudioRows(N8TracksApiFactory factory) =>
        TestDatabase.Rows(
            factory.DataPath,
            "SELECT id, path, size_bytes, modified_utc, first_seen_utc, last_seen_utc, status, metadata_readable, ifnull(song_id, ''), revision FROM audio_files ORDER BY path;");

    private static List<string> Statuses(N8TracksApiFactory factory) =>
        TestDatabase.Rows(factory.DataPath, "SELECT path, status FROM audio_files ORDER BY path;");

    private static bool CanList(string directory)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).Any();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
