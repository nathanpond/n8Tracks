using System.Globalization;
using System.Text.Json;

namespace n8Tracks.Application.Media;

/// <summary>
/// How often the media folder is scanned without being asked (#204): on or off, and the interval in
/// minutes, counted from the end of the latest scan of any kind. Turning the schedule off keeps the
/// interval. The startup scan and Scan Library do not depend on it.
/// </summary>
/// <param name="Enabled">Whether scheduled scans run.</param>
/// <param name="IntervalMinutes"><see cref="MinimumInterval"/> to <see cref="MaximumInterval"/>.</param>
public sealed record MediaScanSchedule(bool Enabled, int IntervalMinutes)
{
    public const int MinimumInterval = 1;
    public const int MaximumInterval = 1440;

    /// <summary>On, every 15 minutes: what every instance has until the administrator changes it.</summary>
    public static readonly MediaScanSchedule Default = new(true, 15);

    public const string EnabledField = "enabled";
    public const string IntervalField = "intervalMinutes";

    /// <summary>The interval as a span.</summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    /// <summary>
    /// Reads a schedule as a client sends it: <paramref name="enabled"/> must be true or false and
    /// <paramref name="intervalMinutes"/> a whole number in range, even when the schedule is off.
    /// A missing field arrives as <see cref="JsonValueKind.Undefined"/>. The errors are keyed by field
    /// name; empty when valid.
    /// </summary>
    public static (MediaScanSchedule? Schedule, Dictionary<string, string[]> Errors) Parse(JsonElement enabled, JsonElement intervalMinutes)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        bool on = false;
        switch (enabled.ValueKind)
        {
            case JsonValueKind.True:
                on = true;
                break;
            case JsonValueKind.False:
                break;
            default:
                errors[EnabledField] = ["Say whether scheduled scans are on: true or false."];
                break;
        }

        if (intervalMinutes.ValueKind != JsonValueKind.Number
            || !intervalMinutes.TryGetInt32(out var interval)
            || interval < MinimumInterval
            || interval > MaximumInterval)
        {
            errors[IntervalField] = [string.Create(
                CultureInfo.InvariantCulture,
                $"Enter a whole number of minutes from {MinimumInterval} to {MaximumInterval:N0}.")];
            interval = 0;
        }

        return errors.Count > 0 ? (null, errors) : (new MediaScanSchedule(on, interval), errors);
    }
}

/// <summary>The schedule as it is stored.</summary>
/// <param name="Revision">0 until it is first saved, then 1, 2, ….</param>
public sealed record StoredMediaScanSchedule(MediaScanSchedule Schedule, int Revision)
{
    /// <summary>What an instance that never saved the schedule has: the defaults, at revision 0.</summary>
    public static readonly StoredMediaScanSchedule Unstored = new(MediaScanSchedule.Default, 0);
}

/// <summary>Where the schedule is kept.</summary>
public interface IMediaScanScheduleStore
{
    /// <summary>The stored schedule, or null when it was never saved.</summary>
    Task<StoredMediaScanSchedule?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the stored schedule.</summary>
    Task WriteAsync(StoredMediaScanSchedule schedule, CancellationToken cancellationToken);
}

/// <summary>How a change of the schedule ended.</summary>
public abstract record MediaScanScheduleUpdate
{
    private MediaScanScheduleUpdate()
    {
    }

    /// <summary>Written; this is the schedule now.</summary>
    public sealed record Updated(StoredMediaScanSchedule Current) : MediaScanScheduleUpdate;

    /// <summary>The revision sent is not the current one; nothing was written.</summary>
    public sealed record Stale(StoredMediaScanSchedule Current) : MediaScanScheduleUpdate;
}

/// <summary>What one look of the scheduler did.</summary>
public enum MediaScanScheduleAction
{
    /// <summary>Nothing: not due, a scan is queued or running, the schedule is off, the folder is unavailable, setup is not complete, or the instance is in maintenance.</summary>
    None,

    /// <summary>The startup scan was queued, or one already queued was taken as it.</summary>
    Startup,

    /// <summary>A scheduled scan was queued.</summary>
    Scheduled,
}

/// <summary>When a scheduled scan is due. Pure.</summary>
public static class MediaScanScheduleRules
{
    /// <summary>
    /// When the next scheduled scan is due: one interval after the latest scan of any kind ended,
    /// whether it succeeded or failed; now (<paramref name="now"/>) when no scan has ever ended; and
    /// null when the schedule is off.
    /// </summary>
    public static DateTimeOffset? DueAt(MediaScanSchedule schedule, MediaScanSummary? last, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (!schedule.Enabled)
        {
            return null;
        }

        return last is null ? now : last.FinishedUtc + schedule.Interval;
    }

    /// <summary>Whether a scheduled scan is due at <paramref name="now"/>. Whether a scan is in progress is not considered here.</summary>
    public static bool IsDue(MediaScanSchedule schedule, MediaScanSummary? last, DateTimeOffset now) =>
        DueAt(schedule, last, now) is { } due && due <= now;

    /// <summary>
    /// Whether the scan <paramref name="summary"/> describes is kept out of the jobs list once the
    /// next scan finishes: a startup or scheduled scan that succeeded and found nothing new or
    /// changed. A manual or recovery scan, and any scan that failed, is kept.
    /// </summary>
    public static bool IsForgettable(MediaScanSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return summary.Trigger is MediaScanTrigger.Startup or MediaScanTrigger.Scheduled
            && summary.Outcome == MediaScanOutcome.Succeeded
            && summary.Counts.FoundNothing;
    }
}
