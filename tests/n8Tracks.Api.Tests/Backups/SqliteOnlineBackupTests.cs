using Microsoft.Data.Sqlite;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Api.Tests.Backups;

/// <summary>The online copy on its own: a database with known rows, and one written to while it is copied.</summary>
public sealed class SqliteOnlineBackupTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("n8tracks-online-backup-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task ACopyOfADatabaseWithKnownRowsOpensToTheSameRows()
    {
        var source = Path.Combine(folder, "source.db");
        await using (var connection = await OpenAsync(source))
        {
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL; CREATE TABLE songs (id INTEGER PRIMARY KEY, body TEXT NOT NULL);");
            for (var i = 0; i < 300; i++)
            {
                await ExecuteAsync(connection, $"INSERT INTO songs (body) VALUES ('Song {i} {new string('x', 500)}');");
            }
        }

        var copy = Path.Combine(folder, "copy.db");
        var reports = new List<(int Done, int Total)>();
        await SqliteOnlineBackup.CopyAsync(source, copy, (done, total) => reports.Add((done, total)), CancellationToken.None, pagesPerStep: 8);

        Assert.Equal(await RowsAsync(source, "songs"), await RowsAsync(copy, "songs"));
        Assert.Equal(300, (await RowsAsync(copy, "songs")).Count);
        Assert.Equal("ok", await ScalarAsync(copy, "PRAGMA integrity_check;"));

        // One self-contained file, not a WAL database that needs a journal beside it.
        Assert.Equal("delete", await ScalarAsync(copy, "PRAGMA journal_mode;"));

        // Progress came in steps, from the page counts, up to all of them.
        Assert.True(reports.Count > 2, $"{reports.Count} report(s)");
        Assert.Equal(reports[^1].Total, reports[^1].Done);
        Assert.True(reports.Zip(reports.Skip(1)).All(static pair => pair.First.Done <= pair.Second.Done));
    }

    /// <summary>
    /// Writes land between the steps of the copy, from another connection, as the app's would. The
    /// copy passes an integrity check and is one point in time: the rows as they were when it began,
    /// with every pair of tables (always written together) still matching.
    /// </summary>
    [Fact]
    public async Task WritesDuringTheCopyNeitherCorruptItNorAppearInIt()
    {
        var source = Path.Combine(folder, "source.db");
        await using (var connection = await OpenAsync(source))
        {
            await ExecuteAsync(
                connection,
                "PRAGMA journal_mode = WAL; CREATE TABLE a (id INTEGER PRIMARY KEY, body TEXT); CREATE TABLE b (id INTEGER PRIMARY KEY, body TEXT);");
            for (var i = 0; i < 200; i++)
            {
                await ExecuteAsync(connection, $"BEGIN; INSERT INTO a (body) VALUES ('{new string('a', 400)}'); INSERT INTO b (body) VALUES ('{new string('b', 400)}'); COMMIT;");
            }
        }

        var before = await RowsAsync(source, "a");
        var writes = 0;
        await using var writer = await OpenAsync(source);
        await ExecuteAsync(writer, "PRAGMA busy_timeout = 5000;");

        var copy = Path.Combine(folder, "copy.db");
        await SqliteOnlineBackup.CopyAsync(
            source,
            copy,
            (done, total) =>
            {
                if (done < total)
                {
                    // Between two steps: a new pair, a change to an early page, and a delete, committed.
                    using var command = writer.CreateCommand();
                    command.CommandText = $"""
                        BEGIN;
                        INSERT INTO a (body) VALUES ('{new string('n', 400)}');
                        INSERT INTO b (body) VALUES ('{new string('n', 400)}');
                        UPDATE a SET body = 'changed' WHERE id = 1;
                        DELETE FROM b WHERE id = {200 - writes};
                        DELETE FROM a WHERE id = {200 - writes};
                        COMMIT;
                        """;
                    command.ExecuteNonQuery();
                    writes++;
                }
            },
            CancellationToken.None,
            pagesPerStep: 4);

        Assert.True(writes > 5, $"Only {writes} write(s) landed during the copy.");
        Assert.Equal("ok", await ScalarAsync(copy, "PRAGMA integrity_check;"));
        Assert.Equal(before, await RowsAsync(copy, "a"));
        Assert.Equal(await ScalarAsync(copy, "SELECT count(*) FROM a;"), await ScalarAsync(copy, "SELECT count(*) FROM b;"));

        // Complement: the writes did land, in the source.
        Assert.NotEqual(before, await RowsAsync(source, "a"));
        Assert.Equal("ok", await ScalarAsync(source, "PRAGMA integrity_check;"));
    }

    private static async Task<SqliteConnection> OpenAsync(string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string file, string sql)
    {
        await using var connection = await OpenAsync(file);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> RowsAsync(string file, string table)
    {
        await using var connection = await OpenAsync(file);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id || ':' || body FROM {table} ORDER BY id;";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
