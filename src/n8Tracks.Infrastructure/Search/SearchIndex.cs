using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using n8Tracks.Application.Search;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Search;

/// <summary>
/// The search index (#223): the FTS5 table <c>search_index</c> (one row per Song, field, and owner) and
/// <c>search_rows</c>, which maps each of its rows to its Song so a Song's rows are replaced without
/// scanning the index. A rebuild fills <c>search_index_next</c> and <c>search_rows_next</c> and then
/// renames them over the two. Songs touched by a write are read from <c>search_dirty_songs</c>, which
/// the tables' triggers fill. Values always travel as parameters; only these fixed table names are
/// written into the text.
/// </summary>
internal sealed class SearchIndex(N8TracksDbContext context) : ISearchIndex
{
    public const string IndexTable = "search_index";
    public const string RowsTable = "search_rows";
    public const string NextIndexTable = "search_index_next";
    public const string NextRowsTable = "search_rows_next";
    public const string DirtyTable = "search_dirty_songs";

    /// <summary>The <c>settings</c> row holding the index's format version, <c>{"version": n}</c>; none means 1.</summary>
    public const string VersionSettingKey = "search.index";

    private const string Columns = "text, song_id UNINDEXED, field UNINDEXED, owner_kind UNINDEXED, owner_reference UNINDEXED, owner_label UNINDEXED, owner_state UNINDEXED, tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3'";

    /// <summary>The statements that create an empty index pair named <paramref name="index"/> and <paramref name="rows"/>, as the migration created the first.</summary>
    public static string CreateStatements(string index, string rows) =>
        $"""
        CREATE VIRTUAL TABLE {index} USING fts5({Columns});
        CREATE TABLE {rows} (row_id INTEGER NOT NULL PRIMARY KEY, song_id TEXT NOT NULL);
        CREATE INDEX ix_{rows}_song_id ON {rows} (song_id);
        """;

    public async Task<bool> AnyPendingAsync(CancellationToken cancellationToken) =>
        Convert.ToInt64(await ScalarAsync($"SELECT EXISTS (SELECT 1 FROM {DirtyTable});", [], cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;

    public async Task<IReadOnlyList<Guid>> TakePendingAsync(CancellationToken cancellationToken)
    {
        RequireTransaction();

        var pending = new List<Guid>();
        var command = await CommandAsync($"SELECT song_id FROM {DirtyTable};", [], cancellationToken).ConfigureAwait(false);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    pending.Add(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
                }
            }
        }

        if (pending.Count > 0)
        {
            await ExecuteAsync($"DELETE FROM {DirtyTable};", [], cancellationToken).ConfigureAwait(false);
        }

        return pending;
    }

    public async Task ReplaceAsync(IReadOnlyCollection<Guid> songIds, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);
        ArgumentNullException.ThrowIfNull(rows);
        RequireTransaction();

        await WriteAsync(IndexTable, RowsTable, songIds, rows, cancellationToken).ConfigureAwait(false);

        // While a rebuild fills the next index, what is written now goes into it too, so nothing saved
        // meanwhile is missing after the swap.
        if (await NextExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteAsync(NextIndexTable, NextRowsTable, songIds, rows, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task FillNextAsync(IReadOnlyCollection<Guid> songIds, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songIds);
        ArgumentNullException.ThrowIfNull(rows);
        RequireTransaction();

        await WriteAsync(NextIndexTable, NextRowsTable, songIds, rows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchIndexAnswer> QueryAsync(IReadOnlyList<string> termExpressions, string anyExpression, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(termExpressions);

        var termSongs = new List<IReadOnlySet<Guid>>();
        HashSet<Guid>? common = null;
        foreach (var expression in termExpressions)
        {
            var songs = new HashSet<Guid>();
            var command = await CommandAsync($"SELECT DISTINCT song_id FROM {IndexTable} WHERE {IndexTable} MATCH $p0;", [expression], cancellationToken).ConfigureAwait(false);
            await using (command.ConfigureAwait(false))
            {
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        songs.Add(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
                    }
                }
            }

            termSongs.Add(songs);
            common = common is null ? [.. songs] : [.. common.Where(songs.Contains)];
        }

        if (common is not { Count: > 0 })
        {
            return new SearchIndexAnswer(termSongs, [], new Dictionary<Guid, SearchSongOrder>());
        }

        var ids = JsonSerializer.Serialize(common.Select(Text));
        var rows = new List<SearchHitRow>();
        var hits = await CommandAsync(
            $"""
            SELECT song_id, field, owner_kind, owner_reference, owner_label, owner_state, highlight({IndexTable}, 0, char(1), char(2)), bm25({IndexTable})
            FROM {IndexTable}
            WHERE {IndexTable} MATCH $p0 AND song_id IN (SELECT value FROM json_each($p1));
            """,
            [anyExpression, ids],
            cancellationToken).ConfigureAwait(false);
        await using (hits.ConfigureAwait(false))
        {
            var reader = await hits.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var owner = reader.IsDBNull(2)
                        ? null
                        : new SearchOwner(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
                    rows.Add(new SearchHitRow(
                        Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                        reader.GetString(1),
                        owner,
                        reader.GetString(6),
                        reader.GetDouble(7)));
                }
            }
        }

        var order = new Dictionary<Guid, SearchSongOrder>();
        var songsCommand = await CommandAsync(
            "SELECT id, updated_utc, shortcode_number FROM songs WHERE id IN (SELECT value FROM json_each($p0));",
            [ids],
            cancellationToken).ConfigureAwait(false);
        await using (songsCommand.ConfigureAwait(false))
        {
            var reader = await songsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    order[Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture)] = new SearchSongOrder(
                        DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                        reader.GetInt64(2));
                }
            }
        }

        return new SearchIndexAnswer(termSongs, rows, order);
    }

    public async Task<int> StoredVersionAsync(CancellationToken cancellationToken)
    {
        var stored = await ScalarAsync("SELECT json_extract(value, '$.version') FROM settings WHERE key = $p0;", [VersionSettingKey], cancellationToken).ConfigureAwait(false);
        return stored is null or DBNull ? 1 : Convert.ToInt32(stored, CultureInfo.InvariantCulture);
    }

    public async Task WriteVersionAsync(int version, CancellationToken cancellationToken)
    {
        RequireTransaction();
        await ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES ($p0, json_object('version', $p1)) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            [VersionSettingKey, version],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsEmptyWithSongsAsync(CancellationToken cancellationToken) =>
        Convert.ToInt64(
            await ScalarAsync($"SELECT NOT EXISTS (SELECT 1 FROM {RowsTable}) AND EXISTS (SELECT 1 FROM songs);", [], cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;

    public async Task<bool> NextExistsAsync(CancellationToken cancellationToken) =>
        Convert.ToInt64(
            await ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $p0;", [NextIndexTable], cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;

    public async Task CreateNextAsync(CancellationToken cancellationToken)
    {
        RequireTransaction();
        await ExecuteAsync($"DROP TABLE IF EXISTS {NextRowsTable}; DROP TABLE IF EXISTS {NextIndexTable};", [], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(CreateStatements(NextIndexTable, NextRowsTable), [], cancellationToken).ConfigureAwait(false);
    }

    public async Task SwapAsync(CancellationToken cancellationToken)
    {
        RequireTransaction();

        // The row map's index is renamed with it, so the next rebuild can create its own again.
        await ExecuteAsync(
            $"""
            DROP TABLE {RowsTable};
            DROP TABLE {IndexTable};
            ALTER TABLE {NextIndexTable} RENAME TO {IndexTable};
            ALTER TABLE {NextRowsTable} RENAME TO {RowsTable};
            DROP INDEX ix_{NextRowsTable}_song_id;
            CREATE INDEX ix_{RowsTable}_song_id ON {RowsTable} (song_id);
            """,
            [],
            cancellationToken).ConfigureAwait(false);
    }

    public Task DropNextAsync(CancellationToken cancellationToken) =>
        ExecuteAsync($"DROP TABLE IF EXISTS {NextRowsTable}; DROP TABLE IF EXISTS {NextIndexTable};", [], cancellationToken);

    public async Task<int> SongCountAsync(CancellationToken cancellationToken) =>
        Convert.ToInt32(await ScalarAsync("SELECT count(*) FROM songs;", [], cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<Guid>> SongIdsAfterAsync(Guid? after, int count, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        var command = await CommandAsync(
            "SELECT id FROM songs WHERE $p0 IS NULL OR id > $p0 ORDER BY id LIMIT $p1;",
            [after is { } last ? Text(last) : null, count],
            cancellationToken).ConfigureAwait(false);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
                }
            }
        }

        return ids;
    }

    /// <summary>A Song ID as the catalog's tables store it: upper-case, hyphenated.</summary>
    private static string Text(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>Replaces every row of <paramref name="songIds"/> in one index pair with <paramref name="rows"/>.</summary>
    private async Task WriteAsync(string index, string map, IReadOnlyCollection<Guid> songIds, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken)
    {
        var ids = JsonSerializer.Serialize(songIds.Select(Text));
        await ExecuteAsync(
            $"""
            DELETE FROM {index} WHERE rowid IN (SELECT row_id FROM {map} WHERE song_id IN (SELECT value FROM json_each($p0)));
            DELETE FROM {map} WHERE song_id IN (SELECT value FROM json_each($p0));
            """,
            [ids],
            cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return;
        }

        var insert = await CommandAsync(
            $"""
            INSERT INTO {map} (song_id) VALUES ($p0);
            INSERT INTO {index} (rowid, text, song_id, field, owner_kind, owner_reference, owner_label, owner_state)
            VALUES (last_insert_rowid(), $p1, $p0, $p2, $p3, $p4, $p5, $p6);
            """,
            new object?[7],
            cancellationToken).ConfigureAwait(false);
        await using (insert.ConfigureAwait(false))
        {
            foreach (var row in rows)
            {
                insert.Parameters[0].Value = Text(row.SongId);
                insert.Parameters[1].Value = row.Text;
                insert.Parameters[2].Value = row.Field;
                insert.Parameters[3].Value = (object?)row.Owner?.Kind ?? DBNull.Value;
                insert.Parameters[4].Value = (object?)row.Owner?.Reference ?? DBNull.Value;
                insert.Parameters[5].Value = (object?)row.Owner?.Label ?? DBNull.Value;
                insert.Parameters[6].Value = (object?)row.Owner?.State ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void RequireTransaction()
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The search index changes only inside the transaction of the write it follows.");
        }
    }

    private async Task ExecuteAsync(string sql, object?[] parameters, CancellationToken cancellationToken)
    {
        var command = await CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
}
