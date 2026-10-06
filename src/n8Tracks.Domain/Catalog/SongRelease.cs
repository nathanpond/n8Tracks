namespace n8Tracks.Domain.Catalog;

/// <summary>Whether a Song's content is marked explicit or clean; a Song may be neither (not set).</summary>
public enum ExplicitContent
{
    /// <summary>Marked as containing explicit content.</summary>
    Explicit,

    /// <summary>Marked as clean.</summary>
    Clean,
}

/// <summary>An external link of a Song (streaming or distribution): an absolute http or https URL and an optional label, as an Artist's.</summary>
public sealed record SongLink(string? Label, string Url);

/// <summary>
/// How a Song is released; every part is optional (<see cref="SongReleaseRules"/>). Dates are
/// partial dates as entered, as an Album's are, and are not checked against each other.
/// </summary>
/// <param name="ReleaseDate">A partial date (<c>YYYY</c>, <c>YYYY-MM</c>, or <c>YYYY-MM-DD</c>), or null.</param>
/// <param name="OriginalReleaseDate">A partial date, or null.</param>
/// <param name="Explicit">Explicit or clean, or null when not set.</param>
/// <param name="Copyright">Plain text with line breaks, or null.</param>
/// <param name="Publishing">Plain text with line breaks, or null.</param>
/// <param name="Isrc">Twelve characters, upper case, no hyphens, or null.</param>
/// <param name="Language">A code from <see cref="Languages"/>, or null.</param>
/// <param name="Links">External links, in the user's order.</param>
public sealed record SongRelease(
    string? ReleaseDate,
    string? OriginalReleaseDate,
    ExplicitContent? Explicit,
    string? Copyright,
    string? Publishing,
    string? Isrc,
    string? Language,
    IReadOnlyList<SongLink> Links)
{
    /// <summary>No release details.</summary>
    public static SongRelease None { get; } = new(null, null, null, null, null, null, null, []);

    /// <summary>Equal when every part is, the links compared in order.</summary>
    public bool Equals(SongRelease? other) =>
        other is not null
        && string.Equals(ReleaseDate, other.ReleaseDate, StringComparison.Ordinal)
        && string.Equals(OriginalReleaseDate, other.OriginalReleaseDate, StringComparison.Ordinal)
        && Explicit == other.Explicit
        && string.Equals(Copyright, other.Copyright, StringComparison.Ordinal)
        && string.Equals(Publishing, other.Publishing, StringComparison.Ordinal)
        && string.Equals(Isrc, other.Isrc, StringComparison.Ordinal)
        && string.Equals(Language, other.Language, StringComparison.Ordinal)
        && Links.SequenceEqual(other.Links);

    public override int GetHashCode() => HashCode.Combine(ReleaseDate, OriginalReleaseDate, Explicit, Isrc, Language, Links.Count);
}
