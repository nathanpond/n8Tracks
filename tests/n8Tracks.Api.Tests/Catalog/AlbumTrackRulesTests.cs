using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>The domain rules for numbering an Album's tracks.</summary>
public sealed class AlbumTrackRulesTests
{
    private static readonly Guid A = new("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid B = new("00000000-0000-7000-8000-00000000000b");
    private static readonly Guid C = new("00000000-0000-7000-8000-00000000000c");
    private static readonly Guid D = new("00000000-0000-7000-8000-00000000000d");

    [Fact]
    public void DiscAndTrackNumbersAreWholeNumbersFromOneToNineHundredNinetyNine()
    {
        Assert.True(AlbumTrackRules.IsNumber(1));
        Assert.True(AlbumTrackRules.IsNumber(999));
        Assert.False(AlbumTrackRules.IsNumber(0));
        Assert.False(AlbumTrackRules.IsNumber(1000));
        Assert.False(AlbumTrackRules.IsNumber(-1));
    }

    [Fact]
    public void ANewSongGoesAtTheEndOfTheLastDiscWithTheNextTrackNumber()
    {
        Assert.Equal(new AlbumTrackPlace(A, 1, 1), AlbumTrackRules.NextPlace([], A));
        Assert.Equal(new AlbumTrackPlace(D, 2, 8), AlbumTrackRules.NextPlace([new(A, 1, 9), new(B, 2, 3), new(C, 2, 7)], D));

        // The last disc has track 999: adding is refused, even when earlier discs have room.
        Assert.Null(AlbumTrackRules.NextPlace([new(A, 1, 1), new(B, 2, 999)], C));
    }

    [Fact]
    public void DiscGapsCloseUpKeepingTrackNumbersAndTheOrderIsDiscThenTrack()
    {
        Assert.Equal(
            [new(C, 1, 2), new(A, 2, 1), new(B, 2, 5)],
            AlbumTrackRules.CloseDiscGaps([new(B, 7, 5), new(A, 7, 1), new(C, 3, 2)]));
    }

    [Fact]
    public void RenumberSetsEachDiscToOneTwoThreeInItsOrder()
    {
        Assert.Equal(
            [new(B, 1, 1), new(A, 1, 2), new(C, 2, 1)],
            AlbumTrackRules.Renumber([new(A, 1, 9), new(C, 2, 4), new(B, 1, 3)]));
    }

    [Fact]
    public void ATrackLeavingRenumbersTheRestOfItsDiscAndAnEmptiedDiscDisappears()
    {
        Assert.Equal(
            [new(A, 1, 1), new(C, 1, 2), new(D, 2, 4)],
            AlbumTrackRules.Without([new(A, 1, 1), new(B, 1, 2), new(C, 1, 5), new(D, 2, 4)], B));
        Assert.Equal(
            [new(A, 1, 1), new(C, 2, 1)],
            AlbumTrackRules.Without([new(A, 1, 1), new(B, 2, 1), new(C, 3, 1)], B));

        // A Song that is not on the Album changes nothing.
        Assert.Equal([new(A, 1, 1)], AlbumTrackRules.Without([new(A, 1, 1)], B));
    }

    [Fact]
    public void AClashNamesTheTrackThatHeldTheNumberBefore()
    {
        IReadOnlyList<AlbumTrackPlace> before = [new(A, 1, 1), new(B, 1, 2), new(C, 2, 1)];

        // B is typed as track 1: A held it.
        var clash = AlbumTrackRules.FindClash([new(A, 1, 1), new(B, 1, 1), new(C, 2, 1)], before);
        Assert.Equal((new AlbumTrackPlace(A, 1, 1), new AlbumTrackPlace(B, 1, 1)), clash);

        // Neither held it: the earlier one in the list is the holder.
        clash = AlbumTrackRules.FindClash([new(B, 1, 3), new(A, 1, 3), new(C, 2, 1)], before);
        Assert.Equal((new AlbumTrackPlace(B, 1, 3), new AlbumTrackPlace(A, 1, 3)), clash);

        // The same number on different discs is no clash.
        Assert.Null(AlbumTrackRules.FindClash([new(A, 1, 1), new(B, 1, 2), new(C, 2, 1), new(D, 2, 2)], before));
    }
}
