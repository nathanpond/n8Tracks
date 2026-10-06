namespace n8Tracks.Infrastructure.Persistence;

/// <summary>One row of <c>playlists</c>: a Playlist's own fields. Its Songs are <c>playlist_songs</c>.</summary>
public sealed class PlaylistRecord
{
    public required Guid Id { get; set; }

    /// <summary>One line, trimmed, 1 to 300 UTF-16 code units. Not unique.</summary>
    public required string Title { get; set; }

    /// <summary>What titles are sorted by: trimmed, NFC-normalised, and upper-cased invariantly.</summary>
    public required string TitleKey { get; set; }

    /// <summary>Plain text, or null.</summary>
    public string? Description { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>); the tie-breaker of the title order.</summary>
    public required string CreatedUtc { get; set; }

    /// <summary>Moved by every change of the Playlist, its Songs and their order included.</summary>
    public required string UpdatedUtc { get; set; }

    /// <summary>Starts at 1; every change of the Playlist (adding, removing, and reordering Songs included) raises it.</summary>
    public int Revision { get; set; } = 1;
}

/// <summary>One row of <c>playlist_songs</c>: a Song on a Playlist, at its place. A Song is on a Playlist at most once.</summary>
public sealed class PlaylistSongRecord
{
    public required Guid PlaylistId { get; set; }

    public required Guid SongId { get; set; }

    /// <summary>The Song's place on the Playlist, from 0, with no gaps.</summary>
    public required int Position { get; set; }
}
