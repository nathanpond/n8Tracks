using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Catalog;
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
            VersionInputRules.Defaults(CreateFieldInventory.Embedded, "Run") with { Kind = VersionKind.Speech, SongMode = CreationMode.Simple, SimplePrompt = "fast", Weirdness = 10 },
            Created,
            Created,
            Revision: 4,
            new VersionLineage(
                [new VersionSource(SystemRelationshipTypes.Cover.Id, SunoActions.Cover, VersionSourceTarget.OfExternal("clip-1"))],
                [],
                null,
                new VersionVoice("persona-1", "Persona"),
                []));
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

        // Every option is copied, the ones that do not apply to the kind and mode included.
        Assert.Equal(source.Inputs, version.Inputs);

        // Its sources (#122) are creation inputs too: the same targets, in the same order.
        Assert.Equal(source.Lineage, version.Lineage);
        Assert.Single(version.Lineage.AudioSources);
        Assert.False(version.IsFrozen);
        Assert.Equal(Now, version.CreatedUtc);
        Assert.Equal(Now, version.UpdatedUtc);
        Assert.Equal(1, version.Revision);

        // Complement: without a name it has none, and an invalid name is never stored.
        Assert.Null(VersionRules.CreateFrom(source, id, VersionNumber.Parse("2"), "   ", Now).Name);
        Assert.Throws<ArgumentException>(() => VersionRules.CreateFrom(source, id, VersionNumber.Parse("2"), "a\nb", Now));

        // Lyrics or styles given replace the copy (line endings normalised); invalid ones are never stored.
        var carried = VersionRules.CreateFrom(source, id, VersionNumber.Parse("2"), null, Now, "Carried\r\nwords", styles: null);
        Assert.Equal("Carried\nwords", carried.Lyrics);
        Assert.Equal(source.Styles, carried.Styles);
        Assert.Equal("", VersionRules.CreateFrom(source, id, VersionNumber.Parse("2"), null, Now, lyrics: null, styles: "").Styles);
        Assert.Throws<ArgumentException>(() => VersionRules.CreateFrom(source, id, VersionNumber.Parse("2"), null, Now, new string('a', VersionRules.LyricsMaximumLength + 1)));
        Assert.Throws<ArgumentException>(() => VersionRules.CreateFrom(source, id, VersionNumber.Parse("2"), null, Now, styles: "a\0b"));
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

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\r\nb\r", "a\n\nb\n")]
    [InlineData("  [Verse]  \n\tline\t\n\n", "  [Verse]  \n\tline\t\n\n")]
    [InlineData("", "")]
    public void InputsOnlyHaveTheirLineEndingsConverted(string sent, string stored)
    {
        Assert.Equal(stored, VersionRules.NormaliseInput(sent));
    }

    [Fact]
    public void LyricsAndStylesAcceptAnyUnicodeTextUpToTheirLimits()
    {
        Assert.Empty(VersionRules.LyricsErrors(""));
        Assert.Empty(VersionRules.LyricsErrors("[Verse]\n\u05e9\u05dc\u05d5\u05dd e\u0301 \ud83c\udfb8 (ooh)\t\u0007"));
        Assert.Empty(VersionRules.LyricsErrors(new string('x', VersionRules.LyricsMaximumLength)));
        Assert.Empty(VersionRules.StylesErrors(new string('x', VersionRules.StylesMaximumLength)));

        // The limit counts code units after line endings are converted: 5,000 CRLF pairs are 5,000.
        Assert.Empty(VersionRules.LyricsErrors(string.Concat(Enumerable.Repeat("\r\n", VersionRules.LyricsMaximumLength))));
    }

    [Fact]
    public void LyricsAndStylesOverTheLimitNullTheNullCharacterAndBrokenPairsAreRefused()
    {
        Assert.Equal(["Use at most 5,000 characters."], VersionRules.LyricsErrors(new string('x', VersionRules.LyricsMaximumLength + 1)));
        Assert.Equal(["Use at most 1,000 characters."], VersionRules.StylesErrors(new string('x', VersionRules.StylesMaximumLength + 1)));
        Assert.Equal(["Send text: an empty string clears the lyrics."], VersionRules.LyricsErrors(null));
        Assert.Equal(["Send text: an empty string clears the styles."], VersionRules.StylesErrors(null));
        Assert.Single(VersionRules.LyricsErrors("a\0b"));
        Assert.Single(VersionRules.StylesErrors("\ud83c"));
        Assert.Single(VersionRules.LyricsErrors("x\udfb8"));
    }
}
