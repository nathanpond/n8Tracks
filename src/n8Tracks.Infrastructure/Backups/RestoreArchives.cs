using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>
/// Reads archives for a restore. Uploads are held in <c>restore-uploads</c> under the data path,
/// named by an ID this class makes, never by anything a request sent; a listed archive is found
/// through <see cref="IBackupStorage"/>, so a name can never point anywhere else. The database is
/// extracted for its checks into <c>restore-work/&lt;id&gt;</c> under the data path and removed
/// again. Only files the manifest lists are read, and only the database is ever written out, under
/// a fixed name, so no entry name in an archive can reach outside the work folder. Nothing under the
/// media mount is read or written, and the live database, settings, and assets are never touched.
/// </summary>
internal sealed partial class RestoreArchives(
    N8TracksOptions options,
    IBackupStorage storage,
    N8TracksDbContext context,
    ILogger<RestoreArchives> logger) : IRestoreArchives
{
    public const string UploadsFolderName = "restore-uploads";
    public const string WorkFolderName = "restore-work";

    private string UploadsFolder => Path.Combine(options.DataPath, UploadsFolderName);

    private string WorkFolder => Path.Combine(options.DataPath, WorkFolderName);

    public async Task<Guid?> SaveUploadAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        Directory.CreateDirectory(UploadsFolder);
        var id = Guid.CreateVersion7();
        var path = UploadPath(id);
        var written = false;
        try
        {
            var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        return null;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            written = true;
            return id;
        }
        finally
        {
            if (!written)
            {
                TryDelete(path);
            }
        }
    }

    public void DeleteUpload(Guid uploadId) => TryDelete(UploadPath(uploadId));

    public void DeleteLeftovers()
    {
        foreach (var folder in new[] { UploadsFolder, WorkFolder })
        {
            try
            {
                var directory = new DirectoryInfo(folder);
                if (directory.Exists && directory.LinkTarget is null)
                {
                    directory.Delete(recursive: true);
                    LogRemovedLeftovers(logger, Path.GetFileName(folder));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogCleanupFailed(logger, exception);
            }
        }
    }

    public async Task<ArchiveReading?> ReadAsync(RestoreSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var stream = await OpenAsync(source, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            try
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                if (archive.GetEntry(BackupManifest.EntryName) is null)
                {
                    return Refused(RestoreRefusalReason.MissingManifest, "The file has no manifest, so it is not an n8Tracks backup.");
                }

                if (BackupManifest.Read(archive) is not var (manifest, created))
                {
                    return Refused(RestoreRefusalReason.UnreadableManifest, "The backup's manifest cannot be read, or names a file the archive does not have.");
                }

                if (manifest.Validity == BackupValidity.Newer)
                {
                    return new ArchiveReading.Refused(new RestoreRefusal(
                        RestoreRefusalReason.NewerFormat,
                        $"This backup was made by n8Tracks {manifest.ApplicationVersion}, in a format this version cannot read. Restore it with n8Tracks {manifest.ApplicationVersion} or later.",
                        NeededVersion: manifest.ApplicationVersion));
                }

                if (!manifest.Files.Any(static file => file.Path == BackupManifest.DatabaseEntry))
                {
                    return Refused(RestoreRefusalReason.MissingDatabase, "The backup holds no database.");
                }

                var unpacked = archive.Entries.Sum(static entry => entry.Length);
                var (name, location) = source switch
                {
                    RestoreSource.Listed listed => (listed.Name, (BackupLocation?)listed.Location),
                    RestoreSource.Uploaded uploaded => (uploaded.FileName, null),
                    _ => throw new ArgumentOutOfRangeException(nameof(source)),
                };

                return new ArchiveReading.Readable(
                    new RestoreArchiveSummary(name, location, stream.Length, created, manifest.ApplicationVersion, manifest.Kind, manifest.LastMigration),
                    unpacked);
            }
            catch (InvalidDataException)
            {
                return Refused(RestoreRefusalReason.NotAZip, "The file is not a ZIP archive, so it is not an n8Tracks backup.");
            }
        }
    }

    public async Task<RestoreRefusal?> VerifyAsync(RestoreSource source, RestoreArchiveSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(summary);

        var work = Directory.CreateDirectory(Path.Combine(WorkFolder, Guid.CreateVersion7().ToString("N")));
        try
        {
            var database = Path.Combine(work.FullName, BackupManifest.DatabaseEntry);
            var stream = await OpenAsync(source, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                return new RestoreRefusal(RestoreRefusalReason.MissingManifest, "The backup is no longer there.");
            }

            await using (stream.ConfigureAwait(false))
            {
                if (await CheckEntriesAsync(stream, database, cancellationToken).ConfigureAwait(false) is { } refusal)
                {
                    return refusal;
                }
            }

            return await CheckDatabaseAsync(database, summary, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Removed at the next start instead.
                LogCleanupFailed(logger, exception);
            }
        }
    }

    public long LiveDataBytes()
    {
        var database = SqliteDatabase.FilePath(options.DataPath);
        long total = 0;
        foreach (var file in new[] { database, database + "-wal" })
        {
            var info = new FileInfo(file);
            if (info.Exists)
            {
                total += info.Length;
            }
        }

        var assets = new DirectoryInfo(Path.Combine(options.DataPath, BackupWriter.AssetsFolderName));
        if (assets.Exists && assets.LinkTarget is null)
        {
            total += assets.EnumerateFiles("*", SearchOption.AllDirectories).Where(static file => file.LinkTarget is null).Sum(static file => file.Length);
        }

        return total;
    }

    /// <summary>Every entry listed, with the right size and checksum; the database is copied out as it is hashed.</summary>
    private static async Task<RestoreRefusal?> CheckEntriesAsync(Stream stream, string databaseCopy, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (BackupManifest.Read(archive) is not var (manifest, _))
            {
                return new RestoreRefusal(RestoreRefusalReason.UnreadableManifest, "The backup's manifest cannot be read, or names a file the archive does not have.");
            }

            var listed = manifest.Files.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
            if (archive.Entries.FirstOrDefault(entry =>
                !entry.FullName.EndsWith('/') && entry.FullName != BackupManifest.EntryName && !listed.Contains(entry.FullName)) is { } unlisted)
            {
                return new RestoreRefusal(RestoreRefusalReason.UnlistedEntry, $"The backup holds a file its manifest does not list ({Shorten(unlisted.FullName)}).");
            }

            foreach (var file in manifest.Files)
            {
                var entry = archive.GetEntry(file.Path)!;
                var copyTo = file.Path == BackupManifest.DatabaseEntry ? databaseCopy : null;
                var (size, sha256) = await HashAsync(entry, copyTo, cancellationToken).ConfigureAwait(false);
                if (size != file.Size || !string.Equals(sha256, file.Sha256, StringComparison.Ordinal))
                {
                    return Mismatch(file.Path);
                }
            }

            return null;
        }
        catch (InvalidDataException)
        {
            // A damaged entry: its data does not match its own CRC.
            return new RestoreRefusal(RestoreRefusalReason.ChecksumMismatch, "The backup is damaged: a file in it cannot be read back.");
        }
    }

    /// <summary>The extracted database: its integrity, and a migration history this build knows.</summary>
    private async Task<RestoreRefusal?> CheckDatabaseAsync(string databaseFile, RestoreArchiveSummary summary, CancellationToken cancellationToken)
    {
        List<string> applied;
        bool hasOtherTables;
        try
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = databaseFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                var problems = await ColumnAsync(connection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
                if (problems is not ["ok"])
                {
                    return Corrupt();
                }

                var tables = await ColumnAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table';", cancellationToken).ConfigureAwait(false);
                hasOtherTables = tables.Any(static table => table != "__EFMigrationsHistory" && !table.StartsWith("sqlite_", StringComparison.Ordinal));
                applied = tables.Contains("__EFMigrationsHistory")
                    ? await ColumnAsync(connection, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\";", cancellationToken).ConfigureAwait(false)
                    : [];
            }
        }
        catch (SqliteException)
        {
            return Corrupt();
        }

        var known = context.Database.GetMigrations().ToList();
        if (applied.Except(known, StringComparer.Ordinal).Any())
        {
            return new RestoreRefusal(
                RestoreRefusalReason.NewerSchema,
                $"This backup was made by n8Tracks {summary.ApplicationVersion}, whose database is newer than this version knows. Restore it with n8Tracks {summary.ApplicationVersion} or later.",
                NeededVersion: summary.ApplicationVersion);
        }

        if (applied.Count == 0
            || SchemaVersionCheck.Refusal(known, applied, hasOtherTables) is not null
            || !string.Equals(applied[^1], summary.LastMigration, StringComparison.Ordinal))
        {
            return new RestoreRefusal(RestoreRefusalReason.UnknownSchema, "The backup's database does not have a migration history this application produced.");
        }

        return null;
    }

    private async Task<Stream?> OpenAsync(RestoreSource source, CancellationToken cancellationToken)
    {
        switch (source)
        {
            case RestoreSource.Listed listed:
                return await storage.FindAsync(listed.Location, listed.Name, cancellationToken).ConfigureAwait(false) is { } archive
                    ? storage.OpenRead(archive)
                    : null;

            case RestoreSource.Uploaded uploaded:
                try
                {
                    return new FileStream(UploadPath(uploaded.UploadId), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    return null;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    private string UploadPath(Guid id) => Path.Combine(UploadsFolder, id.ToString("N") + BackupFolders.ArchiveExtension);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Removed at the next start instead.
            LogCleanupFailed(logger, exception);
        }
    }

    private static async Task<(long Size, string Sha256)> HashAsync(ZipArchiveEntry entry, string? copyTo, CancellationToken cancellationToken)
    {
        var source = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var copy = copyTo is null ? Stream.Null : new FileStream(copyTo, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (copy.ConfigureAwait(false))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long size = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    size += read;
                    await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                return (size, Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
        }
    }

    private static async Task<List<string>> ColumnAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var values = new List<string>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    values.Add(reader.GetString(0));
                }

                return values;
            }
        }
    }

    private static ArchiveReading.Refused Refused(RestoreRefusalReason reason, string message) => new(new RestoreRefusal(reason, message));

    private static RestoreRefusal Mismatch(string path) =>
        new(RestoreRefusalReason.ChecksumMismatch, $"The backup is damaged or was changed: the checksum of {Shorten(path)} does not match its manifest.");

    private static RestoreRefusal Corrupt() =>
        new(RestoreRefusalReason.CorruptDatabase, "The backup's database is damaged: it fails SQLite's integrity check.");

    /// <summary>An entry name for a message: control characters dropped, at most 80 characters.</summary>
    private static string Shorten(string name)
    {
        var clean = new string([.. name.Where(static c => !char.IsControl(c))]);
        return clean.Length > 80 ? string.Create(CultureInfo.InvariantCulture, $"{clean[..77]}...") : clean;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the {FolderName} folder a restore left behind")]
    private static partial void LogRemovedLeftovers(ILogger logger, string folderName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove a restore's temporary file; it is removed at the next start")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}

/// <summary>Free space on the data path's disk.</summary>
internal sealed class DataDiskSpace(N8TracksOptions options) : IDiskSpace
{
    public long AvailableForData() => new DriveInfo(Path.GetFullPath(options.DataPath)).AvailableFreeSpace;
}
