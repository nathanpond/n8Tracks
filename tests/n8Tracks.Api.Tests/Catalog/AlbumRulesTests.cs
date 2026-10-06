using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>The domain rules for an Album's title, text, partial dates, UPC/EAN, and links.</summary>
public sealed class AlbumRulesTests
{
    [Fact]
    public void ATitleIsTrimmedAndOneToThreeHundredCodeUnits()
    {
        Assert.Equal("Pack EP", AlbumRules.NormaliseTitle("  Pack EP \t"));
        Assert.Empty(AlbumRules.TitleErrors("  " + new string('a', AlbumRules.TitleMaximumLength) + "  "));
        Assert.Equal(["Use at most 300 characters."], AlbumRules.TitleErrors(new string('a', 301)));
        Assert.Equal(["Enter a title."], AlbumRules.TitleErrors("   "));
        Assert.Equal(["Enter a title."], AlbumRules.TitleErrors(null));
        Assert.Equal(["A title is one line, with no control characters."], AlbumRules.TitleErrors("Pack\nEP"));
        Assert.Equal(AlbumRules.TitleKey("pack ep"), AlbumRules.TitleKey(" PACK EP "));
    }

    [Fact]
    public void DescriptionCopyrightAndPublishingArePlainTextWithLineBreaksAndBlankIsNone()
    {
        Assert.Equal("Line one\nLine two", AlbumRules.NormaliseText("  Line one\r\nLine two \n"));
        Assert.Null(AlbumRules.NormaliseText(" \r\n\t "));
        Assert.Null(AlbumRules.NormaliseText(null));
        Assert.Empty(AlbumRules.DescriptionErrors(new string('a', AlbumRules.DescriptionMaximumLength)));
        Assert.Equal(["Use at most 10,000 characters."], AlbumRules.DescriptionErrors(new string('a', 10_001)));
        Assert.Empty(AlbumRules.RightsErrors("℗ 2026 n8\n© 2026 n8"));
        Assert.Empty(AlbumRules.RightsErrors(new string('a', AlbumRules.RightsMaximumLength)));
        Assert.Equal(["Use at most 500 characters."], AlbumRules.RightsErrors(new string('a', 501)));
        Assert.Equal(["This text can contain line breaks but no other control characters."], AlbumRules.RightsErrors("a\u0007"));
        Assert.Equal(["A description cannot contain unpaired surrogate characters."], AlbumRules.DescriptionErrors("a\uD800b"));
    }

    [Theory]
    [InlineData("2026")]
    [InlineData("2026-03")]
    [InlineData("2026-03-01")]
    [InlineData(" 1000-01-01 ")]
    [InlineData("9999-12-31")]
    [InlineData("2024-02-29")]
    [InlineData("")]
    [InlineData(null)]
    public void AReleaseDateIsAYearAYearAndMonthOrAFullDate(string? date)
    {
        Assert.Empty(AlbumRules.DateErrors(date));
    }

    [Theory]
    [InlineData("26", "Enter a year (2026), a year and month (2026-03), or a full date (2026-03-01).")]
    [InlineData("2026-3", "Enter a year (2026), a year and month (2026-03), or a full date (2026-03-01).")]
    [InlineData("2026/03/01", "Enter a year (2026), a year and month (2026-03), or a full date (2026-03-01).")]
    [InlineData("March 2026", "Enter a year (2026), a year and month (2026-03), or a full date (2026-03-01).")]
    [InlineData("２０２６", "Enter a year (2026), a year and month (2026-03), or a full date (2026-03-01).")]
    [InlineData("0999", "Enter a year from 1000 to 9999.")]
    [InlineData("2026-13", "Enter a month from 01 to 12.")]
    [InlineData("2026-00-01", "Enter a month from 01 to 12.")]
    [InlineData("2025-02-29", "That day does not exist in that month.")]
    [InlineData("2026-04-31", "That day does not exist in that month.")]
    [InlineData("2026-04-00", "That day does not exist in that month.")]
    public void AnyOtherDateIsRefused(string date, string error)
    {
        Assert.Equal([error], AlbumRules.DateErrors(date));
    }

    [Fact]
    public void ADateIsStoredAsEnteredTrimmed()
    {
        Assert.Equal("2026-03", AlbumRules.NormaliseDate(" 2026-03 "));
        Assert.Null(AlbumRules.NormaliseDate("   "));
    }

    [Theory]
    [InlineData("036000291452", "036000291452")]
    [InlineData("0 36000 29145 2", "036000291452")]
    [InlineData("4006381333931", "4006381333931")]
    [InlineData("400-6381-33393-1", "4006381333931")]
    [InlineData("0036000291452", "0036000291452")]
    public void AUpcOrEanWithAValidCheckDigitIsKeptAsItsDigits(string sent, string stored)
    {
        Assert.Empty(AlbumRules.UpcErrors(sent));
        Assert.Equal(stored, AlbumRules.NormaliseUpc(sent));
    }

    [Theory]
    [InlineData("036000291453", "The check digit is wrong: check the code for a typing mistake.")]
    [InlineData("4006381333932", "The check digit is wrong: check the code for a typing mistake.")]
    [InlineData("03600029145", "Enter a UPC of 12 digits or an EAN of 13 digits.")]
    [InlineData("40063813339310", "Enter a UPC of 12 digits or an EAN of 13 digits.")]
    [InlineData("03600029145X", "Enter a UPC of 12 digits or an EAN of 13 digits.")]
    [InlineData("036000.291452", "Enter a UPC of 12 digits or an EAN of 13 digits.")]
    public void AnyOtherUpcIsRefused(string upc, string error)
    {
        Assert.Equal([error], AlbumRules.UpcErrors(upc));
    }

    [Fact]
    public void ATwelveDigitUpcAndTheSameCodeAsAnEanWithALeadingZeroShareAKey()
    {
        Assert.Equal(AlbumRules.UpcKey("036000291452"), AlbumRules.UpcKey("0036000291452"));
        Assert.NotEqual(AlbumRules.UpcKey("036000291452"), AlbumRules.UpcKey("4006381333931"));
        Assert.Empty(AlbumRules.UpcErrors(" - "));
        Assert.Null(AlbumRules.NormaliseUpc(" - "));
    }

    [Fact]
    public void LinksFollowTheArtistRulesNamingTheAlbum()
    {
        Assert.Empty(AlbumRules.LinkErrors([("Shop", "https://example.com/pack")]));
        Assert.Equal(["Link 1: Enter a web address starting with http:// or https://."], AlbumRules.LinkErrors([(null, "ftp://example.com")]));
        Assert.Equal(["An Album has at most 20 links."], AlbumRules.LinkErrors([.. Enumerable.Range(1, 21).Select(static i => ((string?)null, (string?)$"https://example.com/{i}"))]));
        Assert.Equal(["An Artist has at most 20 links."], ArtistRules.LinkErrors([.. Enumerable.Range(1, 21).Select(static i => ((string?)null, (string?)$"https://example.com/{i}"))]));
    }
}
