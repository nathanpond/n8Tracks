using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>A Genre and how many Songs have it.</summary>
public sealed record GenreUsage(Genre Genre, int SongCount);

/// <summary>
/// Where Genres and the Songs' Genres are kept. Writes are made inside the caller's transaction,
/// which has checked what it writes.
/// </summary>
public interface IGenreStore
{
    /// <summary>Every Genre with its Song count, alphabetically (by <see cref="GenreRules.NameKey"/>).</summary>
    Task<IReadOnlyList<GenreUsage>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The Genre whose name has <paramref name="nameKey"/>, or null.</summary>
    Task<Genre?> FindByNameKeyAsync(string nameKey, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="ids"/> are Genres.</summary>
    Task<IReadOnlyList<Guid>> FindExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Stores a new Genre. Its name key must be unused; the database refuses a taken one.</summary>
    Task AddAsync(Genre genre, CancellationToken cancellationToken);

    /// <summary>
    /// Makes <paramref name="genreIds"/> (each a Genre, each once) the Genres of the Song with
    /// <paramref name="songId"/>: the ones not listed come off it; the Genres themselves stay.
    /// </summary>
    Task ReplaceSongGenresAsync(Guid songId, IReadOnlyCollection<Guid> genreIds, CancellationToken cancellationToken);
}
