using n8Tracks.Api.Tests.Inventory;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>The rules of deleting one Version (#101), apart from storage.</summary>
public sealed class VersionDeletionRulesTests
{
    private static VersionNumber N(string number) => VersionNumber.Parse(number);

    private static List<LiveVersionNumber> Live(params string[] numbers) =>
        [.. numbers.Select(static number => new LiveVersionNumber(N(number.TrimEnd('*')), number.EndsWith('*')))];

    [Theory]
    [InlineData("1.1", "1", true)]
    [InlineData("1.1.1", "1", true)]
    [InlineData("1.1", "1.1", false)]
    [InlineData("1", "1.1", false)]
    [InlineData("10.1", "1", false)]
    [InlineData("2", "1", false)]
    public void ADescendantIsBelowItsAncestorInTheTree(string descendant, string ancestor, bool expected) =>
        Assert.Equal(expected, VersionDeletionRules.IsDescendant(N(descendant), N(ancestor)));

    [Fact]
    public void RemainingDescendantsCountsEveryLevelBelowTheDeletedVersionOnly()
    {
        Assert.Equal(3, VersionDeletionRules.RemainingDescendants(N("1"), [N("1"), N("1.1"), N("1.1.1"), N("1.2"), N("2"), N("10.1")]));
        Assert.Equal(0, VersionDeletionRules.RemainingDescendants(N("2"), [N("1"), N("1.1"), N("2")]));
    }

    [Theory]
    // Each case: the deleted (current) Version, the Versions left ("*" = archived), and the new current one.
    [InlineData("1.1.1", "1 1.1 2", "1.1")]
    [InlineData("1.1.1", "1 1.1* 2", "1")]
    [InlineData("1.1.1", "1* 1.1* 2 3", "2")]
    [InlineData("3", "1* 2 4", "2")]
    [InlineData("3", "1* 2* 4*", "1")]
    [InlineData("2.1", "1.5* 2* 10", "10")]
    [InlineData("1", "1.1 1.2", "1.1")]
    public void TheNewCurrentVersionIsTheNearestUnarchivedAncestorElseTheLowestUnarchivedElseTheLowest(string deleted, string left, string expected) =>
        Assert.Equal(N(expected), VersionDeletionRules.NewCurrent(N(deleted), Live(left.Split(' '))));

    [Fact]
    public void WithNoVersionLeftThereIsNoNewCurrentOne() =>
        Assert.Null(VersionDeletionRules.NewCurrent(N("1"), Live("1")));

    [Fact]
    public void APlaceholderIsAUsedNumberWithoutALiveVersionThatHasALiveDescendant()
    {
        var used = new[] { "1", "1.1", "1.1.1", "1.2", "2", "3", "3.1", "4" }.Select(N);

        // 1.1 has a live descendant; 3 does not (3.1 was deleted too); 4 has none.
        Assert.Equal([N("1.1")], VersionDeletionRules.Placeholders(used, [N("1"), N("1.1.1"), N("2")]));

        // A chain of deleted ancestors each gets a placeholder, in tree order.
        Assert.Equal([N("1"), N("1.1")], VersionDeletionRules.Placeholders(used, [N("1.1.1"), N("2")]));

        // Complement: with every Version live there is none.
        Assert.Empty(VersionDeletionRules.Placeholders(used, [.. used]));
    }

    [Fact]
    public void TheBlankVersionIsEmptyActiveMutableAndAtRevisionOne()
    {
        var now = new DateTimeOffset(2026, 10, 6, 1, 2, 3, TimeSpan.Zero);
        var inputs = InputValues.Defaults();
        var blank = VersionDeletionRules.Blank(Guid.CreateVersion7(now), Guid.CreateVersion7(now), N("6"), inputs, now);

        Assert.Equal("6", blank.Number);
        Assert.Null(blank.Name);
        Assert.Null(blank.Notes);
        Assert.Equal(VersionVisibility.Active, blank.Visibility);
        Assert.Equal(string.Empty, blank.Lyrics);
        Assert.Equal(string.Empty, blank.Styles);
        Assert.Equal(inputs, blank.Inputs);
        Assert.Equal(1, blank.Revision);
        Assert.False(blank.IsFrozen);
        Assert.Equal(now, blank.CreatedUtc);
    }
}
