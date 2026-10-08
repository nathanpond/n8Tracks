namespace n8Tracks.Domain.Assets;

/// <summary>The image formats artwork may be. Decided by a file's content, never its name or declared type.</summary>
public enum ArtworkFormat
{
    Jpeg,
    Png,
    Webp,
}

/// <summary>
/// One managed artwork asset: an uploaded image, stored once however often its bytes are uploaded.
/// <see cref="Width"/> and <see cref="Height"/> are the original's, with its EXIF orientation applied.
/// <see cref="ThumbnailSizes"/> are the sizes made for it, ascending. <see cref="UploadedUtc"/> is
/// the latest upload of these bytes, from which an asset nothing attaches is kept for a day.
/// </summary>
public sealed record Asset(
    Guid Id,
    string ContentHash,
    ArtworkFormat Format,
    long Bytes,
    int Width,
    int Height,
    IReadOnlyList<int> ThumbnailSizes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UploadedUtc);

/// <summary>The artwork rules: what is accepted, and which thumbnails are made and served. Pure.</summary>
public static class ArtworkRules
{
    /// <summary>The largest file accepted: 25 MB of file content (25 × 1,024 × 1,024 bytes).</summary>
    public const long MaximumBytes = 25L * 1024 * 1024;

    /// <summary>The longest side accepted, in pixels.</summary>
    public const int MaximumSide = 12_000;

    /// <summary>
    /// The most pixels accepted (width × height): 100 megapixels, whatever the shape (#305). A full
    /// decode of that many takes <see cref="BytesPerPixel"/> bytes each, 400,000,000 bytes, within
    /// <see cref="DecodeMemoryCapBytes"/>.
    /// </summary>
    public const long MaximumPixels = 100_000_000;

    /// <summary>The most memory one decode may take for its pixels (four bytes each).</summary>
    public const long DecodeMemoryCapBytes = 512L * 1024 * 1024;

    /// <summary>The bytes a decoded pixel takes.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>The lossy WebP quality thumbnails are written at.</summary>
    public const int ThumbnailQuality = 85;

    /// <summary>The thumbnail sizes, in pixels on the long side, ascending.</summary>
    public static IReadOnlyList<int> ThumbnailSizes { get; } = [96, 320, 1024];

    /// <summary>How many leading bytes <see cref="Sniff"/> needs to tell every format apart.</summary>
    public const int SniffLength = 16;

    /// <summary>
    /// The format the content's own leading bytes say it is, or null when they are not one of the
    /// three: JPEG's start-of-image marker, PNG's eight-byte signature, or a RIFF file of form
    /// <c>WEBP</c> whose first chunk is a VP8 bitstream.
    /// </summary>
    public static ArtworkFormat? Sniff(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return ArtworkFormat.Jpeg;
        }

        if (content.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ArtworkFormat.Png;
        }

        if (content.Length >= SniffLength
            && content[..4].SequenceEqual("RIFF"u8)
            && content[8..12].SequenceEqual("WEBP"u8)
            && (content[12..16].SequenceEqual("VP8 "u8) || content[12..16].SequenceEqual("VP8L"u8) || content[12..16].SequenceEqual("VP8X"u8)))
        {
            return ArtworkFormat.Webp;
        }

        return null;
    }

    /// <summary>The media type an asset of <paramref name="format"/> is served as.</summary>
    public static string MediaType(ArtworkFormat format) => format switch
    {
        ArtworkFormat.Jpeg => "image/jpeg",
        ArtworkFormat.Png => "image/png",
        ArtworkFormat.Webp => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>The file extension an original of <paramref name="format"/> is stored with.</summary>
    public static string Extension(ArtworkFormat format) => format switch
    {
        ArtworkFormat.Jpeg => "jpg",
        ArtworkFormat.Png => "png",
        ArtworkFormat.Webp => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>The format whose <see cref="MediaType"/> is <paramref name="mediaType"/>, or null.</summary>
    public static ArtworkFormat? FormatOf(string? mediaType) => mediaType switch
    {
        "image/jpeg" => ArtworkFormat.Jpeg,
        "image/png" => ArtworkFormat.Png,
        "image/webp" => ArtworkFormat.Webp,
        _ => null,
    };

    /// <summary>Whether an image <paramref name="width"/> by <paramref name="height"/> is within <see cref="MaximumSide"/>.</summary>
    public static bool IsWithinMaximumSide(int width, int height) =>
        width is > 0 and <= MaximumSide && height is > 0 and <= MaximumSide;

    /// <summary>Whether an image <paramref name="width"/> by <paramref name="height"/> has no more than <see cref="MaximumPixels"/>.</summary>
    public static bool IsWithinMaximumPixels(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= MaximumPixels;

    /// <summary>The bytes decoding <paramref name="width"/> by <paramref name="height"/> pixels takes.</summary>
    public static long DecodeBytes(int width, int height) => (long)width * height * BytesPerPixel;

    /// <summary>
    /// The thumbnail sizes made for an image <paramref name="width"/> by <paramref name="height"/>
    /// (orientation applied): every size no larger than its long side. A size larger than the
    /// original is skipped, never enlarged.
    /// </summary>
    public static IReadOnlyList<int> SizesFor(int width, int height)
    {
        var longSide = Math.Max(width, height);
        return [.. ThumbnailSizes.Where(size => size <= longSide)];
    }

    /// <summary>
    /// The dimensions of the <paramref name="size"/> thumbnail of an image <paramref name="width"/>
    /// by <paramref name="height"/>: the long side is <paramref name="size"/>, the short side in
    /// proportion, rounded, and at least one pixel.
    /// </summary>
    public static (int Width, int Height) ThumbnailDimensions(int width, int height, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var shortSide = (int)Math.Max(1, Math.Round((double)Math.Min(width, height) * size / Math.Max(width, height), MidpointRounding.AwayFromZero));
        return width >= height ? (size, shortSide) : (shortSide, size);
    }

    /// <summary>
    /// The thumbnail served for a request of <paramref name="requested"/>, given the sizes made
    /// (<paramref name="available"/>): that size when it was made, otherwise the next smaller one
    /// made; null when none is, which serves the original.
    /// </summary>
    public static int? ServedSize(int requested, IReadOnlyCollection<int> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        var smaller = available.Where(size => size <= requested).ToList();
        return smaller.Count == 0 ? null : smaller.Max();
    }

    /// <summary>The thumbnail size <paramref name="text"/> names (one of <see cref="ThumbnailSizes"/>, as digits), or null.</summary>
    public static int? ParseSize(string? text) =>
        ThumbnailSizes.FirstOrDefault(size => string.Equals(text, size.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)) is var found and > 0
            ? found
            : null;
}
