using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Media;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Application.Notifications;

/// <summary>Turns one job type's finished jobs into notifications: its summary, link, and whether Retry is safe.</summary>
public interface IJobNotificationProducer : INotificationProducer
{
    /// <summary>The job type it reads.</summary>
    string JobType { get; }

    /// <summary>
    /// The notification <paramref name="job"/> records, or null when it records none. Built from fixed
    /// templates with the numbers of the job's result; the error text and the exception's message are
    /// never read.
    /// </summary>
    NotificationDraft? Draft(FinishedJob job);
}

/// <summary>
/// The job-finished hook (#231): finds the producer registered for the job's type and records what it
/// drafts. A job type with no producer records nothing.
/// </summary>
public sealed class JobNotifications(IEnumerable<IJobNotificationProducer> producers, NotificationRecorder recorder) : IJobFinishedHook
{
    public async Task JobFinishedAsync(FinishedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (producers.FirstOrDefault(producer => producer.JobType == job.Type)?.Draft(job) is { } draft)
        {
            await recorder.RecordAsync(draft with { OccurredUtc = draft.OccurredUtc ?? job.FinishedUtc }, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Reading a job's payload and result, and writing counts into sentences.</summary>
internal static class JobReading
{
    public static string? Text(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;

    public static int Number(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var number) && number.TryGetInt32(out var count)
            ? count
            : 0;

    public static long LongNumber(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var number) && number.TryGetInt64(out var count)
            ? count
            : 0;

    public static Guid? Id(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var id) && id.TryGetGuid(out var guid)
            ? guid
            : null;

    /// <summary>"1 file", "2 files".</summary>
    public static string Count(int count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");

    /// <summary>A span as people say it: "an hour", "2 hours", "30 minutes".</summary>
    public static string Duration(TimeSpan span) =>
        span.TotalHours == 1 ? "an hour"
        : span.TotalHours >= 1 && span.TotalHours == Math.Floor(span.TotalHours) ? Count((int)span.TotalHours, "hour", "hours")
        : Count((int)Math.Ceiling(span.TotalMinutes), "minute", "minutes");

    /// <summary>A size in bytes as people read it: "512 bytes", "1.5 MB".</summary>
    public static string Size(long bytes)
    {
        string[] units = ["KB", "MB", "GB", "TB"];
        if (bytes < 1000)
        {
            return Count((int)bytes, "byte", "bytes");
        }

        var value = (double)bytes;
        var unit = -1;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}

/// <summary>
/// Media scans (#203, #204): a manual scan always records what it found; a startup, scheduled, or
/// recovery scan records only a failure or new unmatched files (a success carrying the number), and
/// otherwise only resolves earlier scan failures. A failed scan offers Retry (a manual scan), and a
/// failed scheduled scan coalesces with the scheduled failures before it.
/// </summary>
public sealed class MediaScanNotifications : IJobNotificationProducer
{
    /// <summary>What a later scan's success resolves.</summary>
    public const string Topic = NotificationKinds.MediaScan;

    /// <summary>What repeated scheduled failures coalesce into.</summary>
    public const string ScheduledKey = NotificationKinds.MediaScan + ":scheduled";

    public string Kind => NotificationKinds.MediaScan;

    public string JobType => MediaScanService.JobType;

    public NotificationDraft? Draft(FinishedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var trigger = MediaScanTriggers.Parse(JobReading.Text(job.Payload, "trigger")) ?? MediaScanTrigger.Manual;
        var subject = job.Id.ToString();
        if (job.Status == JobStatus.Failed)
        {
            var summary = job.Interrupted
                ? $"The {Words(trigger)} was interrupted when n8Tracks stopped."
                : job.Exception is MediaFolderUnavailableException
                    ? $"The {Words(trigger)} failed: the media folder is unavailable."
                    : $"The {Words(trigger)} failed.";
            return new NotificationDraft(Kind, NotificationSeverity.Failure, summary, NotificationLinks.Media)
            {
                Detail = "Nothing was marked missing. Retry scans the media folder again; the Media page has the last scan's counts.",
                Retry = new NotificationRetry(NotificationRetryActions.MediaScan),
                CoalesceKey = trigger == MediaScanTrigger.Scheduled ? ScheduledKey : null,
                Topic = Topic,
                Subject = subject,
            };
        }

        var result = job.Result;
        var newUnmatched = JobReading.Number(result, "newUnmatched");
        if (trigger == MediaScanTrigger.Manual)
        {
            return new NotificationDraft(
                Kind,
                NotificationSeverity.Success,
                $"The media scan found {JobReading.Count(JobReading.Number(result, "seen"), "audio file", "audio files")}: "
                + $"{JobReading.Number(result, "new")} new, {JobReading.Number(result, "changed")} changed, {JobReading.Number(result, "missing")} missing, "
                + $"and {JobReading.Number(result, "unmatched")} unmatched.",
                NotificationLinks.Media)
            {
                Detail = newUnmatched > 0 ? $"{JobReading.Count(newUnmatched, "new file is", "new files are")} not matched to a Generation yet." : null,
                Topic = Topic,
                Subject = subject,
            };
        }

        return newUnmatched > 0
            ? new NotificationDraft(
                Kind,
                NotificationSeverity.Success,
                $"The {Words(trigger)} found {JobReading.Count(newUnmatched, "new unmatched audio file", "new unmatched audio files")}.",
                NotificationLinks.Media)
            {
                Topic = Topic,
                Subject = subject,
            }
            : new NotificationDraft(Kind, NotificationSeverity.Success, string.Empty, NotificationLinks.Media) { Topic = Topic, Silent = true };
    }

    private static string Words(MediaScanTrigger trigger) => trigger switch
    {
        MediaScanTrigger.Startup => "startup media scan",
        MediaScanTrigger.Scheduled => "scheduled media scan",
        MediaScanTrigger.Recovery => "media scan after the media folder came back",
        _ => "media scan",
    };
}

/// <summary>
/// Backups (#71, #72): a success with its size, a warning when it fell back to the data volume, and a
/// failure that offers Retry (a manual backup). A failed scheduled backup says whether the scheduler
/// tries again, and coalesces with the scheduled failures before it.
/// </summary>
public sealed class BackupNotifications : IJobNotificationProducer
{
    /// <summary>What a later backup's success resolves.</summary>
    public const string Topic = NotificationKinds.Backup;

    /// <summary>What repeated scheduled failures coalesce into.</summary>
    public const string ScheduledKey = NotificationKinds.Backup + ":scheduled";

    public string Kind => NotificationKinds.Backup;

    public string JobType => BackupService.JobType;

    public NotificationDraft? Draft(FinishedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var kind = BackupKinds.Parse(JobReading.Text(job.Payload, "kind")) ?? BackupKind.Manual;
        var retry = job.Payload is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("retry", out var flag) && flag.ValueKind == JsonValueKind.True;
        var words = kind == BackupKind.Scheduled ? "scheduled backup" : "backup";
        var subject = job.Id.ToString();
        if (job.Status == JobStatus.Failed)
        {
            // The scheduler retries a failed scheduled backup once, but never one a restart interrupted.
            var summary = kind == BackupKind.Scheduled
                ? job.Interrupted
                    ? "The scheduled backup was interrupted when n8Tracks stopped; the next one runs at its planned time."
                    : retry
                        ? "The scheduled backup failed again; the next one runs at its planned time."
                        : $"The scheduled backup failed; n8Tracks tries again in {JobReading.Duration(BackupScheduleRules.RetryDelay)}."
                : job.Interrupted ? "The backup was interrupted when n8Tracks stopped." : "The backup failed.";
            return new NotificationDraft(Kind, NotificationSeverity.Failure, summary, NotificationLinks.Backups)
            {
                Detail = "No archive was kept from this run, and no older one was deleted. The Backups page has the reason; Retry starts a backup now.",
                Retry = new NotificationRetry(NotificationRetryActions.Backup),
                CoalesceKey = kind == BackupKind.Scheduled ? ScheduledKey : null,
                Topic = Topic,
                Subject = subject,
            };
        }

        var size = JobReading.Size(JobReading.LongNumber(job.Result, "size"));
        return JobReading.Text(job.Result, "location") == "data"
            ? new NotificationDraft(
                Kind,
                NotificationSeverity.Warning,
                $"The {words} finished ({size}), but it is on the data volume: the backup folder could not be used.",
                NotificationLinks.Backups)
            {
                Detail = "A backup on the same disk as the data is lost with it. Make the backup folder available and writable.",
                Topic = Topic,
                Subject = subject,
            }
            : new NotificationDraft(Kind, NotificationSeverity.Success, $"The {words} finished ({size}).", NotificationLinks.Backups)
            {
                Topic = Topic,
                Subject = subject,
            };
    }
}

/// <summary>
/// Suno import commits (#140): a success with the records imported, a warning when some failed, and a
/// failure that offers Retry (the commit again) only when the failed commit applied nothing; a commit
/// interrupted with no result is not offered one. A commit that found its export no longer committing
/// records nothing.
/// </summary>
public sealed class ImportCommitNotifications : IJobNotificationProducer
{
    public string Kind => NotificationKinds.SunoImport;

    public string JobType => ImportCommitService.JobType;

    /// <summary>What a later commit of the same export resolves.</summary>
    public static string Topic(Guid exportId) => $"{NotificationKinds.SunoImport}:{exportId}";

    public NotificationDraft? Draft(FinishedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (JobReading.Id(job.Payload, "exportId") is not { } exportId)
        {
            return null;
        }

        var subject = job.Id.ToString();
        var link = NotificationLinks.Export(exportId);
        if (job.Status == JobStatus.Failed)
        {
            var nothingApplied = !job.Interrupted && job.Exception is { } exception && ImportCommitService.AppliedNothing(exception);
            return new NotificationDraft(
                Kind,
                NotificationSeverity.Failure,
                nothingApplied
                    ? "The Suno import failed before it imported anything; the export is ready to commit again."
                    : job.Interrupted
                        ? "The Suno import was interrupted when n8Tracks stopped; the export is back for review."
                        : "The Suno import failed part way; the export is back for review.",
                link)
            {
                Detail = nothingApplied
                    ? "Nothing in the catalog changed. Retry commits the export again with the choices you confirmed."
                    : "What it imported is kept and now shows as linked. Review the export and confirm it again to import the rest.",
                Retry = nothingApplied ? new NotificationRetry(NotificationRetryActions.SunoImport, exportId) : null,
                Topic = Topic(exportId),
                Subject = subject,
            };
        }

        if (job.Result is not { ValueKind: JsonValueKind.Object } result || JobReading.Text(result, "state") == "abandoned"
            || !result.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int total = 0, imported = 0, updated = 0, failed = 0;
        foreach (var record in records.EnumerateArray())
        {
            total++;
            switch (JobReading.Text(record, "outcome"))
            {
                case ImportCommitOutcomes.Created:
                    imported++;
                    break;
                case ImportCommitOutcomes.Updated or ImportCommitOutcomes.Moved or ImportCommitOutcomes.Kept:
                    updated++;
                    break;
                case ImportCommitOutcomes.Failed:
                    failed++;
                    break;
                default:
                    break;
            }
        }

        var summary = $"The Suno import finished: {imported} of {JobReading.Count(total, "record", "records")} imported, {updated} updated, and {failed} failed.";
        return new NotificationDraft(Kind, failed > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success, summary, link)
        {
            Detail = failed > 0 ? "The records that failed are offered again at the next sync; the export's result says why each failed." : null,
            Topic = Topic(exportId),
            Subject = subject,
        };
    }
}
