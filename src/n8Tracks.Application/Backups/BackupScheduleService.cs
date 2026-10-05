using n8Tracks.Application.Auth;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Setup;

namespace n8Tracks.Application.Backups;

/// <summary>When this process started, as the scheduler counts it: a pending retry from before it is dropped.</summary>
public sealed class BackupScheduleProcess(TimeProvider time)
{
    public DateTimeOffset StartedUtc { get; } = time.GetUtcNow();
}

/// <summary>How a change of the schedule ended.</summary>
public abstract record BackupScheduleUpdate
{
    private BackupScheduleUpdate()
    {
    }

    /// <summary>Written; this is the schedule now.</summary>
    public sealed record Updated(StoredBackupSchedule Current) : BackupScheduleUpdate;

    /// <summary>The revision sent is not the current one; nothing was written.</summary>
    public sealed record Stale(StoredBackupSchedule Current) : BackupScheduleUpdate;
}

/// <summary>
/// Scheduled backups: the schedule the administrator sets, what the Backups page shows of it, the
/// once-a-minute look that queues a scheduled backup when one is due, and retention after one
/// succeeds. Times are planned in the configured time zone.
/// </summary>
public sealed class BackupScheduleService(
    IBackupScheduleStore store,
    IJobStore jobs,
    IBackupStorage storage,
    BackupService backups,
    SetupService setup,
    IExclusiveTransaction transaction,
    N8TracksOptions options,
    BackupScheduleProcess process,
    TimeProvider time)
{
    /// <summary>The stored schedule, or the defaults at revision 1 for an instance that has none.</summary>
    public async Task<StoredBackupSchedule> GetAsync(CancellationToken cancellationToken) =>
        await store.FindAsync(cancellationToken).ConfigureAwait(false) ?? StoredBackupSchedule.Unstored;

    /// <summary>
    /// Replaces the schedule when <paramref name="revision"/> is the current one. Switching scheduling
    /// on arms it now, so nothing runs before the next planned time; any change drops a pending retry.
    /// </summary>
    public Task<BackupScheduleUpdate> UpdateAsync(BackupSchedule schedule, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return transaction.RunAsync<BackupScheduleUpdate>(
            async token =>
            {
                var current = await GetAsync(token).ConfigureAwait(false);
                if (current.Revision != revision)
                {
                    return new BackupScheduleUpdate.Stale(current);
                }

                var now = time.GetUtcNow();
                var armed = current.ArmedUtc is { } since && (current.Schedule.Enabled || !schedule.Enabled) ? since : now;
                var updated = new StoredBackupSchedule(schedule, current.Revision + 1, armed, now);
                await store.WriteAsync(updated, token).ConfigureAwait(false);
                return new BackupScheduleUpdate.Updated(updated);
            },
            cancellationToken);
    }

    /// <summary>The schedule, the next planned time, and the latest attempt with its outcome as it stands now.</summary>
    public async Task<BackupScheduleStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var stored = await GetAsync(cancellationToken).ConfigureAwait(false);
        var attempt = await CurrentAttemptAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();

        return new BackupScheduleStatus(
            stored,
            stored.Schedule.Enabled ? BackupScheduleRules.Next(stored.Schedule, options.TimeZone, now) : null,
            attempt,
            BackupScheduleRules.RetryAt(stored, attempt, process.StartedUtc));
    }

    /// <summary>
    /// One look at the schedule, which the scheduler makes once a minute. Before setup is complete it
    /// does nothing. An instance with no stored schedule gets the defaults, armed now. While any
    /// backup is queued or running it waits. Otherwise it records how the last attempt ended and, when
    /// a backup is due, queues the same <c>backup</c> job "Back up now" does, of kind scheduled.
    /// </summary>
    public async Task<BackupScheduleAction> TickAsync(CancellationToken cancellationToken)
    {
        if (!await setup.IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return BackupScheduleAction.None;
        }

        var now = time.GetUtcNow();
        if (await store.FindAsync(cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            await store.TryAddAsync(StoredBackupSchedule.Unstored with { ArmedUtc = now, ChangedUtc = now }, cancellationToken).ConfigureAwait(false);
            return BackupScheduleAction.None;
        }

        if (await jobs.FindActiveAsync(BackupService.JobType, cancellationToken).ConfigureAwait(false) is not null)
        {
            return BackupScheduleAction.None;
        }

        var recorded = await store.FindAttemptAsync(cancellationToken).ConfigureAwait(false);
        var attempt = await ReconcileAsync(recorded, cancellationToken).ConfigureAwait(false);
        if (attempt is not null && attempt != recorded)
        {
            await store.WriteAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        var action = BackupScheduleRules.Decide(stored, attempt, options.TimeZone, now, process.StartedUtc);
        if (action == BackupScheduleAction.None)
        {
            return action;
        }

        var start = await backups.StartAsync(BackupKind.Scheduled, action == BackupScheduleAction.Retry, cancellationToken).ConfigureAwait(false);
        return start.AlreadyInProgress ? BackupScheduleAction.None : action;
    }

    /// <summary>Records that the scheduled backup job <paramref name="jobId"/> has started: the latest attempt.</summary>
    public Task RecordStartAsync(Guid jobId, bool retry, CancellationToken cancellationToken) =>
        store.WriteAttemptAsync(
            new BackupAttempt(jobId, time.GetUtcNow(), retry, BackupAttemptOutcome.Running, null, null),
            cancellationToken);

    /// <summary>
    /// After a successful scheduled backup: deletes the scheduled backups beyond the number kept now,
    /// oldest first. Returns how many were deleted.
    /// </summary>
    public async Task<int> ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        var stored = await GetAsync(cancellationToken).ConfigureAwait(false);
        var archives = await storage.ListAsync(cancellationToken).ConfigureAwait(false);

        return storage.DeleteForRetention(BackupRetention.Select(archives, stored.Schedule.Keep));
    }

    private async Task<BackupAttempt?> CurrentAttemptAsync(CancellationToken cancellationToken) =>
        await ReconcileAsync(await store.FindAttemptAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<BackupAttempt?> ReconcileAsync(BackupAttempt? attempt, CancellationToken cancellationToken) =>
        attempt is { Outcome: BackupAttemptOutcome.Running }
            ? BackupScheduleRules.Reconcile(attempt, await jobs.FindAsync(attempt.JobId, cancellationToken).ConfigureAwait(false))
            : attempt;
}
