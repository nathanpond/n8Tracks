using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Notifications;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>Notifications (#231) in <c>notifications</c>. It writes nothing else.</summary>
internal sealed class NotificationStore(N8TracksDbContext context) : INotificationStore
{
    private const string Success = "success";

    public async Task<Notification?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await context.Notifications.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToNotification(record);
    }

    public async Task<Notification?> FindLatestAsync(string coalesceKey, CancellationToken cancellationToken)
    {
        var record = await context.Notifications.AsNoTracking()
            .Where(row => row.CoalesceKey == coalesceKey)
            .OrderByDescending(static row => row.OccurredUtc)
            .ThenByDescending(static row => row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : ToNotification(record);
    }

    public Task<bool> HasSubjectAsync(string kind, string subject, CancellationToken cancellationToken) =>
        context.Notifications.AsNoTracking().AnyAsync(row => row.Kind == kind && row.Subject == subject, cancellationToken);

    public async Task AddAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var record = new NotificationRecord
        {
            Id = notification.Id,
            Kind = notification.Kind,
            Severity = NotificationSeverities.Text(notification.Severity),
            Summary = notification.Summary,
            Detail = notification.Detail,
            Link = notification.Link,
            RetryAction = notification.Retry?.Action,
            RetrySubject = notification.Retry?.Subject,
            CoalesceKey = notification.CoalesceKey,
            Topic = notification.Topic,
            Subject = notification.Subject,
            Count = notification.Count,
            FirstOccurredUtc = UtcText.From(notification.FirstOccurredUtc),
            OccurredUtc = UtcText.From(notification.OccurredUtc),
            ReadUtc = Text(notification.ReadUtc),
            DismissedUtc = Text(notification.DismissedUtc),
            RetriedUtc = Text(notification.RetriedUtc),
            ResolvedUtc = Text(notification.ResolvedUtc),
            BeforeRestore = notification.BeforeRestore,
        };
        context.Notifications.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public Task RepeatAsync(Guid id, NotificationDraft draft, DateTimeOffset occurredUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var occurred = UtcText.From(occurredUtc);
        return context.Notifications
            .Where(row => row.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static row => row.Count, static row => row.Count + 1)
                    .SetProperty(static row => row.OccurredUtc, occurred)
                    .SetProperty(static row => row.Summary, draft.Summary)
                    .SetProperty(static row => row.Detail, draft.Detail)
                    .SetProperty(static row => row.ReadUtc, (string?)null),
                cancellationToken);
    }

    public Task<int> ResolveAsync(string topic, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var resolved = UtcText.From(now);
        return context.Notifications
            .Where(row => row.Topic == topic && row.Severity != Success && row.DismissedUtc == null && row.ResolvedUtc == null && !row.BeforeRestore)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static row => row.ResolvedUtc, resolved), cancellationToken);
    }

    public async Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(bool history, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = context.Notifications.AsNoTracking();
        if (!history)
        {
            rows = rows.Where(static row => row.DismissedUtc == null);
        }

        var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await rows
            .OrderByDescending(static row => row.OccurredUtc)
            .ThenByDescending(static row => row.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ([.. records.Select(ToNotification)], total);
    }

    public async Task<NotificationUnreadCounts> UnreadAsync(CancellationToken cancellationToken)
    {
        var counts = await context.Notifications.AsNoTracking()
            .Where(static row => row.ReadUtc == null && row.DismissedUtc == null && row.RetriedUtc == null && row.ResolvedUtc == null)
            .GroupBy(static row => row.Severity)
            .Select(static group => new { Severity = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int Of(string severity) => counts.FirstOrDefault(count => count.Severity == severity)?.Count ?? 0;
        return new NotificationUnreadCounts(Of(Success), Of("warning"), Of("failure"));
    }

    public Task<int> MarkSuccessesReadAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var read = UtcText.From(now);
        return context.Notifications
            .Where(row => ids.Contains(row.Id) && row.Severity == Success && row.ReadUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static row => row.ReadUtc, read), cancellationToken);
    }

    public async Task<bool> DismissAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await context.Notifications.AnyAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var dismissed = UtcText.From(now);
        await context.Notifications
            .Where(row => row.Id == id && row.DismissedUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static row => row.DismissedUtc, dismissed), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public Task<int> DismissAllAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dismissed = UtcText.From(now);
        return context.Notifications
            .Where(static row => row.DismissedUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static row => row.DismissedUtc, dismissed), cancellationToken);
    }

    public async Task<bool> MarkRetriedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var retried = UtcText.From(now);
        return await context.Notifications
            .Where(row => row.Id == id && row.RetriedUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static row => row.RetriedUtc, retried), cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public Task<int> MarkBeforeRestoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var read = UtcText.From(now);
        return context.Notifications
            .Where(static row => !row.BeforeRestore || row.ReadUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static row => row.BeforeRestore, true)
                    .SetProperty(row => row.ReadUtc, row => row.ReadUtc ?? read),
                cancellationToken);
    }

    public async Task<int> PruneAsync(DateTimeOffset doneBefore, int keep, CancellationToken cancellationToken)
    {
        // Done with: dismissed, or a success that was read. An undismissed warning or failure never is.
        var done = context.Notifications.Where(static row => row.DismissedUtc != null || (row.Severity == Success && row.ReadUtc != null));
        var cutoff = UtcText.From(doneBefore);
        var pruned = await done
            .Where(row => string.Compare(row.DismissedUtc ?? row.ReadUtc, cutoff) < 0)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        var beyond = await done
            .OrderByDescending(static row => row.OccurredUtc)
            .ThenByDescending(static row => row.Id)
            .Skip(keep)
            .Select(static row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (beyond.Count > 0)
        {
            pruned += await context.Notifications.Where(row => beyond.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return pruned;
    }

    private static string? Text(DateTimeOffset? time) => time is { } value ? UtcText.From(value) : null;

    private static DateTimeOffset? Time(string? text) => text is null ? null : UtcText.Parse(text);

    private static Notification ToNotification(NotificationRecord record) =>
        new(
            record.Id,
            record.Kind,
            NotificationSeverities.Parse(record.Severity) ?? NotificationSeverity.Failure,
            record.Summary,
            record.Detail,
            record.Link,
            record.RetryAction is { } action ? new NotificationRetry(action, record.RetrySubject) : null,
            record.Count,
            UtcText.Parse(record.FirstOccurredUtc),
            UtcText.Parse(record.OccurredUtc),
            Time(record.ReadUtc),
            Time(record.DismissedUtc),
            Time(record.RetriedUtc),
            Time(record.ResolvedUtc),
            record.BeforeRestore)
        {
            CoalesceKey = record.CoalesceKey,
            Topic = record.Topic,
            Subject = record.Subject,
        };
}
