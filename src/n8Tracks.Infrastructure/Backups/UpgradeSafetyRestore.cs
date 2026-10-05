using System.Globalization;
using Microsoft.Data.Sqlite;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>
/// Puts an upgrade's safety backup back over the database the upgrade left, with the restore
/// story's reversible replacement (<see cref="ILiveDataReplacement"/>): the archive's database is
/// unpacked into a work folder, its checksum checked again, and swapped in for the live database and
/// its <c>-wal</c>, <c>-shm</c>, and <c>-journal</c> files; the managed assets, which no migration
/// touches, are moved aside and back unchanged. The restored file must pass
/// <c>PRAGMA integrity_check</c>, or the half-migrated database is put back where it was. Once it
/// passes, the half-migrated database and its companions are kept beside the live one with a
/// <c>.failed-upgrade</c> suffix (a time suffix when one is there already), for diagnosis.
/// Everything moves within the data path; nothing under the media mount is read or written.
/// </summary>
internal sealed class UpgradeSafetyRestore(
    N8TracksOptions options,
    IBackupStorage storage,
    IRestoreArchives archives,
    ILiveDataReplacement replacement,
    TimeProvider time)
{
    public const string FailedUpgradeSuffix = ".failed-upgrade";

    private static readonly string[] Companions = ["-wal", "-shm", "-journal"];

    /// <summary>
    /// Restores the safety backup <paramref name="marker"/> names, under its <see cref="UpgradeMarker.RestoreId"/>.
    /// Work an earlier attempt under the same ID left is undone first. Returns where the half-migrated
    /// database was kept, or null when there was no live database to keep.
    /// </summary>
    /// <exception cref="Exception">
    /// Putting it back failed; the live database is the one the upgrade left (or, if even that could
    /// not be moved back, the replacement's journal says where it is, for the next attempt).
    /// </exception>
    public async Task<string?> RestoreAsync(UpgradeMarker marker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var restoreId = marker.RestoreId ?? throw new ArgumentException("The marker has no restore ID.", nameof(marker));

        if (replacement.FindJournal() is { } earlier)
        {
            if (earlier.Entry.RestoreId != restoreId)
            {
                throw new InvalidOperationException(
                    $"{replacement.PreviousDataFolder} holds the record of a restore that is not this upgrade's; nothing was changed.");
            }

            // An earlier attempt stopped part way: undo it, so this one starts from the upgrade's database.
            if (earlier.SwapBegan)
            {
                replacement.PutBack();
            }
            else
            {
                replacement.Discard();
            }
        }

        var backup = marker.SafetyBackup;
        if (await storage.FindAsync(backup.BackupLocation, backup.Name, cancellationToken).ConfigureAwait(false) is not { Validity: BackupValidity.Valid })
        {
            throw new RestoreStagingException($"The safety backup {backup.Path} is not there, or is not a backup this version can read.");
        }

        replacement.Prepare(new RestoreJournalEntry(restoreId, backup.Name, null, time.GetUtcNow()));
        StagedRestore staged;
        try
        {
            staged = await archives.StageAsync(new RestoreSource.Listed(backup.BackupLocation, backup.Name), restoreId, static _ => { }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            replacement.Discard();
            throw;
        }

        // The live assets stay as they are: the swap moves them aside, and they come back below.
        var stagedAssets = Path.Combine(staged.Folder, BackupWriter.AssetsFolderName);
        if (Directory.Exists(stagedAssets))
        {
            Directory.Delete(stagedAssets, recursive: true);
        }

        try
        {
            replacement.Swap(staged);
            CheckIntegrity(SqliteDatabase.FilePath(options.DataPath));
        }
        catch
        {
            replacement.PutBack();
            throw;
        }

        var kept = KeepHalfMigrated();
        var asideAssets = Path.Combine(replacement.PreviousDataFolder, BackupWriter.AssetsFolderName);
        var liveAssets = Path.Combine(options.DataPath, BackupWriter.AssetsFolderName);
        if (Directory.Exists(asideAssets) && !Directory.Exists(liveAssets))
        {
            Directory.Move(asideAssets, liveAssets);
        }

        // Removes the journal; anything unexpected still aside is kept in a dated folder, never deleted.
        replacement.KeepPrevious(time.GetUtcNow());
        return kept;
    }

    /// <summary>Moves the half-migrated database and its companions from the swap's folder to beside the live database.</summary>
    private string? KeepHalfMigrated()
    {
        var aside = Path.Combine(replacement.PreviousDataFolder, SqliteDatabase.FileName);
        if (!File.Exists(aside))
        {
            return null;
        }

        var live = SqliteDatabase.FilePath(options.DataPath);
        var target = live + FailedUpgradeSuffix;
        if (Taken(target))
        {
            var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            target = $"{live}{FailedUpgradeSuffix}-{stamp}";
            for (var attempt = 2; Taken(target); attempt++)
            {
                target = string.Create(CultureInfo.InvariantCulture, $"{live}{FailedUpgradeSuffix}-{stamp}-{attempt}");
            }
        }

        // The companions first, under SQLite's own names for the kept file, so it opens with its write-ahead log.
        foreach (var companion in Companions)
        {
            var from = aside + companion;
            if (File.Exists(from))
            {
                File.Move(from, target + companion);
            }
        }

        File.Move(aside, target);
        return target;
    }

    private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path) || Companions.Any(companion => File.Exists(path + companion));

    /// <exception cref="InvalidDataException">The restored database cannot be opened or fails the check.</exception>
    private static void CheckIntegrity(string databaseFile)
    {
        var problems = new List<string>();
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseFile, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                problems.Add(reader.GetString(0));
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The restored database cannot be opened.", exception);
        }

        if (problems is not ["ok"])
        {
            throw new InvalidDataException(
                string.Create(CultureInfo.InvariantCulture, $"The restored database failed its integrity check ({problems.Count} problem(s))."));
        }
    }
}
