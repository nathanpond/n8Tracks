using n8Tracks.Application.Assets;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Api.Tests.Assets;

/// <summary>The artwork rules (#97): format sniffing, the size limits, and which thumbnails are made and served.</summary>
public sealed class ArtworkRulesTests
{
    public static TheoryData<byte[], ArtworkFormat?> Sniffed => new()
    {
        { [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10], ArtworkFormat.Jpeg },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D], ArtworkFormat.Png },
        { [.. "RIFF"u8.ToArray(), 1, 2, 3, 4, .. "WEBPVP8 "u8.ToArray()], ArtworkFormat.Webp },
        { [.. "RIFF"u8.ToArray(), 1, 2, 3, 4, .. "WEBPVP8L"u8.ToArray()], ArtworkFormat.Webp },
        { [.. "RIFF"u8.ToArray(), 1, 2, 3, 4, .. "WEBPVP8X"u8.ToArray()], ArtworkFormat.Webp },

        // Complement: near misses and other formats are not artwork.
        { [], null },
        { [0xFF, 0xD8], null },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A], null },
        { [.. "RIFF"u8.ToArray(), 1, 2, 3, 4, .. "WAVEfmt "u8.ToArray()], null },
        { [.. "RIFF"u8.ToArray(), 1, 2, 3, 4, .. "WEBPJUNK"u8.ToArray()], null },
        { "GIF89a\u0001\0\u0001\0"u8.ToArray(), null },
        { "BM\0\0\0\0\0\0\0\0"u8.ToArray(), null },
        { "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), null },
        { "%PDF-1.7"u8.ToArray(), null },
        { [0, 0, 0, 0x18, .. "ftypavif"u8.ToArray()], null },
    };

    [Theory]
    [MemberData(nameof(Sniffed))]
    public void TheLeadingBytesDecideTheFormat(byte[] content, ArtworkFormat? expected) =>
        Assert.Equal(expected, ArtworkRules.Sniff(content));

    [Fact]
    public void EachFormatHasItsOwnMediaTypeAndExtensionBothWays()
    {
        foreach (var format in Enum.GetValues<ArtworkFormat>())
        {
            Assert.Equal(format, ArtworkRules.FormatOf(ArtworkRules.MediaType(format)));
        }

        Assert.Equal(["jpg", "png", "webp"], Enum.GetValues<ArtworkFormat>().Select(ArtworkRules.Extension));
        Assert.Null(ArtworkRules.FormatOf("image/gif"));
        Assert.Null(ArtworkRules.FormatOf("IMAGE/PNG"));
        Assert.Null(ArtworkRules.FormatOf(null));
    }

    [Fact]
    public void TheLimitsAreTwentyFiveMegabytesTwelveThousandPixelsAndFiveHundredTwelveMegabytesToDecode()
    {
        Assert.Equal(26_214_400, ArtworkRules.MaximumBytes);
        Assert.True(ArtworkRules.IsWithinMaximumSide(12_000, 12_000));
        Assert.False(ArtworkRules.IsWithinMaximumSide(12_001, 1));
        Assert.False(ArtworkRules.IsWithinMaximumSide(1, 12_001));
        Assert.False(ArtworkRules.IsWithinMaximumSide(0, 10));
        Assert.Equal(536_870_912, ArtworkRules.DecodeMemoryCapBytes);
        Assert.Equal(576_000_000, ArtworkRules.DecodeBytes(12_000, 12_000));
    }

    [Theory]
    [InlineData(2000, 1000, new[] { 96, 320, 1024 })]
    [InlineData(1024, 10, new[] { 96, 320, 1024 })]
    [InlineData(1023, 1023, new[] { 96, 320 })]
    [InlineData(200, 320, new[] { 96, 320 })]
    [InlineData(96, 50, new[] { 96 })]
    [InlineData(95, 95, new int[0])]
    public void ThumbnailSizesLargerThanTheOriginalAreSkipped(int width, int height, int[] expected) =>
        Assert.Equal(expected, ArtworkRules.SizesFor(width, height));

    [Theory]
    [InlineData(2000, 1000, 320, 320, 160)]
    [InlineData(1000, 2000, 320, 160, 320)]
    [InlineData(500, 500, 96, 96, 96)]
    [InlineData(12_000, 2, 96, 96, 1)]
    [InlineData(1000, 333, 96, 96, 32)]
    [InlineData(1000, 335, 96, 96, 32)]
    [InlineData(1000, 338, 96, 96, 32)]
    [InlineData(1000, 339, 96, 96, 33)]
    public void AThumbnailKeepsTheProportionsWithTheLongSideAtItsSize(int width, int height, int size, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ArtworkRules.ThumbnailDimensions(width, height, size));

    [Fact]
    public void ASizeNotMadeIsServedByTheNextSmallerOneOrTheOriginal()
    {
        Assert.Equal(1024, ArtworkRules.ServedSize(1024, [96, 320, 1024]));
        Assert.Equal(320, ArtworkRules.ServedSize(1024, [96, 320]));
        Assert.Equal(96, ArtworkRules.ServedSize(320, [96]));
        Assert.Null(ArtworkRules.ServedSize(96, []));
        Assert.Equal(96, ArtworkRules.ServedSize(96, [96, 320]));
    }

    [Theory]
    [InlineData("96", 96)]
    [InlineData("320", 320)]
    [InlineData("1024", 1024)]
    [InlineData("0096", null)]
    [InlineData("200", null)]
    [InlineData("original", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyTheThreeSizesAreThumbnailSizes(string? text, int? expected) =>
        Assert.Equal(expected, ArtworkRules.ParseSize(text));

    [Fact]
    public void FilesAreNamedByTheContentHashInsideTheArtworkFolder()
    {
        var hash = new string('a', 2) + new string('0', 62);
        var asset = new Asset(Guid.CreateVersion7(), hash, ArtworkFormat.Png, 10, 400, 200, [96, 320], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.Equal($"artwork/aa/{hash}", ArtworkPaths.Folder(hash));
        Assert.Equal([$"artwork/aa/{hash}/original.png", $"artwork/aa/{hash}/96.webp", $"artwork/aa/{hash}/320.webp"], ArtworkPaths.Files(asset));
        Assert.All(ArtworkPaths.Files(asset), path => Assert.Equal(hash, ArtworkPaths.ContentHashOf(path)));
        Assert.All(ArtworkPaths.Files(asset), path => Assert.True(Application.Retention.RetentionService.IsManagedFilePath(path)));

        // Complement: anything else is not an artwork file.
        Assert.Null(ArtworkPaths.ContentHashOf($"artwork/ab/{hash}/original.png"));
        Assert.Null(ArtworkPaths.ContentHashOf($"artwork/aa/{hash}"));
        Assert.Null(ArtworkPaths.ContentHashOf($"art/aa/{hash}/original.png"));
        Assert.Null(ArtworkPaths.ContentHashOf($"artwork/AA/{hash.ToUpperInvariant()}/original.png"));
        Assert.Null(ArtworkPaths.ContentHashOf("artwork/aa/aa00/original.png"));
        Assert.Null(ArtworkPaths.ContentHashOf(null));
        Assert.Throws<ArgumentException>(() => ArtworkPaths.Folder("../../etc"));
    }
}
