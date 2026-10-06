using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>The domain rules for a Song's credits: an optional primary Artist and up to fifty featured, each Artist once.</summary>
public sealed class SongCreditRulesTests
{
    private static readonly Guid N8 = Guid.CreateVersion7();
    private static readonly Guid Guest = Guid.CreateVersion7();

    [Fact]
    public void APrimaryArtistIsOptionalAndFeaturedArtistsMayBeNone()
    {
        Assert.Empty(SongCreditRules.FeaturedErrors(null, []));
        Assert.Empty(SongCreditRules.FeaturedErrors(N8, []));
        Assert.Empty(SongCreditRules.FeaturedErrors(null, [N8, Guest]));
        Assert.Empty(SongCreditRules.FeaturedErrors(N8, [Guest]));
        Assert.Same(SongCredits.None, SongCredits.None);
        Assert.Null(SongCredits.None.Primary);
        Assert.Empty(SongCredits.None.Featured);
    }

    [Fact]
    public void AnArtistIsNeverBothPrimaryAndFeaturedNorFeaturedTwice()
    {
        Assert.Equal(["An Artist cannot be both the primary Artist and featured."], SongCreditRules.FeaturedErrors(N8, [Guest, N8]));
        Assert.Equal(["An Artist can be featured only once."], SongCreditRules.FeaturedErrors(null, [Guest, N8, Guest]));
        Assert.Equal(
            ["An Artist can be featured only once.", "An Artist cannot be both the primary Artist and featured."],
            SongCreditRules.FeaturedErrors(N8, [N8, N8]));
    }

    [Fact]
    public void ASongHasAtMostFiftyFeaturedArtists()
    {
        var fifty = Enumerable.Range(0, SongCreditRules.FeaturedMaximumCount).Select(static _ => Guid.CreateVersion7()).ToList();
        Assert.Empty(SongCreditRules.FeaturedErrors(N8, fifty));
        Assert.Equal(["A Song has at most 50 featured Artists."], SongCreditRules.FeaturedErrors(N8, [.. fifty, Guest]));
    }
}
