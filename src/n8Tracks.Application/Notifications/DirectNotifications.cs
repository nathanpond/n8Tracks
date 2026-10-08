using n8Tracks.Application.Maintenance;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Notifications;

/// <summary>
/// Suno syncs (#131), recorded directly by the export lifecycle (it is not a job): an export reaching
/// <c>ready</c> is a success with its record count; a failed classification, or a discard with a
/// failure reason, is a failure; a ready export that expired unreviewed is a warning. A cancel, a
/// replacement, and an export abandoned before it was completed record nothing. A ready export
/// resolves the sync failures before it. None offers Retry: the extension syncs again.
/// </summary>
public sealed class SunoSyncNotifications(NotificationRecorder recorder) : INotificationProducer
{
    /// <summary>What a later export reaching ready resolves.</summary>
    public const string Topic = NotificationKinds.SunoSync;

    public string Kind => NotificationKinds.SunoSync;

    /// <summary>The export became ready for review with <paramref name="recordCount"/> records.</summary>
    public Task ReadyAsync(Guid exportId, int recordCount, CancellationToken cancellationToken = default) =>
        recorder.RecordAsync(
            new NotificationDraft(
                Kind,
                NotificationSeverity.Success,
                $"A Suno sync arrived with {JobReading.Count(recordCount, "record", "records")}, ready for review.",
                NotificationLinks.Export(exportId))
            {
                Topic = Topic,
                Subject = $"{exportId}:ready",
            },
            cancellationToken);

    /// <summary>The server could not classify the export (or it never finished): it failed with nothing staged.</summary>
    public Task ClassificationFailedAsync(Guid exportId, CancellationToken cancellationToken = default) =>
        FailedAsync(exportId, "A Suno sync failed: n8Tracks could not prepare it for review.", cancellationToken);

    /// <summary>The export was discarded; recorded only when <paramref name="ending"/> says the sync failed.</summary>
    public Task DiscardedAsync(Guid exportId, SunoExportEnding? ending, CancellationToken cancellationToken = default) =>
        ending is { Reason: SunoExportEndReason.Failed }
            ? FailedAsync(exportId, "A Suno sync failed in the extension before it finished.", cancellationToken)
            : Task.CompletedTask;

    /// <summary>A ready export waited longer than its lifetime and expired: nothing of it was imported.</summary>
    public Task ExpiredAsync(Guid exportId, CancellationToken cancellationToken = default) =>
        recorder.RecordAsync(
            new NotificationDraft(
                Kind,
                NotificationSeverity.Warning,
                $"A Suno sync expired after waiting {JobReading.Count((int)SunoExportRules.ReadyLifetime.TotalDays, "day", "days")} for review; nothing of it was imported.",
                NotificationLinks.SunoImports)
            {
                Detail = "Sync again from the extension to review the library as it is now.",
                Subject = $"{exportId}:expired",
            },
            cancellationToken);

    private Task FailedAsync(Guid exportId, string summary, CancellationToken cancellationToken) =>
        recorder.RecordAsync(
            new NotificationDraft(Kind, NotificationSeverity.Failure, summary, NotificationLinks.SunoImports)
            {
                Detail = "Nothing was imported. The Suno page says where it stopped; sync again from the extension.",
                Topic = Topic,
                Subject = $"{exportId}:failed",
            },
            cancellationToken);
}

/// <summary>
/// Restores (#74), recorded by the restore's runner and, for one a restart cut short, at the next start.
/// A restore that succeeded is a success, written into the restored database after every notification
/// the archive held is marked read and from before the restore. A restore that stopped before replacing
/// anything, or that failed and was rolled back, is a failure. None offers Retry.
/// </summary>
public sealed class RestoreNotifications(NotificationRecorder recorder) : INotificationProducer
{
    public string Kind => NotificationKinds.Restore;

    /// <summary>
    /// Records how a restore ended. <paramref name="subject"/> names the restore (its ID, or the time it
    /// ended), so the same ending is never recorded twice. A rollback that failed records nothing: the
    /// instance stays in maintenance, and the next start that opens it records the put-back.
    /// </summary>
    public Task RecordAsync(MaintenanceOutcome outcome, string subject, DateTimeOffset occurredUtc, CancellationToken cancellationToken = default)
    {
        var draft = outcome switch
        {
            MaintenanceOutcome.Succeeded => new NotificationDraft(
                Kind,
                NotificationSeverity.Success,
                "The restore finished: n8Tracks now holds the backup's data, and every session was ended.",
                NotificationLinks.Backups)
            {
                Detail = "The notifications from the backup are kept as read and marked as from before the restore. A safety backup of the data before it is on the Backups page.",
            },
            MaintenanceOutcome.RolledBack => new NotificationDraft(
                Kind,
                NotificationSeverity.Failure,
                "The restore failed, and the data from before it was put back.",
                NotificationLinks.Backups)
            {
                Detail = "Nothing of the backup was kept. The Backups page says at which step it failed.",
            },
            MaintenanceOutcome.Failed => new NotificationDraft(
                Kind,
                NotificationSeverity.Failure,
                "The restore failed before it replaced anything; nothing was changed.",
                NotificationLinks.Backups)
            {
                Detail = "The server log has the reason.",
            },
            _ => null,
        };

        if (draft is null)
        {
            return Task.CompletedTask;
        }

        draft = draft with { Subject = subject, OccurredUtc = occurredUtc };
        return outcome == MaintenanceOutcome.Succeeded
            ? recorder.RecordRestoredAsync(draft, cancellationToken)
            : recorder.RecordAsync(draft, cancellationToken);
    }
}

/// <summary>
/// Database migrations with their safety backup (#76), recorded at the first start after an upgrade: a
/// success when this start upgraded the database, and a failure, from the marker the failed upgrade
/// left, at the next start that succeeds. Neither offers Retry.
/// </summary>
public sealed class MigrationNotifications(NotificationRecorder recorder) : INotificationProducer
{
    public string Kind => NotificationKinds.Migration;

    /// <summary>This start applied <paramref name="applied"/> migrations, after a verified safety backup.</summary>
    public Task UpgradedAsync(int applied, string toMigration, CancellationToken cancellationToken = default) =>
        recorder.RecordAsync(
            new NotificationDraft(
                Kind,
                NotificationSeverity.Success,
                $"The database was upgraded: {JobReading.Count(applied, "migration", "migrations")} applied after a safety backup.",
                NotificationLinks.Diagnostics)
            {
                Detail = "The safety backup taken before the upgrade is on the Backups page.",
                Subject = $"upgraded:{toMigration}",
            },
            cancellationToken);

    /// <summary>An earlier start failed to upgrade the database and put its safety backup back.</summary>
    public Task FailedAsync(DateTimeOffset startedUtc, CancellationToken cancellationToken = default) =>
        recorder.RecordAsync(
            new NotificationDraft(
                Kind,
                NotificationSeverity.Failure,
                "An earlier database upgrade failed, and the database from before it was restored from its safety backup.",
                NotificationLinks.Diagnostics)
            {
                Detail = "That version does not try the upgrade again. The server log names the migration that failed.",
                Subject = $"failed:{startedUtc.UtcTicks}",
                OccurredUtc = startedUtc,
            },
            cancellationToken);
}
