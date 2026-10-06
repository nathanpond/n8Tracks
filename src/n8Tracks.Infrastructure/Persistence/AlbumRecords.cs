namespace n8Tracks.Infrastructure.Persistence;

/// <summary>One row of <c>albums</c>: an Album and its release details.</summary>
public sealed class AlbumRecord
{
    public required Guid Id { get; set; }

    /// <summary>Trimmed, 1 to 300 UTF-16 code units. Not unique.</summary>
    public required string Title { get; set; }

    /// <summary>What titles are sorted by: trimmed, NFC-normalised, and upper-cased invariantly.</summary>
    public required string TitleKey { get; set; }

    /// <summary>Plain text, or null.</summary>
    public string? Description { get; set; }

    /// <summary>The Album Artist, or null.</summary>
    public Guid? AlbumArtistId { get; set; }

    /// <summary>A partial date as entered (<c>YYYY</c>, <c>YYYY-MM</c>, or <c>YYYY-MM-DD</c>), or null.</summary>
    public string? ReleaseDate { get; set; }

    /// <summary>A partial date as entered, or null.</summary>
    public string? OriginalReleaseDate { get; set; }

    /// <summary>The UPC/EAN's 12 or 13 digits, or null.</summary>
    public string? Upc { get; set; }

    /// <summary>The 13-digit form of <see cref="Upc"/> (a 12-digit UPC with a leading 0), or null.</summary>
    public string? UpcKey { get; set; }

    /// <summary>Plain text, or null.</summary>
    public string? Copyright { get; set; }

    /// <summary>Plain text, or null.</summary>
    public string? Publishing { get; set; }

    /// <summary>UTC, ISO 8601 (<see cref="UtcText"/>); the tie-breaker of the title order.</summary>
    public required string CreatedUtc { get; set; }

    public required string UpdatedUtc { get; set; }

    /// <summary>Starts at 1; every change of the Album (links included) raises it.</summary>
    public int Revision { get; set; } = 1;
}

/// <summary>One row of <c>album_links</c>: an external link of an Album, at its place in the Album's list.</summary>
public sealed class AlbumLinkRecord
{
    public required Guid AlbumId { get; set; }

    /// <summary>The link's place in the Album's list, from 0.</summary>
    public required int Position { get; set; }

    /// <summary>Up to 100 characters, or null.</summary>
    public string? Label { get; set; }

    /// <summary>An absolute http or https URL, up to 2,000 characters.</summary>
    public required string Url { get; set; }
}
