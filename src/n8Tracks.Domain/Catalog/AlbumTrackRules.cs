namespace n8Tracks.Domain.Catalog;

/// <summary>
/// A Song's place on an Album: its disc and track number. A Song is on an Album at most once, and
/// no two Songs on the same disc share a track number. The order is disc, then track number; there
/// is no separate position. Putting a Song on an Album never changes the Song.
/// </summary>
/// <param name="SongId">The Song.</param>
/// <param name="Disc">1 to <see cref="AlbumTrackRules.MaximumNumber"/>; the Album's discs are numbered 1, 2, 3… with no gaps.</param>
/// <param name="Track">1 to <see cref="AlbumTrackRules.MaximumNumber"/>; gaps are allowed.</param>
public sealed record AlbumTrackPlace(Guid SongId, int Disc, int Track);

/// <summary>An Album as a Song names it: the Album's ID and title, and the Song's disc and track on it.</summary>
public sealed record AlbumMembership(Guid AlbumId, string Title, int Disc, int Track);

/// <summary>
/// How an Album's tracks are numbered. Disc and track numbers are whole numbers from 1 to
/// <see cref="MaximumNumber"/>. Discs are numbered without gaps: a disc that has no tracks left
/// disappears and the later discs close up. Track numbers may have gaps (a number typed directly);
/// <see cref="Renumber"/> closes them.
/// </summary>
public static class AlbumTrackRules
{
    public const int MinimumNumber = 1;

    public const int MaximumNumber = 999;

    /// <summary>Whether <paramref name="number"/> is a valid disc or track number.</summary>
    public static bool IsNumber(int number) => number is >= MinimumNumber and <= MaximumNumber;

    /// <summary>The tracks in the Album's order: by disc, then track number, then as given.</summary>
    public static List<AlbumTrackPlace> Ordered(IEnumerable<AlbumTrackPlace> tracks) =>
        [.. tracks.OrderBy(static track => track.Disc).ThenBy(static track => track.Track)];

    /// <summary>
    /// Where a newly added Song goes: at the end of the last disc (disc 1 on an empty Album), with the
    /// next track number. Null when the last disc already has track <see cref="MaximumNumber"/>.
    /// </summary>
    public static AlbumTrackPlace? NextPlace(IReadOnlyCollection<AlbumTrackPlace> tracks, Guid songId)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        if (tracks.Count == 0)
        {
            return new AlbumTrackPlace(songId, MinimumNumber, MinimumNumber);
        }

        var disc = tracks.Max(static track => track.Disc);
        var last = tracks.Where(track => track.Disc == disc).Max(static track => track.Track);
        return last >= MaximumNumber ? null : new AlbumTrackPlace(songId, disc, last + 1);
    }

    /// <summary>
    /// The tracks with their discs numbered 1, 2, 3… in the order of their disc numbers, so a disc
    /// gap (a disc with no tracks) closes up. Track numbers are kept.
    /// </summary>
    public static List<AlbumTrackPlace> CloseDiscGaps(IEnumerable<AlbumTrackPlace> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var list = tracks.ToList();
        var discs = list.Select(static track => track.Disc).Distinct().Order().Select(static (disc, index) => (disc, index)).ToDictionary(static pair => pair.disc, static pair => pair.index + 1);
        return Ordered(list.Select(track => track with { Disc = discs[track.Disc] }));
    }

    /// <summary>The tracks with each disc's track numbers set to 1, 2, 3… in their current order.</summary>
    public static List<AlbumTrackPlace> Renumber(IEnumerable<AlbumTrackPlace> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        return [.. Ordered(tracks)
            .GroupBy(static track => track.Disc)
            .SelectMany(static disc => disc.Select(static (track, index) => track with { Track = index + 1 }))];
    }

    /// <summary>
    /// The tracks without the Song <paramref name="songId"/>: the rest of its disc is renumbered 1, 2,
    /// 3…, and when its disc is left empty the later discs close up.
    /// </summary>
    public static List<AlbumTrackPlace> Without(IReadOnlyCollection<AlbumTrackPlace> tracks, Guid songId)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var leaving = tracks.SingleOrDefault(track => track.SongId == songId);
        if (leaving is null)
        {
            return Ordered(tracks);
        }

        var rest = tracks.Where(track => track.SongId != songId).ToList();
        var disc = Renumber(rest.Where(track => track.Disc == leaving.Disc));
        return CloseDiscGaps(rest.Where(track => track.Disc != leaving.Disc).Concat(disc));
    }

    /// <summary>
    /// The first pair of tracks that share a disc and track number, in the tracks' order, or null.
    /// When one of the pair holds that number in <paramref name="before"/>, it is the holder;
    /// otherwise the earlier one in <paramref name="tracks"/> is.
    /// </summary>
    public static (AlbumTrackPlace Holder, AlbumTrackPlace Refused)? FindClash(IReadOnlyList<AlbumTrackPlace> tracks, IReadOnlyCollection<AlbumTrackPlace> before)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(before);

        foreach (var group in tracks.GroupBy(static track => (track.Disc, track.Track)))
        {
            var sharing = group.ToList();
            if (sharing.Count < 2)
            {
                continue;
            }

            var holder = sharing.FirstOrDefault(track => before.Contains(track)) ?? sharing[0];
            return (holder, sharing.First(track => track != holder));
        }

        return null;
    }
}
