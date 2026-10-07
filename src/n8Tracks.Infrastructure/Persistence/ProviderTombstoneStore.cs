using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>The provider tombstones in <c>provider_tombstones</c> (#130).</summary>
internal sealed class ProviderTombstoneStore(N8TracksDbContext context) : IProviderTombstoneStore
{
    public async Task<ProviderTombstone?> FindAsync(string sunoId, CancellationToken cancellationToken)
    {
        var record = await context.ProviderTombstones.AsNoTracking()
            .SingleOrDefaultAsync(row => row.SunoId == sunoId, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : new ProviderTombstone(record.SunoId, ProviderTombstoneKind.Clip, UtcText.Parse(record.DeletedUtc), record.Title);
    }

    public async Task<IReadOnlySet<string>> TombstonedAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoIds);

        var ids = sunoIds.ToList();
        return (await context.ProviderTombstones.AsNoTracking()
            .Where(row => ids.Contains(row.SunoId))
            .Select(static row => row.SunoId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, string?>> SunoClipsOfAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken)
    {
        var clips = await context.Generations.AsNoTracking()
            .Where(generation => generationIds.Contains(generation.Id) && generation.SunoId != null)
            .Select(static generation => new { generation.SunoId, generation.SunoTitle })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return clips.ToDictionary(static clip => clip.SunoId!, static clip => clip.SunoTitle, StringComparer.Ordinal);
    }

    public async Task SaveAsync(IReadOnlyCollection<ProviderTombstone> tombstones, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tombstones);

        var ids = tombstones.Select(static tombstone => tombstone.SunoId).ToList();
        var existing = await context.ProviderTombstones
            .Where(row => ids.Contains(row.SunoId))
            .ToDictionaryAsync(static row => row.SunoId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        var written = new List<ProviderTombstoneRecord>();
        foreach (var tombstone in tombstones)
        {
            if (!existing.TryGetValue(tombstone.SunoId, out var record))
            {
                record = new ProviderTombstoneRecord { SunoId = tombstone.SunoId, Kind = ProviderTombstoneRecord.ClipKind, DeletedUtc = string.Empty };
                context.ProviderTombstones.Add(record);
            }

            record.Kind = ProviderTombstoneRecord.ClipKind;
            record.DeletedUtc = UtcText.From(tombstone.DeletedUtc);
            record.Title = tombstone.Title;
            written.Add(record);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in written)
        {
            context.Entry(record).State = EntityState.Detached;
        }
    }

    public Task<bool> RemoveAsync(string sunoId, CancellationToken cancellationToken) => RemoveAsync(context, sunoId, cancellationToken);

    /// <summary>Removes the tombstone of <paramref name="sunoId"/> through <paramref name="context"/> (a restore's own); true when there was one.</summary>
    internal static async Task<bool> RemoveAsync(N8TracksDbContext context, string sunoId, CancellationToken cancellationToken) =>
        await context.ProviderTombstones.Where(row => row.SunoId == sunoId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
}
