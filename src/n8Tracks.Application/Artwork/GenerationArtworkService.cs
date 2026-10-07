using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Application.Artwork;

/// <summary>How giving a Generation the cover image staged with its export record ended (#140).</summary>
public enum StagedArtworkOutcome
{
    /// <summary>The Generation shows the staged image (now, or it already did).</summary>
    Attached,

    /// <summary>The Generation already had another image, which it keeps: an import never overwrites.</summary>
    Kept,

    /// <summary>The staged image (or its original file) is gone: the Generation is imported without it.</summary>
    ImageMissing,

    /// <summary>The Generation is gone.</summary>
    GenerationMissing,
}

/// <summary>How uploading a Generation's image ended. Only <see cref="Stored"/> may have stored anything.</summary>
public abstract record GenerationArtworkUploadOutcome
{
    private GenerationArtworkUploadOutcome()
    {
    }

    /// <summary>The Generation as it is now, with its image; <paramref name="Changed"/> when the image is new to it.</summary>
    public sealed record Stored(GenerationSummary Generation, bool Changed) : GenerationArtworkUploadOutcome;

    /// <summary>The reference names no live Generation.</summary>
    public sealed record GenerationNotFound : GenerationArtworkUploadOutcome;

    /// <summary>
    /// The Generation has a different image already, and the caller may only give one an image it does
    /// not have (a sync or generation credential: imports never silently overwrite). Nothing was stored.
    /// </summary>
    public sealed record ArtworkExists(GenerationSummary Generation) : GenerationArtworkUploadOutcome;

    /// <summary>The file is not artwork n8Tracks accepts (#97): <paramref name="Upload"/> says why. Nothing was stored.</summary>
    public sealed record Refused(ArtworkUploadOutcome Upload) : GenerationArtworkUploadOutcome;
}

/// <summary>How picking a Generation's image as the Song's artwork ended. Only <see cref="Copied"/> may have stored anything.</summary>
public abstract record GenerationArtworkCopyOutcome
{
    private GenerationArtworkCopyOutcome()
    {
    }

    /// <summary>The Song as it is now: with the image as its own artwork, its revision raised when that changed it.</summary>
    public sealed record Copied(SongSummary Song) : GenerationArtworkCopyOutcome;

    /// <summary>The reference names no live Song.</summary>
    public sealed record SongNotFound : GenerationArtworkCopyOutcome;

    /// <summary>The Generation named names no live Generation.</summary>
    public sealed record GenerationNotFound : GenerationArtworkCopyOutcome;

    /// <summary>The Generation named is another Song's.</summary>
    public sealed record NotInSong(GenerationSummary Generation) : GenerationArtworkCopyOutcome;

    /// <summary>The Generation has no image to pick.</summary>
    public sealed record NoArtwork(GenerationSummary Generation) : GenerationArtworkCopyOutcome;

    /// <summary>The Generation names an image whose original is no longer in the store.</summary>
    public sealed record Unavailable(GenerationSummary Generation) : GenerationArtworkCopyOutcome;

    /// <summary>No Generation was named; errors by field.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : GenerationArtworkCopyOutcome;

    /// <summary>The Song is not at the revision sent: it as it is now.</summary>
    public sealed record Conflict(SongSummary Current) : GenerationArtworkCopyOutcome;
}

/// <summary>
/// The cover image Suno made for each Generation (#121), kept in the managed artwork store, and its
/// use as Song artwork. A Generation holds at most one image, its own: supplied as an upload (the
/// extension does this during import; n8Tracks never fetches it from Suno), validated like any
/// artwork, shown whole (no crop), and replaced in place, the old image leaving the store at once
/// unless something else uses it (it is not retained). Uploading touches no revision; it moves the
/// Song's last-updated time. A Song without artwork of its own shows its Selected Generation's image,
/// worked out when the Song is read; picking a Generation's image makes it the Song's own artwork, an
/// attachment independent of the Generation, which outlives the Generation's image being replaced
/// and the Generation itself. Its methods take a Song or a Generation, so it is a catalog service:
/// it never touches a Version.
/// </summary>
public sealed class GenerationArtworkService(
    GenerationService generations,
    IGenerationStore store,
    ISongStore songs,
    IAssetStore assets,
    ArtworkService artwork,
    ArtworkAttachmentService attachments,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field the Generation is sent in, and its errors are reported under.</summary>
    public const string GenerationField = "generation";

    /// <summary>The problem code (409) for a sync or generation credential's upload over a different image.</summary>
    public const string ArtworkExistsCode = "artwork_exists";

    /// <summary>The problem code (422) for picking from a Generation with no image.</summary>
    public const string NoArtworkCode = "generation_has_no_artwork";

    /// <summary>The problem code (409) for picking an image whose original has vanished.</summary>
    public const string UnavailableCode = "artwork_unavailable";

    /// <summary>
    /// Stores <paramref name="content"/> as the image of the Generation <paramref name="generation"/>
    /// names, in any state. Identical bytes to its image change nothing. When it has a different
    /// image, only a caller that <paramref name="mayReplace"/> (a session, or <c>artwork.write</c>)
    /// replaces it; a sync or generation credential is refused, as an import never overwrites. A file
    /// that is not accepted artwork stores nothing.
    /// </summary>
    public async Task<GenerationArtworkUploadOutcome> UploadAsync(
        CatalogReference generation,
        ReadOnlyMemory<byte> content,
        bool mayReplace,
        CancellationToken cancellationToken)
    {
        if (await generations.FindAsync(generation, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return new GenerationArtworkUploadOutcome.GenerationNotFound();
        }

        // Settled before anything is stored: the same bytes are a no-op for any caller, and a different
        // image is refused to one that may not replace it, so a refusal leaves no upload behind.
        if (found.Artwork is { } held && await assets.FindAsync(held.AssetId, cancellationToken).ConfigureAwait(false) is { } current)
        {
            if (current.ContentHash == ArtworkService.ContentHashOf(content) && artwork.HasOriginal(current))
            {
                return new GenerationArtworkUploadOutcome.Stored(found, Changed: false);
            }

            if (!mayReplace)
            {
                return new GenerationArtworkUploadOutcome.ArtworkExists(found);
            }
        }

        var upload = await artwork.UploadAsync(content, cancellationToken).ConfigureAwait(false);
        if (upload is not ArtworkUploadOutcome.Stored stored)
        {
            return new GenerationArtworkUploadOutcome.Refused(upload);
        }

        return await transaction.RunAsync<GenerationArtworkUploadOutcome>(
                async ct =>
                {
                    // Read again inside the transaction: another upload or a deletion may have come first.
                    if (await store.FindAsync(found.Generation.Id, ct).ConfigureAwait(false) is not { } now)
                    {
                        return new GenerationArtworkUploadOutcome.GenerationNotFound();
                    }

                    var previous = now.Artwork?.AssetId;
                    if (previous == stored.Asset.Id)
                    {
                        return new GenerationArtworkUploadOutcome.Stored(now, Changed: false);
                    }

                    if (previous is not null && !mayReplace)
                    {
                        return new GenerationArtworkUploadOutcome.ArtworkExists(now);
                    }

                    await store.SetArtworkAsync(now.Generation.Id, stored.Asset.Id, ct).ConfigureAwait(false);
                    await store.TouchSongAsync(now.Generation.SongId, time.GetUtcNow(), ct).ConfigureAwait(false);
                    if (previous is { } replaced)
                    {
                        await artwork.RemoveNowIfUnusedAsync(replaced, ct).ConfigureAwait(false);
                    }

                    return new GenerationArtworkUploadOutcome.Stored(
                        await store.FindAsync(now.Generation.Id, ct).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("The Generation just changed cannot be read back."),
                        Changed: true);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the image of the Generation <paramref name="generation"/> names (its ID or shortcode, one
    /// of the Song's) the artwork of the Song <paramref name="song"/> names, given the Song's revision:
    /// the Song's own, independent of the Generation, in one transaction. Artwork the Song had goes
    /// into retention as any replacement does, and the crop is reset to the centred square. Picking the
    /// image the Song already shows as its own, uncropped, changes nothing.
    /// </summary>
    public Task<GenerationArtworkCopyOutcome> CopyToSongAsync(CatalogReference song, string? generation, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<GenerationArtworkCopyOutcome>(
            async ct =>
            {
                if (await ReferenceResolver.SongIdAsync(songs, song, ct).ConfigureAwait(false) is not { } songId
                    || await songs.FindAsync(songId, ct).ConfigureAwait(false) is not { } current)
                {
                    return new GenerationArtworkCopyOutcome.SongNotFound();
                }

                if (string.IsNullOrWhiteSpace(generation))
                {
                    return new GenerationArtworkCopyOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [GenerationField] = ["Name the Generation by its ID or shortcode."],
                    });
                }

                if (await generations.FindAsync(CatalogReference.Parse(generation.Trim()), ct).ConfigureAwait(false) is not { } chosen)
                {
                    return new GenerationArtworkCopyOutcome.GenerationNotFound();
                }

                if (chosen.Generation.SongId != current.Id)
                {
                    return new GenerationArtworkCopyOutcome.NotInSong(chosen);
                }

                if (chosen.Artwork is not { } image)
                {
                    return new GenerationArtworkCopyOutcome.NoArtwork(chosen);
                }

                if (current.Revision != revision)
                {
                    return new GenerationArtworkCopyOutcome.Conflict(current);
                }

                if (await assets.FindAsync(image.AssetId, ct).ConfigureAwait(false) is not { } asset || !artwork.HasOriginal(asset))
                {
                    return new GenerationArtworkCopyOutcome.Unavailable(chosen);
                }

                if (current.Artwork is { } own && own.AssetId == asset.Id)
                {
                    if (own.Crop is null)
                    {
                        return new GenerationArtworkCopyOutcome.Copied(current);
                    }

                    await attachments.SetCropAsync(ArtworkOwnerTypes.Song, current.Id, null, ct).ConfigureAwait(false);
                }
                else
                {
                    await attachments.ReplaceAsync(ArtworkOwnerTypes.Song, current.Id, current.Shortcode, asset.Id, crop: null, ct).ConfigureAwait(false);
                }

                await attachments.TouchOwnerAsync(ArtworkOwnerTypes.Song, current.Id, ct).ConfigureAwait(false);
                return new GenerationArtworkCopyOutcome.Copied(
                    await songs.FindAsync(current.Id, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The Song just changed cannot be read back."));
            },
            cancellationToken);

    /// <summary>
    /// Gives the Generation <paramref name="generationId"/> the cover image staged with its record in a
    /// Suno export (#140, #152), in a transaction of its own after the import's target is committed. The
    /// image is already in the managed store, checked as any upload is when it was staged, so nothing is
    /// written there. As an import never overwrites (invariant 3), a Generation that already shows an
    /// image keeps it; the same image is a no-op. An asset that has gone, or whose original has, is not
    /// attached: the Generation is imported without it.
    /// </summary>
    internal Task<StagedArtworkOutcome> AttachStagedAsync(Guid generationId, Guid assetId, CancellationToken cancellationToken) =>
        transaction.RunAsync(
            async ct =>
            {
                if (await store.FindAsync(generationId, ct).ConfigureAwait(false) is not { } generation)
                {
                    return StagedArtworkOutcome.GenerationMissing;
                }

                if (await assets.FindAsync(assetId, ct).ConfigureAwait(false) is not { } asset || !artwork.HasOriginal(asset))
                {
                    return StagedArtworkOutcome.ImageMissing;
                }

                if (generation.Artwork is { } held)
                {
                    return held.AssetId == assetId ? StagedArtworkOutcome.Attached : StagedArtworkOutcome.Kept;
                }

                await store.SetArtworkAsync(generationId, assetId, ct).ConfigureAwait(false);
                await store.TouchSongAsync(generation.Generation.SongId, time.GetUtcNow(), ct).ConfigureAwait(false);
                return StagedArtworkOutcome.Attached;
            },
            cancellationToken);

    /// <summary>
    /// Replaces the image of the Generation <paramref name="generationId"/> with the cover image staged
    /// with its record (#141), in a transaction of its own: only for a diff whose image address the user
    /// accepted, an explicit choice, so the old image may go (it leaves the store at once unless something
    /// else uses it, as a replaced upload does). The same image is a no-op; an image that has gone is not
    /// attached and the Generation keeps its own.
    /// </summary>
    internal Task<StagedArtworkOutcome> ReplaceWithStagedAsync(Guid generationId, Guid assetId, CancellationToken cancellationToken) =>
        transaction.RunAsync(
            async ct =>
            {
                if (await store.FindAsync(generationId, ct).ConfigureAwait(false) is not { } generation)
                {
                    return StagedArtworkOutcome.GenerationMissing;
                }

                if (await assets.FindAsync(assetId, ct).ConfigureAwait(false) is not { } asset || !artwork.HasOriginal(asset))
                {
                    return StagedArtworkOutcome.ImageMissing;
                }

                var previous = generation.Artwork?.AssetId;
                if (previous == assetId)
                {
                    return StagedArtworkOutcome.Attached;
                }

                await store.SetArtworkAsync(generationId, assetId, ct).ConfigureAwait(false);
                await store.TouchSongAsync(generation.Generation.SongId, time.GetUtcNow(), ct).ConfigureAwait(false);
                if (previous is { } replaced)
                {
                    await artwork.RemoveNowIfUnusedAsync(replaced, ct).ConfigureAwait(false);
                }

                return StagedArtworkOutcome.Attached;
            },
            cancellationToken);

    /// <summary>
    /// Inside the caller's transaction: the files of the images of <paramref name="generationIds"/>,
    /// which a deletion retaining those Generations lists in its group, so the images are kept for as
    /// long as the group is and a restore finds them.
    /// </summary>
    internal async Task<IReadOnlyList<string>> RetainedFilesAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (var assetId in await store.ArtworkAssetIdsAsync(generationIds, cancellationToken).ConfigureAwait(false))
        {
            if (await assets.FindAsync(assetId, cancellationToken).ConfigureAwait(false) is { } asset)
            {
                files.AddRange(ArtworkPaths.Files(asset));
            }
        }

        return files;
    }
}
