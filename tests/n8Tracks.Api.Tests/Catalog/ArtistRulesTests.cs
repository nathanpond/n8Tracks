using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>The domain rules for an Artist's display name, aliases, notes, and links.</summary>
public sealed class ArtistRulesTests
{
    [Fact]
    public void ADisplayNameIsTrimmedCollapsedAndOneToTwoHundredCodeUnits()
    {
        Assert.Equal("The Band", ArtistRules.NormaliseName("  The \t  Band "));
        Assert.Empty(ArtistRules.NameErrors(new string('a', ArtistRules.NameMaximumLength)));
        Assert.Equal(["Use at most 200 characters."], ArtistRules.NameErrors(new string('a', 201)));
        Assert.Equal(["Enter a name."], ArtistRules.NameErrors(null));
        Assert.Equal(["Enter a name."], ArtistRules.NameErrors("   "));
        Assert.Equal(["A name is one line, with no control characters."], ArtistRules.NameErrors("n8\u0007"));
        Assert.Equal(ArtistRules.NameKey("n8"), ArtistRules.NameKey("  N8 "));

        // Complement: the Genre limit is untouched by the Artist one.
        Assert.Equal(["Use at most 50 characters."], GenreRules.NameErrors(new string('a', 51)));
    }

    [Fact]
    public void AliasesAreValidNamesUniqueWithinTheArtistIgnoringCaseAndNeverItsOwnName()
    {
        Assert.Empty(ArtistRules.AliasErrors(["Nate", "N. Pond"], "n8"));
        Assert.Equal(["Alias 2: The Artist already has this alias."], ArtistRules.AliasErrors(["Nate", "  NATE "], "n8"));
        Assert.Equal(["Alias 1: An alias cannot be the Artist's own name."], ArtistRules.AliasErrors(["N8"], "n8"));
        Assert.Equal(["Alias 1: Enter a name.", "Alias 2: Use at most 200 characters."], ArtistRules.AliasErrors([" ", new string('a', 201)], "n8"));
        Assert.Empty(ArtistRules.AliasErrors([.. Enumerable.Range(1, ArtistRules.AliasMaximumCount).Select(static i => $"alias {i}")], "n8"));
        Assert.Equal(["An Artist has at most 20 aliases."], ArtistRules.AliasErrors([.. Enumerable.Range(1, 21).Select(static i => $"alias {i}")], "n8"));

        // A wrong name does not make an alias wrong.
        Assert.Empty(ArtistRules.AliasErrors(["Nate"], null));
    }

    [Theory]
    [InlineData("https://example.com/n8")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM/A?b=c#d")]
    [InlineData("  https://example.com/padded  ")]
    public void ALinkIsAnAbsoluteHttpOrHttpsUrl(string url)
    {
        Assert.Empty(ArtistRules.LinkErrors([(null, url)]));
        Assert.Equal(url.Trim(), ArtistRules.NormaliseUrl(url));
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:n8@example.com")]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    [InlineData("https://exa mple.com")]
    [InlineData("file:///etc/passwd")]
    public void AnyOtherUrlIsRefused(string url)
    {
        Assert.Equal(["Link 1: Enter a web address starting with http:// or https://."], ArtistRules.LinkErrors([(null, url)]));
    }

    [Fact]
    public void ALinkHasAnOptionalShortLabelAndAUrlWithinTheLimits()
    {
        Assert.Null(ArtistRules.NormaliseLabel("   "));
        Assert.Equal("Official site", ArtistRules.NormaliseLabel(" Official   site "));
        Assert.Empty(ArtistRules.LinkErrors([(new string('l', ArtistRules.LinkLabelMaximumLength), "https://example.com")]));
        Assert.Equal(["Link 1: Use at most 100 characters for the label."], ArtistRules.LinkErrors([(new string('l', 101), "https://example.com")]));
        Assert.Equal(["Link 2: Enter a URL."], ArtistRules.LinkErrors([("a", "https://example.com"), ("b", " ")]));

        var longest = "https://example.com/" + new string('p', ArtistRules.LinkUrlMaximumLength - 20);
        Assert.Empty(ArtistRules.LinkErrors([(null, longest)]));
        Assert.Equal(["Link 1: Use at most 2,000 characters for the URL."], ArtistRules.LinkErrors([(null, longest + "p")]));
        Assert.Equal(
            ["An Artist has at most 20 links."],
            ArtistRules.LinkErrors([.. Enumerable.Range(1, 21).Select(static i => ((string?)null, (string?)$"https://example.com/{i}"))]));
    }

    [Fact]
    public void NotesFollowTheSongNotesRule()
    {
        Assert.Null(ArtistRules.NormaliseNotes("  \r\n "));
        Assert.Equal("one\ntwo", ArtistRules.NormaliseNotes("one\r\ntwo\n"));
        Assert.Empty(ArtistRules.NotesErrors(new string('n', ArtistRules.NotesMaximumLength)));
        Assert.Equal(["Use at most 10,000 characters."], ArtistRules.NotesErrors(new string('n', 10_001)));
    }
}
