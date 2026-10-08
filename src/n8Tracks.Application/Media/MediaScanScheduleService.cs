using n8Tracks.Application.Auth;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Setup;

namespace n8Tracks.Application.Media;

/// <summary>
/// Whether this process has queued its startup scan yet. A singleton: the startup scan is queued
/// once per start, by the scheduler's first look that may queue anything.
/// </summary>
public sealed class MediaScanStartup
{
    private int queued;

    /// <summary>Whether the startup scan has been queued (or one already queued was taken as it).</summary>
    public bool Queued => Volatile.Read(ref queued) == 1;

    /// <summary>Records that the startup scan has been queued.</summary>
    public void MarkQueued() => Volatile.Write(ref queued, 1);
}

/// <summary>
/// Scans nobody asked for (#204): the schedule the administrator sets in Settings → Library, and
/// the look the scheduler takes every 30 seconds, which queues the startup scan once and then a
/// scheduled scan whenever one is due. Every scan is queued through
/// <see cref="MediaScanService.StartAsync"/>, the method Scan Library calls, so the one-scan-at-a-time
/// rule is in one place. Nothing here watches the file system: a file added is found by the next
/// scan and by no other means.
/// </summary>
public sealed class MediaScanScheduleService(
    IMediaScanScheduleStore store,
    MediaScanService scans,
    IJobStore jobs,
    SetupService setup,
    MaintenanceMode maintenance,
    MediaScanStartup startup,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The stored schedule, or the defaults at revision 0 when it was never saved.</summary>
    public async Task<StoredMediaScanSchedule> GetAsync(CancellationToken cancellationToken) =>
        await store.FindAsync(cancellationToken).ConfigureAwait(false) ?? StoredMediaScanSchedule.Unstored;

    /// <summary>
    /// Replaces the schedule when <paramref name="revision"/> is the current one (0 before the first
    /// save). The next look uses it: no restart is needed.
    /// </summary>
    public Task<MediaScanScheduleUpdate> UpdateAsync(MediaScanSchedule schedule, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return transaction.RunAsync<MediaScanScheduleUpdate>(
            async token =>
            {
                var current = await GetAsync(token).ConfigureAwait(false);
                if (current.Revision != revision)
                {
                    return new MediaScanScheduleUpdate.Stale(current);
                }

                var updated = new StoredMediaScanSchedule(schedule, current.Revision + 1);
                await store.WriteAsync(updated, token).ConfigureAwait(false);
                return new MediaScanScheduleUpdate.Updated(updated);
            },
            cancellationToken);
    }

    /// <summary>
    /// One look. Nothing is queued during maintenance or before first-run setup is complete; a scan
    /// held back that way is queued at the first look after. The first look that may queue anything
    /// queues the startup scan, whatever the schedule and even when the media folder is unavailable
    /// (the scan then fails fast); a <c>media-scan</c> job already queued, from before a restart say,
    /// counts as it. Every later look queues a scheduled scan when the schedule is on, no scan is
    /// queued or running (a due scan is skipped, not queued behind it), the interval has passed since
    /// the latest scan ended (read from the stored summary, not memory), and the media folder can be
    /// listed.
    /// </summary>
    public async Task<MediaScanScheduleAction> TickAsync(CancellationToken cancellationToken)
    {
        if (maintenance.IsActive || !await setup.IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return MediaScanScheduleAction.None;
        }

        if (!startup.Queued)
        {
            _ = await scans.StartAsync(MediaScanTrigger.Startup, cancellationToken).ConfigureAwait(false);
            startup.MarkQueued();
            return MediaScanScheduleAction.Startup;
        }

        var stored = await GetAsync(cancellationToken).ConfigureAwait(false);
        if (!stored.Schedule.Enabled
            || await jobs.FindActiveAsync(MediaScanService.JobType, cancellationToken).ConfigureAwait(false) is not null)
        {
            return MediaScanScheduleAction.None;
        }

        var last = await scans.LastScanAsync(cancellationToken).ConfigureAwait(false);
        if (!MediaScanScheduleRules.IsDue(stored.Schedule, last, time.GetUtcNow())
            || !await scans.IsFolderAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return MediaScanScheduleAction.None;
        }

        var start = await scans.StartAsync(MediaScanTrigger.Scheduled, cancellationToken).ConfigureAwait(false);
        return start.AlreadyInProgress ? MediaScanScheduleAction.None : MediaScanScheduleAction.Scheduled;
    }
}
