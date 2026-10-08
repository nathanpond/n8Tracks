namespace n8Tracks.Application.Notifications;

/// <summary>How a notification's work ended. Stored and answered as camelCase text.</summary>
public enum NotificationSeverity
{
    /// <summary>The work did what it was asked. Marked read once shown.</summary>
    Success,

    /// <summary>The work ended, but not entirely as asked (some records failed, a fallback was used). Stays until dismissed.</summary>
    Warning,

    /// <summary>The work failed. Stays until dismissed.</summary>
    Failure,
}

/// <summary>The text of each severity.</summary>
public static class NotificationSeverities
{
    public static string Text(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => "success",
        NotificationSeverity.Warning => "warning",
        NotificationSeverity.Failure => "failure",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown severity."),
    };

    /// <summary>The severity <paramref name="text"/> names, or null.</summary>
    public static NotificationSeverity? Parse(string? text) => text switch
    {
        "success" => NotificationSeverity.Success,
        "warning" => NotificationSeverity.Warning,
        "failure" => NotificationSeverity.Failure,
        _ => null,
    };
}

/// <summary>
/// The kinds of work a notification reports. The list is open: the table holds any kind, so MCP bulk
/// operations (M7) and portable export and import (M8) add theirs without a schema change.
/// </summary>
public static class NotificationKinds
{
    /// <summary>A media scan (#203, #204): a manual one always; a startup, scheduled, or recovery one when it fails or finds new unmatched files.</summary>
    public const string MediaScan = "mediaScan";

    /// <summary>A Suno import commit (#140).</summary>
    public const string SunoImport = "sunoImport";

    /// <summary>A Suno sync (#131): an export received, discarded with a failure, failed, or expired.</summary>
    public const string SunoSync = "sunoSync";

    /// <summary>A manual or scheduled backup (#71, #72).</summary>
    public const string Backup = "backup";

    /// <summary>A restore (#74).</summary>
    public const string Restore = "restore";

    /// <summary>A database migration with its safety backup (#76).</summary>
    public const string Migration = "migration";

    /// <summary>Every kind this version records, each with a producer (<see cref="INotificationProducer"/>).</summary>
    public static readonly IReadOnlyList<string> Recorded = [MediaScan, SunoImport, SunoSync, Backup, Restore, Migration];
}

/// <summary>Where each kind's details are: an address of the web app.</summary>
public static class NotificationLinks
{
    public const string Media = "/library/media";
    public const string SunoImports = "/suno/imports";
    public const string Backups = "/settings/backups";
    public const string Diagnostics = "/settings/diagnostics";

    /// <summary>An export's review, or its result once committed.</summary>
    public static string Export(Guid exportId) => $"{SunoImports}/{exportId}";
}

/// <summary>What Retry does: the action, and the export it commits for an import.</summary>
/// <param name="Action">One of <see cref="NotificationRetryActions"/>.</param>
/// <param name="Subject">The export, for <see cref="NotificationRetryActions.SunoImport"/>; null otherwise.</param>
public sealed record NotificationRetry(string Action, Guid? Subject = null);

/// <summary>The work Retry can start again: only what is safe to repeat.</summary>
public static class NotificationRetryActions
{
    /// <summary>A manual media scan.</summary>
    public const string MediaScan = "mediaScan";

    /// <summary>A manual backup (also for a failed scheduled one).</summary>
    public const string Backup = "backup";

    /// <summary>The commit of the export again, at its current revision; offered only when the failed commit applied nothing.</summary>
    public const string SunoImport = "sunoImport";
}

/// <summary>
/// A notification to record. Its <see cref="Summary"/> and <see cref="Detail"/> are built from fixed
/// templates with numbers, never from an exception's text or anything the user or Suno wrote.
/// </summary>
/// <param name="Kind">One of <see cref="NotificationKinds"/>, or a later kind.</param>
/// <param name="Severity">How the work ended.</param>
/// <param name="Summary">One sentence with the numbers that matter.</param>
/// <param name="Link">Where the details are (<see cref="NotificationLinks"/>).</param>
public sealed record NotificationDraft(string Kind, NotificationSeverity Severity, string Summary, string Link)
{
    /// <summary>A sentence or two more, from a template; null when the summary says it all.</summary>
    public string? Detail { get; init; }

    /// <summary>What Retry does, where repeating is safe; null otherwise.</summary>
    public NotificationRetry? Retry { get; init; }

    /// <summary>
    /// Set for scheduled work: a failure with the same key repeats the newest notification of that key
    /// (its count goes up) while that one is a failure not dismissed, retried, or resolved.
    /// </summary>
    public string? CoalesceKey { get; init; }

    /// <summary>What the work was about (a kind, or a kind and an export): work that did not fail resolves the earlier warnings and failures of its topic.</summary>
    public string? Topic { get; init; }

    /// <summary>
    /// What this one occurrence was (a job, an export, a restore): a second notification of the same
    /// kind and subject is not recorded. Null when every occurrence is recorded.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>When it happened; null for now.</summary>
    public DateTimeOffset? OccurredUtc { get; init; }

    /// <summary>
    /// A success that is not worth telling (a routine scheduled scan that changed nothing): nothing is
    /// recorded, but it still resolves the earlier failures of its <see cref="Topic"/>.
    /// </summary>
    public bool Silent { get; init; }
}

/// <summary>A notification as stored and listed.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Count">How many times it happened: more than 1 only for repeated failures of scheduled work.</param>
/// <param name="FirstOccurredUtc">When it first happened.</param>
/// <param name="OccurredUtc">When it last happened.</param>
/// <param name="ReadUtc">When it was marked read (a success shown, or every notification from before a restore).</param>
/// <param name="DismissedUtc">When the user dismissed it.</param>
/// <param name="RetriedUtc">When the user retried it.</param>
/// <param name="ResolvedUtc">When a later success of its topic resolved it.</param>
/// <param name="BeforeRestore">Whether it came from the archive a restore brought back.</param>
public sealed record Notification(
    Guid Id,
    string Kind,
    NotificationSeverity Severity,
    string Summary,
    string? Detail,
    string Link,
    NotificationRetry? Retry,
    int Count,
    DateTimeOffset FirstOccurredUtc,
    DateTimeOffset OccurredUtc,
    DateTimeOffset? ReadUtc,
    DateTimeOffset? DismissedUtc,
    DateTimeOffset? RetriedUtc,
    DateTimeOffset? ResolvedUtc,
    bool BeforeRestore)
{
    /// <summary>The key a later failure of the same scheduled work coalesces into.</summary>
    public string? CoalesceKey { get; init; }

    /// <summary>What a later success resolves it by.</summary>
    public string? Topic { get; init; }

    /// <summary>The one occurrence it records, when it has one.</summary>
    public string? Subject { get; init; }

    /// <summary>Counts toward the unread number: not read, dismissed, retried, or resolved.</summary>
    public bool Unread => ReadUtc is null && DismissedUtc is null && RetriedUtc is null && ResolvedUtc is null;

    /// <summary>Whether Retry is offered now: it has a retry action and was not retried, resolved, or dismissed, nor from before a restore.</summary>
    public bool Retryable => Retry is not null && RetriedUtc is null && ResolvedUtc is null && DismissedUtc is null && !BeforeRestore;

    /// <summary>Whether a failure of the same scheduled work coalesces into it.</summary>
    public bool TakesRepeats => Severity == NotificationSeverity.Failure && DismissedUtc is null && RetriedUtc is null && ResolvedUtc is null && !BeforeRestore;
}

/// <summary>How many notifications are unread, by severity.</summary>
public sealed record NotificationUnreadCounts(int Success, int Warning, int Failure)
{
    public int Total => Success + Warning + Failure;
}

/// <summary>A page of notifications, newest first, and the unread counts of all of them.</summary>
public sealed record NotificationPage(IReadOnlyList<Notification> Items, int Page, int PageSize, int Total, NotificationUnreadCounts Unread);

/// <summary>What asking for a retry did.</summary>
public abstract record NotificationRetryOutcome
{
    private NotificationRetryOutcome()
    {
    }

    /// <summary>The work was started again as job <paramref name="JobId"/>; the notification is marked retried.</summary>
    public sealed record Started(Guid JobId) : NotificationRetryOutcome;

    /// <summary>No such notification.</summary>
    public sealed record NotFound : NotificationRetryOutcome;

    /// <summary>It offers no retry (none applies, or it was retried, resolved, or dismissed already).</summary>
    public sealed record NotRetryable : NotificationRetryOutcome;

    /// <summary>The same work is queued or running again; nothing was started.</summary>
    public sealed record InProgress : NotificationRetryOutcome;
}

/// <summary>A producer of one kind of notification: a job's (<see cref="IJobNotificationProducer"/>) or a direct caller's.</summary>
public interface INotificationProducer
{
    /// <summary>The kind it records (<see cref="NotificationKinds"/>).</summary>
    string Kind { get; }
}

/// <summary>Where notifications are kept.</summary>
public interface INotificationStore
{
    /// <summary>The notification, or null.</summary>
    Task<Notification?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The newest notification with <paramref name="coalesceKey"/>, or null.</summary>
    Task<Notification?> FindLatestAsync(string coalesceKey, CancellationToken cancellationToken);

    /// <summary>Whether a notification of <paramref name="kind"/> records <paramref name="subject"/> already.</summary>
    Task<bool> HasSubjectAsync(string kind, string subject, CancellationToken cancellationToken);

    /// <summary>Stores a new notification.</summary>
    Task AddAsync(Notification notification, CancellationToken cancellationToken);

    /// <summary>One more occurrence of <paramref name="id"/>: its count goes up, its time, summary, and detail become <paramref name="draft"/>'s, and it is unread again.</summary>
    Task RepeatAsync(Guid id, NotificationDraft draft, DateTimeOffset occurredUtc, CancellationToken cancellationToken);

    /// <summary>Marks resolved every warning or failure of <paramref name="topic"/> not dismissed, resolved, or from before a restore; returns how many.</summary>
    Task<int> ResolveAsync(string topic, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>A page, newest occurrence first; every notification when <paramref name="history"/>, else those not dismissed.</summary>
    Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(bool history, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The unread counts by severity.</summary>
    Task<NotificationUnreadCounts> UnreadAsync(CancellationToken cancellationToken);

    /// <summary>Marks read the unread successes among <paramref name="ids"/>; returns how many.</summary>
    Task<int> MarkSuccessesReadAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Dismisses the notification if it is not already; false when there is no such notification.</summary>
    Task<bool> DismissAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Dismisses every notification not dismissed yet; returns how many.</summary>
    Task<int> DismissAllAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks the notification retried; false when it was retried already.</summary>
    Task<bool> MarkRetriedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks every notification read and from before a restore; returns how many.</summary>
    Task<int> MarkBeforeRestoreAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the read or dismissed notifications done with before <paramref name="doneBefore"/>, and
    /// beyond the newest <paramref name="keep"/> of them; an unread one, or a warning or failure not
    /// dismissed, is never deleted. Returns how many.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset doneBefore, int keep, CancellationToken cancellationToken);
}

/// <summary>Where a notification that could not be recorded is reported: the log, never the work that ended.</summary>
public interface INotificationLog
{
    void RecordFailed(string kind, Exception exception);
}
