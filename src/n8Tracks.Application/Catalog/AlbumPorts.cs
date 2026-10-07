using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>Another record named in an Album's answer: its ID and display name or title.</summary>
public sealed record AlbumNamed(Guid Id, string Name);

/// <summary>
/// An Album as it is read: the record, its Album Artist's name, how many Songs it holds, when it was
/// created and last changed, its revision, the other Albums with the same UPC/EAN
/// (<see cref="AlbumRules.UpcKey"/>), by title, its tracks in order (disc, then track number), and
/// its own artwork (null when it has none; never its Songs').
/// </summary>
public sealed record AlbumDetails(
    Album Album,
    AlbumNamed? AlbumArtist,
    int SongCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision,
    IReadOnlyList<AlbumNamed> SameUpc,
    IReadOnlyList<AlbumTrack> Tracks,
    AttachedArtwork? Artwork);

/// <summary>A Song on an Album, as the Album page shows it: the Song and its disc and track number.</summary>
/// <param name="SongId">The Song's ID.</param>
/// <param name="Shortcode">Its shortcode, <c>n8-&lt;n&gt;</c>.</param>
/// <param name="Title">Its title.</param>
/// <param name="PrimaryArtist">Its primary Artist, or null.</param>
/// <param name="State">Its workflow state.</param>
/// <param name="Disc">The disc it is on, from 1.</param>
/// <param name="Track">Its track number on that disc, from 1.</param>
/// <param name="HasSelectedGeneration">
/// Whether its Song has a Selected Generation (#120); a
/// track without one is marked incomplete.
/// </param>
public sealed record AlbumTrack(Guid SongId, string Shortcode, string Title, AlbumNamed? PrimaryArtist, AlbumTrackState State, int Disc, int Track, bool HasSelectedGeneration)
{
    /// <summary>Its place on the Album.</summary>
    public AlbumTrackPlace Place => new(SongId, Disc, Track);
}

/// <summary>A workflow state as an Album's track shows it.</summary>
public sealed record AlbumTrackState(Guid Id, string Name, string Colour);

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

    /// <summary>
    /// Inside the caller's transaction, once the Album is gone: sets the updated time of each of
    /// <paramref name="songIds"/> to <paramref name="updatedUtc"/>, without raising its revision (no
    /// field of the Song changed; it shows one Album fewer). Missing IDs are skipped.
    /// </summary>
    Task TouchSongsAsync(IReadOnlyCollection<Guid> songIds, DateTimeOffset updatedUtc, CancellationToken cancellationToken);
}
