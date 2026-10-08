using System.Globalization;
using n8Tracks.Application.Songs;

namespace n8Tracks.Application.Dashboard;

/// <summary>The Song the user opened last, as Open last Song (#230) shows it.</summary>
public abstract record LastSong
{
    private LastSong()
    {
    }

    /// <summary>No Song was opened yet.</summary>
    public sealed record None : LastSong;

    /// <summary>The Song opened last was deleted since, restorable or not.</summary>
    public sealed record Deleted : LastSong;

    /// <summary>The Song opened last, archived or not.</summary>
    public sealed record Found(Guid Id, string Shortcode, string Title) : LastSong;
}

/// <summary>Where the ID of the Song opened last is kept.</summary>
public interface ILastSongStore
{
    /// <summary>The ID, or null when none was recorded.</summary>
    Task<Guid?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the ID.</summary>
    Task WriteAsync(Guid songId, CancellationToken cancellationToken);
}

/// <summary>
/// The Song the user opened last (#230), for the dashboard's Open last Song. The Song page records it
/// with an explicit call each time it opens a Song; reading a Song records nothing. It is one
/// <c>settings</c> row holding the Song's ID, last write wins (no revision): two tabs opening Songs
/// at once leave one of the two, and either is right. The Song is read through
/// <see cref="SongService.FindAsync"/>, so a Song deleted since (in the retention store or gone) is
/// answered as deleted, and an archived one as itself.
/// </summary>
public sealed class LastSongService(ILastSongStore store, SongService songs)
{
    /// <summary>The Song opened last: none, deleted since, or found.</summary>
    public async Task<LastSong> GetAsync(CancellationToken cancellationToken)
    {
        var id = await store.FindAsync(cancellationToken).ConfigureAwait(false);
        if (id is not { } songId)
        {
            return new LastSong.None();
        }

        var song = await songs.FindAsync(songId.ToString("D", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return song is null ? new LastSong.Deleted() : new LastSong.Found(song.Id, song.Shortcode, song.Title);
    }

    /// <summary>Records <paramref name="songId"/> as the Song opened last; false (nothing written) when there is no such Song.</summary>
    public async Task<bool> RememberAsync(Guid songId, CancellationToken cancellationToken)
    {
        var song = await songs.FindAsync(songId.ToString("D", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        if (song is null)
        {
            return false;
        }

        await store.WriteAsync(song.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
