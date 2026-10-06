using n8Tracks.Application.Jobs;
using n8Tracks.Application.Scheduling;

namespace n8Tracks.Application.Backups;

/// <summary>
/// When scheduled backups are planned and when one is due. Pure: the clock, the zone, and the
/// records are given. Planned times follow the shared <see cref="DailyTaskRules"/>.
/// </summary>
public static class BackupScheduleRules
{
    /// <summary>How long after a first failure its one retry runs.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    /// <summary>
    /// The instant <paramref name="time"/> on <paramref name="date"/> is in <paramref name="zone"/>.
    /// A time the clocks skip (they go forward) is the next minute that exists; a time that happens
    /// twice (they go back) is its first occurrence, so it is planned once.
    /// </summary>
    public static DateTimeOffset PlannedOn(DateOnly date, TimeOnly time, TimeZoneInfo zone) => DailyTaskRules.PlannedOn(date, time, zone);

    /// <summary>The latest planned time at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset? MostRecent(BackupSchedule schedule, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return DailyTaskRules.MostRecent(schedule.Time, date => Runs(schedule.Frequency, date), zone, now);
    }

    /// <summary>The first planned time after <paramref name="now"/>.</summary>
    public static DateTimeOffset? Next(BackupSchedule schedule, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return DailyTaskRules.Next(schedule.Time, date => Runs(schedule.Frequency, date), zone, now);
    }

    /// <summary>
    /// When the failed <paramref name="attempt"/> is retried, or null for no retry. Only a first
    /// failure is retried, an hour after it ended, and only while scheduling is on. The retry is
    /// dropped by a restart (a failure from before <paramref name="processStartedUtc"/>, or one the
    /// restart itself caused) and by any change of the schedule after the failure.
    /// </summary>
    public static DateTimeOffset? RetryAt(StoredBackupSchedule stored, BackupAttempt? attempt, DateTimeOffset processStartedUtc)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return stored.Schedule.Enabled
            && attempt is { Outcome: BackupAttemptOutcome.Failed, Retry: false, FinishedUtc: { } finished }
            && attempt.Error != JobErrors.InterruptedByRestart
            && finished >= processStartedUtc
            && (stored.ChangedUtc is not { } changed || finished > changed)
                ? finished + RetryDelay
                : null;
    }

    /// <summary>
    /// Whether a scheduled backup is due at <paramref name="now"/>: the retry when it has fallen due,
    /// otherwise a run when the latest planned time is later than both the time scheduling was armed
    /// and the last attempt's start. However many planned times were missed, that is one run, and an
    /// instance never armed runs nothing. Whether another backup is in progress is not considered here.
    /// </summary>
    public static BackupScheduleAction Decide(
        StoredBackupSchedule stored,
        BackupAttempt? attempt,
        TimeZoneInfo zone,
        DateTimeOffset now,
        DateTimeOffset processStartedUtc)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (!stored.Schedule.Enabled || stored.ArmedUtc is not { } armed)
        {
            return BackupScheduleAction.None;
        }

        if (RetryAt(stored, attempt, processStartedUtc) is { } retry && retry <= now)
        {
            return BackupScheduleAction.Retry;
        }

        var since = attempt is not null && attempt.StartedUtc > armed ? attempt.StartedUtc : armed;
        return MostRecent(stored.Schedule, zone, now) is { } planned && planned > since
            ? BackupScheduleAction.Run
            : BackupScheduleAction.None;
    }

    /// <summary>
    /// The attempt brought up to date with its job: a recorded <c>running</c> attempt whose job has
    /// finished takes the job's outcome; one whose job has gone (pruned) counts as failed.
    /// </summary>
    public static BackupAttempt Reconcile(BackupAttempt attempt, JobSummary? job)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (attempt.Outcome != BackupAttemptOutcome.Running)
        {
            return attempt;
        }

        return job switch
        {
            null => attempt with { Outcome = BackupAttemptOutcome.Failed, FinishedUtc = attempt.StartedUtc, Error = "The backup job is no longer recorded." },
            { Status: JobStatus.Succeeded } => attempt with { Outcome = BackupAttemptOutcome.Succeeded, FinishedUtc = job.FinishedUtc },
            { Status: JobStatus.Failed } => attempt with { Outcome = BackupAttemptOutcome.Failed, FinishedUtc = job.FinishedUtc, Error = job.Error ?? "The backup failed." },
            _ => attempt,
        };
    }

    private static bool Runs(BackupFrequency frequency, DateOnly date) =>
        frequency == BackupFrequency.Daily || date.DayOfWeek == BackupSchedule.WeeklyDay;
}
