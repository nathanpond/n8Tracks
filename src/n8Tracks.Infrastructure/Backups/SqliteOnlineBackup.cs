using System.Globalization;
using Microsoft.Data.Sqlite;
using n8Tracks.Infrastructure.Persistence;
using SQLitePCL;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>
/// Copies the live database with SQLite's online backup while the app keeps running. The copy is
/// made in steps of <see cref="DefaultPagesPerStep"/> pages, so progress can be reported from the
/// page counts, under one read transaction held on the source for the whole copy: under WAL, writers
/// carry on meanwhile, and the copy is the database as it was when that transaction began, one
/// consistent point in time, however many writes land during it. The copy is left in rollback
/// journal mode, one self-contained file.
/// </summary>
internal static class SqliteOnlineBackup
{
    /// <summary>How many pages each step copies: 4 MB at the default page size.</summary>
    public const int DefaultPagesPerStep = 1024;

    /// <summary>How long a step that met a lock waits before it tries again.</summary>
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Copies the database at <paramref name="sourceFile"/>, which must exist, to a new file at
    /// <paramref name="destinationFile"/>. After each step, <paramref name="progress"/> is given the
    /// pages copied so far and the total.
    /// </summary>
    /// <exception cref="SqliteException">SQLite refused the copy.</exception>
    public static async Task CopyAsync(
        string sourceFile,
        string destinationFile,
        Action<int, int> progress,
        CancellationToken cancellationToken,
        int pagesPerStep = DefaultPagesPerStep)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(destinationFile);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentOutOfRangeException.ThrowIfLessThan(pagesPerStep, 1);

        var source = new SqliteConnection(SqliteDatabase.ExistingDatabaseConnectionString(sourceFile));
        await using (source.ConfigureAwait(false))
        {
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(source, null, BusyTimeout, cancellationToken).ConfigureAwait(false);

            // The read transaction the whole copy is made under: begun here and held until the end,
            // so no step sees a write that landed after it began.
            var snapshot = source.BeginTransaction(deferred: true);
            await using (snapshot.ConfigureAwait(false))
            {
                await ExecuteAsync(source, snapshot, "SELECT count(*) FROM sqlite_master;", cancellationToken).ConfigureAwait(false);

                var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = destinationFile,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString());
                await using (destination.ConfigureAwait(false))
                {
                    await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await StepAllAsync(source, destination, progress, pagesPerStep, cancellationToken).ConfigureAwait(false);

                    // The copy carries the source's WAL flag in its header; one file needs no journal beside it.
                    await ExecuteAsync(destination, null, "PRAGMA journal_mode = DELETE;", cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static string BusyTimeout { get; } =
        string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {SqliteDatabase.BusyTimeoutMilliseconds};");

    private static async Task StepAllAsync(
        SqliteConnection source,
        SqliteConnection destination,
        Action<int, int> progress,
        int pagesPerStep,
        CancellationToken cancellationToken)
    {
        var backup = raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
        if (backup is null || backup.IsInvalid)
        {
            SqliteException.ThrowExceptionForRC(raw.sqlite3_errcode(destination.Handle), destination.Handle);
            throw new SqliteException("The online backup could not start.", raw.SQLITE_ERROR);
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rc = raw.sqlite3_backup_step(backup, pagesPerStep);
                var total = raw.sqlite3_backup_pagecount(backup);
                progress(total - raw.sqlite3_backup_remaining(backup), total);

                if (rc == raw.SQLITE_DONE)
                {
                    break;
                }

                if (rc is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
                {
                    await Task.Delay(BusyRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                else if (rc != raw.SQLITE_OK)
                {
                    SqliteException.ThrowExceptionForRC(rc, destination.Handle);
                }
            }
        }
        catch
        {
            // The step's own failure (or the cancellation) is the one to report.
            _ = raw.sqlite3_backup_finish(backup);
            throw;
        }

        var finished = raw.sqlite3_backup_finish(backup);
        if (finished != raw.SQLITE_OK)
        {
            SqliteException.ThrowExceptionForRC(finished, destination.Handle);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
