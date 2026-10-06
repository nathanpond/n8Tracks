namespace n8Tracks.Domain.Catalog;

/// <summary>
/// An Album: a titled collection with an optional Album Artist, independent of its Songs' credits,
/// and optional release details. Titles need not be unique. Tracks arrive with the track story and
/// artwork with the collection-artwork story; Albums have no shortcode in V1.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Title">Trimmed, 1 to <see cref="AlbumRules.TitleMaximumLength"/> code units (<see cref="AlbumRules.TitleErrors"/>).</param>
/// <param name="Description">Plain text with line breaks, or null (<see cref="AlbumRules.NormaliseText"/>).</param>
/// <param name="AlbumArtistId">The Album Artist, or null for none.</param>
/// <param name="Release">The release details.</param>
/// <param name="Links">External links, in the user's order, by the Artist link rules.</param>
public sealed record Album(Guid Id, string Title, string? Description, Guid? AlbumArtistId, AlbumRelease Release, IReadOnlyList<AlbumLink> Links)
{
    /// <summary>What titles are sorted by, ignoring case (<see cref="AlbumRules.TitleKey"/>).</summary>
    public string TitleKey => AlbumRules.TitleKey(Title);
}

/// <summary>
/// How an Album is released; every part is optional. Dates are partial dates as entered
/// (<c>YYYY</c>, <c>YYYY-MM</c>, or <c>YYYY-MM-DD</c>; <see cref="AlbumRules.DateErrors"/>), and
/// are not checked against each other.
/// </summary>
/// <param name="ReleaseDate">A partial date, or null.</param>
/// <param name="OriginalReleaseDate">A partial date, or null.</param>
/// <param name="Upc">A UPC (12 digits) or EAN (13 digits) with a valid check digit, digits only, or null.</param>
/// <param name="Copyright">One-paragraph plain text with line breaks, or null.</param>
/// <param name="Publishing">One-paragraph plain text with line breaks, or null.</param>
public sealed record AlbumRelease(string? ReleaseDate, string? OriginalReleaseDate, string? Upc, string? Copyright, string? Publishing)
{
    /// <summary>No release details.</summary>
    public static AlbumRelease None { get; } = new(null, null, null, null, null);
}

/// <summary>An external link of an Album: an absolute http or https URL and an optional label, as an Artist's.</summary>
public sealed record AlbumLink(string? Label, string Url);
