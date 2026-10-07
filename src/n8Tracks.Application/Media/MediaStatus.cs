using System.Text.Json;
using n8Tracks.Application.Jobs;

namespace n8Tracks.Application.Media;

/// <summary>Why a scan failed, as the Media page words it (#208).</summary>
public enum MediaScanFailure
{
    /// <summary>The media folder was absent, or its root could not be listed.</summary>
    FolderUnavailable,

    /// <summary>The scan was stopped by a restart or a shutdown.</summary>
    Interrupted,

    /// <summary>Anything else: the page names the job.</summary>
    Other,
}

/// <summary>
/// One finished scan as the media status reports it: the stored summary, or, for a scan that ended
/// without writing one (a restart cut it off), what its job says. <see cref="Trigger"/> and
/// <see cref="Counts"/> are null when only the job is known (its payload is cleared when it ends, and a
/// failed job has no result). <see cref="Failure"/> is null when it succeeded.
/// </summary>
public sealed record MediaScanReport(
    Guid JobId,
    MediaScanTrigger? Trigger,
    MediaScanOutcome Outcome,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    MediaScanCounts? Counts,
    MediaScanFailure? Failure)
{
    /// <summary>How long it ran.</summary>
    public TimeSpan Duration => FinishedUtc - StartedUtc;

    public static MediaScanReport From(MediaScanSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new(
            summary.JobId,
            summary.Trigger,
            summary.Outcome,
            summary.StartedUtc,
            summary.FinishedUtc,
            summary.Counts,
            summary.Outcome == MediaScanOutcome.Succeeded ? null : MediaStatusRules.FailureOf(summary.Error));
    }
}

/// <summary>
/// The media library's state (#208): the media folder's state, the files by status and association,
/// the last scan (any outcome) and the last one that succeeded, the scan queued or running, the
/// schedule and when its next scan is due (null while it is off), and whether the last successful
/// scan newly marked Missing more than half of what was Available before it.
/// </summary>
public sealed record MediaStatus(
    MediaMountStatus Mount,
    AudioFileCounts Counts,
    MediaScanReport? LastScan,
    MediaScanReport? LastSuccessfulScan,
    Guid? ActiveScanJobId,
    MediaScanSchedule Schedule,
    DateTimeOffset? NextScheduledScan,
    bool MajorityMissingWarning);

/// <summary>The media status rules (#208). Pure.</summary>
public static class MediaStatusRules
{
    /// <summary>What the job error of a scan cut off by a restart says (the worker's own text).</summary>
    private const string InterruptedText = "interrupted";

    /// <summary>
    /// Whether the scan <paramref name="lastSuccessful"/> describes newly marked Missing more than half
    /// of the files that were Available before it: exactly half is not more. Never while the folder is
    /// <see cref="MediaMountState.Unavailable"/> (that is said on its own), and never before a scan has
    /// succeeded. The next scan that succeeds replaces <paramref name="lastSuccessful"/>, and so clears
    /// it; one that fails does not.
    /// </summary>
    public static bool MajorityMissing(MediaScanSummary? lastSuccessful, MediaMountState mount)
    {
        if (mount == MediaMountState.Unavailable || lastSuccessful is not { Outcome: MediaScanOutcome.Succeeded } scan)
        {
            return false;
        }

        var counts = scan.Counts;
        return counts.Missing > 0 && (long)counts.Missing * 2 > counts.AvailableBefore;
    }

    /// <summary>Which known cause a stored scan error or a job error names.</summary>
    public static MediaScanFailure FailureOf(string? error) => error switch
    {
        MediaFolderUnavailableException.Text => MediaScanFailure.FolderUnavailable,
        InterruptedText or JobErrors.InterruptedByRestart => MediaScanFailure.Interrupted,
        _ => MediaScanFailure.Other,
    };

    /// <summary>
    /// The last scan to show: the stored summary, unless the newest finished <c>media-scan</c> job is a
    /// different, later one, which ended without writing a summary (cut off by a restart); then that
    /// job. Null when there is neither.
    /// </summary>
    public static MediaScanReport? LastScan(MediaScanSummary? summary, JobSummary? latestJob)
    {
        if (latestJob is { FinishedUtc: { } finished }
            && (summary is null || (latestJob.Id != summary.JobId && finished > summary.FinishedUtc)))
        {
            var succeeded = latestJob.Status == JobStatus.Succeeded;
            return new MediaScanReport(
                latestJob.Id,
                TriggerOf(latestJob.Result),
                succeeded ? MediaScanOutcome.Succeeded : MediaScanOutcome.Failed,
                latestJob.StartedUtc ?? latestJob.CreatedUtc,
                finished,
                null,
                succeeded ? null : FailureOf(latestJob.Error));
        }

        return summary is null ? null : MediaScanReport.From(summary);
    }

    private static MediaScanTrigger? TriggerOf(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty("trigger", out var trigger)
        && trigger.ValueKind == JsonValueKind.String
            ? MediaScanTriggers.Parse(trigger.GetString())
            : null;
}

/// <summary>
/// Reads the media status (#208) for the Media page. It only reads: the mount state is the one the
/// probe and the scans keep (#207), never probed here, and the counts are the stored ones, so a page
/// that asks every second costs a few queries.
/// </summary>
public sealed class MediaStatusService(
    MediaAvailability availability,
    IAudioFileStore files,
    IMediaScanSummaryStore summaries,
    IMediaScanScheduleStore schedules,
    IJobStore jobs,
    TimeProvider time)
{
    public async Task<MediaStatus> GetAsync(CancellationToken cancellationToken)
    {
        var mount = await availability.CurrentAsync(cancellationToken).ConfigureAwait(false);
        var counts = await files.CountsAsync(cancellationToken).ConfigureAwait(false);
        var summary = await summaries.FindAsync(cancellationToken).ConfigureAwait(false);
        var successful = await summaries.FindLastSuccessfulAsync(cancellationToken).ConfigureAwait(false);
        var latestJob = await jobs.FindLatestFinishedAsync(MediaScanService.JobType, cancellationToken).ConfigureAwait(false);
        var active = await jobs.FindActiveAsync(MediaScanService.JobType, cancellationToken).ConfigureAwait(false);
        var schedule = (await schedules.FindAsync(cancellationToken).ConfigureAwait(false) ?? StoredMediaScanSchedule.Unstored).Schedule;

        return new MediaStatus(
            mount,
            counts,
            MediaStatusRules.LastScan(summary, latestJob),
            successful is null ? null : MediaScanReport.From(successful),
            active,
            schedule,
            MediaScanScheduleRules.DueAt(schedule, summary, time.GetUtcNow()),
            MediaStatusRules.MajorityMissing(successful, mount.State));
    }
}
