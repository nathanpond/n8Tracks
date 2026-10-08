using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Catalog;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class AlbumTrackStore(N8TracksDbContext context) : IAlbumTrackStore
{
    public Task<bool> SongExistsAsync(Guid id, CancellationToken cancellationToken) =>
        context.Songs.AsNoTracking().AnyAsync(song => song.Id == id, cancellationToken);

    public async Task<bool> TrySetTracksAsync(Guid albumId, IReadOnlyList<AlbumTrackPlace> tracks, int revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var updated = UtcText.From(now);
        var count = await context.Albums
            .Where(record => record.Id == albumId && record.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.UpdatedUtc, updated)
                    .SetProperty(record => record.Revision, record => record.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (count != 1)
        {
            return false;
        }

        // The tracks are written as a whole, under the revision just raised, so renumbering never
        // meets the unique disc and track index halfway. Only album_songs changes: no Song row is
        // written, so no Song's revision or updated time moves.
        await context.AlbumSongs.Where(track => track.AlbumId == albumId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var rows = tracks.Select(track => new AlbumSongRecord { AlbumId = albumId, SongId = track.SongId, Disc = track.Disc, Track = track.Track }).ToList();
        if (rows.Count > 0)
        {
            context.AlbumSongs.AddRange(rows);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                context.Entry(row).State = EntityState.Detached;
            }
        }

        return true;
    }

    /// <summary>Each of <paramref name="albumIds"/>' tracks, by disc and then track number (Albums without tracks are left out).</summary>
    internal static async Task<Dictionary<Guid, IReadOnlyList<AlbumTrack>>> ForAlbumsAsync(N8TracksDbContext context, IReadOnlyCollection<Guid> albumIds, CancellationToken cancellationToken)
    {
        var ids = albumIds.ToList();
        var rows = await (
                from track in context.AlbumSongs.AsNoTracking()
                where ids.Contains(track.AlbumId)
                join song in context.Songs.AsNoTracking() on track.SongId equals song.Id
                join state in context.WorkflowStates.AsNoTracking() on song.WorkflowStateId equals state.Id
                orderby track.Disc, track.Track
                select new { track.AlbumId, track.Disc, track.Track, song.Id, song.ShortcodeNumber, song.Title, StateId = state.Id, StateName = state.Name, state.Colour, song.SelectedGenerationId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var credits = await SongCreditStore.ForSongsAsync(context, [.. rows.Select(static row => row.Id).Distinct()], cancellationToken).ConfigureAwait(false);
        var playback = await SongPlaybackRows.StatesAsync(
                context,
                rows.DistinctBy(static row => row.Id).ToDictionary(static row => row.Id, static row => row.SelectedGenerationId),
                cancellationToken)
            .ConfigureAwait(false);
        return rows
            .GroupBy(static row => row.AlbumId)
            .ToDictionary(
                static group => group.Key,
                group => (IReadOnlyList<AlbumTrack>)[.. group.Select(row => new AlbumTrack(
                    row.Id,
                    Shortcodes.ForSong(row.ShortcodeNumber),
                    row.Title,
                    credits.GetValueOrDefault(row.Id)?.Primary is { } primary ? new AlbumNamed(primary.Id, primary.Name) : null,
                    new AlbumTrackState(row.StateId, row.StateName, row.Colour),
                    row.Disc,
                    row.Track,
                    HasSelectedGeneration: row.SelectedGenerationId != null)
                {
                    Playback = playback[row.Id],
                })]);
    }

    /// <summary>The Albums each of <paramref name="songIds"/> is on, with its disc and track, by Album title (ignoring case), the earlier created first on a tie.</summary>
    internal static async Task<Dictionary<Guid, IReadOnlyList<AlbumMembership>>> ForSongsAsync(N8TracksDbContext context, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        var ids = songIds.ToList();
        var rows = await (
                from track in context.AlbumSongs.AsNoTracking()
                where ids.Contains(track.SongId)
                join album in context.Albums.AsNoTracking() on track.AlbumId equals album.Id
                orderby album.TitleKey, album.CreatedUtc, album.Id
                select new { track.SongId, album.Id, album.Title, track.Disc, track.Track })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(static row => row.SongId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<AlbumMembership>)[.. group.Select(static row => new AlbumMembership(row.Id, row.Title, row.Disc, row.Track))]);
    }

    /// <summary>
    /// Discs are numbered without gaps (<see cref="AlbumTrackRules.CloseDiscGaps"/>): when a Song
    /// that was alone on its disc is gone, the later discs move down. The tracks are rewritten as a whole, so the
    /// unique disc and track index is never met halfway. Nothing is written when there is no gap.
    /// </summary>
    internal static async Task CloseDiscGapsAsync(N8TracksDbContext context, Guid albumId, CancellationToken cancellationToken)
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
