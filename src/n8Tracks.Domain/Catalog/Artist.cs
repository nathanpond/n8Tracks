namespace n8Tracks.Domain.Catalog;

/// <summary>
/// An Artist: one record per performer or act, reused across Songs and Albums (the credit and
/// Album stories link them). Names need not be unique: a second Artist may share a name or alias
/// once the user confirms it. Suno creator names are provider metadata and never create or change
/// an Artist.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Name">The display name: valid by <see cref="ArtistRules.NameErrors"/>, normalised by <see cref="ArtistRules.NormaliseName"/>.</param>
/// <param name="Aliases">Other names, in the user's order, each normalised like the name and unique within the Artist ignoring case.</param>
/// <param name="Notes">Plain text, or null when there are none (<see cref="ArtistRules.NormaliseNotes"/>).</param>
/// <param name="Links">External links, in the user's order.</param>
public sealed record Artist(Guid Id, string Name, IReadOnlyList<string> Aliases, string? Notes, IReadOnlyList<ArtistLink> Links)
{
    /// <summary>What the display name is compared by (<see cref="ArtistRules.NameKey"/>).</summary>
    public string NameKey => ArtistRules.NameKey(Name);
}

/// <summary>An external link of an Artist: an absolute http or https URL and an optional label.</summary>
/// <param name="Label">Up to <see cref="ArtistRules.LinkLabelMaximumLength"/> characters, or null.</param>
/// <param name="Url">Up to <see cref="ArtistRules.LinkUrlMaximumLength"/> characters, as the user gave it (trimmed).</param>
public sealed record ArtistLink(string? Label, string Url);
