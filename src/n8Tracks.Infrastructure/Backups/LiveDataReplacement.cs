using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Persistence;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>Test-only seams in a restore's replacement. Empty in the app.</summary>
internal sealed class RestoreTestHooks
{
    /// <summary>Called before each migration of the restored database; throwing fails the restore there.</summary>
    public Func<string, CancellationToken, Task>? BeforeMigration { get; init; }

    /// <summary>Called as putting the previous data back begins; throwing makes putting back fail.</summary>
    public Action? BeforePutBack { get; init; }

    /// <summary>Called after each item the swap moves, aside or in, with its name; throwing stops the swap there.</summary>
    public Action<string>? AfterSwapMove { get; init; }

    /// <summary>Called after each item putting back moves home, with its name; throwing stops putting back there.</summary>
    public Action<string>? AfterPutBackMove { get; init; }
}

/// <summary>
/// The replacement of the live data by a restore, reversible at every step. The live items are the
/// database file with its <c>-wal</c>, <c>-shm</c>, and <c>-journal</c> companions, and the managed
/// <c>assets</c> folder, all directly under the data path. Before any of them moves, the journal
/// <c>restore-previous/restore.json</c> is written; when the swap begins it records which items
/// existed. The swap renames those into <c>restore-previous</c> (the companions before the database,
/// so the database is never left beside a stranger's write-ahead log) and moves the staged database
/// and assets in. Putting back moves every item still in <c>restore-previous</c> home, replacing what
/// the swap brought in, and removes what the swap brought in that had no previous item; an item that
/// existed and is not there was never moved. Each step is a rename on one disk, and the journal is
/// replaced atomically, so putting back can be repeated after a failure part way, or after a restart.
/// Nothing outside the data path is written; the media mount is never touched.
/// </summary>
internal sealed partial class LiveDataReplacement(
    N8TracksOptions options,
    IServiceScopeFactory scopes,
    RestoreTestHooks hooks,
    ILogger<LiveDataReplacement> logger) : ILiveDataReplacement
{
    public const string DatabaseFileName = SqliteDatabase.FileName;
    public const string PreviousFolderName = "restore-previous";
    public const string JournalFileName = "restore.json";

    /// <summary>The start of the name of a folder the container command's restore keeps the previous data in.</summary>
    public const string KeptFolderPrefix = "before-restore-";

    /// <summary>What a job the archive recorded as queued or running says once the restore has finished.</summary>
    public const string SupersededError = "superseded by restore";

    /// <summary>The live items, in the order they are moved aside.</summary>
    public static readonly IReadOnlyList<string> Items =
    [
        DatabaseFileName + "-wal",
        DatabaseFileName + "-shm",
        DatabaseFileName + "-journal",
        DatabaseFileName,
        BackupWriter.AssetsFolderName,
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string PreviousDataFolder => Path.Combine(options.DataPath, PreviousFolderName);

    private string JournalPath => Path.Combine(PreviousDataFolder, JournalFileName);

    public RestoreJournal? FindJournal() => ReadJournal() is { } stored ? new RestoreJournal(stored.Entry(), stored.SwapBegan) : null;

    public void Prepare(RestoreJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (ReadJournal() is { SwapBegan: true })
        {
            // A swap that never ended: its previous data must be put back first, never overwritten.
            throw new InvalidOperationException("An earlier restore's previous data has not been put back.");
        }

        Directory.CreateDirectory(PreviousDataFolder);
        WriteJournal(StoredJournal.From(entry, swapBegan: false, existed: []));
    }

    public void Swap(StagedRestore staged)
    {
        ArgumentNullException.ThrowIfNull(staged);

        var journal = ReadJournal() ?? throw new InvalidOperationException("The restore's journal is missing; nothing was moved.");
        if (journal.SwapBegan)
        {
            throw new InvalidOperationException("The swap has begun already.");
        }

        var stagedDatabase = Path.Combine(staged.Folder, DatabaseFileName);
        if (!File.Exists(stagedDatabase))
        {
            throw new InvalidOperationException("The staged database is missing; nothing was moved.");
        }

        CloseConnections();
        var existed = Items.Where(item => ItemExists(Live(item))).ToArray();
        WriteJournal(journal with { SwapBegan = true, Existed = existed });

        foreach (var item in existed)
        {
            MoveItem(Live(item), Aside(item));
            hooks.AfterSwapMove?.Invoke(item);
        }

        File.Move(stagedDatabase, Live(DatabaseFileName));
        hooks.AfterSwapMove?.Invoke(DatabaseFileName);
        var stagedAssets = Path.Combine(staged.Folder, BackupWriter.AssetsFolderName);
        if (Directory.Exists(stagedAssets))
        {
            Directory.Move(stagedAssets, Live(BackupWriter.AssetsFolderName));
            hooks.AfterSwapMove?.Invoke(BackupWriter.AssetsFolderName);
        }

        LogSwapped(logger, journal.RestoreId);
        TryDeleteFolder(staged.Folder);
    }

    public async Task MigrateAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        CloseConnections();
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>();
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var connection = context.Database.GetDbConnection();
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = "PRAGMA journal_mode = WAL;";
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                }

                var known = context.Database.GetMigrations().ToList();
                var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                if (SchemaVersionCheck.Refusal(known, applied, hasOtherTables: true) is { } refusal)
                {
                    throw new InvalidOperationException(refusal);
                }

                if (await DatabaseStartup.MigrationLockIsHeldAsync(connection, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The restored database holds the migration lock.");
                }

                var pending = known.Except(applied, StringComparer.Ordinal).ToList();
                var migrator = context.GetService<IMigrator>();
                for (var index = 0; index < pending.Count; index++)
                {
                    if (hooks.BeforeMigration is { } hook)
                    {
                        await hook(pending[index], cancellationToken).ConfigureAwait(false);
                    }

                    await migrator.MigrateAsync(pending[index], cancellationToken).ConfigureAwait(false);
                    LogMigrated(logger, pending[index]);
                    progress(100 * (index + 1) / pending.Count);
                }

                var last = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).Last();
                scope.ServiceProvider.GetRequiredService<MigrationStateHolder>().Set(new MigrationState(MigrationStatus.UpToDate, last));
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task FinishAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>();
            var finished = UtcText.From(now);
            await context.Sessions.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await context.Jobs
                .Where(static job => job.Status == JobRecord.Queued || job.Status == JobRecord.Running)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static job => job.Status, JobRecord.Failed)
                        .SetProperty(static job => job.Error, SupersededError)
                        .SetProperty(static job => job.FinishedUtc, finished)
                        .SetProperty(static job => job.Payload, (string?)null),
                    cancellationToken).ConfigureAwait(false);
        }
    }

    public bool PutBack()
    {
        var journal = ReadJournal();
        if (journal is null)
        {
            return false;
        }

        if (journal.SwapBegan)
        {
            hooks.BeforePutBack?.Invoke();
            CloseConnections();
            foreach (var item in Items)
            {
                var aside = Aside(item);
                if (ItemExists(aside))
                {
                    DeleteItem(Live(item));
                    MoveItem(aside, Live(item));
                    hooks.AfterPutBackMove?.Invoke(item);
                }
                else if (!journal.Existed.Contains(item, StringComparer.Ordinal))
                {
                    // Brought in by the swap, or made by SQLite for the restored database.
                    DeleteItem(Live(item));
                }
            }

            LogPutBack(logger, journal.RestoreId);
        }

        // Only now does the journal go; the folder goes only if nothing else is in it.
        File.Delete(JournalPath);
        TryDeleteFolder(Path.Combine(options.DataPath, RestoreArchives.WorkFolderName, journal.RestoreId.ToString("N")));
        try
        {
            Directory.Delete(PreviousDataFolder, recursive: false);
        }
        catch (IOException exception)
        {
            LogPreviousFolderLeft(logger, exception);
        }

        return true;
    }

    public void Discard()
    {
        var journal = ReadJournal();
        if (journal is not null)
        {
            TryDeleteFolder(Path.Combine(options.DataPath, RestoreArchives.WorkFolderName, journal.RestoreId.ToString("N")));
        }

        var previous = new DirectoryInfo(PreviousDataFolder);
        if (previous.Exists && previous.LinkTarget is null)
        {
            previous.Delete(recursive: true);
            LogDiscarded(logger);
        }
    }

    public string? KeepPrevious(DateTimeOffset now)
    {
        var journal = ReadJournal();
        if (journal is not null)
        {
            TryDeleteFolder(Path.Combine(options.DataPath, RestoreArchives.WorkFolderName, journal.RestoreId.ToString("N")));
        }

        var previous = new DirectoryInfo(PreviousDataFolder);
        if (!previous.Exists || previous.LinkTarget is not null)
        {
            return null;
        }

        File.Delete(JournalPath);
        if (!previous.EnumerateFileSystemInfos().Any())
        {
            previous.Delete();
            return null;
        }

        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(options.DataPath, KeptFolderPrefix + stamp);
        for (var attempt = 2; Directory.Exists(target) || File.Exists(target); attempt++)
        {
            target = Path.Combine(options.DataPath, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{KeptFolderPrefix}{stamp}-{attempt}"));
        }

        Directory.Move(PreviousDataFolder, target);
        LogKept(logger, target);
        return target;
    }

    /// <summary>Closes every idle pooled connection, so no handle is left on a file about to move.</summary>
    private static void CloseConnections() => SqliteConnection.ClearAllPools();

    private string Live(string item) => Path.Combine(options.DataPath, item);

    private string Aside(string item) => Path.Combine(PreviousDataFolder, item);

    private static bool ItemExists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static void MoveItem(string from, string to)
    {
        if (Directory.Exists(from) && new DirectoryInfo(from).LinkTarget is null)
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    private static void DeleteItem(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists && directory.LinkTarget is null)
        {
            directory.Delete(recursive: true);
        }
        else if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            File.Delete(path);
        }
    }

    private void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Removed at the next start instead.
            LogWorkFolderLeft(logger, exception);
        }
    }

    private StoredJournal? ReadJournal()
    {
        if (!File.Exists(JournalPath))
        {
            return null;
        }

        // A journal that cannot be read is not guessed at: the caller stays in maintenance.
        try
        {
            return JsonSerializer.Deserialize<StoredJournal>(File.ReadAllBytes(JournalPath), Json) is { Existed: not null, ArchiveName: not null } journal
                ? journal
                : throw new InvalidOperationException("The restore's journal cannot be read.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The restore's journal cannot be read.", exception);
        }
    }

    private void WriteJournal(StoredJournal journal)
    {
        var temporary = JournalPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, JournalPath, overwrite: true);
    }

    private sealed record StoredJournal(
        Guid RestoreId,
        string ArchiveName,
        string? SafetyBackupLocation,
        string? SafetyBackupName,
        string? SafetyBackupPath,
        DateTimeOffset StartedUtc,
        bool SwapBegan,
        string[] Existed)
    {
        public static StoredJournal From(RestoreJournalEntry entry, bool swapBegan, string[] existed) => new(
            entry.RestoreId,
            entry.ArchiveName,
            entry.SafetyBackup is null ? null : entry.SafetyBackup.Location == BackupLocation.Mount ? "mount" : "data",
            entry.SafetyBackup?.Name,
            entry.SafetyBackup?.Path,
            entry.StartedUtc,
            swapBegan,
            existed);

        public RestoreJournalEntry Entry() => new(
            RestoreId,
            ArchiveName,
            SafetyBackupName is null || SafetyBackupPath is null
                ? null
                : new SafetyBackupRecord(SafetyBackupLocation == "mount" ? BackupLocation.Mount : BackupLocation.Data, SafetyBackupName, SafetyBackupPath),
            StartedUtc);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restore {RestoreId}: the live data was moved aside and the backup's moved in")]
    private static partial void LogSwapped(ILogger logger, Guid restoreId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied database migration {MigrationId} to the restored database")]
    private static partial void LogMigrated(ILogger logger, string migrationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restore {RestoreId}: the data from before the restore was put back")]
    private static partial void LogPutBack(ILogger logger, Guid restoreId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the data a finished restore had moved aside")]
    private static partial void LogDiscarded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kept the data from before the restore in {KeptFolder}")]
    private static partial void LogKept(ILogger logger, string keptFolder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The folder of a restore's previous data was not empty after putting it back, so it was left in place")]
    private static partial void LogPreviousFolderLeft(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove a restore's work folder; it is removed at the next start")]
    private static partial void LogWorkFolderLeft(ILogger logger, Exception exception);
}
