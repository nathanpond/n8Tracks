namespace n8Tracks.Domain.Catalog;

/// <summary>An Artist as a Song's credit names it: its ID and display name.</summary>
/// <param name="Id">The Artist's ID.</param>
/// <param name="Name">The Artist's display name as it is now.</param>
public sealed record CreditedArtist(Guid Id, string Name);

/// <summary>
/// Who a Song is credited to: at most one primary Artist and any number of featured Artists (up to
/// <see cref="SongCreditRules.FeaturedMaximumCount"/>), in the user's order. An Artist is credited
/// at most once on a Song, so never both primary and featured. Credits are not part of a Version's
/// inputs and not part of the editing history.
/// </summary>
/// <param name="Primary">The primary Artist, or null when the Song has none.</param>
/// <param name="Featured">The featured Artists, in the user's order.</param>
public sealed record SongCredits(CreditedArtist? Primary, IReadOnlyList<CreditedArtist> Featured)
{
    /// <summary>A Song credited to no one.</summary>
    public static SongCredits None { get; } = new(null, []);
}
