using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using n8Tracks.Domain.Catalog;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Retention;

/// <summary>
/// What restoring a deleted Song does beyond putting its rows back (#102): the Song keeps a workflow
/// state that exists, its used Version numbers make way for the ones its Versions' insert trigger
/// records, its Album and Playlist memberships return at the end (the Albums and Playlists have moved
/// on meanwhile), and the records a membership or relationship returns to have their revisions raised.
/// </summary>
internal static class SongRestore
{
    /// <summary>
    /// The Song as retained, unless the workflow state it was in has been deleted meanwhile: then
    /// in the first visible state, as a new Song would be (the first state of all when every state
    /// is hidden), with a note saying so.
    /// </summary>
    public static async Task<RestorePreparation> KeepStateAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var stateId = row.TextOf("workflow_state_id");
        var database = row.Context.Database;
        var exists = await database.SqlQuery<int>($"SELECT count(*) AS \"Value\" FROM workflow_states WHERE id = {stateId}")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        if (exists > 0)
        {
            return RestorePreparation.With(row.Values);
        }

        var first = await database.SqlQuery<string>($"SELECT id AS \"Value\" FROM workflow_states ORDER BY hidden, position LIMIT 1")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var values = new Dictionary<string, object?>(row.Values, StringComparer.Ordinal) { ["workflow_state_id"] = first };
        return RestorePreparation.With(values, "The Song's workflow state no longer exists, so it was restored in the first visible state.");
    }

    /// <summary>
    /// Just before a used number goes back: removes the same row if the insert trigger of the Version
    /// that used it has already written it (a deleted Version's number, retained with the Song but
    /// never live, has no such row, and simply goes back).
    /// </summary>
    public static Task FreeUsedNumberAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var songId = row.TextOf("song_id");
        var number = row.TextOf("number");
        return row.Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM used_version_numbers WHERE song_id = {songId} AND number = {number};",
            cancellationToken);
    }

    /// <summary>
    /// The membership at the end of the Album's last disc, with the next track number (disc 1, track 1
    /// on an Album with no tracks now), as adding the Song would place it; left out when the last
    /// disc is full. When the Album is gone it is returned as retained, and the parent check leaves it out.
    /// </summary>
    public static async Task<RestorePreparation> AtAlbumEndAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var albumId = row.TextOf("album_id");
        var database = row.Context.Database;
        if (await CountAsync(database, $"SELECT count(*) AS \"Value\" FROM albums WHERE id = {albumId}", cancellationToken).ConfigureAwait(false) == 0)
        {
            return RestorePreparation.With(row.Values);
        }

        var places = await database.SqlQuery<string>($"SELECT disc || ':' || track AS \"Value\" FROM album_songs WHERE album_id = {albumId}")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var tracks = places.Select(static place => place.Split(':')).Select(static parts => new AlbumTrackPlace(
            Guid.Empty,
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture))).ToList();
        if (AlbumTrackRules.NextPlace(tracks, row.GuidOf("song_id")) is not { } next)
        {
            return RestorePreparation.LeftOut("the Album's last disc is full.");
        }

        return RestorePreparation.With(new Dictionary<string, object?>(row.Values, StringComparer.Ordinal)
        {
            ["disc"] = (long)next.Disc,
            ["track"] = (long)next.Track,
        });
    }

    /// <summary>
    /// The entry at the end of the Playlist; left out when the Playlist already holds
    /// <see cref="PlaylistRules.MaximumSongCount"/> Songs. When the Playlist is gone it is returned as
    /// retained, and the parent check leaves it out.
    /// </summary>
    public static async Task<RestorePreparation> AtPlaylistEndAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var playlistId = row.TextOf("playlist_id");
        var database = row.Context.Database;
        if (await CountAsync(database, $"SELECT count(*) AS \"Value\" FROM playlists WHERE id = {playlistId}", cancellationToken).ConfigureAwait(false) == 0)
        {
            return RestorePreparation.With(row.Values);
        }

        if (await CountAsync(database, $"SELECT count(*) AS \"Value\" FROM playlist_songs WHERE playlist_id = {playlistId}", cancellationToken).ConfigureAwait(false) >= PlaylistRules.MaximumSongCount)
        {
            return RestorePreparation.LeftOut("the Playlist is full.");
        }

        var next = await database.SqlQuery<long>($"SELECT COALESCE(MAX(position) + 1, 0) AS \"Value\" FROM playlist_songs WHERE playlist_id = {playlistId}")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        return RestorePreparation.With(new Dictionary<string, object?>(row.Values, StringComparer.Ordinal) { ["position"] = next });
    }

    /// <summary>
    /// Once the row is back: raises the revision of the row of <paramref name="table"/> its
    /// <paramref name="column"/> names and sets its updated time to the restore's, as any change to
    /// what it shows does.
    /// </summary>
    public static Task TouchAsync(RestoredRow row, string table, string column, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var id = row.TextOf(column);
        var updated = UtcText.From(row.RestoredUtc);
        return table switch
        {
            "albums" => row.Context.Database.ExecuteSqlInterpolatedAsync($"UPDATE albums SET revision = revision + 1, updated_utc = {updated} WHERE id = {id};", cancellationToken),
            "playlists" => row.Context.Database.ExecuteSqlInterpolatedAsync($"UPDATE playlists SET revision = revision + 1, updated_utc = {updated} WHERE id = {id};", cancellationToken),
            "songs" => row.Context.Database.ExecuteSqlInterpolatedAsync($"UPDATE songs SET revision = revision + 1, updated_utc = {updated} WHERE id = {id};", cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Only Albums, Playlists, and Songs are touched by a restore."),
        };
    }

    private static Task<int> CountAsync(DatabaseFacade database, FormattableString sql, CancellationToken cancellationToken) =>
        database.SqlQuery<int>(sql).SingleAsync(cancellationToken);
}
