using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Retention;

/// <summary>
/// A table whose rows can be retained. What a row holds, its key, its parents (foreign keys), and its
/// unique keys are read from the database itself, so the type declares only what the schema cannot
/// say. Registered as a singleton; a deletion story registers each table its deletion removes.
/// </summary>
/// <param name="RecordType">The name its records are stored under: forever (see <see cref="RetainedRecordTypes"/>).</param>
/// <param name="Table">The live table.</param>
/// <param name="Noun">How messages name one of its rows, for example "history entry".</param>
/// <param name="ShapeVersion">
/// The version of the table's shape (its columns' names, types, and nullability) that this release
/// writes; 1 for the first. A migration that changes the shape bumps it and adds an upgrader.
/// </param>
internal sealed record RetainedType(string RecordType, string Table, string Noun, int ShapeVersion)
{
    /// <summary>
    /// Upgraders by the shape version they upgrade from: the one at <c>n</c> turns a document written
    /// under shape <c>n</c> into shape <c>n + 1</c>. There is one for every version below
    /// <see cref="ShapeVersion"/>, so a record retained before any migration still restores.
    /// </summary>
    public IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> Upgraders { get; init; } = FrozenDictionary<int, Func<JsonObject, JsonObject>>.Empty;

    /// <summary>
    /// A unique column that only orders rows as they were stored (not data anyone sees): restored as
    /// it was when free, otherwise as the next free number, which the restore reports.
    /// </summary>
    public string? StorageOrderColumn { get; init; }

    /// <summary>
    /// Whether a row is left out of a restore, with a note, when something it belongs to is gone (a
    /// membership whose Album was deleted since). When false, the restore is refused instead.
    /// </summary>
    public bool Optional { get; init; }

    /// <summary>
    /// Run inside the restore's transaction before anything of the group is checked or written, with
    /// the row as retained: the values it is to be restored with (a membership moved to the end of its
    /// Album, a workflow state that is gone replaced), or none, to leave it out with the reason given.
    /// The key columns must not change. The checks that follow see the values returned.
    /// </summary>
    public Func<RestoredRow, CancellationToken, Task<RestorePreparation>>? PrepareRestoreAsync { get; init; }

    /// <summary>
    /// Run inside the restore's transaction just before the row is inserted again, with the values it
    /// is inserted with: lets a live-side record that the insert itself writes (a trigger's) make way.
    /// </summary>
    public Func<RestoredRow, CancellationToken, Task>? BeforeRestoreAsync { get; init; }

    /// <summary>
    /// Run inside the restore's transaction after every row of the group is back, once per restored
    /// row of this type: lets a live-side rule apply its own trimming (the 50-entry history cap).
    /// </summary>
    public Func<RestoredRow, CancellationToken, Task>? AfterRestoreAsync { get; init; }

    /// <summary>
    /// Set for a reference type: its records are not rows of <see cref="Table"/> but the value of
    /// these nullable columns (a foreign key that does not cascade) on a live row that stays. A
    /// deletion that names the type in <see cref="RetentionRequest.Referring"/> clears them on each
    /// live row referring to what it deletes and keeps the row's key with the value; a restore sets
    /// them back where that row still exists and they are still empty, and notes it otherwise. A
    /// reference document holds only the key columns and these.
    /// </summary>
    public IReadOnlyList<string>? ReferenceColumns { get; init; }
}

/// <summary>
/// A row a restore is putting back or has just put back, as stored, the context it is written
/// through, when its group was deleted, what the group's deletion deleted (its kind, see
/// <see cref="RetentionRequest.Kind"/>), the time of the restore, and the keys of every row in the group.
/// </summary>
internal sealed record RestoredRow(
    N8TracksDbContext Context,
    IReadOnlyDictionary<string, object?> Values,
    DateTimeOffset GroupDeletedUtc,
    string GroupKind,
    DateTimeOffset RestoredUtc,
    IReadOnlySet<(string Table, string Key)> GroupKeys)
{
    /// <summary>Whether the group being restored holds the row of <paramref name="table"/> whose key is <paramref name="key"/> (as stored).</summary>
    public bool InGroup(string table, string key) => GroupKeys.Contains((table, key));

    /// <summary>A GUID column's value.</summary>
    public Guid GuidOf(string column) => Guid.Parse((string)Values[column]!, CultureInfo.InvariantCulture);

    /// <summary>A text column's value.</summary>
    public string TextOf(string column) => (string)Values[column]!;
}

/// <summary>How a type's <see cref="RetainedType.PrepareRestoreAsync"/> wants a row restored.</summary>
/// <param name="Values">The values to insert; null to leave the row out.</param>
/// <param name="Note">Why it is left out, or what restored differently; null for nothing to say.</param>
internal sealed record RestorePreparation(IReadOnlyDictionary<string, object?>? Values, string? Note)
{
    /// <summary>Restore with <paramref name="values"/>, saying <paramref name="note"/> when not null.</summary>
    public static RestorePreparation With(IReadOnlyDictionary<string, object?> values, string? note = null) => new(values, note);

    /// <summary>Leave the row out, because of <paramref name="reason"/> ("the Playlist is full").</summary>
    public static RestorePreparation LeftOut(string reason) => new(null, reason);
}

/// <summary>The retained types n8Tracks itself registers.</summary>
internal static class RetainedTypes
{
    /// <summary>
    /// An entry of a Version's editing history. <c>sequence</c> only breaks ties between entries
    /// captured in the same millisecond, so it is renumbered when a newer entry took it. A restore into
    /// a Version that already has 50 entries lets the cap trim the oldest, as any new entry would.
    /// </summary>
    public static readonly RetainedType EditorSnapshot = new(RetainedRecordTypes.EditorSnapshot, "editor_revisions", "history entry", ShapeVersion: 1)
    {
        StorageOrderColumn = "sequence",
        AfterRestoreAsync = static (row, cancellationToken) =>
            new EditorRevisionStore(row.Context).PruneAsync(row.GuidOf("version_id"), EditorRevisionService.MaximumKept, cancellationToken),
    };

    /// <summary>
    /// An owner's artwork that was replaced or removed: the owner, the asset, and the crop. Its group
    /// lists the asset's files, so they stay at least until the group is pruned. Restoring it while
    /// the owner has other artwork clashes on the one-per-owner key.
    /// </summary>
    public static readonly RetainedType ArtworkAttachment = new(RetainedRecordTypes.ArtworkAttachment, "artwork_attachments", "artwork", ShapeVersion: 1);

    /// <summary>
    /// A Version, deleted on its own (#101) or with its Song. Its number stays in
    /// <c>used_version_numbers</c> while it is deleted, so it is never offered again; restoring it lets
    /// that row go just before the insert, whose trigger records the number again (a new Version
    /// still cannot take a used number). Once it is back, a blank Version created because it was the
    /// last one is removed if it was never edited (<see cref="VersionRestore"/>).
    /// </summary>
    public static readonly RetainedType Version = new(RetainedRecordTypes.Version, "versions", "Version", ShapeVersion: 1)
    {
        BeforeRestoreAsync = static (row, cancellationToken) => VersionRestore.FreeNumberAsync(row, cancellationToken),
        AfterRestoreAsync = static (row, cancellationToken) => VersionRestore.RemoveAutoCreatedBlankAsync(row, cancellationToken),
    };

    /// <summary>A Generation, deleted with its Version. In V1 it is the minimal record (#69); its own deletion rules are M4's.</summary>
    public static readonly RetainedType Generation = new(RetainedRecordTypes.Generation, "generations", "Generation", ShapeVersion: 1);

    /// <summary>
    /// A Song, deleted with its Versions, Generations, and everything of its own (#102). Its shortcode
    /// number is never given out again (the sequence only goes up). Restored into the workflow state
    /// it was in, or into the first visible one when that state was deleted meanwhile.
    /// </summary>
    public static readonly RetainedType Song = new(RetainedRecordTypes.Song, "songs", "Song", ShapeVersion: 1)
    {
        PrepareRestoreAsync = static (row, cancellationToken) => SongRestore.KeepStateAsync(row, cancellationToken),
    };

    /// <summary>
    /// A number one of a deleted Song's Versions used. Restored after the Versions, whose insert
    /// trigger has already recorded their own numbers: that row makes way, so the result is the same rows.
    /// </summary>
    public static readonly RetainedType UsedVersionNumber = new(RetainedRecordTypes.UsedVersionNumber, "used_version_numbers", "used Version number", ShapeVersion: 1)
    {
        BeforeRestoreAsync = static (row, cancellationToken) => SongRestore.FreeUsedNumberAsync(row, cancellationToken),
    };

    /// <summary>A link of a deleted Song's release details.</summary>
    public static readonly RetainedType SongLink = new(RetainedRecordTypes.SongLink, "song_links", "Song link", ShapeVersion: 1);

    /// <summary>A deleted Song's Genre; left out of a restore when the Genre was deleted meanwhile.</summary>
    public static readonly RetainedType SongGenre = new(RetainedRecordTypes.SongGenre, "song_genres", "Genre assignment", ShapeVersion: 1) { Optional = true };

    /// <summary>A deleted Song's Tag; left out of a restore when the Tag was deleted meanwhile.</summary>
    public static readonly RetainedType SongTag = new(RetainedRecordTypes.SongTag, "song_tags", "Tag assignment", ShapeVersion: 1) { Optional = true };

    /// <summary>
    /// A Song's credit, deleted with the Song or removed with its Artist (#104). With the Song: left
    /// out of a restore when the Artist was deleted meanwhile. With the Artist: put back in its role
    /// and place while the Song still exists and has room there (<see cref="ArtistRestore"/>), raising
    /// the Song's revision; left out, with a note, otherwise.
    /// </summary>
    public static readonly RetainedType SongCredit = new(RetainedRecordTypes.SongCredit, "song_artist_credits", "Artist credit", ShapeVersion: 1)
    {
        Optional = true,
        PrepareRestoreAsync = static (row, cancellationToken) => row.InGroup("songs", row.TextOf("song_id"))
            ? Task.FromResult(RestorePreparation.With(row.Values))
            : ArtistRestore.KeepCreditPlaceAsync(row, cancellationToken),
        AfterRestoreAsync = static (row, cancellationToken) => row.InGroup("songs", row.TextOf("song_id"))
            ? Task.CompletedTask
            : SongRestore.TouchAsync(row, "songs", "song_id", cancellationToken),
    };

    /// <summary>
    /// A Song's place on an Album, deleted with the Song or with the Album. With the Song: restored at
    /// the end of the Album's last disc (the Album may have changed meanwhile), raising the Album's
    /// revision; left out when the Album is gone or its last disc is full. With the Album (#103):
    /// restored where it was, moving the Song's updated time; left out when the Song is gone.
    /// </summary>
    public static readonly RetainedType AlbumTrack = new(RetainedRecordTypes.AlbumTrack, "album_songs", "membership of an Album", ShapeVersion: 1)
    {
        Optional = true,
        PrepareRestoreAsync = static (row, cancellationToken) => row.InGroup("albums", row.TextOf("album_id"))
            ? Task.FromResult(RestorePreparation.With(row.Values))
            : SongRestore.AtAlbumEndAsync(row, cancellationToken),
        AfterRestoreAsync = static (row, cancellationToken) => row.InGroup("albums", row.TextOf("album_id"))
            ? CollectionRestore.TouchSongAsync(row, cancellationToken)
            : SongRestore.TouchAsync(row, "albums", "album_id", cancellationToken),
    };

    /// <summary>
    /// A Song's entry on a Playlist, deleted with the Song or with the Playlist. With the Song:
    /// restored at the end of the Playlist, raising its revision; left out when the Playlist is gone
    /// or full. With the Playlist (#103): restored where it was, moving the Song's updated time; left
    /// out when the Song is gone (the others keep their places, as a deleted Song's entry leaves them).
    /// </summary>
    public static readonly RetainedType PlaylistEntry = new(RetainedRecordTypes.PlaylistEntry, "playlist_songs", "membership of a Playlist", ShapeVersion: 1)
    {
        Optional = true,
        PrepareRestoreAsync = static (row, cancellationToken) => row.InGroup("playlists", row.TextOf("playlist_id"))
            ? Task.FromResult(RestorePreparation.With(row.Values))
            : SongRestore.AtPlaylistEndAsync(row, cancellationToken),
        AfterRestoreAsync = static (row, cancellationToken) => row.InGroup("playlists", row.TextOf("playlist_id"))
            ? CollectionRestore.TouchSongAsync(row, cancellationToken)
            : SongRestore.TouchAsync(row, "playlists", "playlist_id", cancellationToken),
    };

    /// <summary>
    /// A relationship of a deleted Song. Left out of a restore when the other Song or the type is
    /// gone; once back, the other Song's revision goes up, as it shows the relationship again.
    /// </summary>
    public static readonly RetainedType SongRelationship = new(RetainedRecordTypes.SongRelationship, "song_relationships", "relationship", ShapeVersion: 1)
    {
        Optional = true,
        AfterRestoreAsync = static async (row, cancellationToken) =>
        {
            foreach (var column in (string[])["from_song_id", "to_song_id"])
            {
                // The Song restored with it is back as it was; only the other Song changed.
                if (!row.InGroup("songs", row.TextOf(column)))
                {
                    await SongRestore.TouchAsync(row, "songs", column, cancellationToken).ConfigureAwait(false);
                }
            }
        },
    };

    /// <summary>
    /// An Album, deleted with its links, tracks, and own artwork (#103); its Songs stay. Restored
    /// without its Album Artist when that Artist was deleted meanwhile; a disc left empty by Songs
    /// deleted meanwhile closes up. A live Album with the same title is no clash: titles are not unique.
    /// </summary>
    public static readonly RetainedType Album = new(RetainedRecordTypes.Album, "albums", "Album", ShapeVersion: 1)
    {
        PrepareRestoreAsync = static (row, cancellationToken) => CollectionRestore.KeepAlbumArtistAsync(row, cancellationToken),
        AfterRestoreAsync = static (row, cancellationToken) => CollectionRestore.CloseDiscGapsAsync(row, cancellationToken),
    };

    /// <summary>A link of a deleted Album.</summary>
    public static readonly RetainedType AlbumLink = new(RetainedRecordTypes.AlbumLink, "album_links", "Album link", ShapeVersion: 1);

    /// <summary>
    /// A Playlist, deleted with its entries and own artwork (#103); its Songs stay. A live Playlist
    /// with the same title is no clash: titles are not unique.
    /// </summary>
    public static readonly RetainedType Playlist = new(RetainedRecordTypes.Playlist, "playlists", "Playlist", ShapeVersion: 1);

    /// <summary>
    /// An Artist, deleted with its aliases, links, and own artwork (#104), and with its credits when
    /// they were removed rather than reassigned. A live Artist with the same name is no clash: names
    /// are not unique. The default-Artist setting is not part of it, so a restore leaves it as it is.
    /// </summary>
    public static readonly RetainedType Artist = new(RetainedRecordTypes.Artist, "artists", "Artist", ShapeVersion: 1);

    /// <summary>An alias of a deleted Artist.</summary>
    public static readonly RetainedType ArtistAlias = new(RetainedRecordTypes.ArtistAlias, "artist_aliases", "Artist alias", ShapeVersion: 1);

    /// <summary>A link of a deleted Artist.</summary>
    public static readonly RetainedType ArtistLink = new(RetainedRecordTypes.ArtistLink, "artist_links", "Artist link", ShapeVersion: 1);

    /// <summary>
    /// The Album Artist of an Album whose Album Artist was deleted with its credits removed (#104): a
    /// reference, so the Album stays and only its <c>album_artist_id</c> is cleared and remembered.
    /// Restored where the Album still exists and has no Album Artist, raising its revision.
    /// </summary>
    public static readonly RetainedType AlbumArtist = new(RetainedRecordTypes.AlbumArtist, "albums", "Album Artist", ShapeVersion: 1)
    {
        ReferenceColumns = ["album_artist_id"],
        AfterRestoreAsync = static (row, cancellationToken) => SongRestore.TouchAsync(row, "albums", "id", cancellationToken),
    };

    /// <summary>Every built-in type.</summary>
    public static IReadOnlyList<RetainedType> BuiltIn { get; } =
    [
        EditorSnapshot, ArtworkAttachment, Version, Generation,
        Song, UsedVersionNumber, SongLink, SongGenre, SongTag, SongCredit, AlbumTrack, PlaylistEntry, SongRelationship,
        Album, AlbumLink, Playlist,
        Artist, ArtistAlias, ArtistLink, AlbumArtist,
    ];
}

/// <summary>The registered retained types, checked once when the first is needed.</summary>
internal sealed class RetainedTypeRegistry
{
    private readonly FrozenDictionary<string, RetainedType> byRecordType;
    private readonly FrozenDictionary<string, RetainedType> byTable;
    private readonly IReadOnlyList<RetainedType> references;

    public RetainedTypeRegistry(IEnumerable<RetainedType> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var all = types.ToList();
        foreach (var type in all)
        {
            if (type.ShapeVersion < 1)
            {
                throw new InvalidOperationException($"The retained type '{type.RecordType}' has shape version {type.ShapeVersion}; versions start at 1.");
            }

            if (Enumerable.Range(1, type.ShapeVersion - 1).FirstOrDefault(version => !type.Upgraders.ContainsKey(version)) is var missing and > 0)
            {
                throw new InvalidOperationException(
                    $"The retained type '{type.RecordType}' is at shape {type.ShapeVersion} but has no upgrader from shape {missing}: records retained under it could not be restored.");
            }
        }

        byRecordType = all.ToFrozenDictionary(static type => type.RecordType, StringComparer.Ordinal);
        byTable = all.Where(static type => type.ReferenceColumns is null).ToFrozenDictionary(static type => type.Table, StringComparer.Ordinal);
        references = [.. all.Where(static type => type.ReferenceColumns is not null)];
        All = all;
    }

    public IReadOnlyList<RetainedType> All { get; }

    public RetainedType? ByRecordType(string recordType) => byRecordType.GetValueOrDefault(recordType);

    /// <summary>The row type of <paramref name="table"/> (never a reference type), or null.</summary>
    public RetainedType? ByTable(string table) => byTable.GetValueOrDefault(table);

    /// <summary>The reference type over <paramref name="columns"/> of <paramref name="table"/>, or null.</summary>
    public RetainedType? ReferenceFor(string table, IReadOnlyList<string> columns) =>
        references.FirstOrDefault(type => type.Table == table && type.ReferenceColumns!.SequenceEqual(columns, StringComparer.Ordinal));
}

/// <summary>A column of a live table, as SQLite describes it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Type">Its declared type, as written.</param>
/// <param name="NotNull">Whether it refuses null.</param>
/// <param name="KeyPosition">Its place in the primary key from 1, or 0 when it is not part of it.</param>
internal sealed record TableColumn(string Name, string Type, bool NotNull, int KeyPosition);

/// <summary>The shape of a retained type's table, which the retained-shape guard compares with its baseline.</summary>
internal static class RetainedShapes
{
    /// <summary>
    /// A hash of the columns' names, declared types (upper-cased), and nullability, in name order: a
    /// change to any of them is a new shape, whatever order the columns are in.
    /// </summary>
    public static string Hash(IEnumerable<TableColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var lines = columns
            .OrderBy(static column => column.Name, StringComparer.Ordinal)
            .Select(static column => $"{column.Name}|{column.Type.ToUpperInvariant()}|{(column.NotNull ? "not null" : "null")}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }
}
