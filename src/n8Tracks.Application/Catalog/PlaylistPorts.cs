using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>A Song on a Playlist, as the Playlist page shows it: its shortcode, title, primary Artist, workflow state, and whether it has a Selected Generation.</summary>
/// <param name="Id">The Song's ID.</param>
/// <param name="Shortcode">Its shortcode, <c>n8-&lt;n&gt;</c>.</param>
/// <param name="Title">Its title.</param>
/// <param name="PrimaryArtist">Its primary Artist, or null.</param>
/// <param name="State">Its workflow state.</param>
/// <param name="HasSelectedGeneration">
/// Whether it has a Selected Generation. Always false until Generations can be selected (M4); a
/// Song without one stays on the Playlist and is marked.
/// </param>
public sealed record PlaylistSong(Guid Id, string Shortcode, string Title, PlaylistSongArtist? PrimaryArtist, PlaylistSongState State, bool HasSelectedGeneration);

/// <summary>An Artist as a Playlist's Song names it: its ID and display name.</summary>
public sealed record PlaylistSongArtist(Guid Id, string Name);

/// <summary>A workflow state as a Playlist's Song shows it.</summary>
public sealed record PlaylistSongState(Guid Id, string Name, string Colour);

/// <summary>
/// A Playlist as the list shows it: the record, how many Songs it holds, when it was created and
/// last changed, its revision, and its own artwork (null when it has none; never its Songs').
/// </summary>
public sealed record PlaylistSummary(Playlist Playlist, int SongCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision, AttachedArtwork? Artwork);

/// <summary>A Playlist as its page reads it: the summary and every Song on it, in order.</summary>
public sealed record PlaylistDetails(PlaylistSummary Summary, IReadOnlyList<PlaylistSong> Songs)
{
    public Playlist Playlist => Summary.Playlist;

    public int Revision => Summary.Revision;
}

/// <summary>A page of Playlists, by title.</summary>
public sealed record PlaylistPage(IReadOnlyList<PlaylistSummary> Items, int Page, int PageSize, int Total);

/// <summary>
/// Where Playlists and their Songs are kept. Writes are made inside the caller's transaction, which
/// has checked what it writes. Nothing here changes a Song.
/// </summary>
public interface IPlaylistStore
{
    /// <summary>A page of Playlists by title (ignoring case), the earlier created first on a tie, and the total.</summary>
    Task<PlaylistPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The Playlist with <paramref name="id"/> and its Songs in order, or null.</summary>
    Task<PlaylistDetails?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Whether there is a Song with <paramref name="id"/>.</summary>
    Task<bool> SongExistsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stores a new, empty Playlist, created and last changed at <paramref name="now"/>, at revision 1.</summary>
    Task AddAsync(Playlist playlist, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the Playlist's title and description with <paramref name="playlist"/>'s, moves its
    /// last-changed time to <paramref name="now"/>, and raises its revision, when its revision is
    /// still <paramref name="revision"/>. False when it is not (or there is no such Playlist).
    /// </summary>
    Task<bool> TryUpdateAsync(Playlist playlist, int revision, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Makes <paramref name="songIds"/>, in that order, the Playlist's Songs, moves its last-changed
    /// time to <paramref name="now"/>, and raises its revision, when its revision is still
    /// <paramref name="revision"/>. False when it is not. The Songs themselves are not touched.
    /// </summary>
    Task<bool> TrySetSongsAsync(Guid id, IReadOnlyList<Guid> songIds, int revision, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Inside the caller's transaction, once the Playlist is gone: sets the updated time of each of
    /// <paramref name="songIds"/> to <paramref name="updatedUtc"/>, without raising its revision (no
    /// field of the Song changed; it shows one Playlist fewer). Missing IDs are skipped.
    /// </summary>
    Task TouchSongsAsync(IReadOnlyCollection<Guid> songIds, DateTimeOffset updatedUtc, CancellationToken cancellationToken);
}
