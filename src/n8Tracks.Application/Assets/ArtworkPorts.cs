using System.Globalization;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Application.Assets;

/// <summary>The <c>assets</c> table: one row per distinct uploaded image, keyed by its content hash.</summary>
public interface IAssetStore
{
    /// <summary>The asset with <paramref name="id"/>, or null.</summary>
    Task<Asset?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The asset whose bytes hash to <paramref name="contentHash"/>, or null.</summary>
    Task<Asset?> FindByHashAsync(string contentHash, CancellationToken cancellationToken);

    /// <summary>Stores a new asset. Its content hash is unique.</summary>
    Task AddAsync(Asset asset, CancellationToken cancellationToken);

    /// <summary>Records that the asset's bytes were uploaded again at <paramref name="uploadedUtc"/>.</summary>
    Task TouchAsync(Guid id, DateTimeOffset uploadedUtc, CancellationToken cancellationToken);

    /// <summary>Every asset last uploaded at or before <paramref name="cutoff"/>, oldest first.</summary>
    Task<IReadOnlyList<Asset>> UploadedAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <summary>Removes the asset's row.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// The artwork files in the managed-assets folder (<c>&lt;data&gt;/assets</c>), by paths relative to
/// it. Nothing here deletes: deletion goes only through <see cref="Retention.IManagedFiles"/>.
/// </summary>
public interface IManagedAssetStore
{
    /// <summary>Whether a regular file (not a link) is at <paramref name="path"/>.</summary>
    bool Exists(string path);

    /// <summary>The file at <paramref name="path"/> for reading, or null when there is none (or it is a link).</summary>
    Stream? OpenRead(string path);

    /// <summary>
    /// Writes <paramref name="content"/> at <paramref name="path"/> unless a file is already there:
    /// in full to a staging file first, then moved into place, so a file under its final name is
    /// always complete.
    /// </summary>
    Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>Removes <paramref name="folder"/> and then its parent when they are empty folders.</summary>
    void RemoveEmptyFolders(string folder);

    /// <summary>Removes staging files older than <paramref name="age"/>, left by a write that never finished.</summary>
    void RemoveStaleStaging(TimeSpan age);
}

/// <summary>Decodes an uploaded image, bounded, and makes its thumbnails.</summary>
public interface IArtworkImaging
{
    /// <summary>
    /// Decodes <paramref name="content"/>, which its leading bytes say is <paramref name="format"/>,
    /// and makes the thumbnails <see cref="ArtworkRules.SizesFor"/> names. The dimensions are read
    /// from the header and checked before any pixels are decoded; only the first frame is decoded.
    /// One decode runs at a time, so the decode memory cap holds for the whole server.
    /// </summary>
    Task<ArtworkDecoding> DecodeAsync(ReadOnlyMemory<byte> content, ArtworkFormat format, CancellationToken cancellationToken);
}

/// <summary>What decoding an upload found.</summary>
public abstract record ArtworkDecoding
{
    private ArtworkDecoding()
    {
    }

    /// <summary>
    /// It decoded. <paramref name="Width"/> and <paramref name="Height"/> have the orientation
    /// applied; the thumbnails are lossy WebP, ascending by size.
    /// </summary>
    public sealed record Decoded(int Width, int Height, IReadOnlyList<ArtworkThumbnail> Thumbnails) : ArtworkDecoding;

    /// <summary>The content is not a whole, readable image of its format.</summary>
    public sealed record Undecodable : ArtworkDecoding;

    /// <summary>
    /// A side is over <see cref="ArtworkRules.MaximumSide"/>, or the pixels would take more than
    /// <see cref="ArtworkRules.DecodeMemoryCapBytes"/> to decode. The dimensions are as stored.
    /// </summary>
    public sealed record DimensionsExceeded(int Width, int Height) : ArtworkDecoding;
}

/// <summary>One thumbnail: <paramref name="Size"/> pixels on the long side, WebP-encoded.</summary>
public sealed record ArtworkThumbnail(int Size, ReadOnlyMemory<byte> Content);

/// <summary>
/// Whether a live record attaches an asset. Each store that attaches artwork to its records (Songs
/// from #98; Albums, Playlists, and Artists from #100) registers one; there is none before, so until
/// then every upload is unattached.
/// </summary>
public interface IArtworkAttachments
{
    Task<bool> IsAttachedAsync(Guid assetId, CancellationToken cancellationToken);
}

/// <summary>
/// The <c>artwork_attachments</c> table: which asset each owner (a Song; later an Album, Playlist,
/// or Artist) shows as its artwork, at most one per owner. It is the production
/// <see cref="IArtworkAttachments"/>: an asset any row names is attached.
/// </summary>
public interface IArtworkAttachmentStore : IArtworkAttachments
{
    /// <summary>The owner's attachment, or null when it has no artwork.</summary>
    Task<ArtworkAttachment?> FindAsync(string ownerType, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>Stores a new attachment. The owner must have none (one per owner is a unique key).</summary>
    Task AddAsync(ArtworkAttachment attachment, CancellationToken cancellationToken);
}

/// <summary>
/// Where an asset's files are, relative to the managed-assets folder: a folder named by the content
/// hash (under a two-character fan-out folder), holding <c>original.&lt;ext&gt;</c>, the bytes as
/// uploaded, and <c>&lt;size&gt;.webp</c> for each thumbnail.
/// </summary>
public static class ArtworkPaths
{
    /// <summary>The folder every artwork file is under.</summary>
    public const string Root = "artwork";

    /// <summary>The folder of the asset whose content hash is <paramref name="contentHash"/>.</summary>
    public static string Folder(string contentHash)
    {
        ArgumentNullException.ThrowIfNull(contentHash);

        return IsContentHash(contentHash)
            ? $"{Root}/{contentHash[..2]}/{contentHash}"
            : throw new ArgumentException("A content hash is 64 lower-case hexadecimal digits.", nameof(contentHash));
    }

    /// <summary>The original's path.</summary>
    public static string Original(string contentHash, ArtworkFormat format) => $"{Folder(contentHash)}/original.{ArtworkRules.Extension(format)}";

    /// <summary>The <paramref name="size"/> thumbnail's path.</summary>
    public static string Thumbnail(string contentHash, int size) =>
        $"{Folder(contentHash)}/{size.ToString(CultureInfo.InvariantCulture)}.webp";

    /// <summary>Every file of <paramref name="asset"/>: the original, then each thumbnail.</summary>
    public static IReadOnlyList<string> Files(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        return [Original(asset.ContentHash, asset.Format), .. asset.ThumbnailSizes.Select(size => Thumbnail(asset.ContentHash, size))];
    }

    /// <summary>The content hash whose folder <paramref name="path"/> is a file in, or null when it is not an artwork file's path.</summary>
    public static string? ContentHashOf(string? path)
    {
        var parts = path?.Split('/');
        return parts is [Root, var fanOut, var hash, { Length: > 0 }] && IsContentHash(hash) && fanOut == hash[..2] ? hash : null;
    }

    /// <summary>Whether <paramref name="text"/> is a SHA-256 in lower-case hexadecimal.</summary>
    public static bool IsContentHash(string text) =>
        text is { Length: 64 } && text.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
