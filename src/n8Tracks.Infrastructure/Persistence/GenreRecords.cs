namespace n8Tracks.Infrastructure.Persistence;

/// <summary>One row of <c>genres</c>: a Genre of the user's list.</summary>
public sealed class GenreRecord
{
    public required Guid Id { get; set; }

    /// <summary>Trimmed, inner white space collapsed, 1 to 50 UTF-16 code units.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// What names are compared and ordered by: NFC-normalised and upper-cased invariantly; unique,
    /// so no two Genres differ only in letter case.
    /// </summary>
    public required string NameKey { get; set; }

    /// <summary>Starts at 1 and goes up by one on each rename, and when other Genres are merged into it.</summary>
    public int Revision { get; set; } = 1;
}

/// <summary>One row of <c>song_genres</c>: a Song has a Genre (once).</summary>
public sealed class SongGenreRecord
{
    public required Guid SongId { get; set; }

    public required Guid GenreId { get; set; }
}
