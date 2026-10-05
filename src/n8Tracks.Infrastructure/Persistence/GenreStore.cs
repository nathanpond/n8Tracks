using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Catalog;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class GenreStore(N8TracksDbContext context) : IGenreStore
{
    public async Task<IReadOnlyList<GenreUsage>> ListAsync(CancellationToken cancellationToken)
    {
        var genres = await context.Genres.AsNoTracking()
            .OrderBy(static genre => genre.NameKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var counts = await context.SongGenres.AsNoTracking()
            .GroupBy(static songGenre => songGenre.GenreId)
            .Select(static group => new { GenreId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.GenreId, static group => group.Count, cancellationToken)
            .ConfigureAwait(false);

        return [.. genres.Select(genre => new GenreUsage(new Genre(genre.Id, genre.Name), counts.GetValueOrDefault(genre.Id), genre.Revision))];
    }

    public async Task<GenreUsage?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Genres.AsNoTracking()
            .SingleOrDefaultAsync(genre => genre.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        var count = await context.SongGenres.AsNoTracking()
            .CountAsync(songGenre => songGenre.GenreId == id, cancellationToken)
            .ConfigureAwait(false);
        return new GenreUsage(new Genre(found.Id, found.Name), count, found.Revision);
    }

    public async Task<Genre?> FindByNameKeyAsync(string nameKey, CancellationToken cancellationToken)
    {
        var found = await context.Genres.AsNoTracking()
            .SingleOrDefaultAsync(genre => genre.NameKey == nameKey, cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : new Genre(found.Id, found.Name);
    }

    public async Task<IReadOnlyList<Guid>> FindExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.ToList();
        return await context.Genres.AsNoTracking()
            .Where(genre => wanted.Contains(genre.Id))
            .Select(static genre => genre.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(Genre genre, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(genre);

        var record = new GenreRecord { Id = genre.Id, Name = genre.Name, NameKey = genre.NameKey };
        context.Genres.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task<bool> TryRenameAsync(Guid id, string name, int revision, CancellationToken cancellationToken)
    {
        var nameKey = GenreRules.NameKey(name);

        // One conditional statement: the revision check and the write cannot be split by another writer.
        var count = await context.Genres
            .Where(genre => genre.Id == id && genre.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(genre => genre.Name, name)
                    .SetProperty(genre => genre.NameKey, nameKey)
                    .SetProperty(genre => genre.Revision, genre => genre.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        return count == 1;
    }

    public async Task<bool> TryRaiseRevisionAsync(Guid id, int revision, CancellationToken cancellationToken)
    {
        var count = await context.Genres
            .Where(genre => genre.Id == id && genre.Revision == revision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(genre => genre.Revision, genre => genre.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
        return count == 1;
    }

    public async Task<int> MoveSongsAsync(IReadOnlyCollection<Guid> from, Guid? to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);

        var sources = from.ToList();
        var songIds = await context.SongGenres.AsNoTracking()
            .Where(songGenre => sources.Contains(songGenre.GenreId))
            .Select(static songGenre => songGenre.SongId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (songIds.Count == 0)
        {
            return 0;
        }

        await context.SongGenres
            .Where(songGenre => sources.Contains(songGenre.GenreId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (to is { } target)
        {
            var holding = await context.SongGenres.AsNoTracking()
                .Where(songGenre => songGenre.GenreId == target && songIds.Contains(songGenre.SongId))
                .Select(static songGenre => songGenre.SongId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var added = songIds.Except(holding).Select(songId => new SongGenreRecord { SongId = songId, GenreId = target }).ToList();
            if (added.Count > 0)
            {
                context.SongGenres.AddRange(added);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                foreach (var record in added)
                {
                    context.Entry(record).State = EntityState.Detached;
                }
            }
        }

        // Each Song's Genre list was written as a whole, so a client holding the old list gets a conflict.
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
        await context.Genres
            .Where(genre => doomed.Contains(genre.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReplaceSongGenresAsync(Guid songId, IReadOnlyCollection<Guid> genreIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(genreIds);

        var kept = genreIds.ToList();
        await context.SongGenres
            .Where(songGenre => songGenre.SongId == songId && !kept.Contains(songGenre.GenreId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        var present = await context.SongGenres.AsNoTracking()
            .Where(songGenre => songGenre.SongId == songId)
            .Select(static songGenre => songGenre.GenreId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var added = kept.Where(id => !present.Contains(id)).Select(id => new SongGenreRecord { SongId = songId, GenreId = id }).ToList();
        if (added.Count == 0)
        {
            return;
        }

        context.SongGenres.AddRange(added);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in added)
        {
            context.Entry(record).State = EntityState.Detached;
        }
    }
}
