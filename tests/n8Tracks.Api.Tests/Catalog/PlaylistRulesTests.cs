using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>The domain rules for a Playlist's title and description.</summary>
public sealed class PlaylistRulesTests
{
    [Fact]
    public void ATitleHasLineBreaksAsSpacesIsTrimmedAndIsOneToThreeHundredCodeUnits()
    {
        Assert.Equal("Road trip", PlaylistRules.NormaliseTitle("  Road trip \t"));
        Assert.Equal("Road trip 2026 mix", PlaylistRules.NormaliseTitle("\nRoad trip\r\n2026\rmix\n"));
        Assert.Empty(PlaylistRules.TitleErrors("Road\ntrip"));
        Assert.Empty(PlaylistRules.TitleErrors("  " + new string('a', PlaylistRules.TitleMaximumLength) + "\r\n"));
        Assert.Equal(["Use at most 300 characters."], PlaylistRules.TitleErrors(new string('a', 301)));
        Assert.Equal(["Enter a title."], PlaylistRules.TitleErrors(" \r\n "));
        Assert.Equal(["Enter a title."], PlaylistRules.TitleErrors(null));

        // Other control characters are still refused.
        Assert.Equal(["A title is one line, with no control characters."], PlaylistRules.TitleErrors("Road\ttrip"));
        Assert.Equal(PlaylistRules.TitleKey("road trip"), PlaylistRules.TitleKey(" ROAD TRIP "));
    }

    [Fact]
    public void ADescriptionIsPlainTextWithLineBreaksUpToTenThousandAndBlankIsNone()
    {
        Assert.Equal("For the drive.\nLoud.", PlaylistRules.NormaliseDescription("  For the drive.\r\nLoud. \n"));
        Assert.Null(PlaylistRules.NormaliseDescription(" \r\n "));
        Assert.Null(PlaylistRules.NormaliseDescription(null));
        Assert.Empty(PlaylistRules.DescriptionErrors(new string('a', PlaylistRules.DescriptionMaximumLength)));
        Assert.Equal(["Use at most 10,000 characters."], PlaylistRules.DescriptionErrors(new string('a', 10_001)));
        Assert.Equal(1000, PlaylistRules.MaximumSongCount);
    }
}
