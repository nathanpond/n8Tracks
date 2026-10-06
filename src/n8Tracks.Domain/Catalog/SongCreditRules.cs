using System.Globalization;

namespace n8Tracks.Domain.Catalog;

/// <summary>
/// What a Song's credits may be (<see cref="SongCredits"/>): an optional primary Artist and up to
/// <see cref="FeaturedMaximumCount"/> featured Artists, no Artist featured twice and none both
/// primary and featured. These rules see only IDs; whether each ID is an Artist is the service's
/// check.
/// </summary>
public static class SongCreditRules
{
    public const int FeaturedMaximumCount = 50;

    /// <summary>The roles as they are stored and as the API spells them.</summary>
    public const string PrimaryRole = "primary";

    public const string FeaturedRole = "featured";

    /// <summary>The errors of a featured list given the primary Artist; empty when the credits are valid.</summary>
    public static string[] FeaturedErrors(Guid? primary, IReadOnlyList<Guid> featured)
    {
        ArgumentNullException.ThrowIfNull(featured);

        var errors = new List<string>();
        if (featured.Count > FeaturedMaximumCount)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"A Song has at most {FeaturedMaximumCount} featured Artists."));
        }

        if (featured.Distinct().Count() != featured.Count)
        {
            errors.Add("An Artist can be featured only once.");
        }

        if (primary is { } primaryId && featured.Contains(primaryId))
        {
            errors.Add("An Artist cannot be both the primary Artist and featured.");
        }

        return [.. errors];
    }
}
