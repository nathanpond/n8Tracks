using n8Tracks.Application.Auth;

namespace n8Tracks.Application.Notifications;

/// <summary>
/// Records notifications (#231), each in a transaction of its own. A failure of scheduled work
/// coalesces into the newest notification of its key while that one is a failure still standing; work
/// that did not fail (a success, or a warning such as a backup on the data volume) resolves the earlier
/// warnings and failures of its topic; a subject already recorded is not
/// recorded again. Read or dismissed notifications are pruned as each is recorded: after
/// <see cref="Retention"/>, and beyond the newest <see cref="KeepDone"/>. Recording never fails the
/// work that ended: a failure to record is logged and swallowed. Callers never hold a transaction.
/// </summary>
public sealed class NotificationRecorder(
    INotificationStore store,
    IExclusiveTransaction transaction,
    INotificationLog log,
    TimeProvider time)
{
    /// <summary>How long a notification is kept once it was read or dismissed.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>The most read or dismissed notifications kept.</summary>
    public const int KeepDone = 500;

    /// <summary>Records <paramref name="draft"/>; returns the notification it went to, or null when it was not recorded.</summary>
    public async Task<Guid?> RecordAsync(NotificationDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        try
        {
            return await transaction.RunAsync(ct => RecordInsideAsync(draft, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log.RecordFailed(draft.Kind, exception);
            return null;
        }
    }

    /// <summary>
    /// After a restore replaced the database: every notification the archive held is marked read and
    /// from before the restore, then <paramref name="draft"/> (the restore's own) is recorded.
    /// </summary>
    public async Task<Guid?> RecordRestoredAsync(NotificationDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        try
        {
            return await transaction.RunAsync(
                async ct =>
                {
                    await store.MarkBeforeRestoreAsync(time.GetUtcNow(), ct).ConfigureAwait(false);
                    return await RecordInsideAsync(draft, ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log.RecordFailed(draft.Kind, exception);
            return null;
        }
    }

    private async Task<Guid?> RecordInsideAsync(NotificationDraft draft, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var occurred = draft.OccurredUtc ?? now;
        if (draft.Subject is { } subject && await store.HasSubjectAsync(draft.Kind, subject, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (draft.Severity != NotificationSeverity.Failure && draft.Topic is { } topic)
        {
            await store.ResolveAsync(topic, now, cancellationToken).ConfigureAwait(false);
        }

        if (draft.Silent)
        {
            return null;
        }

        Guid id;
        if (draft.Severity == NotificationSeverity.Failure
            && draft.CoalesceKey is { } key
            && await store.FindLatestAsync(key, cancellationToken).ConfigureAwait(false) is { TakesRepeats: true } previous)
        {
            id = previous.Id;
            await store.RepeatAsync(id, draft, occurred, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            id = Guid.CreateVersion7(now);
            await store.AddAsync(
                new Notification(id, draft.Kind, draft.Severity, draft.Summary, draft.Detail, draft.Link, draft.Retry, 1, occurred, occurred, null, null, null, null, false)
                {
                    CoalesceKey = draft.CoalesceKey,
                    Topic = draft.Topic,
                    Subject = draft.Subject,
                },
                cancellationToken).ConfigureAwait(false);
        }

        await store.PruneAsync(now - Retention, KeepDone, cancellationToken).ConfigureAwait(false);
        return id;
    }
}
