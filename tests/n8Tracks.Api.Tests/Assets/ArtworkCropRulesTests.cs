using n8Tracks.Application.Assets;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Api.Tests.Assets;

/// <summary>The square crop rules (#99): what may be set on an image, the centred default, a kept crop, and the square thumbnails made.</summary>
public sealed class ArtworkCropRulesTests
{
    public static TheoryData<int, int, int, int, int, bool> Crops => new()
    {
        // Inside a 1,200 × 600 image, at least 64 pixels a side.
        { 1200, 600, 0, 0, 600, true },
        { 1200, 600, 600, 0, 600, true },
        { 1200, 600, 1136, 536, 64, true },
        { 1200, 600, 300, 100, 400, true },

        // Complement: outside the image on any edge, too large, or under the minimum.
        { 1200, 600, -1, 0, 600, false },
        { 1200, 600, 0, -1, 600, false },
        { 1200, 600, 601, 0, 600, false },
        { 1200, 600, 0, 1, 600, false },
        { 1200, 600, 0, 0, 601, false },
        { 1200, 600, 0, 0, 63, false },
        { 1200, 600, 0, 0, 0, false },
        { 1200, 600, int.MaxValue, 0, int.MaxValue, false },

        // An original under 64 pixels on its shorter side: only the full shorter side, anywhere along the longer.
        { 50, 40, 0, 0, 40, true },
        { 50, 40, 10, 0, 40, true },
        { 50, 40, 0, 0, 30, false },
        { 50, 40, 11, 0, 40, false },
        { 30, 30, 0, 0, 30, true },
        { 200, 50, 0, 0, 64, false },
    };

    [Theory]
    [MemberData(nameof(Crops))]
    public void ACropMustLieInsideTheImageAndBeAtLeastTheMinimum(int width, int height, int x, int y, int size, bool fits)
    {
        var crop = new ArtworkCrop(x, y, size);

        Assert.Equal(fits, ArtworkCropRules.Fits(crop, width, height));
        Assert.Equal(fits, ArtworkCropRules.Errors(crop, width, height).Length == 0);
    }

    [Fact]
    public void TheErrorsSayWhatIsWrong()
    {
        Assert.Contains("at least 64 pixels", Assert.Single(ArtworkCropRules.Errors(new(0, 0, 63), 1200, 600)), StringComparison.Ordinal);
        Assert.Contains("inside the image, which is 1,200 × 600 pixels", Assert.Single(ArtworkCropRules.Errors(new(700, 0, 600), 1200, 600)), StringComparison.Ordinal);
        Assert.Contains("all of it (40 pixels)", Assert.Single(ArtworkCropRules.Errors(new(0, 0, 30), 50, 40)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1200, 600, 300, 0, 600)]
    [InlineData(600, 1200, 0, 300, 600)]
    [InlineData(500, 500, 0, 0, 500)]
    [InlineData(301, 100, 100, 0, 100)]
    [InlineData(50, 40, 5, 0, 40)]
    public void WithNoCropTheCentredSquareOfTheShorterSideIsShown(int width, int height, int x, int y, int size)
    {
        var centred = ArtworkCropRules.Centred(width, height);

        Assert.Equal(new ArtworkCrop(x, y, size), centred);
        Assert.True(ArtworkCropRules.Fits(centred, width, height));
    }

    [Fact]
    public void AKeptCropStaysOnlyWhenItFitsTheNewImage()
    {
        var crop = new ArtworkCrop(0, 0, 500);

        Assert.Equal(crop, ArtworkCropRules.Kept(crop, 800, 600));
        Assert.Null(ArtworkCropRules.Kept(crop, 400, 400));
        Assert.Null(ArtworkCropRules.Kept(new ArtworkCrop(0, 0, 64), 50, 40));
        Assert.Null(ArtworkCropRules.Kept(null, 800, 600));
    }

    [Fact]
    public void TheKeyIsAShortHashThatChangesWithTheCrop()
    {
        var key = ArtworkCropRules.Key(new ArtworkCrop(0, 0, 600));

        Assert.True(ArtworkCropRules.IsKey(key));
        Assert.Equal(key, ArtworkCropRules.Key(new ArtworkCrop(0, 0, 600)));
        Assert.NotEqual(key, ArtworkCropRules.Key(new ArtworkCrop(1, 0, 600)));
        Assert.NotEqual(key, ArtworkCropRules.Key(new ArtworkCrop(0, 1, 600)));
        Assert.NotEqual(key, ArtworkCropRules.Key(new ArtworkCrop(0, 0, 599)));
        Assert.False(ArtworkCropRules.IsKey(key.ToUpperInvariant()));
        Assert.False(ArtworkCropRules.IsKey(key[..^1]));
        Assert.False(ArtworkCropRules.IsKey("../../secret"));
        Assert.False(ArtworkCropRules.IsKey(null));
    }

    [Fact]
    public void SquareThumbnailsAreNeverEnlarged()
    {
        Assert.Equal([96, 320, 1024], ArtworkCropRules.SizesFor(new ArtworkCrop(0, 0, 2000)));
        Assert.Equal([96, 320], ArtworkCropRules.SizesFor(new ArtworkCrop(0, 0, 600)));
        Assert.Equal([64], ArtworkCropRules.SizesFor(new ArtworkCrop(0, 0, 64)));
        Assert.Equal([40], ArtworkCropRules.SizesFor(new ArtworkCrop(0, 0, 40)));

        Assert.Equal(320, ArtworkCropRules.ServedSize(new ArtworkCrop(0, 0, 600), 1024));
        Assert.Equal(96, ArtworkCropRules.ServedSize(new ArtworkCrop(0, 0, 600), 96));
        Assert.Equal(64, ArtworkCropRules.ServedSize(new ArtworkCrop(0, 0, 64), 320));
    }

    [Fact]
    public void ACropsThumbnailsLiveInTheAssetsFolderUnderTheirKey()
    {
        var hash = new string('a', 64);
        var path = ArtworkPaths.CropThumbnail(hash, "0123456789ab", 320);

        Assert.Equal($"artwork/aa/{hash}/crop-0123456789ab-320.webp", path);
        Assert.Equal(hash, ArtworkPaths.ContentHashOf(path));
        Assert.True(ArtworkPaths.IsCropFile(path));
        Assert.False(ArtworkPaths.IsCropFile(ArtworkPaths.Thumbnail(hash, 320)));
        Assert.False(ArtworkPaths.IsCropFile("elsewhere/crop-0123456789ab-320.webp"));
        Assert.Throws<ArgumentException>(() => ArtworkPaths.CropThumbnail(hash, "../x", 320));
    }
}
