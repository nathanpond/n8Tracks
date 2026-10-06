namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>assets</c>: a distinct uploaded image. Its files are named by
/// <see cref="ContentHash"/> under the managed-assets folder, so the row holds no path.
/// </summary>
public sealed class AssetRecord
{
    public required Guid Id { get; set; }

    /// <summary>The SHA-256 of the original's bytes, lower-case hexadecimal; unique, so identical bytes are stored once.</summary>
    public required string ContentHash { get; set; }

    /// <summary><c>image/jpeg</c>, <c>image/png</c>, or <c>image/webp</c>: what the content was found to be.</summary>
    public required string MediaType { get; set; }

    /// <summary>The original's size in bytes.</summary>
    public required long Bytes { get; set; }

    /// <summary>The original's width in pixels, its EXIF orientation applied.</summary>
    public required int Width { get; set; }

    /// <summary>The original's height in pixels, its EXIF orientation applied.</summary>
    public required int Height { get; set; }

    /// <summary>A JSON array of the thumbnail sizes made, ascending, for example <c>[96,320]</c>.</summary>
    public required string ThumbnailSizes { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string CreatedUtc { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision: the latest upload of these bytes. Indexed, for the sweep.</summary>
    public required string UploadedUtc { get; set; }
}
