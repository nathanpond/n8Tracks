using System.Globalization;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>The domain rules for Songs: the creation rule, title and concept, shortcodes, and the default states and palette.</summary>
public sealed class SongRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreatingASongCreatesAnEmptyMutableVersionOneAndMakesItCurrent()
    {
        var songId = Guid.CreateVersion7();
        var versionId = Guid.CreateVersion7();

        var (song, version) = Song.Create(songId, versionId, 7, "  Running in a Pack ", " A pack song \r\n", DefaultWorkflowStates.Idea, Now);

        Assert.Equal(songId, song.Id);
        Assert.Equal("n8-7", song.Shortcode);
        Assert.Equal("Running in a Pack", song.Title);
        Assert.Equal("A pack song", song.Concept);
        Assert.Equal(DefaultWorkflowStates.Idea.Id, song.StateId);
        Assert.Equal(versionId, song.CurrentVersionId);
        Assert.Equal(1, song.Revision);
        Assert.Equal(Now, song.CreatedUtc);
        Assert.Equal(Now, song.UpdatedUtc);

        Assert.Equal(versionId, version.Id);
        Assert.Equal(songId, version.SongId);
        Assert.Equal("1", version.Number);
        Assert.Null(version.Name);
        Assert.Null(version.Notes);
        Assert.Equal(VersionVisibility.Active, version.Visibility);
        Assert.Equal(string.Empty, version.Lyrics);
        Assert.Equal(string.Empty, version.Styles);
        Assert.Equal(1, version.Revision);
        Assert.Equal("n8-7-v1", Shortcodes.ForVersion(song.ShortcodeNumber, version.Number));
    }

    [Fact]
    public void TheCreationRuleRefusesWhatValidationRefuses()
    {
        var state = DefaultWorkflowStates.Idea;

        Assert.Throws<ArgumentException>(() => Song.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "   ", null, state, Now));
        Assert.Throws<ArgumentException>(() => Song.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "Title", "\u0007", state, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => Song.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "Title", null, state, Now));
    }

    [Theory]
    [InlineData(null, "Enter a title.")]
    [InlineData("", "Enter a title.")]
    [InlineData(" \t\n ", "Enter a title.")]
    [InlineData("Two\nlines", "A title is one line, with no control characters.")]
    [InlineData("Bell\u0007", "A title is one line, with no control characters.")]
    public void ABlankMultiLineOrBrokenTitleIsRefused(string? title, string message)
    {
        Assert.Equal([message], SongRules.TitleErrors(title));
    }

    [Fact]
    public void ATitleWithABrokenSurrogatePairIsRefused()
    {
        // Built here: an attribute argument cannot carry a lone surrogate intact.
        Assert.Equal(["A title cannot contain unpaired surrogate characters."], SongRules.TitleErrors("Broken " + '\ud800' + " pair"));
        Assert.Equal(["A title cannot contain unpaired surrogate characters."], SongRules.TitleErrors("Ends " + '\ud800'));
        Assert.Empty(SongRules.TitleErrors("Paired 🎵 note"));
    }

    [Fact]
    public void ATitleIsOneTo300CodeUnitsAfterTrimming()
    {
        Assert.Empty(SongRules.TitleErrors("x"));
        Assert.Empty(SongRules.TitleErrors("  " + new string('x', 300) + "  "));
        Assert.Equal(["Use at most 300 characters."], SongRules.TitleErrors(new string('x', 301)));

        // Code units, not characters: 150 emoji are 300 units, 151 are too many.
        Assert.Empty(SongRules.TitleErrors(string.Concat(Enumerable.Repeat("🎵", 150))));
        Assert.NotEmpty(SongRules.TitleErrors(string.Concat(Enumerable.Repeat("🎵", 151))));
    }

    [Fact]
    public void AConceptIsOptionalTrimmedAndWhitespaceOnlyIsNone()
    {
        Assert.Empty(SongRules.ConceptErrors(null));
        Assert.Empty(SongRules.ConceptErrors("   \n\t "));
        Assert.Null(SongRules.NormaliseConcept(null));
        Assert.Null(SongRules.NormaliseConcept("   \r\n "));
        Assert.Equal("Fast-paced\nabout a pack", SongRules.NormaliseConcept("  Fast-paced\r\nabout a pack \n"));
        Assert.Equal("a\nb", SongRules.NormaliseConcept("a\rb"));
    }

    [Fact]
    public void AConceptIsUpTo2000CodeUnitsWithLineBreaksButNoOtherControlCharacters()
    {
        Assert.Empty(SongRules.ConceptErrors(new string('x', 2000)));
        Assert.Empty(SongRules.ConceptErrors(" " + new string('x', 2000) + " "));
        Assert.Equal(["Use at most 2,000 characters."], SongRules.ConceptErrors(new string('x', 2001)));

        // CRLF counts as one code unit once it is a line feed: 2,999 units typed, 2,000 stored.
        Assert.Empty(SongRules.ConceptErrors(string.Concat(Enumerable.Repeat("x\r\n", 999)) + "xx"));
        Assert.NotEmpty(SongRules.ConceptErrors(string.Concat(Enumerable.Repeat("x\r\n", 999)) + "xxx"));

        Assert.Empty(SongRules.ConceptErrors("line one\nline two"));
        Assert.Equal(["A concept can contain line breaks but no other control characters."], SongRules.ConceptErrors("tab\there"));
        Assert.Equal(["A concept cannot contain unpaired surrogate characters."], SongRules.ConceptErrors("x\udc00"));
    }

    [Theory]
    [InlineData("n8-1", 1L)]
    [InlineData("N8-42", 42L)]
    [InlineData("n8-9223372036854775807", long.MaxValue)]
    public void ACompleteSongShortcodeIsReadInAnyCase(string text, long number)
    {
        Assert.True(Shortcodes.TryParseSong(text, out var parsed));
        Assert.Equal(number, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("n8-")]
    [InlineData("n8-0")]
    [InlineData("n8-01")]
    [InlineData("n8--1")]
    [InlineData("n8-+1")]
    [InlineData(" n8-1")]
    [InlineData("n8-1 ")]
    [InlineData("n8-1-v1")]
    [InlineData("n9-1")]
    [InlineData("n8-9223372036854775808")]
    public void AnythingElseIsNotASongShortcode(string? text)
    {
        Assert.False(Shortcodes.TryParseSong(text, out _));
    }

    [Fact]
    public void ShortcodesAreLowerCaseWithoutPadding()
    {
        Assert.Equal("n8-1", Shortcodes.ForSong(1));
        Assert.Equal("n8-12345", Shortcodes.ForSong(12345));
        Assert.Equal("n8-12345-v1.1", Shortcodes.ForVersion(12345, "1.1"));
    }

    [Fact]
    public void AVersionNumberSortKeyOrdersTheTree()
    {
        Assert.Equal("0000000001", VersionNumbers.SortKey("1"));
        Assert.Equal("0000000002.0000000010", VersionNumbers.SortKey("2.10"));

        string[] numbers = ["2.10", "1", "2", "10", "2.9", "1.1"];
        Assert.Equal(["1", "1.1", "2", "2.9", "2.10", "10"], numbers.OrderBy(VersionNumbers.SortKey, StringComparer.Ordinal));

        Assert.Throws<ArgumentException>(static () => VersionNumbers.SortKey("1..2"));
        Assert.Throws<ArgumentException>(static () => VersionNumbers.SortKey("0"));
        Assert.Throws<ArgumentException>(static () => VersionNumbers.SortKey("v1"));
    }

    [Fact]
    public void TheSevenDefaultStatesShipInOrderAllVisible()
    {
        Assert.Equal(
            ["Idea", "Writing", "Generating", "Refining", "Final", "Released", "Archived"],
            DefaultWorkflowStates.All.Select(static state => state.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], DefaultWorkflowStates.All.Select(static state => state.Order));
        Assert.All(DefaultWorkflowStates.All, static state => Assert.False(state.Hidden));
        Assert.All(DefaultWorkflowStates.All, static state => Assert.True(StateColours.IsKnown(state.Colour)));
        Assert.All(DefaultWorkflowStates.All, static state => Assert.Equal(7, state.Id.Version));
        Assert.Equal(7, DefaultWorkflowStates.All.Select(static state => state.Id).Distinct().Count());
    }

    [Fact]
    public void ANewSongStartsInTheFirstVisibleState()
    {
        Assert.Equal(DefaultWorkflowStates.Idea, WorkflowState.Initial(DefaultWorkflowStates.All));

        // Order counts, not list position; hidden states are passed over.
        var reordered = DefaultWorkflowStates.All
            .Select(static state => state with { Hidden = state == DefaultWorkflowStates.Idea, Order = 8 - state.Order })
            .ToList();
        Assert.Equal("Archived", WorkflowState.Initial(reordered)?.Name);
        Assert.Equal("Released", WorkflowState.Initial(reordered.Select(static state => state with { Hidden = state.Name is "Archived" or "Idea" }))?.Name);

        Assert.Null(WorkflowState.Initial(DefaultWorkflowStates.All.Select(static state => state with { Hidden = true })));
    }

    [Fact]
    public void ThePaletteHasTwelveNamedColoursEachReadableInBothSchemes()
    {
        Assert.Equal(12, StateColours.All.Count);
        Assert.Equal(12, StateColours.All.Select(static colour => colour.Name).Distinct(StringComparer.Ordinal).Count());

        // Text contrast (WCAG AA, 4.5:1) on the light scheme's white and gray-0 surfaces, and on the
        // dark scheme's body and raised surfaces.
        foreach (var colour in StateColours.All)
        {
            foreach (var surface in new[] { "#ffffff", "#f8f9fa" })
            {
                Assert.True(Contrast(colour.Light, surface) >= 4.5, $"{colour.Name} light on {surface}: {Contrast(colour.Light, surface):0.00}");
            }

            foreach (var surface in new[] { "#242424", "#2e2e2e" })
            {
                Assert.True(Contrast(colour.Dark, surface) >= 4.5, $"{colour.Name} dark on {surface}: {Contrast(colour.Dark, surface):0.00}");
            }
        }

        Assert.False(StateColours.IsKnown("Gray"));
        Assert.False(StateColours.IsKnown("purple"));
        Assert.False(StateColours.IsKnown(null));
    }

    private static double Contrast(string first, string second)
    {
        var (lighter, darker) = (Luminance(first), Luminance(second)) is var (a, b) && a > b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var value = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(1)) + (0.7152 * Channel(3)) + (0.0722 * Channel(5));
    }
}
