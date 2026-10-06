using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>The domain rules for a Song's release details: ISRC, language, explicit flag, dates, rights text, and links.</summary>
public sealed class SongReleaseRulesTests
{
    [Theory]
    [InlineData("US-S1Z-99-00001", "USS1Z9900001")]
    [InlineData("uss1z9900001", "USS1Z9900001")]
    [InlineData(" US S1Z 99 00001 ", "USS1Z9900001")]
    [InlineData("ISRC US-S1Z-99-00001", "USS1Z9900001")]
    [InlineData("isrc:US-S1Z-99-00001", ":USS1Z9900001")]
    [InlineData("GB-AAA-26-12345", "GBAAA2612345")]
    [InlineData("ISRC10000001", "ISRC10000001")]
    public void AnIsrcIsReadIgnoringCaseSpacesHyphensAndALeadingIsrcAndStoredUpperCaseWithoutHyphens(string sent, string stored)
    {
        Assert.Equal(stored, SongReleaseRules.NormaliseIsrc(sent));
    }

    [Theory]
    [InlineData("US-S1Z-99-00001")]
    [InlineData("ISRC US-S1Z-99-00001")]
    [InlineData("gb-a1b-26-00042")]
    [InlineData("ISRC10000001")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void AnIsrcIsTwoLettersThreeLettersOrDigitsAndSevenDigits(string? isrc)
    {
        Assert.Empty(SongReleaseRules.IsrcErrors(isrc));
    }

    [Theory]
    [InlineData("US-S1Z-99-0000", "An ISRC has 12 characters once spaces and hyphens are left out; this has 11.")]
    [InlineData("US-S1Z-99-000011", "An ISRC has 12 characters once spaces and hyphens are left out; this has 13.")]
    [InlineData("isrc:US-S1Z-99-00001", "An ISRC has 12 characters once spaces and hyphens are left out; this has 13.")]
    [InlineData("1S-S1Z-99-00001", "Enter an ISRC as two letters, three letters or digits, and seven digits (such as US-S1Z-99-00001).")]
    [InlineData("US-S_Z-99-00001", "Enter an ISRC as two letters, three letters or digits, and seven digits (such as US-S1Z-99-00001).")]
    [InlineData("US-S1Z-99-0000A", "Enter an ISRC as two letters, three letters or digits, and seven digits (such as US-S1Z-99-00001).")]
    [InlineData("ÜS-S1Z-99-00001", "Enter an ISRC as two letters, three letters or digits, and seven digits (such as US-S1Z-99-00001).")]
    public void AnIsrcOfAnyOtherShapeIsRefused(string isrc, string error)
    {
        Assert.Equal([error], SongReleaseRules.IsrcErrors(isrc));
    }

    [Fact]
    public void ALanguageIsACodeFromTheBundledListInAnyCaseAndIsStoredLowerCase()
    {
        Assert.Equal("en", SongReleaseRules.NormaliseLanguage(" EN "));
        Assert.Empty(SongReleaseRules.LanguageErrors("En"));
        Assert.Empty(SongReleaseRules.LanguageErrors("zxx"));
        Assert.Empty(SongReleaseRules.LanguageErrors(" "));
        Assert.Empty(SongReleaseRules.LanguageErrors(null));
        Assert.Null(SongReleaseRules.NormaliseLanguage("  "));
        Assert.Equal(["Choose a language from the list."], SongReleaseRules.LanguageErrors("eng"));
        Assert.Equal(["Choose a language from the list."], SongReleaseRules.LanguageErrors("xx"));
        Assert.Equal(["Choose a language from the list."], SongReleaseRules.LanguageErrors("en-US"));
    }

    [Fact]
    public void TheLanguageListIsEveryIso6391CodeAndZxxByEnglishName()
    {
        var all = Languages.All;
        Assert.Equal(184, all.Count);
        Assert.Equal(all.Count, all.Select(static language => language.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all.Where(static language => language.Code != Languages.NoLinguisticContent), static language => Assert.Matches("^[a-z]{2}$", language.Code));
        Assert.Equal(all.Select(static language => language.Name).Order(StringComparer.OrdinalIgnoreCase), all.Select(static language => language.Name));
        Assert.Equal("No linguistic content", Languages.Find("zxx")?.Name);
        Assert.Equal("English", Languages.Find("en")?.Name);
        Assert.Equal("Norwegian Bokmål", Languages.Find("nb")?.Name);
        Assert.Null(Languages.Find("EN"));
        Assert.Equal("Abkhazian", all[0].Name);
        Assert.Equal("Zulu", all[^1].Name);
    }

    [Fact]
    public void TheExplicitFlagIsExplicitCleanOrNotSet()
    {
        Assert.Empty(SongReleaseRules.ExplicitErrors("explicit"));
        Assert.Empty(SongReleaseRules.ExplicitErrors("clean"));
        Assert.Empty(SongReleaseRules.ExplicitErrors(null));
        Assert.Equal(["Choose explicit, clean, or null for not set."], SongReleaseRules.ExplicitErrors("Explicit"));
        Assert.Equal(["Choose explicit, clean, or null for not set."], SongReleaseRules.ExplicitErrors(""));
        Assert.Equal(ExplicitContent.Explicit, SongReleaseRules.ParseExplicit("explicit"));
        Assert.Equal(ExplicitContent.Clean, SongReleaseRules.ParseExplicit("clean"));
        Assert.Null(SongReleaseRules.ParseExplicit(null));
        Assert.Equal("clean", SongReleaseRules.ExplicitText(ExplicitContent.Clean));
        Assert.Null(SongReleaseRules.ExplicitText(null));
    }

    [Fact]
    public void DatesRightsTextAndLinksFollowTheAlbumAndArtistRules()
    {
        Assert.Empty(SongReleaseRules.DateErrors("2026-03"));
        Assert.Equal(["Enter a year from 1000 to 9999."], SongReleaseRules.DateErrors("0999"));
        Assert.Equal(["That day does not exist in that month."], SongReleaseRules.DateErrors("2025-02-29"));
        Assert.Equal("2026", SongReleaseRules.NormaliseDate(" 2026 "));
        Assert.Equal("℗ 2026 n8\n© n8", SongReleaseRules.NormaliseText("  ℗ 2026 n8\r\n© n8 "));
        Assert.Null(SongReleaseRules.NormaliseText(" \n "));
        Assert.Equal(["Use at most 500 characters."], SongReleaseRules.RightsErrors(new string('a', 501)));
        Assert.Empty(SongReleaseRules.LinkErrors([("Spotify", "https://open.spotify.com/track/1"), (null, "http://example.com")]));
        Assert.Equal(["Link 1: Enter a web address starting with http:// or https://."], SongReleaseRules.LinkErrors([("FTP", "ftp://example.com/a")]));
        Assert.Equal(["A Song has at most 20 links."], SongReleaseRules.LinkErrors([.. Enumerable.Repeat<(string?, string?)>((null, "https://example.com"), 21)]));
    }

    [Fact]
    public void TwoReleasesAreEqualWhenEveryPartIsTheLinksInOrder()
    {
        var release = new SongRelease("2026", null, ExplicitContent.Clean, null, null, "USS1Z9900001", "en", [new SongLink("A", "https://a.example"), new SongLink(null, "https://b.example")]);
        Assert.Equal(release, release with { Links = [new SongLink("A", "https://a.example"), new SongLink(null, "https://b.example")] });
        Assert.NotEqual(release, release with { Links = [new SongLink(null, "https://b.example"), new SongLink("A", "https://a.example")] });
        Assert.NotEqual(release, release with { Explicit = null });
        Assert.Equal(SongRelease.None, new SongRelease(null, null, null, null, null, null, null, []));
    }
}
