using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>The domain rules for Tag names and the colour a new Tag gets.</summary>
public sealed class TagRulesTests
{
    [Theory]
    [InlineData("running", "running")]
    [InlineData("  late   summer  ", "late summer")]
    [InlineData("road\ttrip\n\nmix", "road trip mix")]
    public void ANameIsTrimmedAndItsInnerWhiteSpaceCollapsed(string sent, string stored)
    {
        Assert.Empty(TagRules.NameErrors(sent));
        Assert.Equal(stored, TagRules.NormaliseName(sent));
    }

    [Fact]
    public void ANameIsOneToFiftyCodeUnitsWithNoControlCharacters()
    {
        Assert.Equal(["Enter a name."], TagRules.NameErrors(null));
        Assert.Equal(["Enter a name."], TagRules.NameErrors("  "));
        Assert.Empty(TagRules.NameErrors(new string('t', TagRules.NameMaximumLength)));
        Assert.Equal(["Use at most 50 characters."], TagRules.NameErrors(new string('t', 51)));
        Assert.Equal(["Use at most 50 characters."], TagRules.NameErrors(new string('t', 49) + "\U0001F3B8"));
        Assert.Equal(["A name is one line, with no control characters."], TagRules.NameErrors("mood\u0007"));
        Assert.Equal(["A name cannot contain unpaired surrogate characters."], TagRules.NameErrors("mood\uD83C"));
    }

    [Fact]
    public void NamesAreComparedIgnoringCaseOnceNfcNormalised()
    {
        Assert.Equal(TagRules.NameKey("Summer"), TagRules.NameKey("  SUMMER "));
        Assert.Equal(TagRules.NameKey("Café"), TagRules.NameKey("café"));

        // Complement: different words stay different.
        Assert.NotEqual(TagRules.NameKey("summer"), TagRules.NameKey("summers"));
    }

    [Fact]
    public void ThePaletteIsTheTwelveWorkflowStateColoursInOrder()
    {
        Assert.Equal(StateColours.All.Select(static colour => colour.Name), TagRules.Palette);
        Assert.Equal(12, TagRules.Palette.Count);
        Assert.True(TagRules.IsColour("teal"));
        Assert.False(TagRules.IsColour("Teal"));
        Assert.False(TagRules.IsColour("purple"));
        Assert.False(TagRules.IsColour(null));
    }

    [Fact]
    public void ANewTagTakesTheFirstColourNoTagHas()
    {
        Assert.Equal("gray", TagRules.NextColour(new Dictionary<string, int>()));
        Assert.Equal("red", TagRules.NextColour(new Dictionary<string, int> { ["gray"] = 4 }));

        // A gap earlier in the palette is filled first, however many Tags the others have.
        var allButCyan = TagRules.Palette.Where(static colour => colour != "cyan").ToDictionary(static colour => colour, static _ => 1);
        Assert.Equal("cyan", TagRules.NextColour(allButCyan));
        allButCyan["cyan"] = 0;
        Assert.Equal("cyan", TagRules.NextColour(allButCyan));
    }

    [Fact]
    public void OnceEveryColourIsInUseANewTagTakesTheLeastUsedTheEarlierOnATie()
    {
        var counts = TagRules.Palette.ToDictionary(static colour => colour, static _ => 2);
        Assert.Equal("gray", TagRules.NextColour(counts));

        counts["gray"] = 3;
        counts["red"] = 3;
        Assert.Equal("pink", TagRules.NextColour(counts));

        counts["orange"] = 1;
        Assert.Equal("orange", TagRules.NextColour(counts));

        // Complement: a name that is not a palette colour is not a candidate and counts for nothing.
        counts["purple"] = 0;
        Assert.Equal("orange", TagRules.NextColour(counts));
    }
}
