using n8Tracks.Application.Backups;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Scheduling;
using n8Tracks.Application.Setup;

namespace n8Tracks.Application.Assets;

/// <summary>When the artwork sweep last ran in this process (held for the process's life).</summary>
public sealed class ArtworkSweepSchedule
{
    private long lastRunTicks = long.MinValue;

    /// <summary>The last run's start, or null when there has been none since the server started.</summary>
    public DateTimeOffset? LastRunUtc
    {
        get => Interlocked.Read(ref lastRunTicks) is var ticks && ticks == long.MinValue ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        set => Interlocked.Exchange(ref lastRunTicks, value?.UtcTicks ?? long.MinValue);
    }
}

/// <summary>
/// The unattached-upload sweep, as one of the shared scheduler's tasks: it runs on the first look
/// after a start and then every <see cref="Interval"/>, in the request scope the scheduler gives it.
/// It waits while maintenance (a restore) is active, before setup, and while a backup (which reads the
/// managed-assets folder) or the retention prune is queued or running.
/// </summary>
public sealed class ArtworkSweepTask(
    ArtworkService artwork,
    ArtworkSweepSchedule schedule,
    IJobStore jobs,
    SetupService setup,
    MaintenanceMode maintenance,
    TimeProvider time) : IDailyTask
{
    /// <summary>How often the sweep runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public string Name => "artwork sweep";

    public async Task<string?> TickAsync(CancellationToken cancellationToken)
    {
        if (maintenance.IsActive || !await setup.IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (schedule.LastRunUtc is { } last && now - last < Interval
            || await jobs.FindActiveAsync(BackupService.JobType, cancellationToken).ConfigureAwait(false) is not null
            || await jobs.FindActiveAsync(RetentionPruneTask.JobType, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        schedule.LastRunUtc = now;
        var summary = await artwork.SweepAsync(cancellationToken).ConfigureAwait(false);
        return summary switch
        {
            { Removed: 0, Failed: 0 } => null,
            { Failed: 0 } => $"removed {summary.Removed} unattached artwork uploads",
            _ => $"removed {summary.Removed} unattached artwork uploads; {summary.Failed} could not be removed and are tried again next time",
        };
    }
}

/// <summary>
/// The artwork side of the retention prune: a file in an asset's folder is still used while the asset
/// is live (attached, or a fresh upload), so a pruned group's artwork files go only when nothing live
/// needs them.
/// </summary>
public sealed class ArtworkFileReferences(IAssetStore assets, ArtworkService artwork) : ILiveFileReferences
{
    public async Task<bool> IsReferencedAsync(string path, CancellationToken cancellationToken) =>
        ArtworkPaths.ContentHashOf(path) is { } hash
        && await assets.FindByHashAsync(hash, cancellationToken).ConfigureAwait(false) is { } asset
        && await artwork.IsLiveAsync(asset, cancellationToken).ConfigureAwait(false);
}
