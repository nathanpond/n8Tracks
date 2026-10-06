namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>artwork_attachments</c>: the asset an owner shows as its artwork, and the square
/// crop it set on it. One per owner, for every owner type; the owner is named by type and ID, not by
/// a foreign key, so the deletion of an owner names its attachment itself.
/// </summary>
public sealed class ArtworkAttachmentRecord
{
    public required Guid Id { get; set; }

    /// <summary>One of <see cref="Domain.Assets.ArtworkOwnerTypes"/>.</summary>
    public required string OwnerType { get; set; }

    public required Guid OwnerId { get; set; }

    /// <summary>The asset (RESTRICT: an attached asset's row cannot be removed).</summary>
    public required Guid AssetId { get; set; }

    /// <summary>The crop's left edge in pixels of the original; null, with the other two, for the centred square.</summary>
    public int? CropX { get; set; }

    /// <summary>The crop's top edge in pixels of the original; null with the other two.</summary>
    public int? CropY { get; set; }

    /// <summary>The crop's side in pixels of the original; null with the other two.</summary>
    public int? CropSize { get; set; }

    /// <summary>UTC, ISO 8601, millisecond precision.</summary>
    public required string AttachedUtc { get; set; }
}
