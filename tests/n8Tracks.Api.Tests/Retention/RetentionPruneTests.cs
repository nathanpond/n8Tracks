using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Scheduling;
using n8Tracks.Infrastructure.Retention;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Retention;

/// <summary>
/// The prune (#95): a group is removed permanently, with its records and then its managed files, once
/// 30 days have passed since it was deleted, and nothing else ever is. A file a live record or another
/// unpruned group still uses is kept; one that cannot be deleted is retried by the next run; a path
/// out of the managed-assets folder, or through a link, is never deleted. The prune is queued daily at
/// 04:00 by the shared daily-task scheduler, once after a start that missed it, never during a backup.
/// </summary>
public sealed class RetentionPruneTests
{
    private static readonly TimeSpan Millisecond = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task AGroupIsKeptUntilThirtyDaysHavePassedThenRemovedWithItsFilesAndNothingElse()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        CreateTestTables(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(client, "Pruned", "Old words", "Live words");
        var liveArtwork = AddTestArtwork(factory, version, "art/live.png");
        var retainedArtwork = AddTestArtwork(factory, version, "art/retained.png");
        var cover = WriteManagedFile(factory, "art/retained.png");
        var shared = WriteManagedFile(factory, "art/shared.png");
        var live = WriteManagedFile(factory, "art/live.png");
        var unrelated = WriteManagedFile(factory, "art/unrelated.png");

        var old = await RetainAsync(factory, new RetentionRequest("test-artwork", "Retained art", null, [new RetainedRoot("test-artwork", retainedArtwork)], ["art/retained.png", "art/shared.png", "art/live.png"]));
        var oldSnapshot = await RetainAsync(factory, Snapshot(snapshots[0]));
        clock.Advance(TimeSpan.FromDays(1));
        var younger = await RetainAsync(factory, new RetentionRequest(RetainedRecordTypes.EditorSnapshot, "Younger", null, [new RetainedRoot(RetainedRecordTypes.EditorSnapshot, snapshots[1])], ["art/shared.png"]));

        // Files stay where they are while their group is retained.
        Assert.True(File.Exists(cover));
        var liveTables = new[] { "songs", "versions", "editor_revisions", "test_artwork", "used_version_numbers", "settings" }.ToDictionary(table => table, table => Dump(factory, table));

        // 29 days, and a millisecond short of 30: nothing goes.
        clock.Advance(TimeSpan.FromDays(28));
        Assert.Equal(new RetentionPruneSummary(0, 0, 0, 0), await PruneAsync(factory));
        clock.Advance(TimeSpan.FromDays(1) - Millisecond);
        Assert.Equal(old.PruneAfterUtc - Millisecond, clock.GetUtcNow());
        Assert.Equal(new RetentionPruneSummary(0, 0, 0, 0), await PruneAsync(factory));
        Assert.Equal(3, (await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None))).Count);
        Assert.True(File.Exists(cover));

        // At 30 days exactly: the two older groups and the file only they used.
        clock.Advance(Millisecond);
        var summary = await PruneAsync(factory);

        Assert.Equal(new RetentionPruneSummary(2, 1, 2, 0), summary);
        Assert.False(File.Exists(cover));
        Assert.True(File.Exists(shared), "A file a younger group lists is kept.");
        Assert.True(File.Exists(live), "A file a live record uses is kept.");
        Assert.True(File.Exists(unrelated), "A file no group lists is never touched.");
        Assert.Null(await WithServiceAsync(factory, service => service.FindAsync(old.Id, CancellationToken.None)));
        Assert.Null(await WithServiceAsync(factory, service => service.FindAsync(oldSnapshot.Id, CancellationToken.None)));
        Assert.NotNull(await WithServiceAsync(factory, service => service.FindAsync(younger.Id, CancellationToken.None)));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_records;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM pending_file_deletions;"));
        Assert.Equal([liveArtwork], TestDatabase.Rows(factory.DataPath, "SELECT id FROM test_artwork;").Select(Guid.Parse));
        foreach (var (table, rows) in liveTables)
        {
            Assert.True(rows.SequenceEqual(Dump(factory, table)), $"The prune changed {table}.");
        }

        // A pruned group can no longer be restored.
        Assert.IsType<RetentionRestoreOutcome.NotFound>(await RestoreAsync(factory, old.Id));

        // A day later the younger group goes, and with it the file it was the last to list.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new RetentionPruneSummary(1, 1, 0, 0), await PruneAsync(factory));
        Assert.False(File.Exists(shared));
        Assert.True(File.Exists(live));
        Assert.True(File.Exists(unrelated));
        Assert.Equal("0|0", TestDatabase.Scalar(factory.DataPath, "SELECT (SELECT count(*) FROM retention_groups) || '|' || (SELECT count(*) FROM retention_records);"));
        Assert.Equal(liveTables["editor_revisions"], Dump(factory, "editor_revisions"));
    }

    [Fact]
    public async Task AFileThatCannotBeDeletedIsRetriedByTheNextRunWithoutScanningStorage()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (_, _, snapshots) = await VersionWithSnapshotsAsync(client, "Stubborn", "Words");

        // A folder where the file should be: deleting it as a file fails.
        var blocked = Path.Combine(factory.DataPath, "assets", "art", "blocked.png");
        Directory.CreateDirectory(blocked);
        await RetainAsync(factory, Snapshot(snapshots[0], "n8-1-v1", "art/blocked.png", "art/missing.png"));
        clock.Advance(TimeSpan.FromDays(30));

        Assert.Equal(new RetentionPruneSummary(1, 1, 0, 1), await PruneAsync(factory));
        Assert.True(Directory.Exists(blocked));
        Assert.Equal("art/blocked.png|1", TestDatabase.Scalar(factory.DataPath, "SELECT path || '|' || attempts FROM pending_file_deletions;"));

        // Still failing: counted again.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new RetentionPruneSummary(0, 0, 0, 1), await PruneAsync(factory));
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT attempts FROM pending_file_deletions;"));

        // Once it can be deleted, the next run does.
        Directory.Delete(blocked);
        File.WriteAllText(blocked, "image bytes");
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new RetentionPruneSummary(0, 1, 0, 0), await PruneAsync(factory));
        Assert.False(File.Exists(blocked));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM pending_file_deletions;"));
    }

    [Fact]
    public async Task APathThroughALinkIsNeverDeletedSoNothingOutsideTheAssetsFolderIs()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (_, _, snapshots) = await VersionWithSnapshotsAsync(client, "Linked", "Words");

        // assets/linked points at a folder in the media mount, which n8Tracks never writes (invariant 2).
        var outside = Path.Combine(factory.MediaPath, "song.mp3");
        File.WriteAllText(outside, "audio");
        Directory.CreateDirectory(Path.Combine(factory.DataPath, "assets"));
        Directory.CreateSymbolicLink(Path.Combine(factory.DataPath, "assets", "linked"), factory.MediaPath);
        await RetainAsync(factory, Snapshot(snapshots[0], "n8-1-v1", "linked/song.mp3"));
        clock.Advance(TimeSpan.FromDays(30));

        Assert.Equal(new RetentionPruneSummary(1, 0, 0, 1), await PruneAsync(factory));
        Assert.True(File.Exists(outside));
        Assert.Contains("link", TestDatabase.Scalar(factory.DataPath, "SELECT last_error FROM pending_file_deletions;"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePruneIsQueuedDailyAtFourOnceAfterMissedRunsAndNeverDuringABackup()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var zone = factory.Services.GetRequiredService<N8TracksOptions>().TimeZone;
        Assert.Equal(TimeZoneInfo.Utc, zone);

        // The first look arms it: 04:00 today (before the clock's 09:00) is not a missed run.
        Assert.Null(await TickAsync(factory));
        Assert.Contains("\"armedUtc\":\"2026-10-01T09:00:00.000Z\"", TestDatabase.Scalar(factory.DataPath, "SELECT value FROM settings WHERE key = 'retention.prune';"), StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromHours(18) + TimeSpan.FromMinutes(59));
        Assert.Null(await TickAsync(factory));

        // 04:00 the next day: queued once, however often it is looked at while it is queued or running.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("queued the retention prune", await TickAsync(factory));
        var first = Assert.Single(PruneJobs(factory));
        Assert.Null(await TickAsync(factory));
        await SucceededAsync(factory, first);
        Assert.Null(await TickAsync(factory));
        Assert.Contains("\"lastStartedUtc\":\"2026-10-02T04:00:00.000Z\"", TestDatabase.Scalar(factory.DataPath, "SELECT value FROM settings WHERE key = 'retention.prune';"), StringComparison.Ordinal);
        Assert.Equal(0, (await SucceededAsync(factory, first)).GetProperty("groupsPruned").GetInt32());

        // Three days missed (the server was down): one run on the first look.
        clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromHours(5));
        Assert.Equal("queued the retention prune", await TickAsync(factory));
        Assert.Null(await TickAsync(factory));
        Assert.Equal(2, PruneJobs(factory).Count);
        await SucceededAsync(factory, PruneJobs(factory)[1]);

        // A backup running at 04:00 holds it back until it ends.
        clock.Advance(TimeSpan.FromDays(1));
        var backup = Guid.CreateVersion7();
        TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO jobs (id, sequence, type, status, progress, created_utc, started_utc) VALUES ('{Upper(backup)}', 1000, '{BackupService.JobType}', 'running', 0, '2026-10-06T04:00:00.000Z', '2026-10-06T04:00:00.000Z');");
        Assert.Null(await TickAsync(factory));
        TestDatabase.Execute(factory.DataPath, $"UPDATE jobs SET status = 'succeeded', finished_utc = '2026-10-06T09:00:00.000Z' WHERE id = '{Upper(backup)}';");
        Assert.Equal("queued the retention prune", await TickAsync(factory));
        await SucceededAsync(factory, PruneJobs(factory)[2]);
    }

    [Fact]
    public async Task ALookWhileThePruneRunsNeverQueuesASecondOneHoweverTheRunEndsAroundIt()
    {
        // Regression: the task read the prune state, then looked for an active prune. A run that
        // started and finished between the two reads left the state read before its start, so the
        // prune looked due again and was queued twice (seen as an intermittent failure under load).
        // Here the run is held at its start, and the state read, if the task makes one, lets it finish.
        var clock = new TestClock();
        var gate = new PruneStartGate();
        using var factory = new N8TracksApiFactory
        {
            TestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.RemoveAll<IRetentionPruneStateStore>();
                services.AddScoped<RetentionPruneStateStore>();
                services.AddScoped<IRetentionPruneStateStore>(provider => new GatedPruneState(provider.GetRequiredService<RetentionPruneStateStore>(), gate));
            },
        };
        using var client = await SessionApi.SignedInClientAsync(factory);
        Assert.Null(await TickAsync(factory));
        clock.Advance(TimeSpan.FromHours(19));
        Assert.Equal("queued the retention prune", await TickAsync(factory));
        var job = Assert.Single(PruneJobs(factory));
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        gate.AfterRead = async () =>
        {
            gate.Release.TrySetResult();
            await SucceededAsync(factory, job);
        };

        Assert.Null(await TickAsync(factory));

        gate.AfterRead = null;
        gate.Release.TrySetResult();
        await SucceededAsync(factory, job);
        Assert.Null(await TickAsync(factory));
        Assert.Equal([job], PruneJobs(factory));
    }

    [Fact]
    public async Task TheQueuedJobPrunesWhatIsDue()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(client, "By the job", "Words", "More");
        Assert.Null(await TickAsync(factory));
        var group = await RetainAsync(factory, Snapshot(snapshots[0]));

        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal("queued the retention prune", await TickAsync(factory));
        var result = await SucceededAsync(factory, Assert.Single(PruneJobs(factory)));

        Assert.Equal(1, result.GetProperty("groupsPruned").GetInt32());
        Assert.Null(await WithServiceAsync(factory, service => service.FindAsync(group.Id, CancellationToken.None)));
        Assert.Equal(Upper(snapshots[1]), TestDatabase.Scalar(factory.DataPath, $"SELECT id FROM editor_revisions WHERE version_id = '{Upper(version)}';"));
    }

    [Fact]
    public void ADailyTaskIsDueOnceForTheLatestPlannedTimeAfterItLastStarted()
    {
        var zone = TimeZoneInfo.Utc;
        var four = new TimeOnly(4, 0);
        var since = DateTimeOffset.Parse("2026-10-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        Assert.False(DailyTaskRules.IsDue(four, zone, since, since.AddHours(18).AddMinutes(59)));
        Assert.True(DailyTaskRules.IsDue(four, zone, since, since.AddHours(19)));
        Assert.True(DailyTaskRules.IsDue(four, zone, since, since.AddDays(5)));
        Assert.False(DailyTaskRules.IsDue(four, zone, since.AddHours(19), since.AddHours(19).AddMinutes(30)));

        // In a zone with summer time, 04:00 local stays 04:00 local either side of the change.
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        Assert.Equal(DateTimeOffset.Parse("2026-10-24T03:00:00Z", System.Globalization.CultureInfo.InvariantCulture), DailyTaskRules.PlannedOn(new DateOnly(2026, 10, 24), four, london));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T04:00:00Z", System.Globalization.CultureInfo.InvariantCulture), DailyTaskRules.PlannedOn(new DateOnly(2026, 10, 25), four, london));
    }

    private static async Task<string?> TickAsync(N8TracksApiFactory factory)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope)
        {
            return await scope.ServiceProvider.GetServices<IDailyTask>().OfType<RetentionPruneTask>().Single().TickAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Waits until the job has succeeded and returns its result. Read from the database: a session
    /// would have expired over the weeks the test clock moves.
    /// </summary>
    private static async Task<JsonElement> SucceededAsync(N8TracksApiFactory factory, Guid job)
    {
        string Status() => TestDatabase.Scalar(factory.DataPath, $"SELECT status FROM jobs WHERE id = '{Upper(job)}';");
        await TestJobs.WaitUntilAsync(() => Status() is "succeeded" or "failed", $"the prune job {job}");
        Assert.Equal("succeeded", Status());
        using var result = JsonDocument.Parse(TestDatabase.Scalar(factory.DataPath, $"SELECT result FROM jobs WHERE id = '{Upper(job)}';"));
        return result.RootElement.Clone();
    }

    private static List<Guid> PruneJobs(N8TracksApiFactory factory) =>
        [.. TestDatabase.Rows(factory.DataPath, $"SELECT id FROM jobs WHERE type = '{RetentionPruneTask.JobType}' ORDER BY sequence;").Select(Guid.Parse)];

    /// <summary>Holds the prune job at its first state write until released, and runs a hook after each state read.</summary>
    private sealed class PruneStartGate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<Task>? AfterRead { get; set; }
    }

    /// <summary>The real prune state store, with <see cref="PruneStartGate"/> around its reads and writes.</summary>
    private sealed class GatedPruneState(IRetentionPruneStateStore inner, PruneStartGate gate) : IRetentionPruneStateStore
    {
        public async Task<RetentionPruneState?> FindAsync(CancellationToken cancellationToken)
        {
            var found = await inner.FindAsync(cancellationToken);
            if (gate.AfterRead is { } hook)
            {
                await hook();
            }

            return found;
        }

        public async Task WriteAsync(RetentionPruneState state, CancellationToken cancellationToken)
        {
            gate.Reached.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
            await inner.WriteAsync(state, cancellationToken);
        }

        public Task<bool> TryAddAsync(RetentionPruneState state, CancellationToken cancellationToken) => inner.TryAddAsync(state, cancellationToken);
    }
}
