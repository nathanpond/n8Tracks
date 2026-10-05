namespace n8Tracks.Infrastructure.Persistence;

/// <summary>One row of <c>tags</c>: a Tag of the user's list.</summary>
public sealed class TagRecord
{
    public required Guid Id { get; set; }

    /// <summary>Trimmed, inner white space collapsed, 1 to 50 UTF-16 code units.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// What names are compared by: NFC-normalised and upper-cased invariantly; unique, so no two
    /// Tags differ only in letter case.
    /// </summary>
    public required string NameKey { get; set; }

    /// <summary>The name of one of the twelve palette colours.</summary>
    public required string Colour { get; set; }

    /// <summary>Starts at 1; Settings → Tags raises it on each change of the Tag itself.</summary>
    public int Revision { get; set; } = 1;
}

/// <summary>One row of <c>song_tags</c>: a Song has a Tag (once).</summary>
public sealed class SongTagRecord
{
    public required Guid SongId { get; set; }

    public required Guid TagId { get; set; }
}
