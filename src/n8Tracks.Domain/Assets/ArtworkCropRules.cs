using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace n8Tracks.Domain.Assets;

/// <summary>
/// The rules of a square crop on artwork, in pixels of the original with its orientation applied.
/// Pure. A crop is square by construction (one side); it lies wholly inside the image and is at least
/// <see cref="MinimumSize"/> pixels on a side, except that an image under that on its shorter side
/// can only be cropped to its full shorter side. With no crop set, the centred square of the shorter
/// side is shown (<see cref="Centred"/>).
/// </summary>
public static class ArtworkCropRules
{
    /// <summary>The smallest crop, in pixels of the original on a side.</summary>
    public const int MinimumSize = 64;

    /// <summary>How many hexadecimal digits of the crop's hash name its square thumbnails.</summary>
    public const int KeyLength = 12;

    /// <summary>The smallest side a crop of an image <paramref name="width"/> by <paramref name="height"/> may have.</summary>
    public static int SmallestSize(int width, int height) => Math.Min(MinimumSize, Math.Min(width, height));

    /// <summary>The centred square of the shorter side: what is shown while no crop is set.</summary>
    public static ArtworkCrop Centred(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var size = Math.Min(width, height);
        return new ArtworkCrop((width - size) / 2, (height - size) / 2, size);
    }

    /// <summary>
    /// What is wrong with <paramref name="crop"/> on an image <paramref name="width"/> by
    /// <paramref name="height"/>, as messages for the user; none when it may be set.
    /// </summary>
    public static string[] Errors(ArtworkCrop crop, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(crop);

        var shorter = Math.Min(width, height);
        if (shorter < MinimumSize && crop.Size != shorter)
        {
            return [$"The image is only {Number(shorter)} pixels on its shorter side, so the crop must be all of it ({Number(shorter)} pixels)."];
        }

        if (crop.Size < SmallestSize(width, height))
        {
            return [$"The crop must be at least {Number(MinimumSize)} pixels on a side."];
        }

        if (crop.X < 0 || crop.Y < 0 || (long)crop.X + crop.Size > width || (long)crop.Y + crop.Size > height)
        {
            return [$"The crop must lie inside the image, which is {Number(width)} × {Number(height)} pixels."];
        }

        return [];
    }

    /// <summary>Whether <paramref name="crop"/> may be set on an image <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static bool Fits(ArtworkCrop crop, int width, int height) => Errors(crop, width, height).Length == 0;

    /// <summary>
    /// A crop carried over to a new image (the "Keep crop" choice when artwork is replaced): the same
    /// crop when it fits the new image, otherwise null, the centred default.
    /// </summary>
    public static ArtworkCrop? Kept(ArtworkCrop? crop, int width, int height) =>
        crop is not null && Fits(crop, width, height) ? crop : null;

    /// <summary>
    /// The short name of <paramref name="crop"/>'s square thumbnails: the first <see cref="KeyLength"/>
    /// hexadecimal digits of the SHA-256 of <c>x,y,size</c>. It changes whenever the crop does, so the
    /// thumbnails' URLs do too.
    /// </summary>
    public static string Key(ArtworkCrop crop)
    {
        ArgumentNullException.ThrowIfNull(crop);

        var text = string.Create(CultureInfo.InvariantCulture, $"{crop.X},{crop.Y},{crop.Size}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(text)))[..KeyLength];
    }

    /// <summary>Whether <paramref name="text"/> has the form of a <see cref="Key"/>.</summary>
    public static bool IsKey(string? text) =>
        text is { Length: KeyLength } && text.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// The square thumbnail sizes made for <paramref name="crop"/>: each thumbnail size no larger than
    /// the crop (never enlarged), or, for a crop smaller than every one, the crop's own size.
    /// </summary>
    public static IReadOnlyList<int> SizesFor(ArtworkCrop crop)
    {
        ArgumentNullException.ThrowIfNull(crop);

        var sizes = ArtworkRules.SizesFor(crop.Size, crop.Size);
        return sizes.Count > 0 ? sizes : [crop.Size];
    }

    /// <summary>The square thumbnail served for a request of <paramref name="requested"/>: as <see cref="ArtworkRules.ServedSize"/>, or the smallest made.</summary>
    public static int ServedSize(ArtworkCrop crop, int requested)
    {
        var sizes = SizesFor(crop);
        return ArtworkRules.ServedSize(requested, sizes) ?? sizes.Min();
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
