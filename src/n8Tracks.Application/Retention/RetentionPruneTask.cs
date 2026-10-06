using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Scheduling;
using n8Tracks.Application.Setup;

namespace n8Tracks.Application.Retention;

/// <summary>
/// The daily retention prune, as one of the shared scheduler's tasks: at <see cref="PlannedTime"/> in
/// the configured time zone it queues a <see cref="JobType"/> job, and on the first look after a start
/// that missed a planned time it queues one at once (however many were missed, that is one run). It
/// waits while a backup or a restore is running, and never queues a second prune. The first look ever
/// arms it, so an instance's first prune is at the next planned time.
/// </summary>
public sealed class RetentionPruneTask(
    IRetentionPruneStateStore state,
    IJobStore jobs,
    IJobQueue queue,
    SetupService setup,
    MaintenanceMode maintenance,
    N8TracksOptions options,
    TimeProvider time) : IDailyTask
{
    /// <summary>The job type the prune runs as.</summary>
    public const string JobType = "retention-prune";

    /// <summary>When the prune runs each day, in the configured time zone.</summary>
    public static readonly TimeOnly PlannedTime = new(4, 0);

    public string Name => "retention prune";

    public async Task<string?> TickAsync(CancellationToken cancellationToken)
    {
        // During maintenance (a restore) the database is not touched; a prune that comes due waits.
        if (maintenance.IsActive || !await setup.IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (await state.FindAsync(cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            await state.TryAddAsync(new RetentionPruneState(now, null, null), cancellationToken).ConfigureAwait(false);
            return null;
        }

        var since = stored.LastStartedUtc is { } started && started > stored.ArmedUtc ? started : stored.ArmedUtc;
        if (!DailyTaskRules.IsDue(PlannedTime, options.TimeZone, since, now)
            || await jobs.FindActiveAsync(JobType, cancellationToken).ConfigureAwait(false) is not null
            || await jobs.FindActiveAsync(BackupService.JobType, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        await queue.EnqueueAsync(JobType, null, cancellationToken).ConfigureAwait(false);
        return "queued the retention prune";
    }
}
