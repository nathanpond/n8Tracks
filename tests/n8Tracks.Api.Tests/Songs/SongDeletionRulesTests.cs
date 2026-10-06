using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>The rules of deleting a Song (#102), apart from storage: when the title must be typed, and how it is compared.</summary>
public sealed class SongDeletionRulesTests
{
    private static readonly SongDeletionCounts Plain = new(Versions: 1, Generations: 0, Artwork: 0, AlbumMemberships: 0, PlaylistMemberships: 0, Relationships: 0, AudioFiles: 0);

    [Fact]
    public void ASongWithOneVersionAndNothingElseNeedsOnlyAPlainConfirmation()
    {
        Assert.False(SongDeletionRules.TitleRequired(Plain));

        // Artwork and audio files alone do not make the title required.
        Assert.False(SongDeletionRules.TitleRequired(Plain with { Artwork = 1, AudioFiles = 2 }));
    }

    [Fact]
    public void MoreThanOneVersionAnyGenerationMembershipOrRelationshipNeedsTheTitle()
    {
        Assert.True(SongDeletionRules.TitleRequired(Plain with { Versions = 2 }));
        Assert.True(SongDeletionRules.TitleRequired(Plain with { Generations = 1 }));
        Assert.True(SongDeletionRules.TitleRequired(Plain with { AlbumMemberships = 1 }));
        Assert.True(SongDeletionRules.TitleRequired(Plain with { PlaylistMemberships = 1 }));
        Assert.True(SongDeletionRules.TitleRequired(Plain with { Relationships = 1 }));
    }

    [Theory]
    [InlineData("Night Drive", true)]
    [InlineData("  Night Drive\t", true)]
    [InlineData("night drive", false)]
    [InlineData("Night  Drive", false)]
    [InlineData("Night Driv", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheTypedTitleMatchesAfterTrimmingAndExactlyOtherwise(string? typed, bool expected) =>
        Assert.Equal(expected, SongDeletionRules.Confirms(typed, "Night Drive"));
}
