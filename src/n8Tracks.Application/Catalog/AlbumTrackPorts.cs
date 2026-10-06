using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>
/// Where an Album's tracks are kept. The Album itself, with its tracks, is read through
/// <see cref="IAlbumStore.FindAsync"/>. Writes are made inside the caller's transaction, which has
/// checked what it writes. Nothing here changes a Song.
/// </summary>
public interface IAlbumTrackStore
{
    /// <summary>Whether there is a Song with <paramref name="id"/>.</summary>
    Task<bool> SongExistsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Makes <paramref name="tracks"/> the Album's tracks, moves its last-changed time to
    /// <paramref name="now"/>, and raises its revision, when its revision is still
    /// <paramref name="revision"/>. False when it is not (or there is no such Album). The Songs
    /// themselves are not touched.
    /// </summary>
    Task<bool> TrySetTracksAsync(Guid albumId, IReadOnlyList<AlbumTrackPlace> tracks, int revision, DateTimeOffset now, CancellationToken cancellationToken);
}
