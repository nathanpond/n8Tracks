using System.Text.Json;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The TS-001 grouping rule (#138): same workspace, <c>created_at</c> within one second of the group's
/// earliest clip, distinct <c>batch_index</c> values starting at 0; and the pure rules of choices.
/// </summary>
public sealed class ClipGroupingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 17, 52, 8, 520, TimeSpan.Zero);

    [Fact]
    public void TwoClipsOneMillisecondApartWithBatchIndexesZeroAndOneAreOneGroup()
    {
        var groups = ClipGrouping.Group([Clip("b", At.AddMilliseconds(1), 1), Clip("a", At, 0)]);

        Assert.Equal([["a", "b"]], Ids(groups));
    }

    [Fact]
    public void TwoRequestsSeventeenSecondsApartAreTwoGroups()
    {
        var groups = ClipGrouping.Group(
        [
            Clip("a0", At, 0), Clip("a1", At.AddMilliseconds(1), 1),
            Clip("b0", At.AddSeconds(17.6), 0), Clip("b1", At.AddSeconds(17.6), 1),
        ]);

        Assert.Equal([["a0", "a1"], ["b0", "b1"]], Ids(groups));
    }

    [Fact]
    public void EqualTimestampsWithEqualBatchIndexesDoNotGroup()
    {
        var groups = ClipGrouping.Group([Clip("a", At, 0), Clip("b", At, 0)]);

        Assert.Equal([["a"], ["b"]], Ids(groups));
    }

    [Fact]
    public void ClipsInDifferentWorkspacesDoNotGroup()
    {
        var groups = ClipGrouping.Group([Clip("a", At, 0), Clip("b", At, 1, workspace: "other")]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, static group => Assert.Single(group));
    }

    /// <summary>Complement: timestamp proximity alone never groups, without a <c>batch_index</c>, or without index 0.</summary>
    [Fact]
    public void TimestampProximityAloneNeverGroups()
    {
        Assert.Equal([["a"], ["b"]], Ids(ClipGrouping.Group([Clip("a", At, null), Clip("b", At.AddMilliseconds(1), null)])));
        Assert.Equal([["a"], ["b"]], Ids(ClipGrouping.Group([Clip("a", At, 1), Clip("b", At.AddMilliseconds(1), 2)])));
        Assert.Equal([["a"], ["b"]], Ids(ClipGrouping.Group([Clip("a", At, 0), Clip("b", At.AddMilliseconds(1), 1, workspace: null)])));
        Assert.Equal([["a"], ["b"]], Ids(ClipGrouping.Group([Clip("a", At, 0), new GroupableClip("b", "studio", null, 1)])));
    }

    /// <summary>A group is anchored on its earliest clip: a clip more than a second after it starts another group.</summary>
    [Fact]
    public void AGroupIsAnchoredOnItsEarliestClip()
    {
        var groups = ClipGrouping.Group(
        [
            Clip("a", At, 0),
            Clip("b", At.AddMilliseconds(600), 1),
            Clip("c", At.AddMilliseconds(1_200), 0),
            Clip("d", At.AddMilliseconds(1_300), 1),
            Clip("e", At.AddMilliseconds(1_400), 1),
        ]);

        Assert.Equal([["a", "b"], ["c", "d"], ["e"]], Ids(groups));
    }

    [Fact]
    public void TemporaryKeysAreNewAndAWholeNumberFromOne()
    {
        Assert.Equal("new:3", ImportChoiceRules.Key(3));
        Assert.True(ImportChoiceRules.IsKey("new:12"));
        Assert.All(["new:", "new:0", "new:01", "new:-1", "new:x", "New:1", "n8-1", null], static text => Assert.False(ImportChoiceRules.IsKey(text)));
    }

    [Fact]
    public void ANewSongIsProposedWithTheSunoTitleOrUntitled()
    {
        Assert.Equal("Night drive", ImportChoiceRules.ProposedTitle("  Night drive ", 300));
        Assert.Equal("Untitled", ImportChoiceRules.ProposedTitle(" \t", 300));
        Assert.Equal("Untitled", ImportChoiceRules.ProposedTitle(null, 300));
        Assert.Equal("Two lines", ImportChoiceRules.ProposedTitle("Two\nlines", 300));
        Assert.Equal("abc", ImportChoiceRules.ProposedTitle("abcdef", 3));
        Assert.Equal("ab", ImportChoiceRules.ProposedTitle("ab\U0001F3B5", 3));
    }

    [Fact]
    public void AStoredChoiceReadsBackAsWritten()
    {
        var song = Guid.CreateVersion7();
        var parent = Guid.CreateVersion7();
        ImportChoice[] choices =
        [
            ImportChoice.Skip,
            ImportChoice.Ignore,
            ImportChoice.To(new ImportTarget.NewSong("new:1", "Title", "studio")),
            ImportChoice.To(new ImportTarget.NewSong("new:1", "Title", null)),
            ImportChoice.To(new ImportTarget.NewVersion("new:2", song, null, parent, "1.1")),
            ImportChoice.To(new ImportTarget.NewVersion("new:3", null, "new:1", null, "2")),
            ImportChoice.To(new ImportTarget.ExistingVersion(parent)),
        ];

        Assert.All(choices, static choice => Assert.Equal(choice, ImportChoiceJson.ReadStored(ImportChoiceJson.Write(choice))));
    }

    [Fact]
    public void AChangeOfChoicesNamesItsMistakesByField()
    {
        var reading = Assert.IsType<ChoiceChangeReading.Invalid>(ImportChoiceJson.ReadChange(JsonDocument.Parse(
            """{"sunoIds":[],"choice":{"action":"import","target":{"kind":"newVersion","key":"new:0","song":"n8-1","extra":1}},"other":true}""").RootElement));

        Assert.Equal(
            ["choice.target.extra", "choice.target.key", "choice.target.number", "other", "sunoIds"],
            reading.Errors.Keys.Order(StringComparer.Ordinal));

        var read = Assert.IsType<ChoiceChangeReading.Read>(ImportChoiceJson.ReadChange(JsonDocument.Parse(
            """{"sunoIds":["a","b","a"],"choice":{"action":"skip"}}""").RootElement));
        Assert.Equal(["a", "b"], read.SunoIds);
        Assert.Equal(ImportAction.Skip, read.Choice.Action);
        Assert.IsType<ChoiceChangeReading.Invalid>(ImportChoiceJson.ReadChange(JsonDocument.Parse(
            """{"sunoIds":["a"],"choice":{"action":"skip","target":{"kind":"version","version":"x"}}}""").RootElement));
    }

    private static GroupableClip Clip(string id, DateTimeOffset created, int? batchIndex, string? workspace = "studio") =>
        new(id, workspace, created, batchIndex);

    private static List<List<string>> Ids(IReadOnlyList<IReadOnlyList<GroupableClip>> groups) =>
        [.. groups.Select(static group => group.Select(static clip => clip.SunoId).ToList())];
}
