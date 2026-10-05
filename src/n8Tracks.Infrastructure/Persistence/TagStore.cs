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
