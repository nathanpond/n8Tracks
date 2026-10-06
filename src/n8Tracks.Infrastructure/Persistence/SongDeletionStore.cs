using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class SongDeletionStore(N8TracksDbContext context) : ISongDeletionStore
{
    public async Task<IReadOnlyList<Guid>> GenerationIdsAsync(Guid songId, CancellationToken cancellationToken) =>
        await context.Generations.AsNoTracking()
            .Where(generation => generation.SongId == songId)
            .OrderBy(static generation => generation.Id)
            .Select(static generation => generation.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task TouchAsync(
        IReadOnlyCollection<Guid> albumIds,
        IReadOnlyCollection<Guid> playlistIds,
        IReadOnlyCollection<Guid> songIds,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(albumIds);
        ArgumentNullException.ThrowIfNull(playlistIds);
        ArgumentNullException.ThrowIfNull(songIds);

        var updated = UtcText.From(updatedUtc);
        var albums = albumIds.ToList();
        var playlists = playlistIds.ToList();
        var songs = songIds.ToList();

        await context.Albums
            .Where(record => albums.Contains(record.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.UpdatedUtc, updated).SetProperty(record => record.Revision, record => record.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
        await context.Playlists
            .Where(record => playlists.Contains(record.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.UpdatedUtc, updated).SetProperty(record => record.Revision, record => record.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
        await context.Songs
            .Where(record => songs.Contains(record.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.UpdatedUtc, updated).SetProperty(record => record.Revision, record => record.Revision + 1), cancellationToken)
            .ConfigureAwait(false);

        foreach (var album in albums)
        {
            await CloseDiscGapsAsync(album, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Discs are numbered without gaps (<see cref="AlbumTrackRules.CloseDiscGaps"/>): when the Song
    /// was alone on its disc, the later discs move down. The tracks are rewritten as a whole, so the
    /// unique disc and track index is never met halfway. Nothing is written when there is no gap.
    /// </summary>
    private async Task CloseDiscGapsAsync(Guid albumId, CancellationToken cancellationToken)
    {
        var tracks = await context.AlbumSongs.AsNoTracking()
            .Where(track => track.AlbumId == albumId)
            .Select(static track => new AlbumTrackPlace(track.SongId, track.Disc, track.Track))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var closed = AlbumTrackRules.CloseDiscGaps(tracks);
        if (AlbumTrackRules.Ordered(tracks).SequenceEqual(closed))
        {
            return;
        }

        await context.AlbumSongs.Where(track => track.AlbumId == albumId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var rows = closed.Select(track => new AlbumSongRecord { AlbumId = albumId, SongId = track.SongId, Disc = track.Disc, Track = track.Track }).ToList();
        context.AlbumSongs.AddRange(rows);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            context.Entry(row).State = EntityState.Detached;
        }
    }
}
