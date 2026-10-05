using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Persistence;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Brings the database to the schema of this build before the app listens: opens the file, turns on
/// WAL, checks that the migration history is one this build can continue, and applies the pending
/// migrations one at a time. Any failure is one Error line naming the step, and the caller exits.
/// <para>
/// An upgrade of a database that already has data (its history has at least one migration) is
/// guarded: first a safety backup is taken and verified with the same routine as every other backup
/// (<see cref="IBackupWriter"/>, kind <c>safety</c>, to the backup mount when it is writable,
/// otherwise the fallback folder: environment paths only, never a database setting); then the marker
/// <c>upgrade-state.json</c> is written; then the migrations run. Success removes the marker and keeps
/// the three newest safety backups. A failed migration puts the safety backup back
/// (<see cref="UpgradeSafetyRestore"/>) and leaves the marker saying so, and a later start of the
/// same version refuses to run (<see cref="RecoverFailedUpgradeAsync"/>). A new, empty database
/// needs no safety backup.
/// </para>
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

    /// <summary>The step that takes and verifies the safety backup before an upgrade.</summary>
    public const string SafetyBackupStep = "safety-backup";

    /// <summary>The step that puts the safety backup back after a failed or interrupted upgrade.</summary>
    public const string SafetyRestoreStep = "safety-restore";

    /// <summary>The step that reads the marker an earlier upgrade left.</summary>
    public const string UpgradeMarkerStep = "upgrade-marker";

    /// <summary>The step that refuses to start the version whose upgrade failed.</summary>
    public const string FailedUpgradeStep = "failed-upgrade";

    private const string HistoryTable = "__EFMigrationsHistory";
    private const string LockTable = "__EFMigrationsLock";

    /// <summary>
    /// The first thing a start does with the data path, before a restore's leftovers are looked at
    /// and before the database is opened: deals with the marker an earlier upgrade left. When it says
    /// the database may be half-migrated (the process stopped during the upgrade, or putting the
    /// safety backup back failed), the safety backup is put back first. Then the version that began
    /// the upgrade refuses to start (one Error line, false), and any other version removes the marker
    /// and goes on to its own upgrade (true). True when there is no marker.
    /// </summary>
    /// <param name="services">The root provider of the built host.</param>
    /// <param name="startupLog">The logger for the failure line: it is never filtered by the configured level.</param>
    public static async Task<bool> RecoverFailedUpgradeAsync(IServiceProvider services, Serilog.ILogger startupLog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(startupLog);

        var markerFile = services.GetRequiredService<UpgradeMarkerFile>();
        var log = services.GetRequiredService<Serilog.ILogger>().ForContext(typeof(DatabaseStartup));
        UpgradeMarker? marker;
        try
        {
            marker = markerFile.Read();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Fail(
                startupLog,
                UpgradeMarkerStep,
                $"{markerFile.FilePath}, the marker an earlier database upgrade left, cannot be read ({exception.Message}); nothing was changed. "
                + $"If an upgrade was interrupted, stop n8Tracks and restore the newest safety backup with `{RestoreRunner.RestoreCommand(null)}`; otherwise delete the marker and start again.");
        }

        if (marker is null)
        {
            return true;
        }

        if (marker.MayBeHalfMigrated)
        {
            marker = marker with { Stage = UpgradeStages.Restoring, RestoreId = marker.RestoreId ?? Guid.CreateVersion7() };
            string? kept;
            try
            {
                markerFile.Write(marker);
                kept = await PutSafetyBackupBackAsync(services, marker, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                startupLog.Error(
                    exception,
                    "Database startup failed at step {Step}: {Reason}",
                    SafetyRestoreStep,
                    $"An upgrade of the database to {marker.TargetMigration} by n8Tracks {marker.ApplicationVersion} did not finish{FailedAt(marker)}, and putting back the safety backup taken before it failed ({exception.Message}). "
                    + $"The database was left as it was found. The safety backup is {marker.SafetyBackup.Path}: stop n8Tracks and restore it by hand with `{RestoreRunner.RestoreCommand(marker.SafetyBackup.Path)}`.");
                return false;
            }

            marker = marker with { Stage = UpgradeStages.Restored };
            markerFile.Write(marker);
            log.Warning(
                "An upgrade of the database to {TargetMigration} by n8Tracks {MarkerVersion} did not finish; the database from before it was restored from the safety backup {SafetyBackupPath} and passed its integrity check. The database it left is kept as {FailedUpgradeFile}",
                marker.TargetMigration,
                marker.ApplicationVersion,
                marker.SafetyBackup.Path,
                kept);
        }

        var version = services.GetRequiredService<ApplicationVersion>().Value;
        if (string.Equals(marker.ApplicationVersion, version, StringComparison.Ordinal))
        {
            return Fail(
                startupLog,
                FailedUpgradeStep,
                $"An earlier start of n8Tracks {version} failed to upgrade the database{FailedAt(marker)}, and the database from before the upgrade was restored from the safety backup {marker.SafetyBackup.Path}. "
                + $"This version does not try again: start a different version of n8Tracks, or delete {markerFile.FilePath} to let this one try again.");
        }

        markerFile.Clear();
        log.Information(
            "Removed the marker of a failed database upgrade by n8Tracks {MarkerVersion}; n8Tracks {ApplicationVersion} upgrades the database itself",
            marker.ApplicationVersion,
            version);
        return true;
    }

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

        UpgradeMarker? marker = null;
        string step;
        (UpgradeMarker Marker, Exception Error)? failedUpgrade = null;
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>();
            var log = scope.ServiceProvider.GetRequiredService<Serilog.ILogger>().ForContext(typeof(DatabaseStartup));
            step = OpenStep;

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

                    // A database that already has data is backed up first; a new one has nothing to lose.
                    if (applied.Count > 0)
                    {
                        step = SafetyBackupStep;
                        try
                        {
                            marker = await TakeSafetyBackupAsync(scope.ServiceProvider, applied[^1], known[^1], cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                        {
                            startupLog.Error(
                                exception,
                                "Database startup failed at step {Step}: {Reason}",
                                step,
                                $"The safety backup to take before upgrading the database from {applied[^1]} to {known[^1]} could not be taken: {exception.Message} "
                                + "No migration was applied; the database is as it was. Make the backup location writable (or free some space), and start again.");
                            return false;
                        }

                        log.Information(
                            "Took a safety backup before upgrading the database from {FromMigration} to {TargetMigration}: {SafetyBackupPath}",
                            marker.FromMigration,
                            marker.TargetMigration,
                            marker.SafetyBackup.Path);
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
                if (marker is not null)
                {
                    await FinishUpgradeAsync(scope.ServiceProvider, marker, log, cancellationToken).ConfigureAwait(false);
                }

                scope.ServiceProvider.GetRequiredService<MigrationStateHolder>().Set(
                    new MigrationState(MigrationStatus.UpToDate, last, pending.Count > 0 ? MigrationOutcome.Succeeded : MigrationOutcome.None));

                log.Information(
                    "Database is up to date: {AppliedCount} migration(s) applied at this start, last applied {LastAppliedMigrationId}",
                    pending.Count,
                    last);

                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (marker is null)
                {
                    startupLog.Error(exception, "Database startup failed at step {Step}: {Reason}", step, exception.Message);
                    return false;
                }

                // The upgrade began: the safety backup goes back once this scope's connection is closed.
                failedUpgrade = (marker, exception);
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        // Reached only from the failure of a begun upgrade.
        if (failedUpgrade is { } failed)
        {
            await PutBackAfterFailureAsync(services, startupLog, failed.Marker, step, failed.Error, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// After a failed upgrade: the safety backup goes back, the marker says so, and the Error line
    /// names the migration that failed and says the database was restored.
    /// </summary>
    private static async Task PutBackAfterFailureAsync(
        IServiceProvider services,
        Serilog.ILogger startupLog,
        UpgradeMarker marker,
        string step,
        Exception failure,
        CancellationToken cancellationToken)
    {
        var markerFile = services.GetRequiredService<UpgradeMarkerFile>();
        marker = marker with { Stage = UpgradeStages.Restoring, FailedMigration = step, RestoreId = Guid.CreateVersion7() };
        string? kept;
        try
        {
            markerFile.Write(marker);
            kept = await PutSafetyBackupBackAsync(services, marker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            startupLog.Error(
                exception,
                "Database startup failed at step {Step}: {Reason}",
                SafetyRestoreStep,
                $"The upgrade of the database failed at {step} ({failure.Message}), and putting back the safety backup taken before it failed too ({exception.Message}). "
                + $"The database is left as the failed upgrade left it, and the safety backup is {marker.SafetyBackup.Path}: stop n8Tracks and restore it by hand with `{RestoreRunner.RestoreCommand(marker.SafetyBackup.Path)}`.");
            return;
        }

        markerFile.Write(marker with { Stage = UpgradeStages.Restored });
        startupLog.Error(
            failure,
            "Database startup failed at step {Step}: {Reason}",
            step,
            $"The upgrade of the database failed at {step}: {failure.Message} The database from before the upgrade was restored from the safety backup {marker.SafetyBackup.Path} and passed its integrity check"
            + (kept is null ? "." : $"; the database the failed upgrade left is kept as {kept}.")
            + $" This version does not try the upgrade again: start a different version of n8Tracks, or delete {markerFile.FilePath} to let this one try again.");
    }

    /// <summary>Takes and verifies the safety backup, then writes the marker. Returns the marker.</summary>
    private static async Task<UpgradeMarker> TakeSafetyBackupAsync(
        IServiceProvider services,
        string fromMigration,
        string targetMigration,
        CancellationToken cancellationToken)
    {
        var storage = services.GetRequiredService<IBackupStorage>();

        // A backup a stopped start was building is removed first; a stopped start never wrote the marker.
        storage.RemoveLeftoverTemporaryFolders();
        var destination = storage.ResolveDestination();
        var created = await services.GetRequiredService<IBackupWriter>()
            .CreateAsync(destination, Guid.CreateVersion7(), BackupKind.Safety, static (_, _) => { }, cancellationToken)
            .ConfigureAwait(false);

        var marker = new UpgradeMarker(
            services.GetRequiredService<ApplicationVersion>().Value,
            fromMigration,
            targetMigration,
            UpgradeSafetyBackup.From(created, destination.Path),
            UpgradeStages.Migrating,
            services.GetRequiredService<TimeProvider>().GetUtcNow());
        services.GetRequiredService<UpgradeMarkerFile>().Write(marker);
        return marker;
    }

    /// <summary>
    /// After a successful upgrade: the three newest safety backups are kept (never the one the marker
    /// names), and the marker is removed. A backup retention cannot delete is only a warning.
    /// </summary>
    private static async Task FinishUpgradeAsync(IServiceProvider services, UpgradeMarker marker, Serilog.ILogger log, CancellationToken cancellationToken)
    {
        var storage = services.GetRequiredService<IBackupStorage>();
        try
        {
            var all = await storage.ListAsync(cancellationToken).ConfigureAwait(false);
            var current = all.FirstOrDefault(archive => archive.Location == marker.SafetyBackup.BackupLocation && archive.Name == marker.SafetyBackup.Name);
            storage.DeleteForRetention(BackupRetention.SelectSafety(all, current));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Warning(exception, "Could not apply safety-backup retention after the upgrade; it is applied after the next one");
        }

        services.GetRequiredService<UpgradeMarkerFile>().Clear();
        log.Information(
            "Upgraded the database from {FromMigration} to {TargetMigration}; the safety backup taken before it is kept: {SafetyBackupPath}",
            marker.FromMigration,
            marker.TargetMigration,
            marker.SafetyBackup.Path);
    }

    /// <summary>Puts the safety backup back in a scope of its own; returns where the half-migrated database was kept.</summary>
    private static async Task<string?> PutSafetyBackupBackAsync(IServiceProvider services, UpgradeMarker marker, CancellationToken cancellationToken)
    {
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<UpgradeSafetyRestore>().RestoreAsync(marker, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FailedAt(UpgradeMarker marker) => marker.FailedMigration is { } failed ? $" at {failed}" : string.Empty;

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
