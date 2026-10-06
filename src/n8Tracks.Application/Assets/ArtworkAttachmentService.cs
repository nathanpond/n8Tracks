using n8Tracks.Application.Retention;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Application.Assets;

/// <summary>The crop field of an owner's edit: left as it is when not sent; when sent, the crop, or null for the centred square.</summary>
public readonly record struct ArtworkCropEdit(bool IsSent, ArtworkCrop? Value)
{
    /// <summary>An edit that sets the crop to <paramref name="crop"/>.</summary>
    public static ArtworkCropEdit Of(ArtworkCrop? crop) => new(IsSent: true, crop);
}

/// <summary>
/// The artwork fields of an owner's edit, as sent: <paramref name="AssetSent"/> says whether
/// <c>artworkAssetId</c> was (<paramref name="AssetId"/> is then the unread text of an asset's ID,
/// or null to remove the artwork), and <paramref name="Crop"/> is <c>artworkCrop</c>.
/// </summary>
public readonly record struct OwnerArtworkEdit(bool AssetSent, string? AssetId, ArtworkCropEdit Crop)
{
    /// <summary>Whether either field was sent; a credential then also needs <c>artwork.write</c>.</summary>
    public bool IsSent => AssetSent || Crop.IsSent;
}

/// <summary>
/// What an owner's artwork edit changes, once checked against the owner's artwork now: the asset
/// (when <paramref name="AssetChanges"/>, <paramref name="AssetId"/> replaces it; null removes it),
/// or else only the crop (when <paramref name="CropChanges"/>); <paramref name="Crop"/> is the crop
/// to set either way.
/// </summary>
internal sealed record OwnerArtworkChange(bool AssetChanges, Guid? AssetId, bool CropChanges, ArtworkCrop? Crop)
{
    /// <summary>The change that changes nothing.</summary>
    public static OwnerArtworkChange None { get; } = new(AssetChanges: false, null, CropChanges: false, null);

    /// <summary>Whether the owner's artwork changes, and so its revision with it.</summary>
    public bool Changes => AssetChanges || CropChanges;
}

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

    /// <summary>The edit field every owner takes its artwork's square crop in (null for the centred square).</summary>
    public const string CropField = "artworkCrop";

    /// <summary>The error for a crop sent when the owner has, and is given, no artwork.</summary>
    public const string NothingToCropMessage = "There is no artwork to crop. Upload an image first.";

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
    /// What is wrong with <paramref name="crop"/> on the asset <paramref name="assetId"/>
    /// (<see cref="ArtworkCropRules.Errors"/> against its oriented dimensions), or null when it may be set.
    /// </summary>
    internal async Task<string?> CropErrorAsync(Guid assetId, ArtworkCrop crop, CancellationToken cancellationToken) =>
        await assets.FindAsync(assetId, cancellationToken).ConfigureAwait(false) is { } asset
            ? ArtworkCropRules.Errors(crop, asset.Width, asset.Height).FirstOrDefault()
            : NothingToCropMessage;

    /// <summary>
    /// Inside the caller's transaction, once its revision check has passed: what
    /// <paramref name="edit"/> changes of the owner's artwork, <paramref name="current"/> (null for
    /// none), or the errors keyed by field: an asset that is not live (<see cref="ReadAssetAsync"/>),
    /// or a crop that does not fit the artwork it applies to (<see cref="CropErrorAsync"/>). Sending
    /// the asset the owner has already keeps its crop unless a crop is sent; replacing the asset
    /// without a crop gives the centred square. The owner applies the change with
    /// <see cref="ApplyAsync"/> once its own write has raised its revision.
    /// </summary>
    internal async Task<(OwnerArtworkChange Change, Dictionary<string, string[]>? Errors)> CheckAsync(
        OwnerArtworkEdit edit,
        AttachedArtwork? current,
        CancellationToken cancellationToken)
    {
        if (!edit.IsSent)
        {
            return (OwnerArtworkChange.None, null);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Guid? assetId = current?.AssetId;
        if (edit.AssetSent)
        {
            var (read, error) = await ReadAssetAsync(edit.AssetId, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                errors[AssetIdField] = [error];
            }

            assetId = read;
        }

        var assetChanges = edit.AssetSent && errors.Count == 0 && assetId != current?.AssetId;
        if (errors.Count == 0
            && edit.Crop is { IsSent: true, Value: { } crop }
            && (assetId is { } cropped
                ? await CropErrorAsync(cropped, crop, cancellationToken).ConfigureAwait(false)
                : NothingToCropMessage) is { } cropError)
        {
            errors[CropField] = [cropError];
        }

        if (errors.Count > 0)
        {
            return (OwnerArtworkChange.None, errors);
        }

        var cropChanges = !assetChanges && edit.Crop.IsSent && current is not null && edit.Crop.Value != current.Crop;
        return (new OwnerArtworkChange(assetChanges, assetId, cropChanges, edit.Crop.Value), null);
    }

    /// <summary>
    /// Inside the caller's transaction, once its revision has been raised: applies a change
    /// <see cref="CheckAsync"/> found, replacing (<see cref="ReplaceAsync"/>) or re-cropping
    /// (<see cref="SetCropAsync"/>) the owner's artwork. A change that changes nothing does nothing.
    /// </summary>
    internal async Task ApplyAsync(string ownerType, Guid ownerId, string ownerLabel, OwnerArtworkChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (change.AssetChanges)
        {
            await ReplaceAsync(ownerType, ownerId, ownerLabel, change.AssetId, change.Crop, cancellationToken).ConfigureAwait(false);
        }
        else if (change.CropChanges)
        {
            await SetCropAsync(ownerType, ownerId, change.Crop, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What an owner's deletion retains of its artwork: its attachment as a root (there is no foreign
    /// key from the attachment to its owner, so nothing else would), and the asset's files, which the
    /// group then keeps at least until it is pruned. No root and no files when it has none.
    /// </summary>
    internal async Task<(RetainedRoot? Root, IReadOnlyList<string> Files)> RetentionOfAsync(string ownerType, Guid ownerId, CancellationToken cancellationToken)
    {
        if (await attachments.FindAsync(ownerType, ownerId, cancellationToken).ConfigureAwait(false) is not { } attachment)
        {
            return (null, []);
        }

        var asset = await assets.FindAsync(attachment.AssetId, cancellationToken).ConfigureAwait(false);
        return (new RetainedRoot(RetainedRecordTypes.ArtworkAttachment, attachment.Id), asset is null ? [] : ArtworkPaths.Files(asset));
    }

    /// <summary>
    /// Inside the caller's transaction: makes <paramref name="assetId"/> the owner's artwork with
    /// <paramref name="crop"/> (null for the centred square), or removes it (null).
    /// The attachment it had goes into retention, labelled "Artwork of
    /// <paramref name="ownerLabel"/>", with the old asset's files. The same asset again changes
    /// nothing. The caller has checked the asset with <see cref="ReadAssetAsync"/>, and the crop with
    /// <see cref="CropErrorAsync"/>, in this transaction.
    /// </summary>
    internal async Task ReplaceAsync(
        string ownerType,
        Guid ownerId,
        string ownerLabel,
        Guid? assetId,
        ArtworkCrop? crop,
        CancellationToken cancellationToken)
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

            if (old is not null && current.Crop is { } oldCrop)
            {
                await ForgetUnusedCropAsync(old, oldCrop, cancellationToken).ConfigureAwait(false);
            }
        }

        if (assetId is { } id)
        {
            var now = time.GetUtcNow();
            await attachments.AddAsync(new ArtworkAttachment(Guid.CreateVersion7(now), ownerType, ownerId, id, crop, now), cancellationToken).ConfigureAwait(false);
            if (crop is not null && await assets.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } asset)
            {
                await artwork.WriteCropAsync(asset, crop, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Inside the caller's transaction, once its revision check has passed: sets the crop of the
    /// owner's artwork (null for the centred square), and makes its square thumbnails from the
    /// original, which is only read. The previous crop's thumbnails go unless another owner sets the
    /// same crop on the asset. The owner has artwork, and the caller has checked the crop with
    /// <see cref="CropErrorAsync"/> in this transaction.
    /// </summary>
    internal async Task SetCropAsync(string ownerType, Guid ownerId, ArtworkCrop? crop, CancellationToken cancellationToken)
    {
        var current = await attachments.FindAsync(ownerType, ownerId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Only artwork that is attached can be cropped.");
        if (current.Crop == crop)
        {
            return;
        }

        await attachments.SetCropAsync(current.Id, crop, cancellationToken).ConfigureAwait(false);
        if (await assets.FindAsync(current.AssetId, cancellationToken).ConfigureAwait(false) is not { } asset)
        {
            return;
        }

        // A thumbnail that cannot be made now (its original is gone) is tried again when it is asked for.
        if (crop is not null)
        {
            await artwork.WriteCropAsync(asset, crop, cancellationToken).ConfigureAwait(false);
        }

        if (current.Crop is { } previous)
        {
            await ForgetUnusedCropAsync(asset, previous, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The square thumbnail of the crop whose <see cref="ArtworkCropRules.Key"/> is
    /// <paramref name="cropKey"/> on the asset, served for <paramref name="size"/>: only while the
    /// asset is live and a live attachment sets that crop on it, and made again from the original
    /// when it is missing. Null otherwise. The caller disposes the stream.
    /// </summary>
    public async Task<ArtworkContent?> OpenCropAsync(Guid assetId, string cropKey, int size, CancellationToken cancellationToken)
    {
        if (!ArtworkCropRules.IsKey(cropKey) || await artwork.FindLiveAsync(assetId, cancellationToken).ConfigureAwait(false) is not { } asset)
        {
            return null;
        }

        var crops = await attachments.CropsOfAsync(assetId, cancellationToken).ConfigureAwait(false);
        return crops.FirstOrDefault(crop => ArtworkCropRules.Key(crop) == cropKey) is { } found
            ? await artwork.OpenCropAsync(asset, found, size, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task ForgetUnusedCropAsync(Asset asset, ArtworkCrop crop, CancellationToken cancellationToken)
    {
        if (!(await attachments.CropsOfAsync(asset.Id, cancellationToken).ConfigureAwait(false)).Contains(crop))
        {
            artwork.ForgetCrop(asset, crop);
        }
    }
}
