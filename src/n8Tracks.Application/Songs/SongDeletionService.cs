using System.Globalization;
using n8Tracks.Application.Artwork;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Retention;
using n8Tracks.Domain.Assets;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>What deleting a Song would take with it, as its warning states it.</summary>
/// <param name="Song">The Song, as it is now.</param>
/// <param name="Counts">What goes with it, and what it is taken out of.</param>
/// <param name="TitleRequired">Whether the user must type its title to confirm (<see cref="SongDeletionRules.TitleRequired"/>).</param>
public sealed record SongDeletionImpact(SongSummary Song, SongDeletionCounts Counts, bool TitleRequired);

/// <summary>A Song deleted within its retention period: how a read of it says it was deleted.</summary>
/// <param name="Id">Its ID.</param>
/// <param name="ShortcodeNumber">The <c>n</c> of its shortcode, which is never given out again.</param>
/// <param name="Title">Its title when it was deleted.</param>
/// <param name="DeletedUtc">When it was deleted.</param>
/// <param name="Group">The retention group it is in.</param>
public sealed record DeletedSong(Guid Id, long ShortcodeNumber, string Title, DateTimeOffset DeletedUtc, RetentionGroup Group)
{
    public string Shortcode => Shortcodes.ForSong(ShortcodeNumber);
}

/// <summary>How asking what deleting a Song would do ended.</summary>
public abstract record SongDeletionImpactOutcome
{
    private SongDeletionImpactOutcome()
    {
    }

    /// <summary>What deleting it would do now.</summary>
    public sealed record Found(SongDeletionImpact Impact) : SongDeletionImpactOutcome;

    /// <summary>There is no such Song.</summary>
    public sealed record NotFound : SongDeletionImpactOutcome;
}

/// <summary>How deleting a Song ended.</summary>
public abstract record SongDeleteOutcome
{
    private SongDeleteOutcome()
    {
    }

    /// <summary>The Song and everything of its own are in the group of <paramref name="Song"/>.</summary>
    public sealed record Deleted(DeletedSong Song) : SongDeleteOutcome;

    /// <summary>There is no such Song. Nothing was changed.</summary>
    public sealed record NotFound : SongDeleteOutcome;

    /// <summary>The Song is at another revision than the one read. Nothing was changed.</summary>
    public sealed record Conflict(SongSummary Current) : SongDeleteOutcome;

    /// <summary>
    /// The title is required now and was not sent, or does not match; <paramref name="Impact"/> is
    /// what deleting would do now, so the confirmation can refresh. Nothing was changed.
    /// </summary>
    public sealed record ConfirmationRequired(SongDeletionImpact Impact) : SongDeleteOutcome;
}

/// <summary>
/// Deleting a Song (#102). The Song, its Versions, their Generations, its artwork attachment, and,
/// by the database's own cascades, its editing history, used Version numbers, release links, Genre
/// and Tag assignments, credits, Album and Playlist memberships, and relationships go into retention
/// as one group, under its shortcode, so the shortcode resolves as deleted and is never given to
/// another Song. The Albums, Playlists, and related Songs it leaves keep everything else; their
/// revisions and updated times move, as each shows one Song fewer. Whether the title must be typed is
/// decided here, at delete time, on the Song as it is now. Restoring is the group's
/// (<see cref="RetentionService.RestoreAsync"/>): memberships and relationships come back where the
/// other side still exists, memberships at the end of each Album and Playlist. Items of the Song
/// deleted separately before (a Version, a history entry, replaced artwork) keep their own groups.
/// </summary>
public sealed class SongDeletionService(
    ISongStore songs,
    IVersionStore versions,
    ISongDeletionStore store,
    ArtworkAttachmentService artwork,
    GenerationArtworkService generationArtwork,
    RetentionService retention,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>What deleting the Song with <paramref name="id"/> would do now, or not found.</summary>
    public Task<SongDeletionImpactOutcome> ImpactAsync(Guid id, CancellationToken cancellationToken) =>
        transaction.RunAsync<SongDeletionImpactOutcome>(
            async ct => await ImpactWithinAsync(id, ct).ConfigureAwait(false) is { } impact
                ? new SongDeletionImpactOutcome.Found(impact)
                : new SongDeletionImpactOutcome.NotFound(),
            cancellationToken);

    /// <summary>
    /// Deletes the Song with <paramref name="id"/> if it is still at <paramref name="revision"/> and,
    /// when the Song as it is now needs it, <paramref name="confirmTitle"/> matches its title
    /// (<see cref="SongDeletionRules.Confirms"/>). A stale revision is reported before a missing or
    /// wrong title. See the class summary for what goes and what is touched.
    /// </summary>
    public Task<SongDeleteOutcome> DeleteAsync(Guid id, int revision, string? confirmTitle, CancellationToken cancellationToken) =>
        transaction.RunAsync<SongDeleteOutcome>(
            async ct =>
            {
                if (await ImpactWithinAsync(id, ct).ConfigureAwait(false) is not { } impact)
                {
                    return new SongDeleteOutcome.NotFound();
                }

                var song = impact.Song;
                if (song.Revision != revision)
                {
                    return new SongDeleteOutcome.Conflict(song);
                }

                if (impact.TitleRequired && !SongDeletionRules.Confirms(confirmTitle, song.Title))
                {
                    return new SongDeleteOutcome.ConfirmationRequired(impact);
                }

                var all = await versions.ListAsync(id, ct).ConfigureAwait(false);
                var generations = await store.GenerationIdsAsync(id, ct).ConfigureAwait(false);
                var (artworkRoot, ownFiles) = await artwork.RetentionOfAsync(ArtworkOwnerTypes.Song, id, ct).ConfigureAwait(false);

                // The Generations' images (#121) are kept with the group too; the Song's own artwork
                // may be a copy of one of them, so each file is listed once.
                string[] files = [.. ownFiles.Concat(await generationArtwork.RetainedFilesAsync(generations, ct).ConfigureAwait(false)).Distinct(StringComparer.Ordinal)];
                // Parents first: the Song, its Versions, their Generations (both refer to the Song and
                // their Versions without cascading), then the artwork, which no key ties to the Song.
                List<RetainedRoot> roots =
                [
                    new(RetainedRecordTypes.Song, id),
                    .. all.Select(static version => new RetainedRoot(RetainedRecordTypes.Version, version.Id)),
                    .. generations.Select(static generation => new RetainedRoot(RetainedRecordTypes.Generation, generation)),
                ];
                if (artworkRoot is not null)
                {
                    roots.Add(artworkRoot);
                }

                // Sources of other Songs' Versions that point at these Generations keep their Suno IDs (#122).
                await versions.RewriteSourcesOfDeletedGenerationsAsync(generations, [.. all.Select(static version => version.Id)], time.GetUtcNow(), ct).ConfigureAwait(false);
                var group = await retention.RetainWithinAsync(
                    new RetentionRequest(RetainedRecordTypes.Song, Label(song.Shortcode, song.Title), song.Shortcode, roots, files),
                    ct).ConfigureAwait(false);

                await store.TouchAsync(
                    [.. song.Albums.Select(static album => album.AlbumId).Distinct()],
                    [.. song.Playlists.Select(static playlist => playlist.Id).Distinct()],
                    [.. song.Relationships.Select(static relation => relation.Song.Id).Where(other => other != id).Distinct()],
                    group.DeletedUtc,
                    ct).ConfigureAwait(false);

                return new SongDeleteOutcome.Deleted(new DeletedSong(id, song.ShortcodeNumber, song.Title, group.DeletedUtc, group));
            },
            cancellationToken);

    /// <summary>
    /// The Song a reference names (its ID or its shortcode) if it was deleted and its retention period
    /// has not passed; null otherwise (live, never existed, deleted too long ago, or another kind of reference).
    /// </summary>
    public Task<DeletedSong?> FindDeletedAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        FindDeletedAsync(retention, time, reference, cancellationToken);

    /// <summary>As <see cref="FindDeletedAsync(CatalogReference, CancellationToken)"/>, reading <paramref name="retention"/>.</summary>
    internal static async Task<DeletedSong?> FindDeletedAsync(RetentionService retention, TimeProvider time, CatalogReference reference, CancellationToken cancellationToken)
    {
        var group = reference.Kind switch
        {
            ReferenceKind.Id => await retention.FindByRecordAsync(RetainedRecordTypes.Song, reference.Id, cancellationToken).ConfigureAwait(false),
            ReferenceKind.Song => await retention.FindByShortcodeAsync(Shortcodes.ForSong(reference.SongShortcodeNumber), cancellationToken).ConfigureAwait(false),
            _ => null,
        };

        return group is null ? null : await DeletedSongOfAsync(retention, time, group, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The deleted Song whose shortcode is <c>n8-<paramref name="shortcodeNumber"/></c>, within its retention period; null otherwise.</summary>
    internal static async Task<DeletedSong?> FindDeletedAsync(RetentionService retention, TimeProvider time, long shortcodeNumber, CancellationToken cancellationToken) =>
        await retention.FindByShortcodeAsync(Shortcodes.ForSong(shortcodeNumber), cancellationToken).ConfigureAwait(false) is { } group
            ? await DeletedSongOfAsync(retention, time, group, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>How the recovery listing names a deleted Song: "Song n8-4 (Title)".</summary>
    internal static string Label(string shortcode, string title) => $"Song {shortcode} ({title})";

    private static async Task<DeletedSong?> DeletedSongOfAsync(RetentionService retention, TimeProvider time, RetentionGroup group, CancellationToken cancellationToken)
    {
        if (group is not { Kind: RetainedRecordTypes.Song } || group.PruneAfterUtc <= time.GetUtcNow())
        {
            return null;
        }

        var fields = await retention.RecordFieldsAsync(group.Id, RetainedRecordTypes.Song, ["id", "shortcode_number", "title"], cancellationToken).ConfigureAwait(false);
        if (fields.FirstOrDefault() is not { } song
            || !Guid.TryParse(song["id"], CultureInfo.InvariantCulture, out var id)
            || !long.TryParse(song["shortcode_number"], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return new DeletedSong(id, number, song["title"] ?? string.Empty, group.DeletedUtc, group);
    }

    private async Task<SongDeletionImpact?> ImpactWithinAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await songs.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } song)
        {
            return null;
        }

        var generations = await store.GenerationIdsAsync(id, cancellationToken).ConfigureAwait(false);
        var counts = new SongDeletionCounts(
            song.VersionCount,
            generations.Count,
            song.Artwork is null ? 0 : 1,
            song.Albums.Count,
            song.Playlists.Count,
            song.Relationships.Count,

            // Local audio files arrive in M5; until then a Song has none.
            AudioFiles: 0);
        return new SongDeletionImpact(song, counts, SongDeletionRules.TitleRequired(counts));
    }
}
