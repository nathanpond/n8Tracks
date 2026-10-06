using System.Security.Cryptography;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Retention;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Application.Assets;

/// <summary>What an upload came to.</summary>
public abstract record ArtworkUploadOutcome
{
    private ArtworkUploadOutcome()
    {
    }

    /// <summary>
    /// The image is stored: a new asset (<paramref name="Created"/>), or the existing one with the same
    /// bytes, whose unattached clock starts again.
    /// </summary>
    public sealed record Stored(Asset Asset, bool Created) : ArtworkUploadOutcome;

    /// <summary>Over <see cref="ArtworkRules.MaximumBytes"/>. Nothing was stored.</summary>
    public sealed record TooLarge : ArtworkUploadOutcome;

    /// <summary>The content is not JPEG, PNG, or WebP, whatever it was called. Nothing was stored.</summary>
    public sealed record UnsupportedType : ArtworkUploadOutcome;

    /// <summary>The content starts like an image but does not decode. Nothing was stored.</summary>
    public sealed record Undecodable(ArtworkFormat Format) : ArtworkUploadOutcome;

    /// <summary>Too many pixels on a side, or in all. Nothing was stored.</summary>
    public sealed record DimensionsExceeded(int Width, int Height) : ArtworkUploadOutcome;
}

/// <summary>An artwork file to serve: its content, its media type (n8Tracks' own, never the uploader's), and a tag for caching.</summary>
public sealed record ArtworkContent(Stream Content, string MediaType, string EntityTag);

/// <summary>What one sweep removed, and how many it could not.</summary>
public sealed record ArtworkSweepSummary(int Removed, int Failed);

/// <summary>
/// The managed artwork store. An upload is proven to be an image by its content (its leading bytes,
/// then a bounded decode), stored once per distinct content under its SHA-256 with the original's
/// bytes unchanged, and given its thumbnails in the same request. Artwork is served only while a
/// live record attaches it, or while it is a fresh upload not yet attached; an upload nothing
/// attaches within <see cref="UnattachedLifetime"/> is removed by the sweep, unless a retention group
/// still lists its files, in which case it goes once that group is pruned.
/// </summary>
public sealed class ArtworkService(
    IAssetStore assets,
    IManagedAssetStore storage,
    IManagedFiles files,
    IArtworkImaging imaging,
    IEnumerable<IArtworkAttachments> attachments,
    IRetentionStore retention,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>How long an upload nothing attaches is kept, from its latest upload.</summary>
    public static readonly TimeSpan UnattachedLifetime = TimeSpan.FromHours(24);

    /// <summary>How old an unfinished staging file must be before the sweep removes it.</summary>
    public static readonly TimeSpan StagingLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Validates and stores <paramref name="content"/>. A refusal stores nothing. Identical bytes are
    /// stored once: uploading them again answers the same asset, restarts its unattached clock, and
    /// puts back any of its files that are missing.
    /// </summary>
    public async Task<ArtworkUploadOutcome> UploadAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (content.Length > ArtworkRules.MaximumBytes)
        {
            return new ArtworkUploadOutcome.TooLarge();
        }

        if (ArtworkRules.Sniff(content.Span) is not { } format)
        {
            return new ArtworkUploadOutcome.UnsupportedType();
        }

        ArtworkDecoding.Decoded decoded;
        switch (await imaging.DecodeAsync(content, format, cancellationToken).ConfigureAwait(false))
        {
            case ArtworkDecoding.Decoded image:
                decoded = image;
                break;
            case ArtworkDecoding.DimensionsExceeded exceeded:
                return new ArtworkUploadOutcome.DimensionsExceeded(exceeded.Width, exceeded.Height);
            default:
                return new ArtworkUploadOutcome.Undecodable(format);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        return await transaction.RunAsync<ArtworkUploadOutcome>(
                async token =>
                {
                    var now = time.GetUtcNow();
                    Asset asset;
                    bool created;
                    if (await assets.FindByHashAsync(hash, token).ConfigureAwait(false) is { } existing)
                    {
                        await assets.TouchAsync(existing.Id, now, token).ConfigureAwait(false);
                        asset = existing with { UploadedUtc = now };
                        created = false;
                    }
                    else
                    {
                        asset = new Asset(
                            Guid.CreateVersion7(now),
                            hash,
                            format,
                            content.Length,
                            decoded.Width,
                            decoded.Height,
                            [.. decoded.Thumbnails.Select(static thumbnail => thumbnail.Size)],
                            now,
                            now);
                        await assets.AddAsync(asset, token).ConfigureAwait(false);
                        created = true;
                    }

                    // Inside the transaction, so a sweep removing these bytes' earlier asset cannot interleave.
                    await WriteMissingFilesAsync(asset, content, decoded, token).ConfigureAwait(false);
                    return new ArtworkUploadOutcome.Stored(asset, created);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The asset with <paramref name="id"/>, or null when there is none or it is not live (see <see cref="IsLiveAsync"/>).</summary>
    public async Task<Asset?> FindLiveAsync(Guid id, CancellationToken cancellationToken) =>
        await assets.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } asset && await IsLiveAsync(asset, cancellationToken).ConfigureAwait(false)
            ? asset
            : null;

    /// <summary>
    /// The live asset's original (<paramref name="size"/> null) or the thumbnail served for
    /// <paramref name="size"/> (<see cref="ArtworkRules.ServedSize"/>), or null when the asset is not
    /// live or the file is missing. The caller disposes the stream.
    /// </summary>
    public async Task<ArtworkContent?> OpenAsync(Guid id, int? size, CancellationToken cancellationToken)
    {
        if (await FindLiveAsync(id, cancellationToken).ConfigureAwait(false) is not { } asset)
        {
            return null;
        }

        var served = size is { } requested ? ArtworkRules.ServedSize(requested, asset.ThumbnailSizes) : null;
        var (path, mediaType, tag) = served is { } thumbnail
            ? (ArtworkPaths.Thumbnail(asset.ContentHash, thumbnail), ArtworkRules.MediaType(ArtworkFormat.Webp), $"{asset.ContentHash}-{thumbnail}")
            : (ArtworkPaths.Original(asset.ContentHash, asset.Format), ArtworkRules.MediaType(asset.Format), asset.ContentHash);
        return storage.OpenRead(path) is { } stream ? new ArtworkContent(stream, mediaType, tag) : null;
    }

    /// <summary>
    /// Whether <paramref name="asset"/> may be served and its files must be kept: a live record
    /// attaches it, or it was uploaded within <see cref="UnattachedLifetime"/>.
    /// </summary>
    public async Task<bool> IsLiveAsync(Asset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        return time.GetUtcNow() < asset.UploadedUtc + UnattachedLifetime
            || await IsAttachedAsync(asset.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes every asset last uploaded <see cref="UnattachedLifetime"/> ago or more that no live
    /// record attaches and no unpruned retention group lists a file of, with its files. Each asset is
    /// decided and removed in a transaction of its own, so an upload of the same bytes either comes
    /// first (and keeps it) or after (and stores it afresh). Also clears unfinished staging files.
    /// </summary>
    public async Task<ArtworkSweepSummary> SweepAsync(CancellationToken cancellationToken)
    {
        storage.RemoveStaleStaging(StagingLifetime);

        var cutoff = time.GetUtcNow() - UnattachedLifetime;
        int removed = 0, failed = 0;
        foreach (var candidate in await assets.UploadedAtOrBeforeAsync(cutoff, cancellationToken).ConfigureAwait(false))
        {
            switch (await transaction.RunAsync(token => RemoveIfUnusedAsync(candidate.Id, cutoff, token), cancellationToken).ConfigureAwait(false))
            {
                case SweepResult.Removed:
                    storage.RemoveEmptyFolders(ArtworkPaths.Folder(candidate.ContentHash));
                    removed++;
                    break;
                case SweepResult.Failed:
                    failed++;
                    break;
                default:
                    break;
            }
        }

        return new ArtworkSweepSummary(removed, failed);
    }

    private async Task<SweepResult> RemoveIfUnusedAsync(Guid id, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // Read again inside the transaction: an upload may have restarted the clock since the list.
        if (await assets.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } asset
            || asset.UploadedUtc > cutoff
            || await IsAttachedAsync(asset.Id, cancellationToken).ConfigureAwait(false))
        {
            return SweepResult.Kept;
        }

        var paths = ArtworkPaths.Files(asset);
        foreach (var path in paths)
        {
            if (await retention.IsFileRetainedAsync(path, cancellationToken).ConfigureAwait(false))
            {
                return SweepResult.Kept;
            }
        }

        // Files first: a failure leaves the row, and its files, for the next sweep to try again.
        foreach (var path in paths)
        {
            if (files.Delete(path, out _) == ManagedFileDeletion.Failed)
            {
                return SweepResult.Failed;
            }
        }

        await assets.DeleteAsync(asset.Id, cancellationToken).ConfigureAwait(false);
        return SweepResult.Removed;
    }

    private async Task<bool> IsAttachedAsync(Guid assetId, CancellationToken cancellationToken)
    {
        foreach (var owners in attachments)
        {
            if (await owners.IsAttachedAsync(assetId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task WriteMissingFilesAsync(Asset asset, ReadOnlyMemory<byte> content, ArtworkDecoding.Decoded decoded, CancellationToken cancellationToken)
    {
        var original = ArtworkPaths.Original(asset.ContentHash, asset.Format);
        if (!storage.Exists(original))
        {
            await storage.WriteAsync(original, content, cancellationToken).ConfigureAwait(false);
        }

        foreach (var thumbnail in decoded.Thumbnails.Where(thumbnail => asset.ThumbnailSizes.Contains(thumbnail.Size)))
        {
            var path = ArtworkPaths.Thumbnail(asset.ContentHash, thumbnail.Size);
            if (!storage.Exists(path))
            {
                await storage.WriteAsync(path, thumbnail.Content, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private enum SweepResult
    {
        Kept,
        Removed,
        Failed,
    }
}
