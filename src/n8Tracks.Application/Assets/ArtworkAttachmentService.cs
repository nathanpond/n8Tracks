using n8Tracks.Application.Retention;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Application.Assets;

/// <summary>
/// Attaching artwork to an owner, as part of that owner's own edit: each owner's service checks the
/// asset ID its edit sends with <see cref="ReadAssetAsync"/>, and inside its transaction, once its
/// revision check has passed, calls <see cref="ReplaceAsync"/>. A replaced or removed attachment
/// goes into retention with the asset's files listed, so the files outlive it by the retention
/// period at least, and longer while anything live still uses the asset. Its methods take only IDs,
/// never an owner's own types.
/// </summary>
public sealed class ArtworkAttachmentService(
    IArtworkAttachmentStore attachments,
    IAssetStore assets,
    ArtworkService artwork,
    RetentionService retention,
    TimeProvider time)
{
    /// <summary>The edit field every owner takes the artwork in, as the API spells it.</summary>
    public const string AssetIdField = "artworkAssetId";

    /// <summary>
    /// The asset an edit names: null for no artwork (the field sent as null), or the ID of an asset
    /// that is live (attached somewhere, or uploaded within a day). Anything else is an error message:
    /// text that is not an ID, an asset that never was, or one removed since.
    /// </summary>
    internal async Task<(Guid? AssetId, string? Error)> ReadAssetAsync(string? text, CancellationToken cancellationToken)
    {
        if (text is null)
        {
            return (null, null);
        }

        return Guid.TryParseExact(text, "D", out var id) && await artwork.FindLiveAsync(id, cancellationToken).ConfigureAwait(false) is not null
            ? (id, null)
            : (null, "There is no such artwork, or it was removed. Upload the image again.");
    }

    /// <summary>
    /// Inside the caller's transaction: makes <paramref name="assetId"/> the owner's artwork, or
    /// removes it (null). The attachment it had goes into retention, labelled "Artwork of
    /// <paramref name="ownerLabel"/>", with the old asset's files. The same asset again changes
    /// nothing. The caller has checked the asset with <see cref="ReadAssetAsync"/> in this transaction.
    /// </summary>
    internal async Task ReplaceAsync(string ownerType, Guid ownerId, string ownerLabel, Guid? assetId, CancellationToken cancellationToken)
    {
        var current = await attachments.FindAsync(ownerType, ownerId, cancellationToken).ConfigureAwait(false);
        if (current?.AssetId == assetId)
        {
            return;
        }

        if (current is not null)
        {
            var old = await assets.FindAsync(current.AssetId, cancellationToken).ConfigureAwait(false);
            await retention.RetainWithinAsync(
                    new RetentionRequest(
                        RetainedRecordTypes.ArtworkAttachment,
                        $"Artwork of {ownerLabel}",
                        Shortcode: null,
                        [new RetainedRoot(RetainedRecordTypes.ArtworkAttachment, current.Id)],
                        old is null ? [] : ArtworkPaths.Files(old)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (assetId is { } id)
        {
            var now = time.GetUtcNow();
            await attachments.AddAsync(new ArtworkAttachment(Guid.CreateVersion7(now), ownerType, ownerId, id, Crop: null, now), cancellationToken).ConfigureAwait(false);
        }
    }
}
