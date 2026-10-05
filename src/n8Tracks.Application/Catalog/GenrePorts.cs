using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>A Genre, how many Songs have it, and its revision (raised by a rename or a merge into it).</summary>
public sealed record GenreUsage(Genre Genre, int SongCount, int Revision);

/// <summary>
/// Where Genres and the Songs' Genres are kept. Writes are made inside the caller's transaction,
/// which has checked what it writes.
/// </summary>
public interface IGenreStore
{
    /// <summary>Every Genre with its Song count, alphabetically (by <see cref="GenreRules.NameKey"/>).</summary>
    Task<IReadOnlyList<GenreUsage>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The Genre with <paramref name="id"/>, its Song count, and its revision, or null.</summary>
    Task<GenreUsage?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The Genre whose name has <paramref name="nameKey"/>, or null.</summary>
    Task<Genre?> FindByNameKeyAsync(string nameKey, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="ids"/> are Genres.</summary>
    Task<IReadOnlyList<Guid>> FindExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Stores a new Genre. Its name key must be unused; the database refuses a taken one.</summary>
    Task AddAsync(Genre genre, CancellationToken cancellationToken);

    /// <summary>
    /// Renames the Genre with <paramref name="id"/> and raises its revision, when its revision is
    /// still <paramref name="revision"/>. False when it is not (or there is no such Genre).
    /// </summary>
    Task<bool> TryRenameAsync(Guid id, string name, int revision, CancellationToken cancellationToken);

    /// <summary>
    /// Raises the revision of the Genre with <paramref name="id"/>, when it is still
    /// <paramref name="revision"/>. False when it is not (or there is no such Genre).
    /// </summary>
    Task<bool> TryRaiseRevisionAsync(Guid id, int revision, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the Genres <paramref name="from"/> off every Song that has any of them, gives each of
    /// those Songs <paramref name="to"/> instead (once, whether or not it had it) unless it is null,
    /// and moves each of those Songs' last-updated time to <paramref name="now"/> and its revision on.
    /// Answers how many Songs changed. The Genres themselves stay.
    /// </summary>
    Task<int> MoveSongsAsync(IReadOnlyCollection<Guid> from, Guid? to, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Removes the Genres <paramref name="ids"/>, which no Song may have any more.</summary>
    Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Makes <paramref name="genreIds"/> (each a Genre, each once) the Genres of the Song with
    /// <paramref name="songId"/>: the ones not listed come off it; the Genres themselves stay.
    /// </summary>
    Task ReplaceSongGenresAsync(Guid songId, IReadOnlyCollection<Guid> genreIds, CancellationToken cancellationToken);
}
