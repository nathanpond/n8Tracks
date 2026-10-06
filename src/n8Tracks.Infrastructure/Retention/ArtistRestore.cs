using Microsoft.EntityFrameworkCore;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Infrastructure.Retention;

/// <summary>
/// What restoring a deleted Artist does beyond putting its rows back (#104): a credit removed with
/// it returns to its Song only where the Song still has room for it there, and the Song shows the
/// credit again at its next revision.
/// </summary>
internal static class ArtistRestore
{
    /// <summary>
    /// A credit restored without its Song (removed with its Artist): as retained, unless the Song now
    /// has another primary Artist (for a primary credit), another featured Artist in its place, or as
    /// many featured Artists as it may have; then left out, saying why. When the Song is gone it is
    /// returned as retained, and the parent check leaves it out.
    /// </summary>
    public static async Task<RestorePreparation> KeepCreditPlaceAsync(RestoredRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var songId = row.TextOf("song_id");
        var database = row.Context.Database;
        if (row.TextOf("role") == SongCreditRules.PrimaryRole)
        {
            var primaries = await database.SqlQuery<int>($"SELECT count(*) AS \"Value\" FROM song_artist_credits WHERE song_id = {songId} AND role = {SongCreditRules.PrimaryRole}")
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            return primaries == 0
                ? RestorePreparation.With(row.Values)
                : RestorePreparation.LeftOut("the Song has another primary Artist now.");
        }

        var position = (long)row.Values["position"]!;
        var featured = await database.SqlQuery<long>($"SELECT position AS \"Value\" FROM song_artist_credits WHERE song_id = {songId} AND role = {SongCreditRules.FeaturedRole}")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (featured.Contains(position))
        {
            return RestorePreparation.LeftOut("another featured Artist holds its place on the Song now.");
        }

        return featured.Count >= SongCreditRules.FeaturedMaximumCount
            ? RestorePreparation.LeftOut("the Song has as many featured Artists as it may.")
            : RestorePreparation.With(row.Values);
    }
}
