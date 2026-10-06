using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using n8Tracks.Application.Retention;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Retention;

/// <summary>
/// The retention store. Retaining reads each row as SQLite holds it (every column, as stored) into a
/// JSON document, follows the database's own cascading foreign keys to collect everything that would
/// go with it, and removes the live rows; restoring checks parents, IDs, and unique keys first, then
/// inserts each row back exactly as it was, its <c>revision</c> incremented. Because both work from the
/// schema, a retained type declares only what the schema cannot say (<see cref="RetainedType"/>).
/// Documents are never logged.
/// </summary>
internal sealed class RetentionStore(N8TracksDbContext context, RetainedTypeRegistry types) : IRetentionStore
{
    /// <summary>A BLOB value in a document: <c>{"$blob": "&lt;base64&gt;"}</c>, so it cannot be mistaken for text.</summary>
    private const string BlobProperty = "$blob";

    /// <summary>The column a restore increments, wherever a retained table has one.</summary>
    private const string RevisionColumn = "revision";

    private readonly Dictionary<string, IReadOnlyList<TableColumn>> columns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ForeignKey>> foreignKeys = new(StringComparer.Ordinal);
    private IReadOnlyList<string>? tables;

    public async Task<RetentionGroup> RetainAsync(
        Guid id,
        RetentionRequest request,
        DateTimeOffset deletedUtc,
        DateTimeOffset pruneAfterUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTransaction();

        var collected = new List<LiveRow>();
        var references = new List<LiveRow>();
        var seen = new HashSet<(string Table, string Key)>();
        var referring = new HashSet<string>(request.Referring ?? [], StringComparer.Ordinal);
        foreach (var root in request.Roots)
        {
            var type = types.ByRecordType(root.RecordType) ?? throw new InvalidOperationException($"'{root.RecordType}' is not a retained record type.");
            var key = await KeyColumnsAsync(type.Table, cancellationToken).ConfigureAwait(false);
            if (key.Count != 1)
            {
                throw new InvalidOperationException($"A root must have a single-column key; {type.Table} does not.");
            }

            var rows = await SelectAsync(type.Table, key, [StoredGuid(root.Id)], cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                throw new InvalidOperationException($"There is no {type.Noun} with the ID {root.Id} to delete.");
            }

            await AddAsync(type, rows[0], collected, seen, cancellationToken).ConfigureAwait(false);
        }

        // Everything the database would remove with what is collected so far, breadth first, so each
        // row comes after the row it belongs to.
        for (var index = 0; index < collected.Count; index++)
        {
            var parent = collected[index];
            foreach (var table in await TablesAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var foreignKey in (await ForeignKeysAsync(table, cancellationToken).ConfigureAwait(false)).Where(key => key.Table == parent.Type.Table))
                {
                    var values = foreignKey.To.Select(column => parent.Values[column]).ToArray();
                    if (values.Any(static value => value is null))
                    {
                        continue;
                    }

                    var dependents = await SelectAsync(table, foreignKey.From, values, cancellationToken).ConfigureAwait(false);
                    if (dependents.Count == 0)
                    {
                        continue;
                    }

                    var dependentType = types.ByTable(table);
                    if (!string.Equals(foreignKey.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase))
                    {
                        // Restrict, set null, or no action: the deletion has to deal with these rows
                        // itself, name them as roots, or name their type as going with it (Referring);
                        // nothing here changes a row it neither retains nor remembers.
                        if (dependentType is not null && referring.Contains(dependentType.RecordType))
                        {
                            foreach (var dependent in dependents)
                            {
                                await AddAsync(dependentType, dependent, collected, seen, cancellationToken).ConfigureAwait(false);
                            }

                            continue;
                        }

                        if (types.ReferenceFor(table, foreignKey.From) is { } reference && referring.Contains(reference.RecordType))
                        {
                            await AddReferencesAsync(reference, dependents, references, seen, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (dependentType is null || !await AllCollectedAsync(dependentType, dependents, seen, cancellationToken).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                $"Rows of {table} refer to the {parent.Type.Noun} being deleted ({foreignKey.OnDelete}): the deletion must remove them or name them first.");
                        }

                        continue;
                    }

                    if (dependentType is null)
                    {
                        throw new InvalidOperationException(
                            $"Deleting the {parent.Type.Noun} would also remove rows of {table}, which is not a retained type: register it, so nothing is removed without being retained.");
                    }

                    foreach (var dependent in dependents)
                    {
                        await AddAsync(dependentType, dependent, collected, seen, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        var group = new RetentionGroupRecord
        {
            Id = id,
            Kind = request.Kind,
            Label = request.Label,
            Shortcode = request.Shortcode,
            DeletedUtc = UtcText.From(deletedUtc),
            PruneAfterUtc = UtcText.From(pruneAfterUtc),
            Files = JsonSerializer.Serialize(request.Files.Distinct(StringComparer.Ordinal).ToArray()),
        };
        context.RetentionGroups.Add(group);
        // References come after every row, so a restore sets them once what they name is back.
        var records = collected.Concat(references).Select((row, position) => new RetentionRecordRecord
        {
            GroupId = id,
            Position = position,
            RecordType = row.Type.RecordType,
            OriginalId = row.OriginalId,
            ShapeVersion = row.Type.ShapeVersion,
            Document = Serialize(row.Values),
        }).ToList();
        context.RetentionRecords.AddRange(records);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(group).State = EntityState.Detached;
        foreach (var record in records)
        {
            context.Entry(record).State = EntityState.Detached;
        }

        // References between rows of the group that would block their removal (a Song names its
        // current Version, which names the Song) are cleared first where they may be null; the
        // documents above already hold them as they were, and a restore puts them back.
        foreach (var row in collected)
        {
            var nullable = (await ColumnsAsync(row.Type.Table, cancellationToken).ConfigureAwait(false))
                .Where(static column => !column.NotNull)
                .Select(static column => column.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var foreignKey in await ForeignKeysAsync(row.Type.Table, cancellationToken).ConfigureAwait(false))
            {
                var values = foreignKey.From.Select(column => row.Values[column]).ToArray();
                if (values.Any(static value => value is null)
                    || !foreignKey.From.All(nullable.Contains)
                    || !collected.Any(other => !ReferenceEquals(other, row) && Refers(foreignKey, values, other)))
                {
                    continue;
                }

                await ExecuteAsync(
                    $"UPDATE {Quote(row.Type.Table)} SET {string.Join(", ", foreignKey.From.Select(static column => $"{Quote(column)} = NULL"))} WHERE {Where(row.Key, 0)};",
                    row.Key.Select(column => row.Values[column]).ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // Live rows that refer to what goes stay, without the reference; the documents above hold it.
        foreach (var reference in references)
        {
            await ExecuteAsync(
                $"UPDATE {Quote(reference.Type.Table)} SET {string.Join(", ", reference.Type.ReferenceColumns!.Select(static column => $"{Quote(column)} = NULL"))} WHERE {Where(reference.Key, 0)};",
                reference.Key.Select(column => reference.Values[column]).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }

        // Children first, so no row is removed by a cascade before it is removed on purpose; but a row
        // frozen with its parent goes after it (its triggers refuse it while the parent is there),
        // removed by the parent's cascade, its document already kept above.
        foreach (var row in Enumerable.Reverse(collected).OrderBy(static row => row.Type.FrozenWithParent))
        {
            await ExecuteAsync(
                $"DELETE FROM {Quote(row.Type.Table)} WHERE {Where(row.Key, 0)};",
                row.Key.Select(column => row.Values[column]).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }

        return From(group, records);
    }

    public async Task<RetentionGroup?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var group = await context.RetentionGroups.AsNoTracking().SingleOrDefaultAsync(record => record.Id == id, cancellationToken).ConfigureAwait(false);
        return group is null ? null : await WithRecordsAsync(group, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RetentionGroup?> FindByShortcodeAsync(string shortcode, CancellationToken cancellationToken)
    {
        var group = await context.RetentionGroups.AsNoTracking()
            .Where(record => record.Shortcode == shortcode)
            .OrderByDescending(static record => record.DeletedUtc)
            .ThenByDescending(static record => record.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return group is null ? null : await WithRecordsAsync(group, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RetentionGroup?> FindByRecordAsync(string recordType, Guid id, CancellationToken cancellationToken)
    {
        var originalId = StoredGuid(id);
        var group = await context.RetentionGroups.AsNoTracking()
            .Where(group => context.RetentionRecords.Any(record =>
                record.GroupId == group.Id && record.RecordType == recordType && record.OriginalId == originalId))
            .OrderByDescending(static record => record.DeletedUtc)
            .ThenByDescending(static record => record.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return group is null ? null : await WithRecordsAsync(group, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RetentionGroup>> ListAsync(CancellationToken cancellationToken)
    {
        var groups = await context.RetentionGroups.AsNoTracking()
            .OrderByDescending(static record => record.DeletedUtc)
            .ThenByDescending(static record => record.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var records = await context.RetentionRecords.AsNoTracking()
            .Select(static record => new { record.GroupId, record.Position, record.RecordType, record.OriginalId, record.ShapeVersion })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byGroup = records.ToLookup(static record => record.GroupId);

        return [.. groups.Select(group => From(
            group,
            [.. byGroup[group.Id].OrderBy(static record => record.Position).Select(static record => new RetainedRecord(record.RecordType, record.OriginalId, record.ShapeVersion))]))];
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> RecordFieldsAsync(Guid groupId, string recordType, IReadOnlyList<string> columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var documents = await context.RetentionRecords.AsNoTracking()
            .Where(record => record.GroupId == groupId && record.RecordType == recordType)
            .OrderBy(static record => record.Position)
            .Select(static record => record.Document)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Only the named columns leave this method; the rest of each document is never read out.
        return [.. documents.Select(text =>
        {
            var document = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException("A retained document is not a JSON object.");
            return (IReadOnlyDictionary<string, string?>)columns.ToDictionary(
                static column => column,
                column => Deserialize(document[column]) is { } value ? Text(value) : null,
                StringComparer.Ordinal);
        })];
    }

    public string NounOf(string recordType) => types.ByRecordType(recordType)?.Noun ?? recordType;

    public async Task<RetentionRestoreResult> RestoreAsync(Guid id, DateTimeOffset restoredUtc, CancellationToken cancellationToken)
    {
        RequireTransaction();

        // Rows of a group may refer to each other both ways (a Song and its current Version), so the
        // parents are checked up front below and the database checks them again before the end of
        // this method, once every row is back, rather than row by row. Reset when the transaction ends.
        await ExecuteAsync("PRAGMA defer_foreign_keys = ON;", [], cancellationToken).ConfigureAwait(false);

        var held = await context.RetentionGroups.AsNoTracking()
            .Where(group => group.Id == id)
            .Select(static group => new { group.DeletedUtc, group.Kind })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("There is no such retention group.");
        var keys = new HashSet<(string Table, string Key)>();
        RestoredRow Restoring(IReadOnlyDictionary<string, object?> values) => new(context, values, UtcText.Parse(held.DeletedUtc), held.Kind, restoredUtc, keys);
        var records = await context.RetentionRecords.AsNoTracking()
            .Where(record => record.GroupId == id)
            .OrderBy(static record => record.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<LiveRow>();
        foreach (var record in records)
        {
            var row = await UpgradeAsync(record, cancellationToken).ConfigureAwait(false);
            rows.Add(row);

            // A reference is not a row of the group: its live row stays where it is.
            if (row.Type.ReferenceColumns is null)
            {
                keys.Add((row.Type.Table, row.OriginalId));
            }
        }

        // Up front, before anything is written: every parent there (live, or earlier in the group and
        // not left out), and no ID or unique key held by a live row.
        var notes = new List<string>();
        var skipped = new HashSet<LiveRow>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Type.ReferenceColumns is not null)
            {
                if (await ReferenceLeftOutAsync(row, rows, skipped, cancellationToken).ConfigureAwait(false) is { } reason)
                {
                    skipped.Add(row);
                    notes.Add($"{Indefinite(row.Type.Noun)} was not restored: {reason}");
                }

                continue;
            }

            // A type may restore a row with other values than it was retained with (a membership
            // goes to the end of its Album), or leave it out, saying why.
            if (row.Type.PrepareRestoreAsync is { } prepare)
            {
                var prepared = await prepare(Restoring(row.Values), cancellationToken).ConfigureAwait(false);
                if (prepared.Values is null)
                {
                    skipped.Add(row);
                    notes.Add($"{Indefinite(row.Type.Noun)} was not restored: {prepared.Note}");
                    continue;
                }

                if (prepared.Note is { } note)
                {
                    notes.Add(note);
                }

                row = rows[index] = row with { Values = prepared.Values };
            }

            if (await MissingParentAsync(row, rows, skipped, cancellationToken).ConfigureAwait(false) is { } missing)
            {
                if (!row.Type.Optional)
                {
                    throw new RetentionRestoreRefusedException(missingParent: true, $"The {missing} this {row.Type.Noun} belongs to no longer exists.");
                }

                skipped.Add(row);
                notes.Add($"{Indefinite(row.Type.Noun)} was not restored: the {missing} it belongs to no longer exists.");
                continue;
            }

            if ((await SelectAsync(row.Type.Table, row.Key, [.. row.Key.Select(column => row.Values[column])], cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                throw new RetentionRestoreRefusedException(missingParent: false, $"A live {row.Type.Noun} already has the ID {row.OriginalId}.");
            }

            foreach (var unique in await UniqueKeysAsync(row.Type.Table, cancellationToken).ConfigureAwait(false))
            {
                var values = unique.Select(column => row.Values[column]).ToArray();
                if (values.Any(static value => value is null)
                    || unique.SequenceEqual(row.Key, StringComparer.Ordinal)
                    || (unique.Count == 1 && unique[0] == row.Type.StorageOrderColumn)
                    || (await SelectAsync(row.Type.Table, unique, values, cancellationToken).ConfigureAwait(false)).Count == 0)
                {
                    continue;
                }

                throw new RetentionRestoreRefusedException(
                    missingParent: false,
                    $"A live {row.Type.Noun} already holds its {string.Join(", ", unique)} ({string.Join(", ", values.Select(Text))}).");
            }
        }

        // A row frozen with its parent goes back before it (its triggers refuse it once the parent is
        // there); the foreign keys are deferred, so the parent may follow.
        var restored = new List<LiveRow>();
        foreach (var row in rows.Where(row => !skipped.Contains(row)).OrderBy(static row => !row.Type.FrozenWithParent))
        {
            if (row.Type.ReferenceColumns is { } referenceColumns)
            {
                await ExecuteAsync(
                    $"UPDATE {Quote(row.Type.Table)} SET {string.Join(", ", referenceColumns.Select((column, index) => $"{Quote(column)} = $p{row.Key.Count + index}"))} WHERE {Where(row.Key, 0)};",
                    [.. row.Key.Select(column => row.Values[column]), .. referenceColumns.Select(column => row.Values[column])],
                    cancellationToken).ConfigureAwait(false);
                restored.Add(row);
                continue;
            }

            var values = new Dictionary<string, object?>(row.Values, StringComparer.Ordinal);
            if (values.TryGetValue(RevisionColumn, out var revision) && revision is long number)
            {
                values[RevisionColumn] = number + 1;
            }

            if (row.Type.StorageOrderColumn is { } order
                && (await SelectAsync(row.Type.Table, [order], [values[order]], cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                values[order] = await ScalarAsync($"SELECT COALESCE(MAX({Quote(order)}), 0) + 1 FROM {Quote(row.Type.Table)};", [], cancellationToken).ConfigureAwait(false);
                notes.Add($"A {row.Type.Noun} was given a new {order}, as its own was taken.");
            }

            if (row.Type.BeforeRestoreAsync is { } before)
            {
                await before(Restoring(values), cancellationToken).ConfigureAwait(false);
            }

            var names = values.Keys.ToList();
            try
            {
                await ExecuteAsync(
                    $"INSERT INTO {Quote(row.Type.Table)} ({string.Join(", ", names.Select(Quote))}) VALUES ({string.Join(", ", names.Select(static (_, index) => $"$p{index}"))});",
                    [.. names.Select(name => values[name])],
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                // A rule the checks above do not read (a partial or expression index, a trigger).
                throw new RetentionRestoreRefusedException(
                    missingParent: exception.SqliteExtendedErrorCode == 787,
                    $"The {row.Type.Noun} {row.OriginalId} cannot be put back: {exception.Message}",
                    exception);
            }

            restored.Add(row with { Values = values });
        }

        foreach (var row in restored)
        {
            if (row.Type.AfterRestoreAsync is { } after)
            {
                await after(Restoring(row.Values), cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var table in restored.Select(static row => row.Type.Table).Distinct(StringComparer.Ordinal))
        {
            if ((await QueryAsync($"PRAGMA foreign_key_check({Quote(table)});", [], cancellationToken).ConfigureAwait(false)).FirstOrDefault() is { } violation)
            {
                throw new RetentionRestoreRefusedException(
                    missingParent: true,
                    $"A restored row of {table} refers to a {Noun((string)violation["parent"]!)} that no longer exists.");
            }
        }

        await context.RetentionGroups.Where(group => group.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return new RetentionRestoreResult(notes, [.. restored.Select(static row => new RetainedRecord(row.Type.RecordType, row.OriginalId, row.Type.ShapeVersion))]);
    }

    public async Task<int> RemoveDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequireTransaction();

        var cutoff = UtcText.From(now);
        var parameters = new object?[] { cutoff, UtcText.From(now) };

        // The files first, from the same set of groups, then the groups (their records cascade).
        await ExecuteAsync(
            """
            INSERT INTO pending_file_deletions (path, queued_utc, attempts)
            SELECT DISTINCT file.value, $p1, 0
            FROM retention_groups AS retained, json_each(retained.files) AS file
            WHERE retained.prune_after_utc <= $p0
            ON CONFLICT (path) DO NOTHING;
            """,
            parameters,
            cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync("DELETE FROM retention_groups WHERE prune_after_utc <= $p0;", [cutoff], cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PendingFileDeletion>> PendingFilesAsync(CancellationToken cancellationToken) =>
        [.. (await context.PendingFileDeletions.AsNoTracking()
            .OrderBy(static pending => pending.QueuedUtc)
            .ThenBy(static pending => pending.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Select(static pending => new PendingFileDeletion(pending.Path, pending.Attempts))];

    public Task<bool> IsFileRetainedAsync(string path, CancellationToken cancellationToken) =>
        context.Database
            .SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM retention_groups AS retained, json_each(retained.files) AS file WHERE file.value = {path}) AS \"Value\"")
            .SingleAsync(cancellationToken);

    public Task CompletePendingAsync(string path, CancellationToken cancellationToken) =>
        context.PendingFileDeletions.Where(pending => pending.Path == path).ExecuteDeleteAsync(cancellationToken);

    public Task FailPendingAsync(string path, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = UtcText.From(now);
        return context.PendingFileDeletions
            .Where(pending => pending.Path == path)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static pending => pending.Attempts, static pending => pending.Attempts + 1)
                    .SetProperty(static pending => pending.LastAttemptUtc, at)
                    .SetProperty(static pending => pending.LastError, error),
                cancellationToken);
    }

    /// <summary>The columns of <paramref name="table"/> as the database has them now (what a document holds).</summary>
    public async Task<IReadOnlyList<TableColumn>> ColumnsAsync(string table, CancellationToken cancellationToken)
    {
        if (columns.TryGetValue(table, out var known))
        {
            return known;
        }

        var read = new List<TableColumn>();
        foreach (var row in await QueryAsync($"PRAGMA table_info({Quote(table)});", [], cancellationToken).ConfigureAwait(false))
        {
            read.Add(new TableColumn((string)row["name"]!, (string)row["type"]!, (long)row["notnull"]! != 0, (int)(long)row["pk"]!));
        }

        if (read.Count == 0)
        {
            throw new InvalidOperationException($"There is no table {table}.");
        }

        return columns[table] = read;
    }

    /// <summary>
    /// The columns a document of <paramref name="type"/> holds, as the database has them now: every
    /// column of its table, or for a reference type only the key's and the reference's (what the
    /// retained-shape guard hashes).
    /// </summary>
    public async Task<IReadOnlyList<TableColumn>> ShapeColumnsAsync(RetainedType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);

        var all = await ColumnsAsync(type.Table, cancellationToken).ConfigureAwait(false);
        if (type.ReferenceColumns is not { } referenceColumns)
        {
            return all;
        }

        var shape = all.Where(column => column.KeyPosition > 0 || referenceColumns.Contains(column.Name, StringComparer.Ordinal)).ToList();
        if (shape.Count(column => referenceColumns.Contains(column.Name, StringComparer.Ordinal)) != referenceColumns.Count
            || shape.Any(column => column.NotNull && referenceColumns.Contains(column.Name, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException($"The reference type '{type.RecordType}' names columns {type.Table} does not have as nullable columns.");
        }

        return shape;
    }

    /// <summary>
    /// Remembers, as references of <paramref name="type"/>, the key and reference of each of
    /// <paramref name="rows"/> that is not itself going (a row the group retains needs no reference).
    /// </summary>
    private async Task AddReferencesAsync(RetainedType type, List<Dictionary<string, object?>> rows, List<LiveRow> references, HashSet<(string Table, string Key)> seen, CancellationToken cancellationToken)
    {
        var shape = await ShapeColumnsAsync(type, cancellationToken).ConfigureAwait(false);
        var key = await KeyColumnsAsync(type.Table, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var originalId = OriginalIdOf(key, row);
            if (seen.Contains((type.Table, originalId)) || references.Any(reference => reference.Type == type && reference.OriginalId == originalId))
            {
                continue;
            }

            var values = shape.ToDictionary(static column => column.Name, column => row[column.Name], StringComparer.Ordinal);
            references.Add(new LiveRow(type, values, key, originalId));
        }
    }

    /// <summary>
    /// Why a remembered reference cannot be set again, or null when it can: its live row must still
    /// exist with the reference empty, and what it names must be live or restored with it.
    /// </summary>
    private async Task<string?> ReferenceLeftOutAsync(LiveRow reference, List<LiveRow> group, HashSet<LiveRow> skipped, CancellationToken cancellationToken)
    {
        var referenceColumns = reference.Type.ReferenceColumns!;
        var live = await SelectAsync(reference.Type.Table, reference.Key, [.. reference.Key.Select(column => reference.Values[column])], cancellationToken).ConfigureAwait(false);
        if (live.Count == 0)
        {
            return $"the {Noun(reference.Type.Table)} it belongs to no longer exists.";
        }

        if (referenceColumns.Any(column => live[0][column] is not null))
        {
            return $"the {Noun(reference.Type.Table)} has another one now.";
        }

        foreach (var foreignKey in (await ForeignKeysAsync(reference.Type.Table, cancellationToken).ConfigureAwait(false))
            .Where(key => key.From.SequenceEqual(referenceColumns, StringComparer.Ordinal)))
        {
            var values = referenceColumns.Select(column => reference.Values[column]).ToArray();
            if (values.Any(static value => value is null)
                || group.Any(other => !skipped.Contains(other) && other.Type.ReferenceColumns is null && Refers(foreignKey, values, other))
                || (await SelectAsync(foreignKey.Table, foreignKey.To, values, cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                continue;
            }

            return $"the {Noun(foreignKey.Table)} it names no longer exists.";
        }

        return null;
    }

    /// <summary>"A" or "An" before <paramref name="noun"/>, as messages start: "An Artist credit", "A used Version number".</summary>
    private static string Indefinite(string noun) =>
        noun.Length > 0 && "AEIOaeio".Contains(noun[0], StringComparison.Ordinal) ? $"An {noun}" : $"A {noun}";

    private async Task AddAsync(RetainedType type, Dictionary<string, object?> values, List<LiveRow> collected, HashSet<(string Table, string Key)> seen, CancellationToken cancellationToken)
    {
        var key = await KeyColumnsAsync(type.Table, cancellationToken).ConfigureAwait(false);
        var row = new LiveRow(type, values, key, OriginalIdOf(key, values));
        if (seen.Add((type.Table, row.OriginalId)))
        {
            collected.Add(row);
        }
    }

    private async Task<bool> AllCollectedAsync(RetainedType type, List<Dictionary<string, object?>> rows, HashSet<(string Table, string Key)> seen, CancellationToken cancellationToken)
    {
        var key = await KeyColumnsAsync(type.Table, cancellationToken).ConfigureAwait(false);
        return rows.All(row => seen.Contains((type.Table, OriginalIdOf(key, row))));
    }

    /// <summary>Reads a retained record, brought up to its type's current shape, as the row it will be again.</summary>
    private async Task<LiveRow> UpgradeAsync(RetentionRecordRecord record, CancellationToken cancellationToken)
    {
        var type = types.ByRecordType(record.RecordType)
            ?? throw new InvalidOperationException($"'{record.RecordType}' is not a retained record type in this release, so its group cannot be restored.");
        if (record.ShapeVersion > type.ShapeVersion)
        {
            throw new InvalidOperationException($"A {type.Noun} was retained under shape {record.ShapeVersion}, newer than this release's {type.ShapeVersion}.");
        }

        var document = JsonNode.Parse(record.Document)?.AsObject() ?? throw new InvalidOperationException("A retained document is not a JSON object.");
        for (var version = record.ShapeVersion; version < type.ShapeVersion; version++)
        {
            document = type.Upgraders[version](document);
        }

        var current = await ShapeColumnsAsync(type, cancellationToken).ConfigureAwait(false);
        var names = document.Select(static property => property.Key).Order(StringComparer.Ordinal);
        if (!names.SequenceEqual(current.Select(static column => column.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"A {type.Noun} retained under shape {record.ShapeVersion} does not match {type.Table} after upgrading: the shape changed without an upgrader.");
        }

        var values = current.ToDictionary(static column => column.Name, column => Deserialize(document[column.Name]), StringComparer.Ordinal);
        var key = await KeyColumnsAsync(type.Table, cancellationToken).ConfigureAwait(false);
        return new LiveRow(type, values, key, OriginalIdOf(key, values));
    }

    /// <summary>The noun of the first parent of <paramref name="row"/> that is neither live nor restored with it; null when all are there.</summary>
    private async Task<string?> MissingParentAsync(LiveRow row, List<LiveRow> group, HashSet<LiveRow> skipped, CancellationToken cancellationToken)
    {
        foreach (var foreignKey in await ForeignKeysAsync(row.Type.Table, cancellationToken).ConfigureAwait(false))
        {
            var values = foreignKey.From.Select(column => row.Values[column]).ToArray();
            if (values.Any(static value => value is null))
            {
                continue;
            }

            var inGroup = group.Any(other => !ReferenceEquals(other, row) && !skipped.Contains(other) && other.Type.ReferenceColumns is null && Refers(foreignKey, values, other));
            if (inGroup || (await SelectAsync(foreignKey.Table, foreignKey.To, values, cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                continue;
            }

            return Noun(foreignKey.Table);
        }

        return null;
    }

    /// <summary>How messages name a row of <paramref name="table"/>: its retained type's noun, or the table's name made singular.</summary>
    private string Noun(string table) => types.ByTable(table)?.Noun ?? table.Replace('_', ' ').TrimEnd('s');

    /// <summary>Whether <paramref name="values"/> of <paramref name="foreignKey"/> name <paramref name="other"/>.</summary>
    private static bool Refers(ForeignKey foreignKey, object?[] values, LiveRow other) =>
        other.Type.Table == foreignKey.Table
        && foreignKey.To.Select(column => other.Values[column]).SequenceEqual(values, ValueComparer.Instance);

    private async Task<IReadOnlyList<string>> KeyColumnsAsync(string table, CancellationToken cancellationToken)
    {
        var key = (await ColumnsAsync(table, cancellationToken).ConfigureAwait(false))
            .Where(static column => column.KeyPosition > 0)
            .OrderBy(static column => column.KeyPosition)
            .Select(static column => column.Name)
            .ToList();
        return key.Count > 0 ? key : throw new InvalidOperationException($"{table} has no primary key, so its rows cannot be retained.");
    }

    private async Task<IReadOnlyList<string>> TablesAsync(CancellationToken cancellationToken) =>
        tables ??= [.. (await QueryAsync(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND name <> '__EFMigrationsHistory' ORDER BY name;",
                [],
                cancellationToken).ConfigureAwait(false))
            .Select(static row => (string)row["name"]!)];

    private async Task<IReadOnlyList<ForeignKey>> ForeignKeysAsync(string table, CancellationToken cancellationToken)
    {
        if (foreignKeys.TryGetValue(table, out var known))
        {
            return known;
        }

        var keys = new List<ForeignKey>();
        foreach (var group in (await QueryAsync($"PRAGMA foreign_key_list({Quote(table)});", [], cancellationToken).ConfigureAwait(false))
            .GroupBy(static row => (long)row["id"]!))
        {
            var parts = group.OrderBy(static row => (long)row["seq"]!).ToList();
            var parent = (string)parts[0]["table"]!;
            var to = parts.Select(static row => row["to"] as string).ToList();

            // A key that names no parent columns refers to the parent's primary key.
            var parentColumns = to.Any(static column => column is null)
                ? await KeyColumnsAsync(parent, cancellationToken).ConfigureAwait(false)
                : to.Select(static column => column!).ToList();
            keys.Add(new ForeignKey(parent, [.. parts.Select(static row => (string)row["from"]!)], parentColumns, (string)parts[0]["on_delete"]!));
        }

        return foreignKeys[table] = keys;
    }

    /// <summary>Each unique index's columns, except the primary key's, partial ones, and ones on expressions (the insert itself checks those).</summary>
    private async Task<List<IReadOnlyList<string>>> UniqueKeysAsync(string table, CancellationToken cancellationToken)
    {
        var keys = new List<IReadOnlyList<string>>();
        foreach (var index in await QueryAsync($"PRAGMA index_list({Quote(table)});", [], cancellationToken).ConfigureAwait(false))
        {
            if ((long)index["unique"]! == 0 || (long)index["partial"]! != 0 || (string)index["origin"]! == "pk")
            {
                continue;
            }

            var parts = await QueryAsync($"PRAGMA index_info({Quote((string)index["name"]!)});", [], cancellationToken).ConfigureAwait(false);
            if (parts.All(static part => part["name"] is string))
            {
                keys.Add([.. parts.OrderBy(static part => (long)part["seqno"]!).Select(static part => (string)part["name"]!)]);
            }
        }

        return keys;
    }

    private async Task<List<Dictionary<string, object?>>> SelectAsync(string table, IReadOnlyList<string> where, object?[] values, CancellationToken cancellationToken) =>
        await QueryAsync($"SELECT * FROM {Quote(table)} WHERE {Where(where, 0)};", values, cancellationToken).ConfigureAwait(false);

    private async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, object?[] parameters, CancellationToken cancellationToken)
    {
        var command = await CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var rows = new List<Dictionary<string, object?>>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var field = 0; field < reader.FieldCount; field++)
                    {
                        row[reader.GetName(field)] = reader.IsDBNull(field) ? null : reader[field];
                    }

                    rows.Add(row);
                }

                return rows;
            }
        }
    }

    private async Task<int> ExecuteAsync(string sql, object?[] parameters, CancellationToken cancellationToken)
    {
        var command = await CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<object?> ScalarAsync(string sql, object?[] parameters, CancellationToken cancellationToken)
    {
        var command = await CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DbCommand> CommandAsync(string sql, object?[] parameters, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var command = connection.CreateCommand();

        // Values come in as parameters; only table and column names, read from the schema or from a
        // registered type, are written into the text, quoted.
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"$p{index}";
            parameter.Value = parameters[index] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private void RequireTransaction()
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Retention changes live rows and must run inside the caller's transaction.");
        }
    }

    private async Task<RetentionGroup> WithRecordsAsync(RetentionGroupRecord group, CancellationToken cancellationToken)
    {
        var records = await context.RetentionRecords.AsNoTracking()
            .Where(record => record.GroupId == group.Id)
            .OrderBy(static record => record.Position)
            .Select(static record => new RetentionRecordRecord
            {
                GroupId = record.GroupId,
                Position = record.Position,
                RecordType = record.RecordType,
                OriginalId = record.OriginalId,
                ShapeVersion = record.ShapeVersion,
                Document = string.Empty,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return From(group, records);
    }

    private static RetentionGroup From(RetentionGroupRecord group, IEnumerable<RetentionRecordRecord> records) =>
        From(group, [.. records.Select(static record => new RetainedRecord(record.RecordType, record.OriginalId, record.ShapeVersion))]);

    private static RetentionGroup From(RetentionGroupRecord group, IReadOnlyList<RetainedRecord> records) =>
        new(
            group.Id,
            group.Kind,
            group.Label,
            group.Shortcode,
            UtcText.Parse(group.DeletedUtc),
            UtcText.Parse(group.PruneAfterUtc),
            JsonSerializer.Deserialize<string[]>(group.Files) ?? [],
            records);

    /// <summary>A GUID as EF Core stores it in SQLite: upper-case text.</summary>
    private static string StoredGuid(Guid id) => id.ToString().ToUpperInvariant();

    private static string OriginalIdOf(IReadOnlyList<string> key, IReadOnlyDictionary<string, object?> values) =>
        string.Join('/', key.Select(column => Text(values[column])));

    private static string Text(object? value) => value switch
    {
        null => "null",
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Where(IReadOnlyList<string> columnNames, int first) =>
        string.Join(" AND ", columnNames.Select((column, index) => $"{Quote(column)} = $p{first + index}"));

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>A row as a JSON object: integers and reals as numbers (a real always with a point), text as strings, BLOBs tagged.</summary>
    private static string Serialize(IReadOnlyDictionary<string, object?> values)
    {
        var document = new JsonObject();
        foreach (var (name, value) in values)
        {
            document[name] = value switch
            {
                null => null,
                long integer => JsonValue.Create(integer),
                double real => JsonNode.Parse(Real(real)),
                string text => JsonValue.Create(text),
                byte[] bytes => new JsonObject { [BlobProperty] = Convert.ToBase64String(bytes) },
                _ => throw new InvalidOperationException($"A column holds a {value.GetType().Name}, which retention cannot keep."),
            };
        }

        return document.ToJsonString();
    }

    private static string Real(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static object? Deserialize(JsonNode? node) => node switch
    {
        null => null,
        JsonObject blob when blob[BlobProperty] is JsonValue base64 => Convert.FromBase64String((string)base64!),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => (string)value!,
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => IsInteger(value.ToJsonString()) ? (object)(long)value : (double)value,
        _ => throw new InvalidOperationException("A retained document holds a value retention does not write."),
    };

    private static bool IsInteger(string number) => !number.Contains('.', StringComparison.Ordinal) && !number.Contains('e', StringComparison.OrdinalIgnoreCase);

    /// <summary>A row as stored, with its type, key columns, and key text.</summary>
    private sealed record LiveRow(RetainedType Type, IReadOnlyDictionary<string, object?> Values, IReadOnlyList<string> Key, string OriginalId);

    /// <summary>A foreign key of a table: its columns, and the parent table's columns they refer to.</summary>
    private sealed record ForeignKey(string Table, IReadOnlyList<string> From, IReadOnlyList<string> To, string OnDelete);

    /// <summary>Stored values compared as SQLite would: by type, then value; BLOBs byte for byte.</summary>
    private sealed class ValueComparer : IEqualityComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public new bool Equals(object? x, object? y) =>
            x is byte[] left && y is byte[] right ? left.AsSpan().SequenceEqual(right) : object.Equals(x, y);

        public int GetHashCode(object? obj) => obj is byte[] bytes ? bytes.Length : obj?.GetHashCode() ?? 0;
    }
}
