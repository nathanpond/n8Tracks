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
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return found.ToDictionary(static attachment => attachment.OwnerId, static attachment => ToAttachment(attachment).Artwork);
    }

    private static ArtworkAttachment ToAttachment(ArtworkAttachmentRecord record) => new(
        record.Id,
        record.OwnerType,
        record.OwnerId,
        record.AssetId,
        record is { CropX: { } x, CropY: { } y, CropSize: { } size } ? new ArtworkCrop(x, y, size) : null,
        UtcText.Parse(record.AttachedUtc));
}
