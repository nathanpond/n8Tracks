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
    /// Run inside the restore's transaction after every row of the group is back, once per restored
    /// row of this type: lets a live-side rule apply its own trimming (the 50-entry history cap).
    /// </summary>
    public Func<RestoredRow, CancellationToken, Task>? AfterRestoreAsync { get; init; }
}

/// <summary>A row a restore has just put back, as stored, and the context it was written through.</summary>
internal sealed record RestoredRow(N8TracksDbContext Context, IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>A GUID column's value.</summary>
    public Guid GuidOf(string column) => Guid.Parse((string)Values[column]!, CultureInfo.InvariantCulture);
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

    /// <summary>Every built-in type.</summary>
    public static IReadOnlyList<RetainedType> BuiltIn { get; } = [EditorSnapshot, ArtworkAttachment];
}

/// <summary>The registered retained types, checked once when the first is needed.</summary>
internal sealed class RetainedTypeRegistry
{
    private readonly FrozenDictionary<string, RetainedType> byRecordType;
    private readonly FrozenDictionary<string, RetainedType> byTable;

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
        byTable = all.ToFrozenDictionary(static type => type.Table, StringComparer.Ordinal);
        All = all;
    }

    public IReadOnlyList<RetainedType> All { get; }

    public RetainedType? ByRecordType(string recordType) => byRecordType.GetValueOrDefault(recordType);

    public RetainedType? ByTable(string table) => byTable.GetValueOrDefault(table);
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
