using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;

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
            await AlbumTrackStore.CloseDiscGapsAsync(context, album, cancellationToken).ConfigureAwait(false);
        }
    }
}
