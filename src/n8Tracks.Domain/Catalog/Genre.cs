namespace n8Tracks.Domain.Catalog;

/// <summary>
/// A Genre from the user's own list. A Song may have any number of Genres, each at most once;
/// taking one off a Song leaves the Genre. Suno style text never becomes a Genre on its own.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Name">Valid by <see cref="GenreRules.NameErrors"/>, normalised by <see cref="GenreRules.NormaliseName"/>.</param>
public sealed record Genre(Guid Id, string Name)
{
    /// <summary>What the name is compared by: unique among Genres (<see cref="GenreRules.NameKey"/>).</summary>
    public string NameKey => GenreRules.NameKey(Name);
}
