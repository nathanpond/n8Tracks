using n8Tracks.Application.Auth;
using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>Whether the media folder can be read, as n8Tracks last saw it (#207).</summary>
public enum MediaMountState
{
    Available,
    Unavailable,
}

/// <summary>
/// The stored mount state: <see cref="State"/> since <see cref="SinceUtc"/> (null before anything was
/// ever recorded), and when a recovery scan was last queued, which spaces recovery scans out.
/// </summary>
public sealed record MediaMountStatus(MediaMountState State, DateTimeOffset? SinceUtc, DateTimeOffset? RecoveryQueuedUtc)
{
    /// <summary>Before any probe or scan has recorded a state: taken as available, since nothing says otherwise.</summary>
    public static MediaMountStatus Unrecorded { get; } = new(MediaMountState.Available, null, null);
}

/// <summary>
/// An audio file's status as it is reported: its stored status while the media folder is available,
/// and <see cref="Unavailable"/> for every file while it is not. Never stored.
/// </summary>
public enum AudioFileReportedStatus
{
    Available,
    Missing,
    Unavailable,
}

/// <summary>The text of the mount state and of a reported status, as the API writes them.</summary>
public static class MediaAvailabilityTexts
{
    public static string Text(MediaMountState state) => state switch
    {
        MediaMountState.Available => "available",
        MediaMountState.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown mount state."),
    };

    public static MediaMountState? ParseState(string? text) => text switch
    {
        "available" => MediaMountState.Available,
        "unavailable" => MediaMountState.Unavailable,
        _ => null,
    };

    public static string Text(AudioFileReportedStatus status) => status switch
    {
        AudioFileReportedStatus.Available => "available",
        AudioFileReportedStatus.Missing => "missing",
        AudioFileReportedStatus.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown reported status."),
    };

    /// <summary>The reported status <paramref name="text"/> names, or null for anything else.</summary>
    public static AudioFileReportedStatus? ParseReported(string? text) => text switch
    {
        "available" => AudioFileReportedStatus.Available,
        "missing" => AudioFileReportedStatus.Missing,
        "unavailable" => AudioFileReportedStatus.Unavailable,
        _ => null,
    };
}

/// <summary>How one probe of the media folder ended.</summary>
public enum MediaProbeResult
{
    /// <summary>The root is a directory and its first entry could be asked for.</summary>
    Readable,

    /// <summary>The root is not there, is not a directory, or could not be read.</summary>
    Unreadable,

    /// <summary>The probe did not answer within <see cref="IMediaFolderProbe.Deadline"/>.</summary>
    TimedOut,
}

/// <summary>A probe's result, with the exception when it failed by throwing (for the log only).</summary>
public readonly record struct MediaProbeOutcome(MediaProbeResult Result, Exception? Exception = null)
{
    public bool Readable => Result == MediaProbeResult.Readable;
}

/// <summary>
/// The one probe of the media folder (#207): the mount's own <see cref="IMediaMount.Probe"/>, given
/// <see cref="Deadline"/> to answer. Health, the availability monitor, the scheduler, and a scan about
/// to write Missing all ask it, so "readable" means one thing everywhere. A singleton: a probe stuck on
/// a dead mount is abandoned, and while it is, the next one answers timed out at once.
/// </summary>
public interface IMediaFolderProbe
{
    /// <summary>How long a probe may take (the 2 seconds the health check has always had).</summary>
    TimeSpan Deadline { get; }

    Task<MediaProbeOutcome> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Where the mount state is kept (a <c>settings</c> row).</summary>
public interface IMediaMountStateStore
{
    /// <summary>The stored state, or null when none was ever written.</summary>
    Task<MediaMountStatus?> FindAsync(CancellationToken cancellationToken);

    Task WriteAsync(MediaMountStatus status, CancellationToken cancellationToken);
}

/// <summary>
/// The media folder's state and the rule that derives what a file reports (#207). The state is one
/// stored row, <see cref="MediaMountState.Available"/> or <see cref="MediaMountState.Unavailable"/> and
/// since when, changed by scans and probes through <see cref="RecordAsync"/> and written only when it
/// changes. While it is unavailable every file reports <see cref="AudioFileReportedStatus.Unavailable"/>;
/// nothing is written per file, so a folder that comes back makes every file report its stored status
/// again at once.
/// </summary>
public sealed class MediaAvailability(IMediaFolderProbe probe, IMediaMountStateStore store, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The least time between two recovery scans.</summary>
    public static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(5);

    /// <summary>What a file with <paramref name="stored"/> status reports while the folder is <paramref name="mount"/>.</summary>
    public static AudioFileReportedStatus Reported(AudioFileStatus stored, MediaMountState mount) =>
        mount == MediaMountState.Unavailable
            ? AudioFileReportedStatus.Unavailable
            : stored switch
            {
                AudioFileStatus.Available => AudioFileReportedStatus.Available,
                AudioFileStatus.Missing => AudioFileReportedStatus.Missing,
                _ => throw new ArgumentOutOfRangeException(nameof(stored), stored, "Unknown audio file status."),
            };

    /// <summary>One probe of the media folder, within the probe's deadline.</summary>
    public Task<MediaProbeOutcome> ProbeAsync(CancellationToken cancellationToken) => probe.ProbeAsync(cancellationToken);

    /// <summary>The stored state, or <see cref="MediaMountStatus.Unrecorded"/>.</summary>
    public async Task<MediaMountStatus> CurrentAsync(CancellationToken cancellationToken) =>
        await store.FindAsync(cancellationToken).ConfigureAwait(false) ?? MediaMountStatus.Unrecorded;

    /// <summary>
    /// Records that the folder was just seen <paramref name="readable"/> or not. The row is written
    /// only when the state changes (or was never written); the state before and after are returned.
    /// </summary>
    public async Task<MediaMountChange> RecordAsync(bool readable, CancellationToken cancellationToken)
    {
        var state = readable ? MediaMountState.Available : MediaMountState.Unavailable;
        var stored = await store.FindAsync(cancellationToken).ConfigureAwait(false);
        if (stored is not null && stored.State == state)
        {
            return new MediaMountChange(stored, stored);
        }

        return await transaction.RunAsync(
            async token =>
            {
                var before = await store.FindAsync(token).ConfigureAwait(false);
                if (before is not null && before.State == state)
                {
                    return new MediaMountChange(before, before);
                }

                var after = new MediaMountStatus(state, time.GetUtcNow(), before?.RecoveryQueuedUtc);
                await store.WriteAsync(after, token).ConfigureAwait(false);
                return new MediaMountChange(before ?? MediaMountStatus.Unrecorded, after);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the right to queue a recovery scan now: true, and the time is recorded, unless one was
    /// queued less than <see cref="RecoveryInterval"/> ago.
    /// </summary>
    public Task<bool> TryClaimRecoveryAsync(CancellationToken cancellationToken) =>
        transaction.RunAsync(
            async token =>
            {
                var now = time.GetUtcNow();
                var current = await store.FindAsync(token).ConfigureAwait(false) ?? MediaMountStatus.Unrecorded;
                if (current.RecoveryQueuedUtc is { } last && now - last < RecoveryInterval)
                {
                    return false;
                }

                await store.WriteAsync(current with { RecoveryQueuedUtc = now }, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
}

/// <summary>The mount state before and after a <see cref="MediaAvailability.RecordAsync"/>.</summary>
public sealed record MediaMountChange(MediaMountStatus Before, MediaMountStatus After)
{
    /// <summary>The folder was unavailable and is available now.</summary>
    public bool BecameAvailable => Before.State == MediaMountState.Unavailable && After.State == MediaMountState.Available;
}
