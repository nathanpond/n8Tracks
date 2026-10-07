using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace n8Tracks.Api.Tests.Assets;

/// <summary>
/// Test images, made in memory: plain ones in each accepted format, and hand-built files for what an
/// encoder will not write (an EXIF orientation, an animation, a header promising more pixels than
/// there are).
/// </summary>
internal static class ArtworkImages
{
    public static readonly SKColor Red = new(255, 0, 0);
    public static readonly SKColor Blue = new(0, 0, 255);

    /// <summary>A <paramref name="width"/> by <paramref name="height"/> image, red on its left half and blue on its right.</summary>
    public static byte[] Halves(SKEncodedImageFormat format, int width, int height, int quality = 100) =>
        Encode(Draw(width, height, static (canvas, w, h) =>
        {
            using var red = new SKPaint { Color = Red };
            using var blue = new SKPaint { Color = Blue };
            canvas.DrawRect(0, 0, w / 2f, h, red);
            canvas.DrawRect(w / 2f, 0, w / 2f, h, blue);
        }), format, quality);

    /// <summary>A <paramref name="width"/> by <paramref name="height"/> image of one colour.</summary>
    public static byte[] Solid(SKEncodedImageFormat format, int width, int height, SKColor colour, int quality = 100) =>
        Encode(Draw(width, height, (canvas, _, _) => canvas.Clear(colour)), format, quality);

    /// <summary>A PNG whose left half is transparent and right half opaque blue.</summary>
    public static byte[] HalfTransparentPng(int width, int height) =>
        Encode(Draw(width, height, static (canvas, w, h) =>
        {
            canvas.Clear(SKColors.Transparent);
            using var blue = new SKPaint { Color = Blue };
            canvas.DrawRect(w / 2f, 0, w / 2f, h, blue);
        }), SKEncodedImageFormat.Png, 100);

    /// <summary>
    /// A PNG of one opaque colour whose channel values are Display P3's (written as stored, not converted
    /// from sRGB), with that colour space's profile embedded.
    /// </summary>
    public static byte[] DisplayP3Png(int width, int height, SKColor colour)
    {
        var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, p3);
        using var bitmap = new SKBitmap(info);
        var pixels = new byte[info.BytesSize];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]) = (colour.Red, colour.Green, colour.Blue, 255);
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("The test image could not be encoded.");
        return data.ToArray();
    }

    /// <summary>
    /// <paramref name="jpeg"/> with an EXIF block saying it is stored rotated (orientation
    /// <paramref name="orientation"/>; 6 means "rotate 90° clockwise to display").
    /// </summary>
    public static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        // A big-endian TIFF header and one IFD entry: tag 0x0112, SHORT, one value.
        var tiff = new byte[26];
        "MM"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), orientation);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(22), 0);

        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);

        // Right after the start-of-image marker.
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    /// <summary>
    /// An animated PNG (APNG) of two frames: the default image (<paramref name="first"/>) and a second
    /// frame (<paramref name="second"/>, a PNG of the same size whose image data becomes the frame's).
    /// </summary>
    public static byte[] AnimatedPng(byte[] first, byte[] second)
    {
        var chunks = Chunks(first);
        var header = chunks.First(static chunk => chunk.Type == "IHDR").Data;
        var width = BinaryPrimitives.ReadUInt32BigEndian(header);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        var secondData = Chunks(second).Where(static chunk => chunk.Type == "IDAT").SelectMany(static chunk => chunk.Data).ToArray();

        var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        uint sequence = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.Type == "IDAT" && sequence == 0)
            {
                WriteChunk(output, "acTL", [.. BigEndian(2), .. BigEndian(0)]);
                WriteChunk(output, "fcTL", FrameControl(sequence++, width, height));
            }

            if (chunk.Type == "IEND")
            {
                WriteChunk(output, "fcTL", FrameControl(sequence++, width, height));
                WriteChunk(output, "fdAT", [.. BigEndian(sequence++), .. secondData]);
            }

            WriteChunk(output, chunk.Type, chunk.Data);
        }

        return output.ToArray();
    }

    /// <summary>
    /// An animated WebP of two frames, each a still WebP of the same size (its <c>VP8 </c> or
    /// <c>VP8L</c> chunk becomes the frame's bitstream).
    /// </summary>
    public static byte[] AnimatedWebp(byte[] first, byte[] second, int width, int height)
    {
        var body = new MemoryStream();
        body.Write("WEBP"u8);

        // VP8X: the animation flag, and the canvas size less one, 24-bit little-endian.
        WriteRiffChunk(body, "VP8X", [0x02, 0, 0, 0, .. Uint24(width - 1), .. Uint24(height - 1)]);
        WriteRiffChunk(body, "ANIM", [0, 0, 0, 0, 0, 0]);
        foreach (var frame in new[] { first, second })
        {
            var bitstream = frame[12..];
            WriteRiffChunk(body, "ANMF", [.. Uint24(0), .. Uint24(0), .. Uint24(width - 1), .. Uint24(height - 1), .. Uint24(100), 0, .. bitstream]);
        }

        var riff = new MemoryStream();
        riff.Write("RIFF"u8);
        riff.Write(BitConverter.GetBytes((uint)body.Length));
        body.Position = 0;
        body.CopyTo(riff);
        return riff.ToArray();
    }

    /// <summary>
    /// A PNG that only claims to be <paramref name="width"/> by <paramref name="height"/>: a valid
    /// header and a few bytes of image data, so it can be refused only from its header.
    /// </summary>
    public static byte[] PngHeaderClaiming(int width, int height)
    {
        var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(output, "IHDR", [.. BigEndian((uint)width), .. BigEndian((uint)height), 8, 6, 0, 0, 0]);

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(new byte[64]);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>
    /// A real, whole <paramref name="width"/> by <paramref name="height"/> PNG, black and 8-bit grey,
    /// written row by row so even a 100-megapixel one is made without holding its pixels: every row
    /// is zero, which compresses to a few hundred kilobytes.
    /// </summary>
    public static byte[] BlankPng(int width, int height)
    {
        var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(output, "IHDR", [.. BigEndian((uint)width), .. BigEndian((uint)height), 8, 0, 0, 0, 0]);

        // Each row is its filter byte (none) and one byte a pixel.
        var row = new byte[width + 1];
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>The image's dimensions.</summary>
    public static (int Width, int Height) Dimensions(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded) ?? throw new InvalidOperationException("Not an image.");
        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>The colour at (<paramref name="x"/>, <paramref name="y"/>) of the decoded image, in sRGB, unpremultiplied.</summary>
    public static SKColor Pixel(byte[] encoded, int x, int y)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded)) ?? throw new InvalidOperationException("Not an image.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        using var bitmap = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels()));
        return bitmap.GetPixel(x, y);
    }

    /// <summary>Whether two colours are within <paramref name="tolerance"/> on every channel (lossy thumbnails drift a little).</summary>
    public static bool Near(SKColor actual, SKColor expected, int tolerance = 24) =>
        Math.Abs(actual.Red - expected.Red) <= tolerance
        && Math.Abs(actual.Green - expected.Green) <= tolerance
        && Math.Abs(actual.Blue - expected.Blue) <= tolerance
        && Math.Abs(actual.Alpha - expected.Alpha) <= tolerance;

    private static SKBitmap Draw(int width, int height, Action<SKCanvas, int, int> paint)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        paint(canvas, width, height);
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using (bitmap)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var pixels = image.PeekPixels();
            using var data = (format == SKEncodedImageFormat.Webp
                ? pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, quality))
                : pixels.Encode(format, quality)) ?? throw new InvalidOperationException("The test image could not be encoded.");
            return data.ToArray();
        }
    }

    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        for (var offset = 8; offset < png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            chunks.Add((type, png[(offset + 8)..(offset + 8 + length)]));
            offset += 12 + length;
        }

        return chunks;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(BigEndian((uint)data.Length));
        output.Write(typed);
        output.Write(BigEndian(Crc32(typed)));
    }

    private static void WriteRiffChunk(Stream output, string type, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(BitConverter.GetBytes((uint)data.Length));
        output.Write(data);
        if (data.Length % 2 == 1)
        {
            output.WriteByte(0);
        }
    }

    private static byte[] FrameControl(uint sequence, uint width, uint height) =>
        [.. BigEndian(sequence), .. BigEndian(width), .. BigEndian(height), .. BigEndian(0), .. BigEndian(0), 0, 1, 0, 10, 0, 0];

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>PNG's CRC-32 (ISO 3309, the one zlib uses).</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static byte[] Uint24(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];
}
