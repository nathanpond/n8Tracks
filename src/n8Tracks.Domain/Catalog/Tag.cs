namespace n8Tracks.Domain.Catalog;

/// <summary>
/// A Tag from the user's own list: a free-form coloured label for Songs (mood, theme, project, or
/// anything else). A Song may have any number of Tags, each at most once; taking one off a Song
/// leaves the Tag. Suno display tags stay Version and Generation data and never become Tags.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Name">Valid by <see cref="TagRules.NameErrors"/>, normalised by <see cref="TagRules.NormaliseName"/>.</param>
/// <param name="Colour">The name of one of the palette's colours (<see cref="TagRules.Palette"/>).</param>
public sealed record Tag(Guid Id, string Name, string Colour)
{
    /// <summary>What the name is compared by: unique among Tags (<see cref="TagRules.NameKey"/>).</summary>
    public string NameKey => TagRules.NameKey(Name);
}
