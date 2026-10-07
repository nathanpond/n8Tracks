using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Setup;

namespace n8Tracks.Application.Media;

/// <summary>
/// How many probes in a row the availability monitor has seen fail. A singleton: the count lives as
/// long as the process, and a readable probe from anywhere resets it.
/// </summary>
public sealed class MediaProbeStreak
{
    private int failures;

    /// <summary>Counts one more failed probe and returns how many there have been in a row.</summary>
    public int Fail() => Interlocked.Increment(ref failures);

    public void Reset() => Volatile.Write(ref failures, 0);
}

/// <summary>What one look of the availability monitor did.</summary>
public enum MediaRecoveryAction
{
    /// <summary>Nothing changed.</summary>
    None,

    /// <summary>The folder became unavailable.</summary>
    Unavailable,

    /// <summary>The folder became available again, and no recovery scan was queued: one was queued less than five minutes ago.</summary>
    Available,

    /// <summary>The folder became available again and a recovery scan was queued.</summary>
    Recovered,
}

/// <summary>
/// Brings the media library back when the folder comes back (#207). The health check reports what
/// its own probe saw through <see cref="ObserveAsync"/>, which changes the state at once either way;
/// the availability monitor looks every 60 seconds through <see cref="CheckAsync"/>, where one readable
/// probe makes the folder available but two failed ones in a row are needed to make it unavailable.
/// When the folder becomes available again, a recovery scan is queued through
/// <see cref="MediaScanService.StartAsync"/> (so a scan already queued or running is taken instead), at
/// most once every five minutes. Nothing is probed or recorded here during maintenance or before setup is complete.
/// </summary>
public sealed class MediaRecoveryService(
    MediaAvailability availability,
    MediaScanService scans,
    MediaProbeStreak streak,
    SetupService setup,
    MaintenanceMode maintenance)
{
    /// <summary>Failed probes in a row after which the monitor makes the folder unavailable.</summary>
    public const int FailuresToUnavailable = 2;

    /// <summary>One look of the monitor. Nothing is probed during maintenance or before setup is complete.</summary>
    public async Task<MediaRecoveryAction> CheckAsync(CancellationToken cancellationToken)
    {
        if (maintenance.IsActive || !await setup.IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return MediaRecoveryAction.None;
        }

        var outcome = await availability.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!outcome.Readable && streak.Fail() < FailuresToUnavailable)
        {
            return MediaRecoveryAction.None;
        }

        return await RecordAsync(outcome.Readable, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records what a probe saw, and queues a recovery scan when the folder has just come back. Nothing
    /// is recorded during maintenance or before setup is complete.
    /// </summary>
    public async Task<MediaRecoveryAction> ObserveAsync(bool readable, CancellationToken cancellationToken)
    {
        if (maintenance.IsActive || !await setup.IsCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            return MediaRecoveryAction.None;
        }

        return await RecordAsync(readable, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MediaRecoveryAction> RecordAsync(bool readable, CancellationToken cancellationToken)
    {
        if (readable)
        {
            streak.Reset();
        }

        var change = await availability.RecordAsync(readable, cancellationToken).ConfigureAwait(false);
        if (!change.BecameAvailable)
        {
            return change.Before.State == MediaMountState.Available && change.After.State == MediaMountState.Unavailable
                ? MediaRecoveryAction.Unavailable
                : MediaRecoveryAction.None;
        }

        if (!await availability.TryClaimRecoveryAsync(cancellationToken).ConfigureAwait(false))
        {
            return MediaRecoveryAction.Available;
        }

        _ = await scans.StartAsync(MediaScanTrigger.Recovery, cancellationToken).ConfigureAwait(false);
        return MediaRecoveryAction.Recovered;
    }
}
