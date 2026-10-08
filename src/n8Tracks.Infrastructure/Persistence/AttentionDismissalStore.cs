using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Dashboard;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>Dismissed problems (#229) in <c>attention_dismissals</c>. It writes nothing else.</summary>
internal sealed class AttentionDismissalStore(N8TracksDbContext context) : IAttentionDismissalStore
{
    public Task<bool> IsDismissedAsync(AttentionKind kind, Guid subject, CancellationToken cancellationToken)
    {
        var name = AttentionKinds.NameOf(kind);
        return context.AttentionDismissals.AsNoTracking().AnyAsync(row => row.Kind == name && row.Subject == subject, cancellationToken);
    }

    public async Task AddAsync(AttentionKind kind, Guid subject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await IsDismissedAsync(kind, subject, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var record = new AttentionDismissalRecord { Kind = AttentionKinds.NameOf(kind), Subject = subject, DismissedUtc = UtcText.From(now) };
        context.AttentionDismissals.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }
}
