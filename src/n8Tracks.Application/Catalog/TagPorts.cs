using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>A Tag, how many Songs have it, and its revision (raised by the management story's changes).</summary>
public sealed record TagUsage(Tag Tag, int SongCount, int Revision);

/// <summary>
/// Where Tags and the Songs' Tags are kept. Writes are made inside the caller's transaction, which
/// has checked what it writes.
/// </summary>
public interface ITagStore
{
    /// <summary>Every Tag with its Song count, alphabetically (ignoring case, invariant culture).</summary>
    Task<IReadOnlyList<TagUsage>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The Tag with <paramref name="id"/>, its Song count, and its revision, or null.</summary>
    Task<TagUsage?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The Tag whose name has <paramref name="nameKey"/>, or null.</summary>
    Task<Tag?> FindByNameKeyAsync(string nameKey, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="ids"/> are Tags.</summary>
    Task<IReadOnlyList<Guid>> FindExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>How many Tags have each colour; a colour no Tag has is left out.</summary>
    Task<IReadOnlyDictionary<string, int>> CountByColourAsync(CancellationToken cancellationToken);

    /// <summary>Stores a new Tag. Its name key must be unused; the database refuses a taken one.</summary>
    Task AddAsync(Tag tag, CancellationToken cancellationToken);

    /// <summary>
    /// Makes <paramref name="tagIds"/> (each a Tag, each once) the Tags of the Song with
    /// <paramref name="songId"/>: the ones not listed come off it; the Tags themselves stay.
    /// </summary>
    Task ReplaceSongTagsAsync(Guid songId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);
}
