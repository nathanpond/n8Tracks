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
}

/// <summary>
/// A reference to a Song or a Version as a caller wrote it, read but not looked up: a stable ID
/// (hyphenated, any letter case) or a complete shortcode (any letter case). Anything else is
/// <see cref="ReferenceKind.Malformed"/>, which names nothing; reading never fails, so an endpoint
/// that binds one answers its own not-found for a reference it cannot use rather than a 400.
/// </summary>
public readonly record struct CatalogReference
{
    private CatalogReference(string text, ReferenceKind kind, Guid id, long songShortcodeNumber, VersionNumber? versionNumber)
    {
        Text = text;
        Kind = kind;
        Id = id;
        SongShortcodeNumber = songShortcodeNumber;
        VersionNumber = versionNumber;
    }

    /// <summary>The text as written.</summary>
    public string Text { get; }

    public ReferenceKind Kind { get; }

    /// <summary>The ID, for <see cref="ReferenceKind.Id"/>.</summary>
    public Guid Id { get; }

    /// <summary>The Song's shortcode number, for <see cref="ReferenceKind.Song"/> and <see cref="ReferenceKind.Version"/>.</summary>
    public long SongShortcodeNumber { get; }

    /// <summary>The Version's number, for <see cref="ReferenceKind.Version"/>.</summary>
    public VersionNumber? VersionNumber { get; }

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

        return Shortcodes.TryParseVersion(text, out var versionSongNumber, out var number)
            ? new(text, ReferenceKind.Version, Guid.Empty, versionSongNumber, number)
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
/// <param name="EntityType"><see cref="ReferenceResolver.SongType"/> or <see cref="ReferenceResolver.VersionType"/>.</param>
/// <param name="Id">Its stable ID.</param>
/// <param name="Shortcode">Its canonical (lower-case) shortcode.</param>
/// <param name="Status"><see cref="ReferenceResolver.ActiveStatus"/> or, for a Version, <see cref="ReferenceResolver.ArchivedStatus"/>.</param>
/// <param name="Song">For a Version, its Song; null for a Song.</param>
public sealed record ResolvedReference(string EntityType, Guid Id, string Shortcode, string Status, ResolvedSong? Song);

/// <summary>The Song a resolved Version belongs to.</summary>
public sealed record ResolvedSong(Guid Id, string Shortcode);

/// <summary>
/// Turns a reference to a Song or a Version (<see cref="CatalogReference"/>) into the thing it
/// names. Shortcodes are worked out from the Song's sequence number and the Version's number, so
/// they are resolved by parsing and looking those up; nothing extra is stored. A reference of the
/// wrong kind for what is asked (a Version shortcode where a Song is wanted) names nothing.
/// </summary>
public sealed class ReferenceResolver(ISongStore songs, IVersionStore versions)
{
    public const string SongType = "song";
    public const string VersionType = "version";
    public const string ActiveStatus = "active";
    public const string ArchivedStatus = "archived";

    /// <summary>
    /// The Song or Version a reference names, whichever it is; null when it names neither. Deleted and
    /// moved statuses, and Generation shortcodes, come with later milestones.
    /// </summary>
    public async Task<ResolvedReference?> ResolveAsync(CatalogReference reference, CancellationToken cancellationToken)
    {
        switch (reference.Kind)
        {
            case ReferenceKind.Id:
                if (await songs.FindAsync(reference.Id, cancellationToken).ConfigureAwait(false) is { } song)
                {
                    return Of(song);
                }

                return await versions.FindSummaryAsync(reference.Id, cancellationToken).ConfigureAwait(false) is { } version
                    ? Of(version)
                    : null;

            case ReferenceKind.Song:
                return await songs.FindByShortcodeNumberAsync(reference.SongShortcodeNumber, cancellationToken).ConfigureAwait(false) is { } named
                    ? Of(named)
                    : null;

            case ReferenceKind.Version:
                return await FindVersionByShortcodeAsync(versions, reference, cancellationToken).ConfigureAwait(false) is { } found
                    ? Of(found)
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

    private static ResolvedReference Of(SongSummary song) => new(SongType, song.Id, song.Shortcode, ActiveStatus, null);

    private static ResolvedReference Of(VersionSummary version) =>
        new(
            VersionType,
            version.Id,
            version.Shortcode,
            version.Archived ? ArchivedStatus : ActiveStatus,
            new ResolvedSong(version.SongId, Shortcodes.ForSong(version.SongShortcodeNumber)));
}
