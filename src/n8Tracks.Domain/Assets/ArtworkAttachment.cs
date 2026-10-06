namespace n8Tracks.Domain.Assets;

/// <summary>
/// The kinds of record artwork can be attached to, as the attachments table names them. A name is
/// stored, so it never changes. Songs have artwork from the Song-artwork story; Albums, Playlists,
/// and Artists from the story that gives them their own.
/// </summary>
public static class ArtworkOwnerTypes
{
    public const string Song = "song";
    public const string Album = "album";
    public const string Playlist = "playlist";
    public const string Artist = "artist";

    /// <summary>Every owner type the attachments table accepts.</summary>
    public static IReadOnlyList<string> All { get; } = [Song, Album, Playlist, Artist];
}

/// <summary>
/// A square crop of an asset's original, in pixels of the original with its orientation applied:
/// the top-left corner and the side. Stored beside the attachment; the original is never changed.
/// </summary>
public sealed record ArtworkCrop(int X, int Y, int Size);

/// <summary>An owner's artwork as the owner shows it: the asset, and the crop the owner set on it (null for the centred square).</summary>
public sealed record AttachedArtwork(Guid AssetId, ArtworkCrop? Crop);

/// <summary>
/// One owner's artwork: the asset attached to it and the crop it set. An owner has at most one;
/// replacing or removing it moves this attachment into retention, and the asset's files stay while
/// any live attachment or unpruned retention group refers to them.
/// </summary>
public sealed record ArtworkAttachment(
    Guid Id,
    string OwnerType,
    Guid OwnerId,
    Guid AssetId,
    ArtworkCrop? Crop,
    DateTimeOffset AttachedUtc)
{
    /// <summary>The artwork as the owner shows it.</summary>
    public AttachedArtwork Artwork => new(AssetId, Crop);
}
