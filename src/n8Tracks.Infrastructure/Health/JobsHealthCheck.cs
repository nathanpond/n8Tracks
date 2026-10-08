using n8Tracks.Application.Health;
using n8Tracks.Infrastructure.Jobs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Health;

/// <summary>How far the database may be used by a check on this health call.</summary>
internal enum DatabaseAccess
{
    /// <summary>The database answered: a check may read it.</summary>
    Reachable,

    /// <summary>The database did not answer: a check does not try.</summary>
    Unreachable,

    /// <summary>A restore may be replacing the database: nothing opens it.</summary>
    Maintenance,
}

/// <summary>
/// The <c>jobs</c> component: whether the background job worker is alive and the queue is moving. The
/// worker's liveness comes from its <see cref="JobWorkerHeartbeat"/>, without the database. A job in
/// progress is healthy however long it takes. Only when the worker is idle is the queue read, under
/// the health deadline: a queued job that has waited longer than <see cref="StallAfter"/> means the
/// worker is not taking work. Its wait counts from the later of when it was queued and when the
/// worker last became idle, so a job that queued behind a long one is not counted as stalled. The
/// component carries a status and a fixed one-word detail, never a job's type, payload, or error.
/// </summary>
internal sealed class JobsHealthCheck
{
    /// <summary>How long a queued job may wait, with nothing running, before the queue counts as stalled.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(10);

    private static readonly HealthComponent Starting = new(HealthStatus.Healthy, HealthDetails.JobsStarting);
    private static readonly HealthComponent Idle = new(HealthStatus.Healthy, HealthDetails.JobsIdle);
    private static readonly HealthComponent Running = new(HealthStatus.Healthy, HealthDetails.JobsRunning);
    private static readonly HealthComponent Stalled = new(HealthStatus.Degraded, HealthDetails.JobsStalled);
    private static readonly HealthComponent Unknown = new(HealthStatus.Degraded, HealthDetails.JobsUnknown);
    private static readonly HealthComponent Stopping = new(HealthStatus.Healthy, HealthDetails.JobsStopping);
    private static readonly HealthComponent Stopped = new(HealthStatus.Unhealthy, HealthDetails.JobsStopped);

    private static readonly CheckOutcome Passed = new(CheckResult.Passed);
    private static readonly CheckOutcome Failed = new(CheckResult.Failed);

    private readonly JobWorkerHeartbeat heartbeat;
    private readonly JobWorkerOptions options;
    private readonly IDatabaseConnectionFactory connections;
    private readonly TimeProvider time;
    private readonly DeadlineCheck queueCheck;

    private readonly Lock gate = new();
    private DateTimeOffset? oldestQueued;

    public JobsHealthCheck(JobWorkerHeartbeat heartbeat, JobWorkerOptions options, IDatabaseConnectionFactory connections, TimeProvider time)
    {
        this.heartbeat = heartbeat;
        this.options = options;
        this.connections = connections;
        this.time = time;
        queueCheck = new DeadlineCheck(ReadOldestQueued, HealthService.CheckTimeout);
    }

    /// <summary>The component, and how its check went, for the health log.</summary>
    public async Task<(HealthComponent Component, CheckOutcome Outcome)> CheckAsync(DatabaseAccess database, CancellationToken cancellationToken)
    {
        var pulse = heartbeat.Read();
        switch (pulse.Phase)
        {
            case JobWorkerPhase.Exited:
                return (Stopped, Failed);
            case JobWorkerPhase.Stopping:
                return (Stopping, Passed);
        }

        if (pulse.SinceBeat is not { } sinceBeat)
        {
            return pulse.SinceStart <= options.FirstHeartbeatWithin ? (Starting, Passed) : (Stopped, Failed);
        }

        if (sinceBeat > options.HeartbeatLostAfter)
        {
            return (Stalled, Failed);
        }

        if (pulse.Busy)
        {
            return (Running, Passed);
        }

        switch (database)
        {
            case DatabaseAccess.Maintenance:
                // Nothing is claimed during maintenance by design, and the database is not opened.
                return (Idle, Passed);
            case DatabaseAccess.Unreachable:
                return (Unknown, Failed);
        }

        var outcome = await queueCheck.RunAsync(cancellationToken).ConfigureAwait(false);
        if (outcome.Result != CheckResult.Passed)
        {
            return (Unknown, outcome);
        }

        DateTimeOffset? queued;
        lock (gate)
        {
            queued = oldestQueued;
        }

        if (queued is { } since && time.GetUtcNow() - Max(since, pulse.IdleSinceUtc) > StallAfter)
        {
            return (Stalled, outcome);
        }

        return (Idle, outcome);
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    /// <summary>When the oldest queued job was queued, into <see cref="oldestQueued"/>; null when nothing is queued.</summary>
    private bool ReadOldestQueued(CancellationToken deadline)
    {
        using var connection = connections.CreateForExistingDatabase();
        deadline.ThrowIfCancellationRequested();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT min(created_utc) FROM jobs WHERE status = '{JobRecord.Queued}';";
        command.CommandTimeout = (int)HealthService.CheckTimeout.TotalSeconds;
        deadline.ThrowIfCancellationRequested();

        var found = command.ExecuteScalar() is string text ? UtcText.Parse(text) : (DateTimeOffset?)null;
        lock (gate)
        {
            oldestQueued = found;
        }

        return true;
    }
}
