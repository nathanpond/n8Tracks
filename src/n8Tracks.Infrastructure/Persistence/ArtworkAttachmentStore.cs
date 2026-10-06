using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Assets;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class ArtworkAttachmentStore(N8TracksDbContext context) : IArtworkAttachmentStore
{
    public Task<bool> IsAttachedAsync(Guid assetId, CancellationToken cancellationToken) =>
        context.ArtworkAttachments.AsNoTracking().AnyAsync(attachment => attachment.AssetId == assetId, cancellationToken);

    public async Task<ArtworkAttachment?> FindAsync(string ownerType, Guid ownerId, CancellationToken cancellationToken)
    {
        var found = await context.ArtworkAttachments.AsNoTracking()
            .SingleOrDefaultAsync(attachment => attachment.OwnerType == ownerType && attachment.OwnerId == ownerId, cancellationToken)
            .ConfigureAwait(false);
        return found is null ? null : ToAttachment(found);
    }

    public async Task AddAsync(ArtworkAttachment attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var record = new ArtworkAttachmentRecord
        {
            Id = attachment.Id,
            OwnerType = attachment.OwnerType,
            OwnerId = attachment.OwnerId,
            AssetId = attachment.AssetId,
            CropX = attachment.Crop?.X,
            CropY = attachment.Crop?.Y,
            CropSize = attachment.Crop?.Size,
            AttachedUtc = UtcText.From(attachment.AttachedUtc),
        };
        context.ArtworkAttachments.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task SetCropAsync(Guid id, ArtworkCrop? crop, CancellationToken cancellationToken) =>
        await context.ArtworkAttachments
            .Where(attachment => attachment.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(attachment => attachment.CropX, crop == null ? null : crop.X)
                    .SetProperty(attachment => attachment.CropY, crop == null ? null : crop.Y)
                    .SetProperty(attachment => attachment.CropSize, crop == null ? null : crop.Size),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ArtworkCrop>> CropsOfAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var found = await context.ArtworkAttachments.AsNoTracking()
            .Where(attachment => attachment.AssetId == assetId && attachment.CropSize != null)
            .Select(attachment => new { attachment.CropX, attachment.CropY, attachment.CropSize })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. found.Select(static crop => new ArtworkCrop(crop.CropX!.Value, crop.CropY!.Value, crop.CropSize!.Value)).Distinct()];
    }

    /// <summary>The artwork of each of <paramref name="ownerIds"/> that has some, by owner ID.</summary>
    internal static async Task<Dictionary<Guid, AttachedArtwork>> ForOwnersAsync(
        N8TracksDbContext context,
        string ownerType,
        IReadOnlyCollection<Guid> ownerIds,
        CancellationToken cancellationToken)
    {
        var ids = ownerIds.ToList();
        var found = await context.ArtworkAttachments.AsNoTracking()
            .Where(attachment => attachment.OwnerType == ownerType && ids.Contains(attachment.OwnerId))
            .Join(context.Assets, attachment => attachment.AssetId, asset => asset.Id, (attachment, asset) => new { Attachment = attachment, asset.Width, asset.Height })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return found.ToDictionary(
            static row => row.Attachment.OwnerId,
            static row => new AttachedArtwork(row.Attachment.AssetId, ToAttachment(row.Attachment).Crop, row.Width, row.Height));
    }

    private static ArtworkAttachment ToAttachment(ArtworkAttachmentRecord record) => new(
        record.Id,
        record.OwnerType,
        record.OwnerId,
        record.AssetId,
        record is { CropX: { } x, CropY: { } y, CropSize: { } size } ? new ArtworkCrop(x, y, size) : null,
        UtcText.Parse(record.AttachedUtc));
}
