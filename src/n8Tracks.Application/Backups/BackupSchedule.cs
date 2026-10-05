using System.Globalization;

namespace n8Tracks.Application.Backups;

/// <summary>How often a scheduled backup runs.</summary>
public enum BackupFrequency
{
    /// <summary>Every day at the configured time.</summary>
    Daily,

    /// <summary>Every <see cref="BackupSchedule.WeeklyDay"/> at the configured time.</summary>
    Weekly,
}

/// <summary>
/// The backup schedule the administrator chose: on or off, daily or weekly, the time of day in the
/// configured time zone, and how many scheduled backups are kept.
/// </summary>
/// <param name="Enabled">Whether scheduled backups run at all.</param>
/// <param name="Frequency">Daily, or weekly on <see cref="WeeklyDay"/>.</param>
/// <param name="Time">The local time of day, to the minute, in the configured time zone.</param>
/// <param name="Keep">How many scheduled backups are kept: <see cref="MinimumKeep"/> to <see cref="MaximumKeep"/>.</param>
public sealed record BackupSchedule(bool Enabled, BackupFrequency Frequency, TimeOnly Time, int Keep)
{
    public const int MinimumKeep = 1;
    public const int MaximumKeep = 365;

    /// <summary>The day a weekly backup runs.</summary>
    public const DayOfWeek WeeklyDay = DayOfWeek.Sunday;

    /// <summary>On, daily at 03:00, keeping seven: what every instance has until the administrator changes it.</summary>
    public static readonly BackupSchedule Default = new(true, BackupFrequency.Daily, new TimeOnly(3, 0), 7);

    public const string EnabledField = "enabled";
    public const string FrequencyField = "frequency";
    public const string TimeField = "time";
    public const string KeepField = "keep";

    public const string DailyText = "daily";
    public const string WeeklyText = "weekly";

    /// <summary>The frequency as the API and the settings row write it.</summary>
    public static string FrequencyText(BackupFrequency frequency) => frequency == BackupFrequency.Weekly ? WeeklyText : DailyText;

    /// <summary>The time as the API and the settings row write it: <c>HH:mm</c>.</summary>
    public static string TimeText(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a schedule from its four fields as a client sends them. Every field is required. The
    /// errors are keyed by field name (<paramref name="prefix"/> before each); empty when valid.
    /// </summary>
    public static (BackupSchedule? Schedule, Dictionary<string, string[]> Errors) Parse(BackupScheduleInput? input, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (input?.Enabled is not { } enabled)
        {
            errors[prefix + EnabledField] = ["Say whether scheduled backups are on."];
            enabled = false;
        }

        BackupFrequency frequency = BackupFrequency.Daily;
        switch (input?.Frequency)
        {
            case DailyText:
                break;
            case WeeklyText:
                frequency = BackupFrequency.Weekly;
                break;
            default:
                errors[prefix + FrequencyField] = ["Choose daily or weekly."];
                break;
        }

        TimeOnly time = default;
        if (input?.Time is not { Length: 5 } text
            || !TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
        {
            errors[prefix + TimeField] = ["Enter a time of day as HH:mm, from 00:00 to 23:59."];
        }

        if (input?.Keep is not { } keep || keep < MinimumKeep || keep > MaximumKeep)
        {
            errors[prefix + KeepField] = [$"Keep from {MinimumKeep} to {MaximumKeep} backups."];
            keep = 0;
        }

        return errors.Count > 0 ? (null, errors) : (new BackupSchedule(enabled, frequency, time, keep), errors);
    }
}

/// <summary>The four fields of a schedule as a client sends them; any may be missing.</summary>
public sealed record BackupScheduleInput(bool? Enabled, string? Frequency, string? Time, int? Keep);

/// <summary>The schedule as it is stored.</summary>
/// <param name="Schedule">The administrator's choices.</param>
/// <param name="Revision">The record's revision: 1 until the first change.</param>
/// <param name="ArmedUtc">
/// When scheduling was last turned on (by setup, by switching it on, or, for an instance that had
/// no schedule, when the scheduler first saw it). No planned time at or before it runs. Null only
/// for an instance with no stored schedule.
/// </param>
/// <param name="ChangedUtc">When the schedule was last changed; a pending retry from before it is dropped.</param>
public sealed record StoredBackupSchedule(BackupSchedule Schedule, int Revision, DateTimeOffset? ArmedUtc, DateTimeOffset? ChangedUtc)
{
    /// <summary>What an instance with no stored schedule has: the defaults, at revision 1, not yet armed.</summary>
    public static readonly StoredBackupSchedule Unstored = new(BackupSchedule.Default, 1, null, null);
}

/// <summary>Where the latest scheduled attempt stands.</summary>
public enum BackupAttemptOutcome
{
    /// <summary>Its job has started and not finished, as far as the record knows.</summary>
    Running,

    /// <summary>It made a verified archive.</summary>
    Succeeded,

    /// <summary>It failed; <see cref="BackupAttempt.Error"/> says why.</summary>
    Failed,
}

/// <summary>The latest scheduled backup attempt, recorded when its job starts.</summary>
/// <param name="JobId">The <c>backup</c> job that ran it.</param>
/// <param name="StartedUtc">When the job started.</param>
/// <param name="Retry">Whether it was the one retry after a failure; a failed retry is not retried.</param>
/// <param name="Outcome">How it stands.</param>
/// <param name="FinishedUtc">When it finished, or null while running.</param>
/// <param name="Error">Why it failed (the job's own scrubbed error), or null.</param>
public sealed record BackupAttempt(
    Guid JobId,
    DateTimeOffset StartedUtc,
    bool Retry,
    BackupAttemptOutcome Outcome,
    DateTimeOffset? FinishedUtc,
    string? Error);

/// <summary>What one look at the schedule decided.</summary>
public enum BackupScheduleAction
{
    /// <summary>Nothing is due, or a backup is already queued or running.</summary>
    None,

    /// <summary>A planned time has passed with no attempt since it: a scheduled backup was queued.</summary>
    Run,

    /// <summary>The first failure's retry fell due: the retry was queued.</summary>
    Retry,
}

/// <summary>What the Backups page shows of the schedule.</summary>
/// <param name="Stored">The schedule.</param>
/// <param name="NextUtc">The next planned time, or null when scheduling is off.</param>
/// <param name="LastAttempt">The latest scheduled attempt, or null when there has been none.</param>
/// <param name="RetryUtc">When the pending retry of a failed attempt runs, or null when none is pending.</param>
public sealed record BackupScheduleStatus(StoredBackupSchedule Stored, DateTimeOffset? NextUtc, BackupAttempt? LastAttempt, DateTimeOffset? RetryUtc);
