using System.Globalization;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Application.Catalog;

/// <summary>How a change to an Album's tracks ended. Every refusal leaves the Album as it was.</summary>
public abstract record AlbumTrackOutcome
{
    private AlbumTrackOutcome()
    {
    }

    /// <summary>The Album as it is now: changed, or unchanged when the request changed nothing.</summary>
    public sealed record Saved(AlbumDetails Album, bool Changed) : AlbumTrackOutcome;

    /// <summary>Something sent is wrong. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AlbumTrackOutcome;

    /// <summary>The revision sent is not the Album's. <paramref name="Current"/> is the Album now.</summary>
    public sealed record Conflict(AlbumDetails Current) : AlbumTrackOutcome;

    /// <summary>There is no such Album.</summary>
    public sealed record NotFound : AlbumTrackOutcome;

    /// <summary>There is no such Song (to remove).</summary>
    public sealed record NoSuchSong : AlbumTrackOutcome;

    /// <summary>The Song is on the Album already; a Song is on an Album at most once.</summary>
    public sealed record AlreadyOnAlbum(AlbumDetails Current) : AlbumTrackOutcome;

    /// <summary>The last disc, <paramref name="Disc"/>, has track <see cref="AlbumTrackRules.MaximumNumber"/> already.</summary>
    public sealed record DiscFull(AlbumDetails Current, int Disc) : AlbumTrackOutcome;

    /// <summary>A full list that does not name exactly the Album's Songs, each once.</summary>
    public sealed record Mismatch(AlbumDetails Current) : AlbumTrackOutcome;

    /// <summary>
    /// Two tracks on the same disc would share a track number: <paramref name="Holder"/> holds it,
    /// and <paramref name="Refused"/> is the entry that was given it too.
    /// </summary>
    public sealed record TrackNumberTaken(AlbumDetails Current, AlbumTrack Holder, AlbumTrackPlace Refused) : AlbumTrackOutcome;
}

/// <summary>
/// An Album's tracks: Songs on the Album, each with a disc and a track number. A Song is on an Album
/// at most once, but on any number of Albums. Adding puts a Song at the end of the last disc;
/// removing renumbers the rest of its disc; reordering, renumbering, and moving tracks between discs
/// replace the whole list. Each works under the Album's revision and raises it. Changing an Album's
/// tracks never changes its Songs.
/// </summary>
public sealed class AlbumTrackService(IAlbumStore albums, IAlbumTrackStore tracks, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string SongIdField = "songId";

    public const string TracksField = "tracks";

    /// <summary>
    /// Adds the Song <paramref name="songId"/> at the end of the Album's last disc with the next track
    /// number, under the Album's revision. A Song already on it is
    /// <see cref="AlbumTrackOutcome.AlreadyOnAlbum"/>; one that does not exist is
    /// <see cref="AlbumTrackOutcome.Invalid"/>; a last disc with track 999 is
    /// <see cref="AlbumTrackOutcome.DiscFull"/>.
    /// </summary>
    public Task<AlbumTrackOutcome> AddAsync(Guid albumId, Guid songId, int revision, CancellationToken cancellationToken) =>
        RunAsync(
            albumId,
            revision,
            async (current, ct) =>
            {
                if (!await tracks.SongExistsAsync(songId, ct).ConfigureAwait(false))
                {
                    return new AlbumTrackOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [SongIdField] = ["There is no such Song."] });
                }

                var places = Places(current);
                if (places.Any(place => place.SongId == songId))
                {
                    return new AlbumTrackOutcome.AlreadyOnAlbum(current);
                }

                if (AlbumTrackRules.NextPlace(places, songId) is not { } next)
                {
                    return new AlbumTrackOutcome.DiscFull(current, places.Max(static place => place.Disc));
                }

                return await SetAsync(albumId, [.. places, next], revision, ct).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Takes the Song <paramref name="songId"/> off the Album, under its revision. The rest of its
    /// disc is renumbered 1, 2, 3…, and a disc left empty disappears. A Song that is not on the Album
    /// leaves it unchanged; one that does not exist is <see cref="AlbumTrackOutcome.NoSuchSong"/>.
    /// </summary>
    public Task<AlbumTrackOutcome> RemoveAsync(Guid albumId, Guid songId, int revision, CancellationToken cancellationToken) =>
        RunAsync(
            albumId,
            revision,
            async (current, ct) =>
            {
                var places = Places(current);
                if (!places.Any(place => place.SongId == songId))
                {
                    return await tracks.SongExistsAsync(songId, ct).ConfigureAwait(false)
                        ? new AlbumTrackOutcome.Saved(current, Changed: false)
                        : new AlbumTrackOutcome.NoSuchSong();
                }

                return await SetAsync(albumId, AlbumTrackRules.Without(places, songId), revision, ct).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Makes <paramref name="sent"/> the Album's tracks, under its revision: how reordering,
    /// renumbering, typing a track number, and moving a track to another disc are saved. It must name
    /// exactly the Songs on the Album, each once (<see cref="AlbumTrackOutcome.Mismatch"/>), with disc
    /// and track numbers from 1 to 999 (<see cref="AlbumTrackOutcome.Invalid"/>). Disc gaps close up;
    /// track numbers are kept as sent, so two tracks on one disc may not share one
    /// (<see cref="AlbumTrackOutcome.TrackNumberTaken"/>).
    /// </summary>
    public Task<AlbumTrackOutcome> ReplaceAsync(Guid albumId, IReadOnlyList<AlbumTrackPlace> sent, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sent);

        if (sent.Any(static place => !AlbumTrackRules.IsNumber(place.Disc) || !AlbumTrackRules.IsNumber(place.Track)))
        {
            return Task.FromResult<AlbumTrackOutcome>(new AlbumTrackOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [TracksField] = [string.Create(CultureInfo.InvariantCulture, $"Disc and track numbers are whole numbers from {AlbumTrackRules.MinimumNumber} to {AlbumTrackRules.MaximumNumber}.")],
            }));
        }

        return RunAsync(
            albumId,
            revision,
            async (current, ct) =>
            {
                var places = Places(current);
                var songs = places.Select(static place => place.SongId).ToHashSet();
                var named = sent.Select(static place => place.SongId).ToList();
                if (named.Count != songs.Count || named.Distinct().Count() != named.Count || !named.All(songs.Contains))
                {
                    return new AlbumTrackOutcome.Mismatch(current);
                }

                var next = AlbumTrackRules.CloseDiscGaps(sent);
                if (AlbumTrackRules.FindClash(next, places) is { } clash)
                {
                    var holder = current.Tracks.Single(track => track.SongId == clash.Holder.SongId) with { Disc = clash.Holder.Disc, Track = clash.Holder.Track };
                    return new AlbumTrackOutcome.TrackNumberTaken(current, holder, clash.Refused);
                }

                return next.SequenceEqual(places)
                    ? new AlbumTrackOutcome.Saved(current, Changed: false)
                    : await SetAsync(albumId, next, revision, ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static List<AlbumTrackPlace> Places(AlbumDetails album) => AlbumTrackRules.Ordered(album.Tracks.Select(static track => track.Place));

    /// <summary>Runs <paramref name="change"/> in one transaction on the Album as it is, once it is found at <paramref name="revision"/>.</summary>
    private Task<AlbumTrackOutcome> RunAsync(
        Guid albumId,
        int revision,
        Func<AlbumDetails, CancellationToken, Task<AlbumTrackOutcome>> change,
        CancellationToken cancellationToken) =>
        transaction.RunAsync<AlbumTrackOutcome>(
            async ct =>
            {
                if (await albums.FindAsync(albumId, ct).ConfigureAwait(false) is not { } current)
                {
                    return new AlbumTrackOutcome.NotFound();
                }

                return current.Revision != revision
                    ? new AlbumTrackOutcome.Conflict(current)
                    : await change(current, ct).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task<AlbumTrackOutcome> SetAsync(Guid albumId, IReadOnlyList<AlbumTrackPlace> places, int revision, CancellationToken cancellationToken)
    {
        if (!await tracks.TrySetTracksAsync(albumId, places, revision, time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            return await albums.FindAsync(albumId, cancellationToken).ConfigureAwait(false) is { } current
                ? new AlbumTrackOutcome.Conflict(current)
                : new AlbumTrackOutcome.NotFound();
        }

        return new AlbumTrackOutcome.Saved((await albums.FindAsync(albumId, cancellationToken).ConfigureAwait(false))!, Changed: true);
    }
}
