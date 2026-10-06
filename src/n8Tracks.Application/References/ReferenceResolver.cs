using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.References;

/// <summary>What kind of thing a reference's text is, before anything is looked up.</summary>
public enum ReferenceKind
{
    /// <summary>Not an ID or a complete shortcode: it names nothing.</summary>
    Malformed,

    /// <summary>A stable ID (a hyphenated UUID, any letter case), which may name a Song or a Version.</summary>
    Id,

    /// <summary>A Song shortcode, <c>n8-12</c>.</summary>
    Song,

    /// <summary>A Version shortcode, <c>n8-12-v1.1</c>.</summary>
    Version,

    /// <summary>A Generation shortcode, <c>n8-12-v1.1-g3</c>.</summary>
    Generation,
}

/// <summary>
/// A reference to a Song, a Version, or a Generation as a caller wrote it, read but not looked up: a stable ID
/// (hyphenated, any letter case) or a complete shortcode (any letter case). Anything else is
/// <see cref="ReferenceKind.Malformed"/>, which names nothing; reading never fails, so an endpoint
/// that binds one answers its own not-found for a reference it cannot use rather than a 400.
/// </summary>
public readonly record struct CatalogReference
{
    private CatalogReference(string text, ReferenceKind kind, Guid id, long songShortcodeNumber, VersionNumber? versionNumber, int generationOrdinal = 0)
    {
        Text = text;
        Kind = kind;
        Id = id;
        SongShortcodeNumber = songShortcodeNumber;
        VersionNumber = versionNumber;
        GenerationOrdinal = generationOrdinal;
    }

    /// <summary>The text as written.</summary>
    public string Text { get; }

    public ReferenceKind Kind { get; }

    /// <summary>The ID, for <see cref="ReferenceKind.Id"/>.</summary>
    public Guid Id { get; }

    /// <summary>The Song's shortcode number, for <see cref="ReferenceKind.Song"/>, <see cref="ReferenceKind.Version"/>, and <see cref="ReferenceKind.Generation"/>.</summary>
    public long SongShortcodeNumber { get; }

    /// <summary>The Version's number, for <see cref="ReferenceKind.Version"/> and <see cref="ReferenceKind.Generation"/>.</summary>
    public VersionNumber? VersionNumber { get; }

    /// <summary>The Generation's ordinal, for <see cref="ReferenceKind.Generation"/>.</summary>
    public int GenerationOrdinal { get; }

    /// <summary>Reads <paramref name="text"/> as a reference. Never fails: text that is none is <see cref="ReferenceKind.Malformed"/>.</summary>
    public static CatalogReference Parse(string? text)
    {
        text ??= string.Empty;
        if (Guid.TryParseExact(text, "D", out var id))
        {
            return new(text, ReferenceKind.Id, id, 0, null);
        }

        if (Shortcodes.TryParseSong(text, out var songNumber))
        {
            return new(text, ReferenceKind.Song, Guid.Empty, songNumber, null);
        }

        if (Shortcodes.TryParseVersion(text, out var versionSongNumber, out var number))
        {
            return new(text, ReferenceKind.Version, Guid.Empty, versionSongNumber, number);
        }

        return Shortcodes.TryParseGeneration(text, out var generationSongNumber, out var generationVersion, out var ordinal)
            ? new(text, ReferenceKind.Generation, Guid.Empty, generationSongNumber, generationVersion, ordinal)
            : new(text, ReferenceKind.Malformed, Guid.Empty, 0, null);
    }

    /// <summary>
    /// How route and query values are bound: always true, with <see cref="ReferenceKind.Malformed"/>
    /// for text that is no reference, so the endpoint decides what "not found" means.
    /// </summary>
    public static bool TryParse(string? value, out CatalogReference result)
    {
        result = Parse(value);
        return true;
    }

    public override string ToString() => Text;
}

/// <summary>What a reference names, as the resolve endpoint answers it.</summary>
/// <param name="EntityType"><see cref="ReferenceResolver.SongType"/>, <see cref="ReferenceResolver.VersionType"/>, or <see cref="ReferenceResolver.GenerationType"/>.</param>
/// <param name="Id">Its stable ID.</param>
/// <param name="Shortcode">Its canonical (lower-case) shortcode.</param>
/// <param name="Status">
/// <see cref="ReferenceResolver.ActiveStatus"/> or, for a Version or a Generation, <see cref="ReferenceResolver.ArchivedStatus"/>
/// or <see cref="ReferenceResolver.DeletedStatus"/>; for a Generation's old shortcode, <see cref="ReferenceResolver.MovedStatus"/>.
/// </param>
/// <param name="Song">For a Version or a Generation, its Song; null for a Song.</param>
/// <param name="Version">For a Generation, its Version; null otherwise.</param>
/// <param name="CanonicalShortcode">For a moved Generation's old shortcode, the shortcode it has now (the same as <paramref name="Shortcode"/>); null otherwise.</param>
public sealed record ResolvedReference(
    string EntityType,
    Guid Id,
    string Shortcode,
    string Status,
    ResolvedSong? Song,
    ResolvedVersion? Version = null,
    string? CanonicalShortcode = null);

/// <summary>The Song a resolved Version or Generation belongs to.</summary>
public sealed record ResolvedSong(Guid Id, string Shortcode);

/// <summary>The Version a resolved Generation belongs to.</summary>
public sealed record ResolvedVersion(Guid Id, string Shortcode);

/// <summary>
/// Turns a reference to a Song, a Version, or a Generation (<see cref="CatalogReference"/>) into the
/// thing it names. Shortcodes are worked out from the Song's sequence number, the Version's number,
/// and the Generation's ordinal, so
/// they are resolved by parsing and looking those up; nothing extra is stored. A reference of the
/// wrong kind for what is asked (a Version shortcode where a Song is wanted) names nothing.
/// </summary>
public sealed class ReferenceResolver(ISongStore songs, IVersionStore versions, RetentionService retention, TimeProvider time)
{
    public const string SongType = "song";
    public const string VersionType = "version";
    public const string GenerationType = "generation";
    public const string ActiveStatus = "active";
    public const string ArchivedStatus = "archived";

    /// <summary>
    /// Deleted within its retention period: a Version deleted on its own (#101), or a Song with its
    /// Versions and Generations (#102).
    /// </summary>
    public const string DeletedStatus = "deleted";

    /// <summary>
    /// A Generation's old shortcode, from before it moved to another Song or Version (#123, #141): it
    /// resolves for good to the Generation where it is now, with that place's shortcode as canonical.
    /// </summary>
    public const string MovedStatus = "moved";

    /// <summary>
    /// The Song, Version, or Generation a reference names, whichever it is; null when it names none.
    /// A Version deleted on its own resolves as <see cref="DeletedStatus"/> for its retention period,
    /// by its ID or its shortcode, while its Song is live. A Generation is active or archived (its
    /// user-facing state); its old shortcode, from before a move, is <see cref="MovedStatus"/> while
    /// it is live, resolves as it does by its ID once deleted, and names nothing once it is purged.
    /// </summary>
    public async Task<ResolvedReference?> ResolveAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        await ResolveLiveAsync(reference, cancellationToken).ConfigureAwait(false)
            ?? await ResolveAliasAsync(reference, cancellationToken).ConfigureAwait(false)
            ?? await ResolveDeletedAsync(reference, cancellationToken).ConfigureAwait(false);

    /// <summary>A moved Generation's old shortcode (#123): where the Generation is now, or what its ID resolves to once it is deleted.</summary>
    private async Task<ResolvedReference?> ResolveAliasAsync(CatalogReference reference, CancellationToken cancellationToken)
    {
        if (reference.Kind != ReferenceKind.Generation
            || await versions.FindAliasAsync(reference.Text, cancellationToken).ConfigureAwait(false) is not { GenerationId: { } id })
        {
            return null;
        }

        if (await versions.FindGenerationAsync(id, cancellationToken).ConfigureAwait(false) is { } moved)
        {
            return Of(moved) with { Status = MovedStatus, CanonicalShortcode = moved.Shortcode };
        }

        return await ResolveDeletedAsync(CatalogReference.Parse(id.ToString()), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedReference?> ResolveDeletedAsync(CatalogReference reference, CancellationToken cancellationToken)
    {
        if (await VersionDeletionService.FindDeletedAsync(retention, time, reference, cancellationToken).ConfigureAwait(false) is { } deleted
            && await songs.FindByShortcodeNumberAsync(deleted.SongShortcodeNumber, cancellationToken).ConfigureAwait(false) is { } song)
        {
            return new(VersionType, deleted.Id, deleted.Shortcode, DeletedStatus, new ResolvedSong(song.Id, song.Shortcode));
        }

        return await ResolveInDeletedSongAsync(reference, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A deleted Song (#102), or a Version or Generation of one, within the Song's retention period:
    /// <see cref="DeletedStatus"/>. A Version or Generation is found in the Song's group, or in the
    /// group of its own earlier deletion; either way its Song must be deleted too (a Version deleted on
    /// its own while its Song is live is <see cref="VersionDeletionService.FindDeletedAsync(CatalogReference, CancellationToken)"/>'s).
    /// </summary>
    private async Task<ResolvedReference?> ResolveInDeletedSongAsync(CatalogReference reference, CancellationToken cancellationToken)
    {
        switch (reference.Kind)
        {
            case ReferenceKind.Song:
                return await SongDeletionService.FindDeletedAsync(retention, time, reference, cancellationToken).ConfigureAwait(false) is { } named
                    ? new(SongType, named.Id, named.Shortcode, DeletedStatus, null)
                    : null;

            case ReferenceKind.Id:
                if (await SongDeletionService.FindDeletedAsync(retention, time, reference, cancellationToken).ConfigureAwait(false) is { } byId)
                {
                    return new(SongType, byId.Id, byId.Shortcode, DeletedStatus, null);
                }

                foreach (var recordType in (string[])[RetainedRecordTypes.Version, RetainedRecordTypes.Generation])
                {
                    if (await retention.FindByRecordAsync(recordType, reference.Id, cancellationToken).ConfigureAwait(false) is { } holding
                        && await ItemsOfAsync(holding, cancellationToken).ConfigureAwait(false) is { } items
                        && await SongDeletionService.FindDeletedAsync(retention, time, items.SongShortcodeNumber, cancellationToken).ConfigureAwait(false) is { } song)
                    {
                        return items.Resolve(recordType == RetainedRecordTypes.Version ? VersionType : GenerationType, reference.Id, song);
                    }
                }

                return null;

            case ReferenceKind.Version or ReferenceKind.Generation:
                if (await SongDeletionService.FindDeletedAsync(retention, time, reference.SongShortcodeNumber, cancellationToken).ConfigureAwait(false) is not { } deletedSong)
                {
                    return null;
                }

                var number = reference.VersionNumber!.ToString();
                var groups = new List<RetentionGroup> { deletedSong.Group };
                if (await retention.FindByShortcodeAsync(Shortcodes.ForVersion(reference.SongShortcodeNumber, number), cancellationToken).ConfigureAwait(false) is { Kind: RetainedRecordTypes.Version } alone
                    && alone.PruneAfterUtc > time.GetUtcNow())
                {
                    groups.Add(alone);
                }

                foreach (var group in groups)
                {
                    if (await ItemsOfAsync(group, cancellationToken).ConfigureAwait(false) is not { } items
                        || items.Versions.FirstOrDefault(version => version.Number == number) is not { } version)
                    {
                        continue;
                    }

                    if (reference.Kind == ReferenceKind.Version)
                    {
                        return items.Resolve(VersionType, version.Id, deletedSong);
                    }

                    if (items.Generations.FirstOrDefault(generation => generation.VersionId == version.Id && generation.Ordinal == reference.GenerationOrdinal) is { } generation)
                    {
                        return items.Resolve(GenerationType, generation.Id, deletedSong);
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// The Versions and Generations a group of a deleted Song or Version holds, with the Song's
    /// shortcode number; null when it is neither, or has expired.
    /// </summary>
    private async Task<DeletedItems?> ItemsOfAsync(RetentionGroup group, CancellationToken cancellationToken)
    {
        if (group.PruneAfterUtc <= time.GetUtcNow())
        {
            return null;
        }

        long songNumber;
        if (group.Kind == RetainedRecordTypes.Song)
        {
            var song = (await retention.RecordFieldsAsync(group.Id, RetainedRecordTypes.Song, ["shortcode_number"], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            if (song?["shortcode_number"] is not { } text || !long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out songNumber))
            {
                return null;
            }
        }
        else if (group.Kind != RetainedRecordTypes.Version || group.Shortcode is null || !Shortcodes.TryParseVersion(group.Shortcode, out songNumber, out _))
        {
            return null;
        }

        var versionFields = await retention.RecordFieldsAsync(group.Id, RetainedRecordTypes.Version, ["id", "number"], cancellationToken).ConfigureAwait(false);
        var generationFields = await retention.RecordFieldsAsync(group.Id, RetainedRecordTypes.Generation, ["id", "version_id", "ordinal"], cancellationToken).ConfigureAwait(false);
        return new DeletedItems(
            songNumber,
            [.. versionFields.Select(static fields => new DeletedVersionItem(Guid.Parse(fields["id"]!, System.Globalization.CultureInfo.InvariantCulture), fields["number"]!))],
            [.. generationFields.Select(static fields => new DeletedGenerationItem(
                Guid.Parse(fields["id"]!, System.Globalization.CultureInfo.InvariantCulture),
                Guid.Parse(fields["version_id"]!, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(fields["ordinal"]!, System.Globalization.CultureInfo.InvariantCulture)))]);
    }

    private async Task<ResolvedReference?> ResolveLiveAsync(CatalogReference reference, CancellationToken cancellationToken)
    {
        switch (reference.Kind)
        {
            case ReferenceKind.Id:
                if (await songs.FindAsync(reference.Id, cancellationToken).ConfigureAwait(false) is { } song)
                {
                    return Of(song);
                }

                if (await versions.FindSummaryAsync(reference.Id, cancellationToken).ConfigureAwait(false) is { } version)
                {
                    return Of(version);
                }

                return await versions.FindGenerationAsync(reference.Id, cancellationToken).ConfigureAwait(false) is { } generation
                    ? Of(generation)
                    : null;

            case ReferenceKind.Song:
                return await songs.FindByShortcodeNumberAsync(reference.SongShortcodeNumber, cancellationToken).ConfigureAwait(false) is { } named
                    ? Of(named)
                    : null;

            case ReferenceKind.Version:
                return await FindVersionByShortcodeAsync(versions, reference, cancellationToken).ConfigureAwait(false) is { } found
                    ? Of(found)
                    : null;

            case ReferenceKind.Generation:
                return await versions.FindGenerationIdByShortcodeAsync(
                        reference.SongShortcodeNumber,
                        reference.VersionNumber!.ToString(),
                        reference.GenerationOrdinal,
                        cancellationToken).ConfigureAwait(false) is { } generationId
                    && await versions.FindGenerationAsync(generationId, cancellationToken).ConfigureAwait(false) is { } generationNamed
                    ? Of(generationNamed)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// The ID of the Song a reference names. An ID is returned as it is, without looking it up (the
    /// caller's own lookup decides whether there is such a Song); a Song shortcode is looked up; any
    /// other reference names no Song.
    /// </summary>
    public Task<Guid?> SongIdAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        SongIdAsync(songs, reference, cancellationToken);

    /// <summary>
    /// The ID of the Version a reference names. An ID is returned as it is, without looking it up; a
    /// Version shortcode is looked up; any other reference names no Version.
    /// </summary>
    public Task<Guid?> VersionIdAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        VersionIdAsync(versions, reference, cancellationToken);

    /// <summary>The ID of the Song a reference names in <paramref name="store"/>, as <see cref="SongIdAsync(CatalogReference, CancellationToken)"/> reads it.</summary>
    internal static async Task<Guid?> SongIdAsync(ISongStore store, CatalogReference reference, CancellationToken cancellationToken) =>
        reference.Kind switch
        {
            ReferenceKind.Id => reference.Id,
            ReferenceKind.Song => (await store.FindByShortcodeNumberAsync(reference.SongShortcodeNumber, cancellationToken).ConfigureAwait(false))?.Id,
            _ => null,
        };

    /// <summary>The ID of the Version a reference names in <paramref name="store"/>, as <see cref="VersionIdAsync(CatalogReference, CancellationToken)"/> reads it.</summary>
    internal static async Task<Guid?> VersionIdAsync(IVersionStore store, CatalogReference reference, CancellationToken cancellationToken) =>
        reference.Kind switch
        {
            ReferenceKind.Id => reference.Id,
            ReferenceKind.Version => await store.FindIdByShortcodeAsync(reference.SongShortcodeNumber, reference.VersionNumber!.ToString(), cancellationToken).ConfigureAwait(false),
            _ => null,
        };

    private static async Task<VersionSummary?> FindVersionByShortcodeAsync(IVersionStore store, CatalogReference reference, CancellationToken cancellationToken) =>
        await VersionIdAsync(store, reference, cancellationToken).ConfigureAwait(false) is { } id
            ? await store.FindSummaryAsync(id, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>A retained Version, by its ID and number.</summary>
    private sealed record DeletedVersionItem(Guid Id, string Number);

    /// <summary>A retained Generation, by its ID, its Version's ID, and its ordinal.</summary>
    private sealed record DeletedGenerationItem(Guid Id, Guid VersionId, int Ordinal);

    /// <summary>What one group holds of a deleted Song's tree.</summary>
    private sealed record DeletedItems(long SongShortcodeNumber, IReadOnlyList<DeletedVersionItem> Versions, IReadOnlyList<DeletedGenerationItem> Generations)
    {
        /// <summary>The Version or Generation with <paramref name="id"/> as deleted, under <paramref name="song"/>; null when the group does not hold it.</summary>
        public ResolvedReference? Resolve(string entityType, Guid id, DeletedSong song)
        {
            if (entityType == VersionType)
            {
                return Versions.FirstOrDefault(version => version.Id == id) is { } version
                    ? new(VersionType, id, Shortcodes.ForVersion(SongShortcodeNumber, version.Number), DeletedStatus, new ResolvedSong(song.Id, song.Shortcode))
                    : null;
            }

            if (Generations.FirstOrDefault(generation => generation.Id == id) is not { } generation
                || Versions.FirstOrDefault(version => version.Id == generation.VersionId) is not { } parent)
            {
                return null;
            }

            var versionShortcode = Shortcodes.ForVersion(SongShortcodeNumber, parent.Number);
            return new(
                GenerationType,
                id,
                Shortcodes.ForGeneration(SongShortcodeNumber, parent.Number, generation.Ordinal),
                DeletedStatus,
                new ResolvedSong(song.Id, song.Shortcode),
                new ResolvedVersion(parent.Id, versionShortcode));
        }
    }

    private static ResolvedReference Of(SongSummary song) => new(SongType, song.Id, song.Shortcode, ActiveStatus, null);

    private static ResolvedReference Of(VersionSummary version) =>
        new(
            VersionType,
            version.Id,
            version.Shortcode,
            version.Archived ? ArchivedStatus : ActiveStatus,
            new ResolvedSong(version.SongId, Shortcodes.ForSong(version.SongShortcodeNumber)));

    private static ResolvedReference Of(GenerationSummary generation) =>
        new(
            GenerationType,
            generation.Generation.Id,
            generation.Shortcode,
            generation.Generation.State == GenerationState.Archived ? ArchivedStatus : ActiveStatus,
            new ResolvedSong(generation.Generation.SongId, Shortcodes.ForSong(generation.SongShortcodeNumber)),
            new ResolvedVersion(generation.Generation.VersionId, generation.VersionShortcode));
}
