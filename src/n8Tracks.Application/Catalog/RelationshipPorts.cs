using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>A relationship type, how many relationships use it, and its revision (raised by a rename).</summary>
public sealed record RelationshipTypeUsage(RelationshipType Type, int RelationshipCount, int Revision);

/// <summary>A stored relationship: its type, the Song it starts from (forward), and the other Song.</summary>
public sealed record StoredRelationship(Guid Id, Guid TypeId, Guid FromSongId, Guid ToSongId);

/// <summary>
/// Where relationship types and Songs' relationships are kept. Writes are made inside the caller's
/// transaction, which has checked what it writes. A relationship write moves its Songs'
/// last-updated times, never their revisions.
/// </summary>
public interface IRelationshipStore
{
    /// <summary>Every type with its relationship count: the system types in their order, then the user's alphabetically (ignoring case).</summary>
    Task<IReadOnlyList<RelationshipTypeUsage>> ListTypesAsync(CancellationToken cancellationToken);

    /// <summary>The type with <paramref name="id"/>, its relationship count, and its revision, or null.</summary>
    Task<RelationshipTypeUsage?> FindTypeAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stores a new user-defined type at revision 1. Its names must be unused; the caller has checked.</summary>
    Task AddTypeAsync(RelationshipType type, CancellationToken cancellationToken);

    /// <summary>
    /// Renames the type <paramref name="id"/> and raises its revision, when its revision is still
    /// <paramref name="revision"/> and it is not a system type. False otherwise.
    /// </summary>
    Task<bool> TryRenameTypeAsync(Guid id, string name, string reverseName, int revision, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every relationship of the type <paramref name="id"/>, moving each affected Song's
    /// last-updated time to <paramref name="now"/>, then the type itself, when its revision is still
    /// <paramref name="revision"/>. Answers how many relationships went, or null when nothing was
    /// removed because the revision moved on.
    /// </summary>
    Task<int?> TryDeleteTypeAsync(Guid id, int revision, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Whether there is a Song with <paramref name="id"/>.</summary>
    Task<bool> SongExistsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Whether the two Songs are related under the type already, either way round.</summary>
    Task<bool> ExistsAsync(Guid typeId, Guid oneSongId, Guid otherSongId, CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="relationship"/> and moves both its Songs' last-updated times to <paramref name="now"/>.</summary>
    Task AddAsync(StoredRelationship relationship, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The relationship with <paramref name="id"/>, or null.</summary>
    Task<StoredRelationship?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Removes the relationship <paramref name="relationship"/> and moves both its Songs' last-updated times to <paramref name="now"/>.</summary>
    Task RemoveAsync(StoredRelationship relationship, DateTimeOffset now, CancellationToken cancellationToken);
}
