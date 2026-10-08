using System.Data.Common;
using Microsoft.Data.Sqlite;
using n8Tracks.Application.Health;
using n8Tracks.Infrastructure.Health;
using n8Tracks.Infrastructure.Jobs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Health;

/// <summary>
/// The rules of the <c>jobs</c> component (#237), on a heartbeat driven by hand and a clock moved by
/// hand: no worker, no host. The queue is a <c>jobs</c> table in a file of its own.
/// </summary>
public sealed class JobsHealthCheckTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualClock clock = new(Start);
    private readonly TemporaryDirectory directory = new();
    private readonly QueueDatabase queue;
    private readonly JobWorkerHeartbeat heartbeat;
    private readonly JobsHealthCheck check;

    public JobsHealthCheckTests()
    {
        queue = new QueueDatabase(Path.Combine(directory.Path, "queue.db"));
        heartbeat = new JobWorkerHeartbeat(clock);
        check = new JobsHealthCheck(heartbeat, new JobWorkerOptions(), queue, clock);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        directory.Dispose();
    }

    [Fact]
    public async Task BeforeTheFirstBeatItIsStartingForUpToAMinuteThenStopped()
    {
        heartbeat.Started();

        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsStarting);

        clock.Advance(TimeSpan.FromSeconds(60));
        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsStarting);

        clock.Advance(TimeSpan.FromSeconds(1));
        await AssertJobs(HealthStatus.Unhealthy, HealthDetails.JobsStopped);
        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public async Task ABeatOlderThanThirtySecondsIsStalledEvenWhileAJobRuns()
    {
        heartbeat.Started();
        heartbeat.Busy();

        clock.Advance(TimeSpan.FromSeconds(30));
        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsRunning);

        clock.Advance(TimeSpan.FromSeconds(1));
        await AssertJobs(HealthStatus.Degraded, HealthDetails.JobsStalled);

        heartbeat.Beat();
        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsRunning);
    }

    [Fact]
    public async Task AJobInProgressIsHealthyHoweverLongAndTheQueueIsNotRead()
    {
        heartbeat.Started();
        queue.Add(Start, "queued");
        heartbeat.Busy();

        for (var hour = 0; hour < 5; hour++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            heartbeat.Beat();
            await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsRunning);
        }

        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public async Task AQueuedJobIsStalledAfterTenMinutesWithNothingRunning()
    {
        heartbeat.Started();
        heartbeat.Beat();
        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsIdle);

        queue.Add(Start, "queued");
        queue.Add(Start - TimeSpan.FromDays(1), "succeeded");

        clock.Advance(TimeSpan.FromMinutes(10));
        heartbeat.Beat();
        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsIdle);

        clock.Advance(TimeSpan.FromSeconds(1));
        heartbeat.Beat();
        await AssertJobs(HealthStatus.Degraded, HealthDetails.JobsStalled);
    }

    [Fact]
    public async Task TheWaitCountsFromTheLaterOfQueuedAndLastIdle()
    {
        heartbeat.Started();
        heartbeat.Busy();
        queue.Add(Start, "queued");

        // The job ahead ran for 30 minutes; the queued one waited behind it.
        clock.Advance(TimeSpan.FromMinutes(30));
        heartbeat.Idle();
        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsIdle);

        clock.Advance(TimeSpan.FromMinutes(10));
        heartbeat.Beat();
        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsIdle);

        clock.Advance(TimeSpan.FromSeconds(1));
        heartbeat.Beat();
        await AssertJobs(HealthStatus.Degraded, HealthDetails.JobsStalled);
    }

    [Fact]
    public async Task AQueueThatCannotBeReadIsUnknownAndDegraded()
    {
        heartbeat.Started();
        heartbeat.Beat();
        queue.Fault = new InvalidOperationException("cannot open");

        var (component, outcome) = await check.CheckAsync(DatabaseAccess.Reachable, CancellationToken.None);

        Assert.Equal(new HealthComponent(HealthStatus.Degraded, HealthDetails.JobsUnknown), component);
        Assert.Equal(CheckResult.Failed, outcome.Result);
        Assert.Equal(1, queue.Calls);
    }

    [Fact]
    public async Task AnUnreachableDatabaseIsUnknownWithoutTryingAndMaintenanceIsIdleWithoutOpeningIt()
    {
        heartbeat.Started();
        heartbeat.Beat();
        queue.Add(Start - TimeSpan.FromHours(1), "queued");

        await AssertJobs(HealthStatus.Degraded, HealthDetails.JobsUnknown, DatabaseAccess.Unreachable);

        // Nothing is claimed during maintenance by design, so a waiting queue is not a stall.
        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsIdle, DatabaseAccess.Maintenance);

        Assert.Equal(0, queue.Calls);
    }

    [Fact]
    public async Task StoppingIsHealthyAndAnExitedWorkerIsStoppedWhateverItsLastBeat()
    {
        heartbeat.Started();
        heartbeat.Beat();

        heartbeat.Stopping();
        await AssertJobs(HealthStatus.Healthy, HealthDetails.JobsStopping);

        heartbeat.Exited();
        heartbeat.Beat();
        await AssertJobs(HealthStatus.Unhealthy, HealthDetails.JobsStopped);
    }

    [Fact]
    public async Task AWorkerThatGaveUpIsStopped()
    {
        heartbeat.Started();
        heartbeat.Beat();
        heartbeat.Exited();

        await AssertJobs(HealthStatus.Unhealthy, HealthDetails.JobsStopped);
    }

    private async Task AssertJobs(HealthStatus status, string detail, DatabaseAccess access = DatabaseAccess.Reachable)
    {
        var (component, _) = await check.CheckAsync(access, CancellationToken.None);

        Assert.Equal(new HealthComponent(status, detail), component);
    }

    /// <summary>Wall clock and monotonic clock, both moved by hand together.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        private long ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => ticks;

        public void Advance(TimeSpan by)
        {
            now += by;
            ticks += by.Ticks;
        }
    }

    /// <summary>A <c>jobs</c> table with the two columns the check reads, behind the connection factory.</summary>
    private sealed class QueueDatabase : IDatabaseConnectionFactory
    {
        private readonly string connectionString;

        public QueueDatabase(string file)
        {
            connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString();
            Execute("CREATE TABLE jobs (status TEXT NOT NULL, created_utc TEXT NOT NULL);");
        }

        public Exception? Fault { get; set; }

        public int Calls { get; private set; }

        public void Add(DateTimeOffset created, string status) =>
            Execute($"INSERT INTO jobs VALUES ('{status}', '{UtcText.From(created)}');");

        public DbConnection CreateForExistingDatabase()
        {
            Calls++;
            return Fault is { } fault ? throw fault : new SqliteConnection(connectionString);
        }

        private void Execute(string sql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
