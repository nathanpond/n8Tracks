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

        return [.. genres.Select(genre => new GenreUsage(new Genre(genre.Id, genre.Name), counts.GetValueOrDefault(genre.Id)))];
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
