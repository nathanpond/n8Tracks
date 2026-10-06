using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>Where provider tombstones are kept (<c>provider_tombstones</c>). Writes run inside the caller's transaction.</summary>
public interface IProviderTombstoneStore
{
    /// <summary>The tombstone of <paramref name="sunoId"/>, or null.</summary>
    Task<ProviderTombstone?> FindAsync(string sunoId, CancellationToken cancellationToken);

    /// <summary>
    /// The Suno IDs, with Suno's titles, of those of the live Generations <paramref name="generationIds"/>
    /// that have one; a Generation without Suno data is left out.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> SunoClipsOfAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken);

    /// <summary>Writes each tombstone, replacing one already held for its Suno ID.</summary>
    Task SaveAsync(IReadOnlyCollection<ProviderTombstone> tombstones, CancellationToken cancellationToken);

    /// <summary>Removes the tombstone of <paramref name="sunoId"/>; true when there was one.</summary>
    Task<bool> RemoveAsync(string sunoId, CancellationToken cancellationToken);
}

/// <summary>
/// Provider tombstones (#130): record, check, remove. Every deletion of a Generation, alone (#124),
/// with its Version (#101), or with its Song (#102), records a tombstone for each Generation that has
/// a Suno ID, inside the deletion's transaction and before the Generation goes into retention. The
/// attach service refuses a tombstoned Suno ID unless the user chose Reimport for it (invariant 3:
/// imports never silently change the catalog), and the export classifier calls it <c>deleted</c>.
/// A tombstone is removed when its Generation comes back from retention (the retained Generation's
/// restore rule removes it, whatever ran the restore) or when a Reimport attaches the clip afresh.
/// Nothing else removes one: the retention prune never touches them.
/// </summary>
public sealed class TombstoneService(IProviderTombstoneStore store)
{
    /// <summary>The tombstone of <paramref name="sunoId"/>, or null when the clip was never deleted from n8Tracks (or came back).</summary>
    public Task<ProviderTombstone?> FindAsync(string sunoId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sunoId);

        return store.FindAsync(sunoId, cancellationToken);
    }

    /// <summary>
    /// Inside the deleting transaction, while the Generations <paramref name="generationIds"/> are still
    /// live: a tombstone, deleted at <paramref name="deletedUtc"/>, for each of them that has a Suno ID.
    /// Returns how many were written.
    /// </summary>
    internal async Task<int> RecordForAsync(IReadOnlyCollection<Guid> generationIds, DateTimeOffset deletedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationIds);
        if (generationIds.Count == 0)
        {
            return 0;
        }

        var clips = await store.SunoClipsOfAsync(generationIds, cancellationToken).ConfigureAwait(false);
        if (clips.Count > 0)
        {
            await store.SaveAsync(
                [.. clips.Select(clip => new ProviderTombstone(clip.Key, ProviderTombstoneKind.Clip, deletedUtc, clip.Value))],
                cancellationToken).ConfigureAwait(false);
        }

        return clips.Count;
    }

    /// <summary>Inside the caller's transaction: removes the tombstone of <paramref name="sunoId"/> (a Reimport); true when there was one.</summary>
    internal Task<bool> RemoveAsync(string sunoId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sunoId);

        return store.RemoveAsync(sunoId, cancellationToken);
    }
}
