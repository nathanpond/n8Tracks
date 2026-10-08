using n8Tracks.Application.Auth;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Media;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Notifications;

/// <summary>
/// Reading and acting on notifications (#231): the list with its unread counts, marking successes
/// read, dismissing, and Retry, which calls the same application-service method the original action
/// did. Reading needs <c>catalog.read</c>; everything else is the signed-in user's alone (the endpoints
/// enforce both). It holds no catalog type: an import's retry goes through <see cref="ImportCommitService"/>,
/// which the Version immutability guard covers.
/// </summary>
public sealed class NotificationService(
    INotificationStore store,
    IExclusiveTransaction transaction,
    MediaScanService scans,
    BackupService backups,
    ExportStagingService exports,
    ImportCommitService commits,
    TimeProvider time)
{
    /// <summary>How many notifications a page holds.</summary>
    public const int PageSize = 30;

    /// <summary>The most IDs one mark-read takes.</summary>
    public const int MaximumReadIds = 200;

    /// <summary>A page, newest first: those not dismissed, or every one with <paramref name="history"/>; and the unread counts.</summary>
    public async Task<NotificationPage> ListAsync(bool history, int page, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        var (items, total) = await store.ListAsync(history, page, PageSize, cancellationToken).ConfigureAwait(false);
        var unread = await store.UnreadAsync(cancellationToken).ConfigureAwait(false);
        return new NotificationPage(items, page, PageSize, total, unread);
    }

    /// <summary>Marks read the successes among <paramref name="ids"/>; a warning or failure stays unread until dismissed. Returns how many.</summary>
    public Task<int> MarkReadAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return ids.Count == 0
            ? Task.FromResult(0)
            : transaction.RunAsync(ct => store.MarkSuccessesReadAsync(ids, time.GetUtcNow(), ct), cancellationToken);
    }

    /// <summary>Dismisses one; false when there is no such notification. Dismissing it again changes nothing.</summary>
    public Task<bool> DismissAsync(Guid id, CancellationToken cancellationToken = default) =>
        transaction.RunAsync(ct => store.DismissAsync(id, time.GetUtcNow(), ct), cancellationToken);

    /// <summary>Dismisses every notification not dismissed yet; returns how many.</summary>
    public Task<int> DismissAllAsync(CancellationToken cancellationToken = default) =>
        transaction.RunAsync(ct => store.DismissAllAsync(time.GetUtcNow(), ct), cancellationToken);

    /// <summary>
    /// Starts the notification's work again, when it offers Retry, and marks it retried (it stays listed).
    /// Refused while the same work is queued or running.
    /// </summary>
    public async Task<NotificationRetryOutcome> RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } notification)
        {
            return new NotificationRetryOutcome.NotFound();
        }

        if (!notification.Retryable)
        {
            return new NotificationRetryOutcome.NotRetryable();
        }

        var started = notification.Retry!.Action switch
        {
            NotificationRetryActions.MediaScan => await RetryScanAsync(cancellationToken).ConfigureAwait(false),
            NotificationRetryActions.Backup => await RetryBackupAsync(cancellationToken).ConfigureAwait(false),
            NotificationRetryActions.SunoImport when notification.Retry.Subject is { } exportId => await RetryImportAsync(exportId, cancellationToken).ConfigureAwait(false),
            _ => new NotificationRetryOutcome.NotRetryable(),
        };

        if (started is NotificationRetryOutcome.Started)
        {
            await transaction.RunAsync(ct => store.MarkRetriedAsync(id, time.GetUtcNow(), ct), cancellationToken).ConfigureAwait(false);
        }

        return started;
    }

    private async Task<NotificationRetryOutcome> RetryScanAsync(CancellationToken cancellationToken)
    {
        var start = await scans.StartAsync(MediaScanTrigger.Manual, cancellationToken).ConfigureAwait(false);
        return start.AlreadyInProgress ? new NotificationRetryOutcome.InProgress() : new NotificationRetryOutcome.Started(start.JobId);
    }

    private async Task<NotificationRetryOutcome> RetryBackupAsync(CancellationToken cancellationToken)
    {
        var start = await backups.StartAsync(BackupKind.Manual, cancellationToken).ConfigureAwait(false);
        return start.AlreadyInProgress || start.Deferred ? new NotificationRetryOutcome.InProgress() : new NotificationRetryOutcome.Started(start.JobId);
    }

    /// <summary>
    /// Commits the export again at its current revision. Its choices are the user's own: the failed commit
    /// applied nothing, and putting it back to ready only turned a record a Generation holds now into Skip.
    /// </summary>
    private async Task<NotificationRetryOutcome> RetryImportAsync(Guid exportId, CancellationToken cancellationToken)
    {
        if (await exports.FindAsync(exportId, null, cancellationToken).ConfigureAwait(false) is not { } view)
        {
            return new NotificationRetryOutcome.NotRetryable();
        }

        return view.Export.State switch
        {
            SunoExportState.Committing => new NotificationRetryOutcome.InProgress(),
            SunoExportState.Ready => await commits.CommitAsync(exportId, view.Export.Revision, cancellationToken).ConfigureAwait(false) switch
            {
                ImportCommitOutcome.Started started => new NotificationRetryOutcome.Started(started.JobId),
                ImportCommitOutcome.InProgress => new NotificationRetryOutcome.InProgress(),
                _ => new NotificationRetryOutcome.NotRetryable(),
            },
            _ => new NotificationRetryOutcome.NotRetryable(),
        };
    }
}
