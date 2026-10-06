using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Catalog;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class PlaylistStore(N8TracksDbContext context) : IPlaylistStore
{
    public async Task<PlaylistPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var playlists = context.Playlists.AsNoTracking();
        var total = await playlists.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await playlists
            .OrderBy(static playlist => playlist.TitleKey)
            .ThenBy(static playlist => playlist.CreatedUtc)
            .ThenBy(static playlist => playlist.Id)
            .Skip((int)Math.Min(((long)page - 1) * pageSize, int.MaxValue))
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ids = records.Select(static record => record.Id).ToList();
        var counts = await context.PlaylistSongs.AsNoTracking()
            .Where(entry => ids.Contains(entry.PlaylistId))
            .GroupBy(static entry => entry.PlaylistId)
            .Select(static group => new { PlaylistId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.PlaylistId, static group => group.Count, cancellationToken)
            .ConfigureAwait(false);
        var artwork = await ArtworkAttachmentStore.ForOwnersAsync(context, ArtworkOwnerTypes.Playlist, ids, cancellationToken).ConfigureAwait(false);

        return new PlaylistPage(
            [.. records.Select(record => Summary(record, counts.GetValueOrDefault(record.Id), artwork.GetValueOrDefault(record.Id)))],
            page,
            pageSize,
            total);
    }

    public async Task<PlaylistDetails?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await context.Playlists.AsNoTracking()
            .SingleOrDefaultAsync(playlist => playlist.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var rows = await (
                from entry in context.PlaylistSongs.AsNoTracking()
                where entry.PlaylistId == id
                join song in context.Songs.AsNoTracking() on entry.SongId equals song.Id
                join state in context.WorkflowStates.AsNoTracking() on song.WorkflowStateId equals state.Id
                orderby entry.Position
                select new { song.Id, song.ShortcodeNumber, song.Title, StateId = state.Id, StateName = state.Name, state.Colour })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var credits = await SongCreditStore.ForSongsAsync(context, [.. rows.Select(static row => row.Id)], cancellationToken).ConfigureAwait(false);
        var songs = rows.Select(row => new PlaylistSong(
            row.Id,
            Shortcodes.ForSong(row.ShortcodeNumber),
            row.Title,
            credits.GetValueOrDefault(row.Id)?.Primary is { } primary ? new PlaylistSongArtist(primary.Id, primary.Name) : null,
            new PlaylistSongState(row.StateId, row.StateName, row.Colour),

            // Selected Generations arrive in M4; until then no Song has one.
            HasSelectedGeneration: false)).ToList();

        var artwork = await ArtworkAttachmentStore.ForOwnersAsync(context, ArtworkOwnerTypes.Playlist, [id], cancellationToken).ConfigureAwait(false);
        return new PlaylistDetails(Summary(record, songs.Count, artwork.GetValueOrDefault(id)), songs);
    }

    public Task<bool> SongExistsAsync(Guid id, CancellationToken cancellationToken) =>
        context.Songs.AsNoTracking().AnyAsync(song => song.Id == id, cancellationToken);

    public async Task AddAsync(Playlist playlist, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        var stamp = UtcText.From(now);
        var record = new PlaylistRecord
        {
            Id = playlist.Id,
            Title = playlist.Title,
            TitleKey = playlist.TitleKey,
            Description = playlist.Description,
            CreatedUtc = stamp,
            UpdatedUtc = stamp,
        };
        context.Playlists.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task<bool> TryUpdateAsync(Playlist playlist, int revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        var titleKey = playlist.TitleKey;
        var updated = UtcText.From(now);

        // One conditional statement: the revision check and the write cannot be split by another writer.
        var count = await context.Playlists
            .Where(record => record.Id == playlist.Id && record.Revision == revision)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.Title, playlist.Title)
                    .SetProperty(record => record.TitleKey, titleKey)
                    .SetProperty(record => record.Description, playlist.Description)
                    .SetProperty(record => record.UpdatedUtc, updated)
                    .SetProperty(record => record.Revision, record => record.Revision + 1),
                cancellationToken)
            .ConfigureAwait(false);
        return count == 1;
    }

    public Task TouchSongsAsync(IReadOnlyCollection<Guid> songIds, DateTimeOffset updatedUtc, CancellationToken cancellationToken) =>
        SongTouch.UpdatedTimeOnlyAsync(context, songIds, updatedUtc, cancellationToken);

    public async Task<bool> TrySetSongsAsync(Guid id, IReadOnlyList<Guid> songIds, int revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);

        var updated = UtcText.From(now);
        var count = await context.Playlists
            .Where(record => record.Id == id && record.Revision == revision)
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

        // The entries are written as a whole, under the revision just raised. Only playlist_songs
        // changes: no Song row is written, so no Song's revision or updated time moves.
        await context.PlaylistSongs.Where(entry => entry.PlaylistId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var rows = songIds.Select((songId, position) => new PlaylistSongRecord { PlaylistId = id, SongId = songId, Position = position }).ToList();
        if (rows.Count > 0)
        {
            context.PlaylistSongs.AddRange(rows);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                context.Entry(row).State = EntityState.Detached;
            }
        }

        return true;
    }

    /// <summary>The Playlists each of <paramref name="songIds"/> is on, by title (ignoring case), the earlier created first on a tie.</summary>
    internal static async Task<Dictionary<Guid, IReadOnlyList<PlaylistNamed>>> ForSongsAsync(N8TracksDbContext context, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        var ids = songIds.ToList();
        var rows = await (
                from entry in context.PlaylistSongs.AsNoTracking()
                where ids.Contains(entry.SongId)
                join playlist in context.Playlists.AsNoTracking() on entry.PlaylistId equals playlist.Id
                orderby playlist.TitleKey, playlist.CreatedUtc, playlist.Id
                select new { entry.SongId, playlist.Id, playlist.Title })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(static row => row.SongId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<PlaylistNamed>)[.. group.Select(static row => new PlaylistNamed(row.Id, row.Title))]);
    }

    private static PlaylistSummary Summary(PlaylistRecord record, int songCount, AttachedArtwork? artwork) =>
        new(
            new Playlist(record.Id, record.Title, record.Description),
            songCount,
            UtcText.Parse(record.CreatedUtc),
            UtcText.Parse(record.UpdatedUtc),
            record.Revision,
            artwork);
}
