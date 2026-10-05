using n8Tracks.Application.Backups;
using n8Tracks.Application.Jobs;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Api.Tests.Backups;

/// <summary>When a scheduled backup is planned and due, with a controllable clock, and which archives retention deletes.</summary>
public sealed class BackupScheduleRulesTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    /// <summary>Long before every test's clock, so a failure in a test is "since this process started".</summary>
    private static readonly DateTimeOffset ProcessStart = At("2020-01-01T00:00:00Z");

    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture);

    private static StoredBackupSchedule Stored(
        string time = "03:00",
        BackupFrequency frequency = BackupFrequency.Daily,
        bool enabled = true,
        string armed = "2026-09-01T00:00:00Z",
        string? changed = null) =>
        new(new BackupSchedule(enabled, frequency, TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture), 7), 1, At(armed), changed is null ? null : At(changed));

    private static BackupAttempt Attempt(string started, BackupAttemptOutcome outcome = BackupAttemptOutcome.Succeeded, string? finished = null, bool retry = false, string? error = null) =>
        new(Guid.CreateVersion7(), At(started), retry, outcome, finished is null ? null : At(finished), error);

    private static BackupScheduleAction Decide(StoredBackupSchedule stored, BackupAttempt? attempt, string now, TimeZoneInfo? zone = null) =>
        BackupScheduleRules.Decide(stored, attempt, zone ?? Utc, At(now), ProcessStart);

    [Fact]
    public void TheDefaultsAreDailyAtThreeKeepingSeven()
    {
        Assert.Equal(new BackupSchedule(true, BackupFrequency.Daily, new TimeOnly(3, 0), 7), BackupSchedule.Default);
        Assert.Equal(1, StoredBackupSchedule.Unstored.Revision);
        Assert.Null(StoredBackupSchedule.Unstored.ArmedUtc);
    }

    [Fact]
    public void ADailyBackupIsDueAtTheConfiguredLocalTimeOnBothSidesOfAClockChange()
    {
        // London: GMT (UTC+0) until 29 March 2026 at 01:00 UTC, then BST (UTC+1).
        var stored = Stored(armed: "2026-03-27T12:00:00Z");

        Assert.Equal(BackupScheduleAction.None, Decide(stored, null, "2026-03-28T02:59:00Z", London));
        Assert.Equal(BackupScheduleAction.Run, Decide(stored, null, "2026-03-28T03:00:00Z", London));

        var first = Attempt("2026-03-28T03:00:02Z");
        Assert.Equal(BackupScheduleAction.None, Decide(stored, first, "2026-03-29T01:59:00Z", London));

        // 03:00 BST is 02:00 UTC.
        Assert.Equal(At("2026-03-29T02:00:00Z"), BackupScheduleRules.Next(stored.Schedule, London, At("2026-03-28T03:00:02Z")));
        Assert.Equal(BackupScheduleAction.Run, Decide(stored, first, "2026-03-29T02:00:00Z", London));
    }

    [Fact]
    public void ATimeTheClocksSkipRunsAtTheNextValidMinute()
    {
        // New York skips 02:00–02:59 on 8 March 2026: 02:30 does not exist, so the backup runs at 03:00 EDT.
        var stored = Stored(time: "02:30", armed: "2026-03-07T12:00:00Z");

        Assert.Equal(At("2026-03-08T07:00:00Z"), BackupScheduleRules.PlannedOn(new DateOnly(2026, 3, 8), new TimeOnly(2, 30), NewYork));
        Assert.Equal(BackupScheduleAction.None, Decide(stored, null, "2026-03-08T06:59:00Z", NewYork));
        Assert.Equal(BackupScheduleAction.Run, Decide(stored, null, "2026-03-08T07:00:00Z", NewYork));

        // The day before and after, 02:30 is an ordinary time.
        Assert.Equal(At("2026-03-07T07:30:00Z"), BackupScheduleRules.PlannedOn(new DateOnly(2026, 3, 7), new TimeOnly(2, 30), NewYork));
        Assert.Equal(At("2026-03-09T06:30:00Z"), BackupScheduleRules.PlannedOn(new DateOnly(2026, 3, 9), new TimeOnly(2, 30), NewYork));
    }

    [Fact]
    public void ATimeThatHappensTwiceRunsOnce()
    {
        // New York repeats 01:00–01:59 on 1 November 2026: 01:30 EDT (05:30 UTC), then 01:30 EST (06:30 UTC).
        var stored = Stored(time: "01:30", armed: "2026-10-31T12:00:00Z");

        Assert.Equal(At("2026-11-01T05:30:00Z"), BackupScheduleRules.PlannedOn(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), NewYork));
        Assert.Equal(BackupScheduleAction.Run, Decide(stored, null, "2026-11-01T05:30:00Z", NewYork));

        var attempt = Attempt("2026-11-01T05:30:02Z");
        Assert.Equal(BackupScheduleAction.None, Decide(stored, attempt, "2026-11-01T06:30:00Z", NewYork));
        Assert.Equal(BackupScheduleAction.None, Decide(stored, attempt, "2026-11-01T07:00:00Z", NewYork));
        Assert.Equal(At("2026-11-02T06:30:00Z"), BackupScheduleRules.Next(stored.Schedule, NewYork, At("2026-11-01T06:30:00Z")));
    }

    [Fact]
    public void AWeeklyBackupRunsOnSundaysOnly()
    {
        // 4 October 2026 is a Sunday.
        var stored = Stored(frequency: BackupFrequency.Weekly, armed: "2026-09-28T12:00:00Z");

        Assert.Equal(BackupScheduleAction.None, Decide(stored, null, "2026-10-03T03:00:00Z"));
        Assert.Equal(BackupScheduleAction.Run, Decide(stored, null, "2026-10-04T03:00:00Z"));

        var attempt = Attempt("2026-10-04T03:00:01Z");
        foreach (var day in new[] { "2026-10-05", "2026-10-07", "2026-10-10" })
        {
            Assert.Equal(BackupScheduleAction.None, Decide(stored, attempt, $"{day}T03:00:00Z"));
        }

        Assert.Equal(At("2026-10-11T03:00:00Z"), BackupScheduleRules.Next(stored.Schedule, Utc, At("2026-10-05T12:00:00Z")));
        Assert.Equal(BackupScheduleAction.Run, Decide(stored, attempt, "2026-10-11T03:00:00Z"));
    }

    [Fact]
    public void ADisabledScheduleRunsNothingAndPlansNothing()
    {
        var stored = Stored(enabled: false);

        Assert.Equal(BackupScheduleAction.None, Decide(stored, null, "2026-10-05T03:00:00Z"));
        Assert.Equal(BackupScheduleAction.None, Decide(stored, Attempt("2026-09-01T03:00:00Z"), "2026-10-05T03:00:00Z"));
        var failed = Attempt("2026-10-05T03:00:00Z", BackupAttemptOutcome.Failed, "2026-10-05T03:00:05Z", error: "disk full");
        Assert.Null(BackupScheduleRules.RetryAt(stored, failed, ProcessStart));
    }

    [Fact]
    public void AMissedRunFiresOnceAfterStartNotOncePerMissedDay()
    {
        // Down from 20 September to 5 October: fifteen planned times missed.
        var stored = Stored();
        var last = Attempt("2026-09-20T03:00:01Z");

        Assert.Equal(BackupScheduleAction.Run, Decide(stored, last, "2026-10-05T10:00:00Z"));

        var caughtUp = Attempt("2026-10-05T10:00:30Z");
        Assert.Equal(BackupScheduleAction.None, Decide(stored, caughtUp, "2026-10-05T10:01:00Z"));
        Assert.Equal(BackupScheduleAction.None, Decide(stored, caughtUp, "2026-10-06T02:59:00Z"));
        Assert.Equal(BackupScheduleAction.Run, Decide(stored, caughtUp, "2026-10-06T03:00:00Z"));
    }

    [Fact]
    public void AnInstanceWithNoAttemptWaitsForTheNextPlannedTimeAfterItWasArmed()
    {
        // Set up (armed) at 10:00, after today's 03:00: nothing runs at first start.
        var stored = Stored(armed: "2026-10-05T10:00:00Z");
        Assert.Equal(BackupScheduleAction.None, Decide(stored, null, "2026-10-05T10:01:00Z"));
        Assert.Equal(BackupScheduleAction.None, Decide(stored, null, "2026-10-06T02:59:59Z"));
        Assert.Equal(BackupScheduleAction.Run, Decide(stored, null, "2026-10-06T03:00:00Z"));

        // Never armed (no stored schedule): nothing, ever.
        Assert.Equal(BackupScheduleAction.None, Decide(StoredBackupSchedule.Unstored, null, "2026-10-06T03:00:00Z"));
    }

    [Fact]
    public void MovingTheTimeToOneAlreadyPastTodayRunsWhenNothingWasAttemptedSinceIt()
    {
        var last = Attempt("2026-10-05T03:00:01Z");

        // 14:00 now; the time moved to 13:00: due. Moved to 02:00 (before the 03:00 attempt): not due.
        Assert.Equal(BackupScheduleAction.Run, Decide(Stored(time: "13:00"), last, "2026-10-05T14:00:00Z"));
        Assert.Equal(BackupScheduleAction.None, Decide(Stored(time: "02:00"), last, "2026-10-05T14:00:00Z"));

        // Re-enabled at 14:00 (armed again): the first run is the next planned time.
        Assert.Equal(BackupScheduleAction.None, Decide(Stored(time: "13:00", armed: "2026-10-05T14:00:00Z"), last, "2026-10-05T14:01:00Z"));
    }

    [Fact]
    public void AFirstFailureIsRetriedOnceAnHourLaterThenWaitsForTheNextPlannedTime()
    {
        var stored = Stored();
        var failed = Attempt("2026-10-05T03:00:01Z", BackupAttemptOutcome.Failed, "2026-10-05T03:00:09Z", error: "disk full");

        Assert.Equal(At("2026-10-05T04:00:09Z"), BackupScheduleRules.RetryAt(stored, failed, ProcessStart));
        Assert.Equal(BackupScheduleAction.None, Decide(stored, failed, "2026-10-05T04:00:08Z"));
        Assert.Equal(BackupScheduleAction.Retry, Decide(stored, failed, "2026-10-05T04:00:09Z"));

        var retryFailed = Attempt("2026-10-05T04:01:00Z", BackupAttemptOutcome.Failed, "2026-10-05T04:01:05Z", retry: true, error: "disk full");
        Assert.Null(BackupScheduleRules.RetryAt(stored, retryFailed, ProcessStart));
        Assert.Equal(BackupScheduleAction.None, Decide(stored, retryFailed, "2026-10-05T05:01:05Z"));
        Assert.Equal(BackupScheduleAction.Run, Decide(stored, retryFailed, "2026-10-06T03:00:00Z"));
    }

    [Fact]
    public void APendingRetryIsDroppedByARestartOrAScheduleChange()
    {
        var failed = Attempt("2026-10-05T03:00:01Z", BackupAttemptOutcome.Failed, "2026-10-05T03:00:09Z", error: "disk full");

        // Restarted after the failure.
        Assert.Null(BackupScheduleRules.RetryAt(Stored(), failed, At("2026-10-05T03:30:00Z")));
        Assert.Equal(BackupScheduleAction.None, BackupScheduleRules.Decide(Stored(), failed, Utc, At("2026-10-05T04:30:00Z"), At("2026-10-05T03:30:00Z")));

        // Interrupted by the restart itself.
        var interrupted = failed with { Error = JobErrors.InterruptedByRestart };
        Assert.Null(BackupScheduleRules.RetryAt(Stored(), interrupted, ProcessStart));

        // The schedule changed after the failure.
        Assert.Null(BackupScheduleRules.RetryAt(Stored(changed: "2026-10-05T03:10:00Z"), failed, ProcessStart));
        Assert.NotNull(BackupScheduleRules.RetryAt(Stored(changed: "2026-10-05T02:00:00Z"), failed, ProcessStart));
    }

    [Fact]
    public void ARunningAttemptTakesItsJobsOutcome()
    {
        var running = Attempt("2026-10-05T03:00:01Z", BackupAttemptOutcome.Running);
        JobSummary Job(JobStatus status, string? error = null) =>
            new(running.JobId, BackupService.JobType, status, 0, null, running.StartedUtc, running.StartedUtc, status is JobStatus.Succeeded or JobStatus.Failed ? At("2026-10-05T03:00:30Z") : null, null, error);

        Assert.Same(running, BackupScheduleRules.Reconcile(running, Job(JobStatus.Running)));
        Assert.Equal(
            running with { Outcome = BackupAttemptOutcome.Succeeded, FinishedUtc = At("2026-10-05T03:00:30Z") },
            BackupScheduleRules.Reconcile(running, Job(JobStatus.Succeeded)));
        Assert.Equal(
            running with { Outcome = BackupAttemptOutcome.Failed, FinishedUtc = At("2026-10-05T03:00:30Z"), Error = "disk full" },
            BackupScheduleRules.Reconcile(running, Job(JobStatus.Failed, "disk full")));
        Assert.Equal(BackupAttemptOutcome.Failed, BackupScheduleRules.Reconcile(running, null).Outcome);

        var done = Attempt("2026-10-04T03:00:01Z");
        Assert.Same(done, BackupScheduleRules.Reconcile(done, Job(JobStatus.Failed)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    [InlineData(-1)]
    public void AKeepCountOutsideOneTo365IsRefused(int keep)
    {
        var (schedule, errors) = BackupSchedule.Parse(new BackupScheduleInput(true, "daily", "03:00", keep));

        Assert.Null(schedule);
        Assert.Equal([BackupSchedule.KeepField], errors.Keys);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(365)]
    public void AKeepCountFromOneTo365IsAccepted(int keep)
    {
        var (schedule, errors) = BackupSchedule.Parse(new BackupScheduleInput(false, "weekly", "23:59", keep));

        Assert.Empty(errors);
        Assert.Equal(new BackupSchedule(false, BackupFrequency.Weekly, new TimeOnly(23, 59), keep), schedule);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("3:00")]
    [InlineData("03:00:00")]
    [InlineData("03.00")]
    [InlineData("")]
    public void ATimeThatIsNotHhMmIsRefused(string time)
    {
        var (_, errors) = BackupSchedule.Parse(new BackupScheduleInput(true, "daily", time, 7));

        Assert.Equal([BackupSchedule.TimeField], errors.Keys);
    }

    [Fact]
    public void EveryFieldIsRequiredAndAnUnknownFrequencyIsRefused()
    {
        var (_, missing) = BackupSchedule.Parse(null, "backupSchedule.");
        Assert.Equal(
            ["backupSchedule.enabled", "backupSchedule.frequency", "backupSchedule.keep", "backupSchedule.time"],
            missing.Keys.Order(StringComparer.Ordinal));

        var (_, monthly) = BackupSchedule.Parse(new BackupScheduleInput(true, "monthly", "03:00", 7));
        Assert.Equal([BackupSchedule.FrequencyField], monthly.Keys);
    }

    private static BackupArchive Archive(string kind, int day, BackupValidity validity = BackupValidity.Valid) =>
        new(BackupLocation.Data, $"n8tracks-backup-202610{day:00}-030000-v0.1.0-{kind}.zip", 100, At($"2026-10-{day:00}T03:00:00Z"), "0.1.0", kind, validity);

    [Fact]
    public void AnEighthScheduledSuccessDeletesExactlyTheOldestScheduledOne()
    {
        var scheduled = Enumerable.Range(1, 8).Select(static day => Archive("scheduled", day)).ToList();
        var manual = new[] { Archive("manual", 1), Archive("manual", 9) };

        var doomed = BackupRetention.Select([.. scheduled, .. manual], keep: 7);

        Assert.Equal([scheduled[0]], doomed);
    }

    [Fact]
    public void RetentionNeverDeletesManualSafetyInvalidOrNewerArchives()
    {
        var others = new[]
        {
            Archive("manual", 1),
            Archive("safety", 2),
            Archive("scheduled", 3, BackupValidity.Invalid),
            Archive("scheduled", 4, BackupValidity.Newer),
            Archive("future-kind", 5),
        };
        var scheduled = new[] { Archive("scheduled", 6), Archive("scheduled", 7), Archive("scheduled", 8) };

        Assert.Empty(BackupRetention.Select(others, keep: 1));
        Assert.Equal([scheduled[0], scheduled[1]], BackupRetention.Select([.. others, .. scheduled], keep: 1));
        Assert.Empty(BackupRetention.Select([.. others, .. scheduled], keep: 3));
    }

    [Fact]
    public void TheSchedulerLooksJustAfterEachWholeMinute()
    {
        var past = TimeSpan.FromSeconds(2);

        Assert.Equal(TimeSpan.FromSeconds(2), BackupScheduler.UntilNextLook(At("2026-10-05T10:00:00Z"), past));
        Assert.Equal(TimeSpan.FromSeconds(1), BackupScheduler.UntilNextLook(At("2026-10-05T10:00:01Z"), past));
        Assert.Equal(TimeSpan.FromSeconds(60), BackupScheduler.UntilNextLook(At("2026-10-05T10:00:02Z"), past));
        Assert.Equal(TimeSpan.FromSeconds(32), BackupScheduler.UntilNextLook(At("2026-10-05T10:00:30Z"), past));
    }
}
