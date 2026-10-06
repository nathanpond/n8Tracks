using n8Tracks.Application.Assets;
using n8Tracks.Domain.Assets;
using SkiaSharp;

namespace n8Tracks.Infrastructure.Assets;

/// <summary>
/// Decodes uploads and makes thumbnails with SkiaSharp (MIT; Skia's own codecs: libjpeg-turbo,
/// libpng, libwebp). Bounded: the codec reads only the header first, and the dimensions are checked
/// against <see cref="ArtworkRules.MaximumSide"/> and the decode memory cap before a pixel buffer is
/// allocated. An image too large for the cap at full size is decoded at the largest scale the codec
/// supports that fits (JPEG and WebP scale while decoding; PNG cannot, so such a PNG is refused). Only
/// the first frame is decoded, into sRGB (an embedded colour profile is converted); a decode that
/// does not finish cleanly (a truncated or corrupt file) is refused. Thumbnails are drawn with the EXIF
/// orientation applied and written as lossy WebP, so no metadata from the upload reaches them.
/// Decodes run one at a time (this is a singleton), so two uploads never hold two decodes' pixels.
/// </summary>
internal sealed class SkiaArtworkImaging : IArtworkImaging, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Skia's codec name for each accepted format.</summary>
    private static readonly Dictionary<SKEncodedImageFormat, ArtworkFormat> Formats = new()
    {
        [SKEncodedImageFormat.Jpeg] = ArtworkFormat.Jpeg,
        [SKEncodedImageFormat.Png] = ArtworkFormat.Png,
        [SKEncodedImageFormat.Webp] = ArtworkFormat.Webp,
    };

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public async Task<ArtworkDecoding> DecodeAsync(ReadOnlyMemory<byte> content, ArtworkFormat format, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var decoded = Decode(content, format, out var failure);
            if (decoded is null)
            {
                return failure!;
            }

            var (width, height) = decoded.Full;
            var thumbnails = ArtworkRules.SizesFor(width, height)
                .Select(thumbnail => new ArtworkThumbnail(thumbnail, Thumbnail(decoded, ArtworkRules.ThumbnailDimensions(width, height, thumbnail))))
                .ToList();
            return new ArtworkDecoding.Decoded(width, height, thumbnails);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ArtworkThumbnail>?> CropAsync(
        ReadOnlyMemory<byte> content,
        ArtworkFormat format,
        ArtworkCrop crop,
        IReadOnlyList<int> sizes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(crop);
        ArgumentNullException.ThrowIfNull(sizes);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var decoded = Decode(content, format, out _);
            return decoded is null ? null : [.. sizes.Select(size => new ArtworkThumbnail(size, Square(decoded, crop, size)))];
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    /// <summary>
    /// The first frame of <paramref name="content"/>, decoded within the bounds, or null with the
    /// reason in <paramref name="failure"/>. The caller disposes it.
    /// </summary>
    private static DecodedImage? Decode(ReadOnlyMemory<byte> content, ArtworkFormat format, out ArtworkDecoding? failure)
    {
        using var data = SKData.CreateCopy(content.Span);
        using var codec = SKCodec.Create(data);

        // The decoder must agree with the leading bytes: content that only starts like an image is not one.
        if (codec is null || !Formats.TryGetValue(codec.EncodedFormat, out var found) || found != format)
        {
            failure = new ArtworkDecoding.Undecodable();
            return null;
        }

        var origin = codec.EncodedOrigin;
        var encoded = codec.Info;
        var (width, height) = Oriented(origin, encoded.Width, encoded.Height);
        if (!ArtworkRules.IsWithinMaximumSide(encoded.Width, encoded.Height))
        {
            failure = encoded.Width > 0 && encoded.Height > 0 ? new ArtworkDecoding.DimensionsExceeded(width, height) : new ArtworkDecoding.Undecodable();
            return null;
        }

        if (DecodeSize(codec, encoded.Width, encoded.Height) is not { } size)
        {
            failure = new ArtworkDecoding.DimensionsExceeded(width, height);
            return null;
        }

        var target = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap();
        try
        {
            if (!bitmap.TryAllocPixels(target))
            {
                failure = new ArtworkDecoding.DimensionsExceeded(width, height);
                return null;
            }

            if (codec.GetPixels(target, bitmap.GetPixels(), new SKCodecOptions(0)) != SKCodecResult.Success)
            {
                failure = new ArtworkDecoding.Undecodable();
                return null;
            }

            bitmap.SetImmutable();
            failure = null;
            var decoded = new DecodedImage(bitmap, SKImage.FromBitmap(bitmap), origin, Oriented(origin, size.Width, size.Height), (width, height));
            bitmap = null;
            return decoded;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    /// <summary>
    /// The size to decode at: full size when its pixels fit the memory cap, otherwise the largest
    /// eighth-step scale the codec supports that fits; null when none does.
    /// </summary>
    private static SKSizeI? DecodeSize(SKCodec codec, int width, int height)
    {
        if (ArtworkRules.DecodeBytes(width, height) <= ArtworkRules.DecodeMemoryCapBytes)
        {
            return new SKSizeI(width, height);
        }

        for (var eighths = 7; eighths >= 1; eighths--)
        {
            var scaled = codec.GetScaledDimensions(eighths / 8f);
            if (scaled.Width is > 0 && scaled.Height > 0
                && scaled.Width < width
                && ArtworkRules.DecodeBytes(scaled.Width, scaled.Height) <= ArtworkRules.DecodeMemoryCapBytes)
            {
                return scaled;
            }
        }

        return null;
    }

    /// <summary>One thumbnail: the whole image scaled to <paramref name="target"/> with its orientation applied, as lossy WebP.</summary>
    private static byte[] Thumbnail(DecodedImage decoded, (int Width, int Height) target) =>
        Draw(target, canvas => canvas.Scale((float)target.Width / decoded.Shown.Width, (float)target.Height / decoded.Shown.Height), decoded);

    /// <summary>
    /// One square thumbnail, <paramref name="size"/> pixels a side: the <paramref name="crop"/> of
    /// the image (in pixels of the full-size original, orientation applied), as lossy WebP.
    /// </summary>
    private static byte[] Square(DecodedImage decoded, ArtworkCrop crop, int size) =>
        Draw(
            (size, size),
            canvas =>
            {
                canvas.Scale((float)size / crop.Size);
                canvas.Translate(-crop.X, -crop.Y);

                // A very large original may have been decoded at a smaller scale: back to full-size pixels.
                canvas.Scale((float)decoded.Full.Width / decoded.Shown.Width, (float)decoded.Full.Height / decoded.Shown.Height);
            },
            decoded);

    /// <summary>
    /// Draws the decoded image the right way up onto a <paramref name="target"/> surface, after
    /// <paramref name="place"/> has set where its oriented pixels go, and encodes it as lossy WebP.
    /// </summary>
    private static byte[] Draw((int Width, int Height) target, Action<SKCanvas> place, DecodedImage decoded)
    {
        var info = new SKImageInfo(target.Width, target.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("A thumbnail surface could not be created.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        place(canvas);
        var orientation = OrientationMatrix(decoded.Origin, decoded.Shown.Width, decoded.Shown.Height);
        canvas.Concat(in orientation);
        canvas.DrawImage(decoded.Image, 0, 0, Sampling, null);
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var pixels = snapshot.PeekPixels() ?? throw new InvalidOperationException("A thumbnail's pixels could not be read.");
        using var encoded = pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, ArtworkRules.ThumbnailQuality))
            ?? throw new InvalidOperationException("A thumbnail could not be encoded.");
        return encoded.ToArray();
    }

    /// <summary>The dimensions an image of <paramref name="width"/> by <paramref name="height"/> has once <paramref name="origin"/> is applied.</summary>
    internal static (int Width, int Height) Oriented(SKEncodedOrigin origin, int width, int height) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom
            ? (height, width)
            : (width, height);

    /// <summary>
    /// The matrix that draws the stored pixels the right way up (Skia's <c>SkEncodedOriginToMatrix</c>);
    /// <paramref name="width"/> and <paramref name="height"/> are the dimensions with the orientation applied.
    /// </summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, width, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, width, -1, 0, height, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, height, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    /// <summary>
    /// A decoded first frame: the pixels as stored (<see cref="Image"/>, over <see cref="Bitmap"/>),
    /// the orientation to apply, the decoded size with it applied (<see cref="Shown"/>), and the
    /// full-size original's (<see cref="Full"/>), larger when the decode was scaled down.
    /// </summary>
    private sealed record DecodedImage(SKBitmap Bitmap, SKImage Image, SKEncodedOrigin Origin, (int Width, int Height) Shown, (int Width, int Height) Full) : IDisposable
    {
        public void Dispose()
        {
            Image.Dispose();
            Bitmap.Dispose();
        }
    }
}
