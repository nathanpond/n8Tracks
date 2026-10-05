using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Catalog;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class TagStore(N8TracksDbContext context) : ITagStore
{
    /// <summary>The order Tags are listed in everywhere: by name ignoring case (invariant culture), then exactly.</summary>
    internal static IOrderedEnumerable<T> Alphabetical<T>(IEnumerable<T> tags, Func<T, string> name) =>
        tags.OrderBy(name, StringComparer.InvariantCultureIgnoreCase).ThenBy(name, StringComparer.Ordinal);

    public async Task<IReadOnlyList<TagUsage>> ListAsync(CancellationToken cancellationToken)
    {
        var tags = await context.Tags.AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var counts = await context.SongTags.AsNoTracking()
            .GroupBy(static songTag => songTag.TagId)
            .Select(static group => new { TagId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.TagId, static group => group.Count, cancellationToken)
            .ConfigureAwait(false);

        return [.. Alphabetical(tags, static tag => tag.Name)
            .Select(tag => new TagUsage(new Tag(tag.Id, tag.Name, tag.Colour), counts.GetValueOrDefault(tag.Id), tag.Revision))];
    }

    public async Task<TagUsage?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Tags.AsNoTracking()
            .SingleOrDefaultAsync(tag => tag.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        var count = await context.SongTags.AsNoTracking()
            .CountAsync(songTag => songTag.TagId == id, cancellationToken)
            .ConfigureAwait(false);
        return new TagUsage(new Tag(found.Id, found.Name, found.Colour), count, found.Revision);
    }

    public async Task<Tag?> FindByNameKeyAsync(string nameKey, CancellationToken cancellationToken)
    {
        var found = await context.Tags.AsNoTracking()
            .SingleOrDefaultAsync(tag => tag.NameKey == nameKey, cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : new Tag(found.Id, found.Name, found.Colour);
    }

    public async Task<IReadOnlyList<Guid>> FindExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.ToList();
        return await context.Tags.AsNoTracking()
            .Where(tag => wanted.Contains(tag.Id))
            .Select(static tag => tag.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByColourAsync(CancellationToken cancellationToken) =>
        await context.Tags.AsNoTracking()
            .GroupBy(static tag => tag.Colour)
            .Select(static group => new { Colour = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.Colour, static group => group.Count, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

    public async Task AddAsync(Tag tag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tag);

        var record = new TagRecord { Id = tag.Id, Name = tag.Name, NameKey = tag.NameKey, Colour = tag.Colour };
        context.Tags.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task<bool> TryUpdateAsync(Guid id, string name, string colour, int revision, CancellationToken cancellationToken)
    {
        var nameKey = TagRules.NameKey(name);

        // One conditional statement: the revision check and the write cannot be split by another writer.
        var count = await context.Tags
            .Where(tag => tag.Id == id && tag.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(tag => tag.Name, name)
                    .SetProperty(tag => tag.NameKey, nameKey)
                    .SetProperty(tag => tag.Colour, colour)
                    .SetProperty(tag => tag.Revision, tag => tag.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        return count == 1;
    }

    public async Task<bool> TryRaiseRevisionAsync(Guid id, int revision, CancellationToken cancellationToken)
    {
        var count = await context.Tags
            .Where(tag => tag.Id == id && tag.Revision == revision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(tag => tag.Revision, tag => tag.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
        return count == 1;
    }

    public async Task<int> MoveSongsAsync(IReadOnlyCollection<Guid> from, Guid? to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);

        var sources = from.ToList();
        var songIds = await context.SongTags.AsNoTracking()
            .Where(songTag => sources.Contains(songTag.TagId))
            .Select(static songTag => songTag.SongId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (songIds.Count == 0)
        {
            return 0;
        }

        await context.SongTags
            .Where(songTag => sources.Contains(songTag.TagId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (to is { } target)
        {
            var holding = await context.SongTags.AsNoTracking()
                .Where(songTag => songTag.TagId == target && songIds.Contains(songTag.SongId))
                .Select(static songTag => songTag.SongId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var added = songIds.Except(holding).Select(songId => new SongTagRecord { SongId = songId, TagId = target }).ToList();
            if (added.Count > 0)
            {
                context.SongTags.AddRange(added);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                foreach (var record in added)
                {
                    context.Entry(record).State = EntityState.Detached;
                }
            }
        }

        // Each Song's Tag list was written as a whole, so a client holding the old list gets a conflict.
        var updated = UtcText.From(now);
        await context.Songs
            .Where(song => songIds.Contains(song.Id))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(song => song.UpdatedUtc, updated)
                    .SetProperty(song => song.Revision, song => song.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        return songIds.Count;
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var doomed = ids.ToList();
        await context.Tags
            .Where(tag => doomed.Contains(tag.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReplaceSongTagsAsync(Guid songId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tagIds);

        var kept = tagIds.ToList();
        await context.SongTags
            .Where(songTag => songTag.SongId == songId && !kept.Contains(songTag.TagId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        var present = await context.SongTags.AsNoTracking()
            .Where(songTag => songTag.SongId == songId)
            .Select(static songTag => songTag.TagId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var added = kept.Where(id => !present.Contains(id)).Select(id => new SongTagRecord { SongId = songId, TagId = id }).ToList();
        if (added.Count == 0)
        {
            return;
        }

        context.SongTags.AddRange(added);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in added)
        {
            context.Entry(record).State = EntityState.Detached;
        }
    }
}
