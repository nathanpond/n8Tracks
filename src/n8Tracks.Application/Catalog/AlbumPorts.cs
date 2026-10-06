using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>Another record named in an Album's answer: its ID and display name or title.</summary>
public sealed record AlbumNamed(Guid Id, string Name);

/// <summary>
/// An Album as it is read: the record, its Album Artist's name, how many Songs it holds (0 until the
/// track story adds tracks), when it was created and last changed, its revision, and the other
/// Albums with the same UPC/EAN (<see cref="AlbumRules.UpcKey"/>), by title.
/// </summary>
public sealed record AlbumDetails(
    Album Album,
    AlbumNamed? AlbumArtist,
    int SongCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision,
    IReadOnlyList<AlbumNamed> SameUpc);

/// <summary>A page of Albums, in the order asked for.</summary>
public sealed record AlbumPage(IReadOnlyList<AlbumDetails> Items, int Page, int PageSize, int Total);

/// <summary>What the Albums list is ordered by.</summary>
public enum AlbumSort
{
    /// <summary>The title, ignoring case; the earlier created first on a tie.</summary>
    Title,

    /// <summary>The release date, else the original release date, by earliest possible day; undated Albums last.</summary>
    ReleaseDate,

    /// <summary>The Album Artist's name, ignoring case; Albums without one last.</summary>
    Artist,
}

/// <summary>The Albums list's query, read: its order, page, and optionally only one Album Artist's.</summary>
public sealed record AlbumListQuery(AlbumSort Sort, bool Descending, int Page, int PageSize, Guid? AlbumArtistId);

/// <summary>
/// Where Albums and their links are kept. Writes are made inside the caller's transaction, which has
/// checked what it writes.
/// </summary>
public interface IAlbumStore
{
    /// <summary>A page of Albums by <paramref name="query"/>, and the total.</summary>
    Task<AlbumPage> ListAsync(AlbumListQuery query, CancellationToken cancellationToken);

    /// <summary>The Album with <paramref name="id"/>, or null.</summary>
    Task<AlbumDetails?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Whether there is an Artist with <paramref name="id"/>.</summary>
    Task<bool> ArtistExistsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stores a new Album, created and last changed at <paramref name="now"/>, at revision 1.</summary>
    Task AddAsync(Album album, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the Album's fields and links with <paramref name="album"/>'s, moves its last-changed
    /// time to <paramref name="now"/>, and raises its revision, when its revision is still
    /// <paramref name="revision"/>. False when it is not (or there is no such Album).
    /// </summary>
    Task<bool> TryUpdateAsync(Album album, int revision, DateTimeOffset now, CancellationToken cancellationToken);
}
