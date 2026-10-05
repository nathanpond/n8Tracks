using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Application.Persistence;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Brings the database to the schema of this build before the app listens: opens the file, turns on
/// WAL, checks that the migration history is one this build can continue, and applies the pending
/// migrations one at a time. Any failure is one Error line naming the step, and the caller exits.
/// </summary>
public static class DatabaseStartup
{
    /// <summary>The step that opens the file and reads its header.</summary>
    public const string OpenStep = "open";

    /// <summary>The step that sets the journal mode.</summary>
    public const string ConfigureStep = "configure";

    /// <summary>The step that reads the migration history.</summary>
    public const string HistoryStep = "history";

    /// <summary>The step that compares the migration history with the migrations of this build.</summary>
    public const string SchemaVersionStep = "schema-version";

    /// <summary>The step that checks no other process holds, or left behind, the migration lock.</summary>
    public const string MigrationLockStep = "migration-lock";

    private const string HistoryTable = "__EFMigrationsHistory";
    private const string LockTable = "__EFMigrationsLock";

    /// <summary>
    /// Runs the startup steps in a scope of their own. Returns false after logging the failure to
    /// <paramref name="startupLog"/>; a step that applies a migration is named by the migration ID.
    /// </summary>
    /// <param name="services">The root provider of the built host.</param>
    /// <param name="startupLog">The logger for the failure line: it is never filtered by the configured level.</param>
    public static async Task<bool> RunAsync(IServiceProvider services, Serilog.ILogger startupLog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(startupLog);

        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>();
            var log = scope.ServiceProvider.GetRequiredService<Serilog.ILogger>().ForContext(typeof(DatabaseStartup));
            var step = OpenStep;

            try
            {
                await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connection = context.Database.GetDbConnection();

                // A file that is not a SQLite database opens without complaint; reading the header is what fails.
                await ScalarAsync(connection, "PRAGMA schema_version;", cancellationToken).ConfigureAwait(false);

                step = ConfigureStep;
                var journalMode = await ScalarAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
                if (!string.Equals(journalMode as string, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(startupLog, step, $"The database could not be switched to WAL journaling; its journal mode is '{journalMode}'.");
                }

                step = HistoryStep;
                var known = context.Database.GetMigrations().ToList();
                var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                var hasOtherTables = await CountAsync(
                    connection,
                    $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('{HistoryTable}', '{LockTable}');",
                    cancellationToken).ConfigureAwait(false) > 0;

                step = SchemaVersionStep;
                if (SchemaVersionCheck.Refusal(known, applied, hasOtherTables) is { } refusal)
                {
                    return Fail(startupLog, step, refusal);
                }

                var pending = known.Except(applied, StringComparer.Ordinal).ToList();
                if (pending.Count > 0)
                {
                    step = MigrationLockStep;
                    if (await MigrationLockIsHeldAsync(connection, cancellationToken).ConfigureAwait(false))
                    {
                        return Fail(
                            startupLog,
                            step,
                            $"The migration lock is held: another n8Tracks process is upgrading this database, or an earlier upgrade was interrupted. "
                            + $"Make sure no other instance uses this data path; if none does, restore the database from a backup, or delete the row in the {LockTable} table, and start again.");
                    }

                    var migrator = context.GetService<IMigrator>();
                    foreach (var migration in pending)
                    {
                        step = migration;
                        await migrator.MigrateAsync(migration, cancellationToken).ConfigureAwait(false);
                        log.Information("Applied database migration {MigrationId}", migration);
                    }
                }

                step = HistoryStep;
                var last = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).Last();
                scope.ServiceProvider.GetRequiredService<MigrationStateHolder>().Set(new MigrationState(MigrationStatus.UpToDate, last));

                log.Information(
                    "Database is up to date: {AppliedCount} migration(s) applied at this start, last applied {LastAppliedMigrationId}",
                    pending.Count,
                    last);

                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                startupLog.Error(exception, "Database startup failed at step {Step}: {Reason}", step, exception.Message);
                return false;
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether the database already has exactly this build's schema. It reads the header, the
    /// migration history, and the migration lock, and changes nothing: no journal mode, no migration.
    /// The file must exist; the caller checks.
    /// </summary>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The file cannot be opened, or is not a SQLite database.</exception>
    internal static async Task<DatabaseCondition> CheckWithoutChangingAsync(N8TracksDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = context.Database.GetDbConnection();
            await ScalarAsync(connection, "PRAGMA schema_version;", cancellationToken).ConfigureAwait(false);

            if (await MigrationLockIsHeldAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                return DatabaseCondition.Upgrading;
            }

            var known = context.Database.GetMigrations().ToList();
            var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();

            return known.SequenceEqual(applied, StringComparer.Ordinal) ? DatabaseCondition.Current : DatabaseCondition.SchemaMismatch;
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static bool Fail(Serilog.ILogger startupLog, string step, string reason)
    {
        startupLog.Error("Database startup failed at step {Step}: {Reason}", step, reason);
        return false;
    }

    /// <summary>
    /// EF Core's SQLite migration lock is a row in a table, and it waits for that row to go without
    /// a time limit. A process killed in the middle of an upgrade leaves the row behind, so the next
    /// start would wait forever, saying nothing. This turns that wait into a failure the operator can read.
    /// </summary>
    internal static async Task<bool> MigrationLockIsHeldAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var tableExists = await CountAsync(
            connection,
            $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{LockTable}';",
            cancellationToken).ConfigureAwait(false) > 0;

        return tableExists
            && await CountAsync(connection, $"SELECT count(*) FROM \"{LockTable}\";", cancellationToken).ConfigureAwait(false) > 0;
    }

    private static async Task<long> CountAsync(DbConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToInt64(await ScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
