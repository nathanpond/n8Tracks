using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>The domain rules for creating a Version from another, and for its name and notes.</summary>
public sealed class VersionRulesTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewVersionCopiesTheSourcesInputsButNotItsAnnotations()
    {
        var source = new SongVersion(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "1",
            "First take",
            "Too slow.",
            VersionVisibility.Archived,
            "[Verse]\nRun",
            "punk, fast",
            Created,
            Created,
            Revision: 4);
        var id = Guid.CreateVersion7();

        var version = VersionRules.CreateFrom(source, id, VersionNumber.Parse("1.1"), "  Guitar experimentation ", Now);

        Assert.Equal(id, version.Id);
        Assert.Equal(source.SongId, version.SongId);
        Assert.Equal("1.1", version.Number);
        Assert.Equal("Guitar experimentation", version.Name);
        Assert.Null(version.Notes);
        Assert.Equal(VersionVisibility.Active, version.Visibility);
        Assert.Equal(source.Lyrics, version.Lyrics);
        Assert.Equal(source.Styles, version.Styles);
        Assert.Equal(Now, version.CreatedUtc);
        Assert.Equal(Now, version.UpdatedUtc);
        Assert.Equal(1, version.Revision);

        // Complement: without a name it has none, and an invalid name is never stored.
        Assert.Null(VersionRules.CreateFrom(source, id, VersionNumber.Parse("2"), "   ", Now).Name);
        Assert.Throws<ArgumentException>(() => VersionRules.CreateFrom(source, id, VersionNumber.Parse("2"), "a\nb", Now));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Guitar experimentation", true)]
    [InlineData("two\nlines", false)]
    [InlineData("tab\there", false)]
    [InlineData("🎸 paired", true)]
    public void ANameIsOptionalAndOneLine(string? name, bool valid)
    {
        Assert.Equal(valid, VersionRules.NameErrors(name).Length == 0);
    }

    [Fact]
    public void ANameWithAnUnpairedSurrogateIsRefused()
    {
        // Built at run time: an attribute argument would not keep a lone surrogate.
        var broken = new string([(char)0xD800, ' ', 'x']);

        Assert.Equal(["A name cannot contain unpaired surrogate characters."], VersionRules.NameErrors(broken));
    }

    [Fact]
    public void ANameIsAtMostTwoHundredCharactersOnceTrimmed()
    {
        Assert.Empty(VersionRules.NameErrors(new string('a', 200)));
        Assert.Empty(VersionRules.NameErrors($"  {new string('a', 200)}  "));
        Assert.Equal(["Use at most 200 characters."], VersionRules.NameErrors(new string('a', 201)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" \n\t ", null)]
    [InlineData("  Faster chorus.  ", "Faster chorus.")]
    [InlineData("Line one\r\nLine two\rLine three\n", "Line one\nLine two\nLine three")]
    public void NotesAreTrimmedWithLineEndingsAsNewlinesAndBlankIsNone(string? notes, string? stored)
    {
        Assert.Empty(VersionRules.NotesErrors(notes));
        Assert.Equal(stored, VersionRules.NormaliseNotes(notes));
    }

    [Fact]
    public void NotesTakeLineBreaksButNoOtherControlCharactersAndAreAtMostTenThousandCharacters()
    {
        Assert.Equal(["Notes can contain line breaks but no other control characters."], VersionRules.NotesErrors("tab\there"));
        Assert.Equal(["Notes cannot contain unpaired surrogate characters."], VersionRules.NotesErrors(new string([(char)0xDC00, 'x'])));
        Assert.Equal(["Use at most 10,000 characters."], VersionRules.NotesErrors(new string('a', VersionRules.NotesMaximumLength + 1)));

        // Complement: the limit itself, once trimmed, and a pair of surrogates are taken.
        Assert.Empty(VersionRules.NotesErrors($"\n{new string('a', VersionRules.NotesMaximumLength)}\n"));
        Assert.Empty(VersionRules.NotesErrors("🎸\nriff"));
    }
}
