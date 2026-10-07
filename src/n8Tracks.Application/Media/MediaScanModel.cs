namespace n8Tracks.Application.Media;

/// <summary>What started a scan. Recorded in the job's result and the last-scan summary.</summary>
public enum MediaScanTrigger
{
    /// <summary>The user asked (<c>POST /api/v1/media/scans</c>).</summary>
    Manual,

    /// <summary>The server started (#204).</summary>
    Startup,

    /// <summary>The schedule (#204).</summary>
    Scheduled,

    /// <summary>The media folder became readable again (#207).</summary>
    Recovery,
}

/// <summary>The text of each trigger, as payloads, results, and the summary write it.</summary>
public static class MediaScanTriggers
{
    public static string Text(MediaScanTrigger trigger) => trigger switch
    {
        MediaScanTrigger.Manual => "manual",
        MediaScanTrigger.Startup => "startup",
        MediaScanTrigger.Scheduled => "scheduled",
        MediaScanTrigger.Recovery => "recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown scan trigger."),
    };

    /// <summary>The trigger <paramref name="text"/> names, or null for one this build does not know.</summary>
    public static MediaScanTrigger? Parse(string? text) => text switch
    {
        "manual" => MediaScanTrigger.Manual,
        "startup" => MediaScanTrigger.Startup,
        "scheduled" => MediaScanTrigger.Scheduled,
        "recovery" => MediaScanTrigger.Recovery,
        _ => null,
    };
}

/// <summary>
/// What a scan counted. <see cref="Seen"/> is supported audio files found; <see cref="New"/>,
/// <see cref="Changed"/>, and <see cref="Unchanged"/> partition it; <see cref="Unreadable"/> counts the
/// files in <see cref="Seen"/> whose header could not be read (zero-byte files included);
/// <see cref="Skipped"/> is every other entry (other files, and links, which #203 does not follow);
/// <see cref="UnreadableDirectories"/> is subdirectories that could not be listed.
/// <see cref="Associated"/> is files the Suno ID matcher associated at the end of the scan, and
/// <see cref="Unmatched"/> every record left without an association (Missing and user-unassociated
/// ones included); both are 0 for a scan that did not complete (#206).
/// </summary>
public sealed record MediaScanCounts(
    int Seen,
    int New,
    int Changed,
    int Unchanged,
    int Skipped,
    int Unreadable,
    int UnreadableDirectories,
    int Associated = 0,
    int Unmatched = 0)
{
    public static MediaScanCounts None { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Whether the scan found nothing new and nothing changed (#204: such a startup or scheduled
    /// scan is dropped from the jobs list). #207 adds files gone missing to this test.
    /// </summary>
    public bool FoundNothing => New == 0 && Changed == 0;
}

/// <summary>What a scan that succeeded reports.</summary>
/// <param name="Elapsed">How long it took.</param>
public sealed record MediaScanResult(MediaScanTrigger Trigger, MediaScanCounts Counts, TimeSpan Elapsed);

/// <summary>How a scan ended.</summary>
public enum MediaScanOutcome
{
    Succeeded,

    /// <summary>It failed or was interrupted; the records it had already written are kept.</summary>
    Failed,
}

/// <summary>The stored summary of the last scan that ended (#204 and #208 read it).</summary>
/// <param name="JobId">The scan's job.</param>
/// <param name="Error">Why it failed, in plain words; null when it succeeded.</param>
/// <param name="Counts">What it counted, up to where it got.</param>
public sealed record MediaScanSummary(
    Guid JobId,
    MediaScanTrigger Trigger,
    MediaScanOutcome Outcome,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    MediaScanCounts Counts,
    string? Error);

/// <summary>How asking for a scan ended.</summary>
/// <param name="JobId">The new job, or the one already queued or running.</param>
/// <param name="AlreadyInProgress">True when nothing was queued because a scan was queued or running already.</param>
public sealed record MediaScanStart(Guid JobId, bool AlreadyInProgress);

/// <summary>The media folder is absent or its root cannot be listed: the scan changes nothing.</summary>
public sealed class MediaFolderUnavailableException : Exception
{
    /// <summary>The job's error says this.</summary>
    public const string Text = "media folder unavailable";

    public MediaFolderUnavailableException()
        : base(Text)
    {
    }

    public MediaFolderUnavailableException(string message)
        : base(message)
    {
    }

    public MediaFolderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The scan's limits. A singleton; tests replace it to shorten the time limits.</summary>
public sealed class MediaScanOptions
{
    /// <summary>How long opening one file and reading its header may take; longer counts as unreadable.</summary>
    public TimeSpan HeaderReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long listing one directory may take; longer counts as an unreadable directory (the root: the folder is unavailable).</summary>
    public TimeSpan DirectoryListTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many records are written in one transaction.</summary>
    public int BatchSize { get; init; } = 200;
}
