namespace n8Tracks.Infrastructure.Persistence;

/// <summary>One row of <c>artists</c>: an Artist.</summary>
public sealed class ArtistRecord
{
    public required Guid Id { get; set; }

    /// <summary>The display name: trimmed, inner white space collapsed, 1 to 200 UTF-16 code units.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// What names are compared and sorted by: NFC-normalised and upper-cased invariantly. Not unique:
    /// two Artists may share a name once the user confirms it.
    /// </summary>
    public required string NameKey { get; set; }

    /// <summary>Plain text, or null when there are none.</summary>
    public string? Notes { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>); the tie-breaker of the name order.</summary>
    public required string CreatedUtc { get; set; }

    public required string UpdatedUtc { get; set; }

    /// <summary>Starts at 1; every change of the Artist (aliases and links included) raises it.</summary>
    public int Revision { get; set; } = 1;
}

/// <summary>One row of <c>artist_aliases</c>: an alias of an Artist, at its place in the Artist's list.</summary>
public sealed class ArtistAliasRecord
{
    public required Guid ArtistId { get; set; }

    /// <summary>The alias's place in the Artist's list, from 0.</summary>
    public required int Position { get; set; }

    public required string Name { get; set; }

    /// <summary>As <see cref="ArtistRecord.NameKey"/>; unique within the Artist.</summary>
    public required string NameKey { get; set; }
}

/// <summary>One row of <c>artist_links</c>: an external link of an Artist, at its place in the Artist's list.</summary>
public sealed class ArtistLinkRecord
{
    public required Guid ArtistId { get; set; }

    /// <summary>The link's place in the Artist's list, from 0.</summary>
    public required int Position { get; set; }

    /// <summary>Up to 100 characters, or null.</summary>
    public string? Label { get; set; }

    /// <summary>An absolute http or https URL, up to 2,000 characters.</summary>
    public required string Url { get; set; }
}
