using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>The domain rules for Genre names and Song notes.</summary>
public sealed class GenreRulesTests
{
    [Theory]
    [InlineData("Folk", "Folk")]
    [InlineData("  Indie   Rock  ", "Indie Rock")]
    [InlineData("Hip\tHop\n\nFusion", "Hip Hop Fusion")]
    public void ANameIsTrimmedAndItsInnerWhiteSpaceCollapsed(string sent, string stored)
    {
        Assert.Empty(GenreRules.NameErrors(sent));
        Assert.Equal(stored, GenreRules.NormaliseName(sent));
    }

    [Fact]
    public void ANameIsOneToFiftyCodeUnitsAfterNormalising()
    {
        Assert.Equal(["Enter a name."], GenreRules.NameErrors(null));
        Assert.Equal(["Enter a name."], GenreRules.NameErrors("   "));
        Assert.Empty(GenreRules.NameErrors(new string('a', GenreRules.NameMaximumLength)));
        Assert.Equal(["Use at most 50 characters."], GenreRules.NameErrors(new string('a', GenreRules.NameMaximumLength + 1)));

        // Counted after collapsing: 25 + 1 + 24 = 50 once the run of spaces is one.
        Assert.Empty(GenreRules.NameErrors(new string('a', 25) + "      " + new string('b', 24)));

        // UTF-16 code units: an emoji is two.
        Assert.Equal(["Use at most 50 characters."], GenreRules.NameErrors(new string('a', 49) + "\U0001F3B8"));
    }

    [Fact]
    public void ANameHasNoControlCharactersAndNoBrokenSurrogates()
    {
        Assert.Equal(["A name is one line, with no control characters."], GenreRules.NameErrors("Folk\u0007"));
        Assert.Equal(["A name cannot contain unpaired surrogate characters."], GenreRules.NameErrors("Folk\uD83C"));
    }

    [Fact]
    public void NamesAreComparedIgnoringCaseSpacingAndUnicodeForm()
    {
        Assert.Equal(GenreRules.NameKey("Indie Rock"), GenreRules.NameKey("  INDIE   rock "));

        // "é" precomposed and decomposed are the same name.
        Assert.Equal(GenreRules.NameKey("Café Jazz"), GenreRules.NameKey("Café jazz"));

        // Complement: different names have different keys.
        Assert.NotEqual(GenreRules.NameKey("Indie Rock"), GenreRules.NameKey("Indie Pop"));
        Assert.Equal(GenreRules.NameKey("Folk"), new Genre(Guid.CreateVersion7(), "folk").NameKey);
    }

    [Fact]
    public void SongNotesFollowTheVersionNotesRule()
    {
        Assert.Empty(SongRules.NotesErrors(null));
        Assert.Null(SongRules.NormaliseNotes("   "));
        Assert.Equal("One\nTwo", SongRules.NormaliseNotes("  One\r\nTwo "));
        Assert.Empty(SongRules.NotesErrors(new string('a', SongRules.NotesMaximumLength)));
        Assert.Equal(["Use at most 10,000 characters."], SongRules.NotesErrors(new string('a', SongRules.NotesMaximumLength + 1)));
        Assert.Equal(["Notes can contain line breaks but no other control characters."], SongRules.NotesErrors("Bad\u0007"));
    }
}
