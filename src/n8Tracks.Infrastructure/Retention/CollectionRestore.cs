using Microsoft.EntityFrameworkCore;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Retention;

/// <summary>
/// What restoring a deleted Album or Playlist does beyond putting its rows back (#103): an Album
/// Artist deleted meanwhile is let go, discs left empty by Songs deleted meanwhile close up, and
/// each Song that is on the collection again has its updated time moved (not its revision), as its
/// deletion did.
/// </summary>
internal static class CollectionRestore
{
    /// <summary>
    /// The Album as retained, unless its Album Artist has been deleted meanwhile: then without one,
    /// as an Album whose Album Artist is deleted is left, with a note saying so.
    /// </summary>
    public static async Task<RestorePreparation> KeepAlbumArtistAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Values["album_artist_id"] is not string artistId || row.InGroup("artists", artistId))
        {
            return RestorePreparation.With(row.Values);
        }

        var exists = await row.Context.Database.SqlQuery<int>($"SELECT count(*) AS \"Value\" FROM artists WHERE id = {artistId}")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        if (exists > 0)
        {
            return RestorePreparation.With(row.Values);
        }

        var values = new Dictionary<string, object?>(row.Values, StringComparer.Ordinal) { ["album_artist_id"] = null };
        return RestorePreparation.With(values, "The Album's Album Artist no longer exists, so the Album was restored without one.");
    }

    /// <summary>
    /// Once the Album and its tracks are back: closes the gap a disc leaves when every Song on it was
    /// deleted meanwhile (its tracks were left out), as deleting those Songs would have.
    /// </summary>
    public static Task CloseDiscGapsAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        return AlbumTrackStore.CloseDiscGapsAsync(row.Context, row.GuidOf("id"), cancellationToken);
    }

    /// <summary>
    /// Once a membership restored with its own Album or Playlist is back: moves the Song's updated
    /// time to the restore's, leaving its revision as it is.
    /// </summary>
    public static Task TouchSongAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        return SongTouch.UpdatedTimeOnlyAsync(row.Context, [row.GuidOf("song_id")], row.RestoredUtc, cancellationToken);
    }
}
