using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// The tree-numbering rules: which numbers a new Version may take from any source, which is
/// proposed, and how numbers are read and ordered.
/// </summary>
public sealed class VersionNumberingTests
{
    /// <summary>63 characters: thirty-two parts of 1. A child of it would be 65.</summary>
    private static readonly string SixtyThree = string.Join('.', Enumerable.Repeat("1", 32));

    public static TheoryData<string, string[], string[]> OptionCases => new()
    {
        // The PRD's examples.
        { "1", ["1"], ["2 sibling proposed", "1.1 child"] },
        { "1", ["1", "2"], ["1.1 child proposed", "3 sibling"] },
        { "1.3.1", ["1", "1.3", "1.3.1"], ["1.3.2 sibling proposed", "1.3.1.1 child"] },
        { "1.3.1", ["1", "1.3", "1.3.1", "1.3.2"], ["1.3.1.1 child proposed", "1.3.3 sibling"] },
        { "2", ["1", "2", "2.1", "2.2", "3"], ["2.3 child proposed", "4 sibling"] },

        // More: gaps, runs of used siblings and children, and numbers used elsewhere in the tree.
        { "1", ["1", "3"], ["2 sibling proposed", "1.1 child"] },
        { "1", ["1", "2", "3", "4", "1.1"], ["1.2 child proposed", "5 sibling"] },
        { "1.1", ["1", "1.1", "1.2", "1.1.1"], ["1.1.2 child proposed", "1.3 sibling"] },
        { "1", ["1", "1.1", "1.3"], ["2 sibling proposed", "1.2 child"] },
        { "2.1", ["1", "2", "2.1", "3.1", "2.1.1.1"], ["2.2 sibling proposed", "2.1.1 child"] },
        { "9", ["1", "9", "10"], ["9.1 child proposed", "11 sibling"] },

        // An archived or deleted Version's number is in the used set like any other.
        { "1", ["1", "2", "1.1"], ["1.2 child proposed", "3 sibling"] },
    };

    [Theory]
    [MemberData(nameof(OptionCases))]
    public void TheOptionsAreTheNextSiblingAndTheFirstChildWithTheProposalFirst(string source, string[] used, string[] expected)
    {
        var options = VersionNumbering.Options(VersionNumber.Parse(source), used.Select(VersionNumber.Parse));

        Assert.Equal(expected, options.Select(Describe));
        Assert.Single(options, static option => option.Proposed);
    }

    [Fact]
    public void ANumberEverUsedIsNeverOffered()
    {
        string[] used = ["1", "2", "3", "1.1", "1.2", "2.1"];
        var taken = used.Select(VersionNumber.Parse).ToHashSet();

        foreach (var source in used)
        {
            var options = VersionNumbering.Options(VersionNumber.Parse(source), taken);

            Assert.Equal(2, options.Count);
            Assert.All(options, option => Assert.DoesNotContain(option.Number, taken));
            Assert.Equal(options.Count, options.Select(static option => option.Number).Distinct().Count());
        }
    }

    [Fact]
    public void AChildOverSixtyFourCharactersIsLeftOutAndTheSiblingProposed()
    {
        var source = VersionNumber.Parse(SixtyThree);

        var options = VersionNumbering.Options(source, [source]);

        var only = Assert.Single(options);
        Assert.Equal(VersionNumberKind.Sibling, only.Kind);
        Assert.True(only.Proposed);
        Assert.Equal(SixtyThree[..^1] + "2", only.Number.ToString());

        // Still the proposal when it is not the number straight after the source's.
        var withNextUsed = VersionNumbering.Options(source, [source, source.WithLast(2)!]);
        Assert.Equal([SixtyThree[..^1] + "3 sibling proposed"], withNextUsed.Select(Describe));
    }

    [Fact]
    public void ASiblingPastTheLargestPartIsLeftOutAndTheChildProposed()
    {
        var source = VersionNumber.Parse("2147483647");

        var options = VersionNumbering.Options(source, [source]);

        Assert.Equal(["2147483647.1 child proposed"], options.Select(Describe));
    }

    [Fact]
    public void WhenBothOptionsAreTooLongThereAreNone()
    {
        // 64 characters ending in 99: the sibling (…100) and the child are both 65 or more.
        var source = VersionNumber.Parse(string.Join('.', Enumerable.Repeat("1", 31)) + ".99");
        Assert.Equal(64, source.ToString().Length);

        Assert.Empty(VersionNumbering.Options(source, [source]));
    }

    [Theory]
    [InlineData(new string[0], "1")]
    [InlineData(new[] { "1" }, "2")]
    [InlineData(new[] { "1", "1.1", "2", "5", "5.3", "5.3.1" }, "6")]
    [InlineData(new[] { "3.1" }, "4")]
    public void TheReplacementForASongsLastVersionTakesTheNextNeverUsedTopLevelNumber(string[] used, string expected)
    {
        Assert.Equal(expected, VersionNumbering.NextTopLevel(used.Select(VersionNumber.Parse))?.ToString());
    }

    [Fact]
    public void ThereIsNoReplacementNumberOnceTheTopLevelIsExhausted()
    {
        Assert.Null(VersionNumbering.NextTopLevel([VersionNumber.Parse("2147483647")]));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2.1.4")]
    [InlineData("10.200.3000")]
    [InlineData("2147483647")]
    public void AValidNumberReadsBackAsWritten(string text)
    {
        Assert.True(VersionNumber.TryParse(text, out var number));
        Assert.Equal(text, number.ToString());
        Assert.Equal(text.Split('.').Length, number.Depth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1.0")]
    [InlineData("1..2")]
    [InlineData("01")]
    [InlineData("1.02")]
    [InlineData("1.a")]
    [InlineData(".1")]
    [InlineData("1.")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("v1")]
    [InlineData("V1.1")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1,1")]
    [InlineData("１")]
    [InlineData("2147483648")]
    [InlineData("1.99999999999")]
    public void AnythingElseIsNotAVersionNumber(string? text)
    {
        Assert.False(VersionNumber.TryParse(text, out _));
        if (text is not null)
        {
            Assert.Throws<ArgumentException>(() => VersionNumber.Parse(text));
        }
    }

    [Fact]
    public void ANumberIsAtMostSixtyFourCharacters()
    {
        var sixtyFour = SixtyThree[..^1] + "11";
        Assert.Equal(64, sixtyFour.Length);
        Assert.True(VersionNumber.TryParse(sixtyFour, out _));
        Assert.False(VersionNumber.TryParse(sixtyFour + "1", out _));
        Assert.False(VersionNumber.TryParse(SixtyThree + ".1", out _));
    }

    [Fact]
    public void NumbersSortAsTheTreeDoesWithOnePointTwoBeforeOnePointTen()
    {
        string[] numbers = ["1.10", "2", "1", "1.2", "10", "1.2.1", "1.1", "2.9", "2.10"];

        var sorted = numbers.Select(VersionNumber.Parse).Order().Select(static number => number.ToString());

        Assert.Equal(["1", "1.1", "1.2", "1.2.1", "1.10", "2", "2.9", "2.10", "10"], sorted);
        Assert.True(VersionNumber.Parse("1.2") < VersionNumber.Parse("1.10"));

        // The stored sort key orders the same way as text.
        Assert.Equal(sorted, numbers.OrderBy(VersionNumbers.SortKey, StringComparer.Ordinal));
        Assert.Equal("0000000001.0000000010", VersionNumber.Parse("1.10").SortKey);
    }

    [Fact]
    public void ANumberKnowsItsParentAndItsNeighbours()
    {
        var number = VersionNumber.Parse("1.3.2");

        Assert.Equal(VersionNumber.Parse("1.3"), number.Parent);
        Assert.Null(VersionNumber.Parse("1").Parent);
        Assert.Equal("1.3.7", number.WithLast(7)?.ToString());
        Assert.Equal("1.3.2.1", number.Child(1)?.ToString());
        Assert.Equal(2, number.Last);
        Assert.Equal(VersionNumber.Parse("1.3.2"), number);
        Assert.Equal(VersionNumber.Parse("1.3.2").GetHashCode(), number.GetHashCode());
        Assert.NotEqual(VersionNumber.Parse("1.3"), number);
        Assert.Equal(VersionNumbers.Initial, VersionNumber.Initial.ToString());
    }

    private static string Describe(VersionNumberOption option) =>
        $"{option.Number} {(option.Kind == VersionNumberKind.Sibling ? "sibling" : "child")}{(option.Proposed ? " proposed" : string.Empty)}";
}
