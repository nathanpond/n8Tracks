using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>Test-only seams in a backup. Empty in the app.</summary>
internal sealed class BackupTestHooks
{
    /// <summary>Called with the path of the database copy once it is made, before it is archived.</summary>
    public Func<string, CancellationToken, Task>? AfterDatabaseCopy { get; init; }
}

/// <summary>
/// Builds one backup archive: an online copy of the database, <c>settings.json</c>, the managed
/// <c>assets/</c> folder, and <c>manifest.json</c> with a SHA-256 of every other file. It is built in
/// <c>.tmp-&lt;job id&gt;</c> inside the destination folder, so the move into place stays on one disk
/// and is atomic, and is verified before the move: the finished ZIP is read back, every checksum is
/// recomputed from its entries, and the database is extracted and passes <c>PRAGMA integrity_check</c>.
/// An archive that fails is deleted with the temporary folder. Nothing outside the data path and
/// the destination folder is read or written; audio under the media mount is never included.
/// </summary>
internal sealed partial class BackupWriter(
    N8TracksOptions options,
    ApplicationVersion version,
    TimeProvider time,
    BackupTestHooks hooks,
    ILogger<BackupWriter> logger) : IBackupWriter
{
    /// <summary>The managed-assets folder under the data path (empty until artwork arrives).</summary>
    public const string AssetsFolderName = "assets";

    /// <summary>Settings keys whose values are secret and left out of <c>settings.json</c>. None yet.</summary>
    public static readonly IReadOnlySet<string> SecretSettingKeys = new HashSet<string>(StringComparer.Ordinal);

    public async Task<CreatedBackup> CreateAsync(
        BackupDestination destination,
        Guid jobId,
        BackupKind kind,
        Action<BackupPhase, int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(progress);

        Directory.CreateDirectory(destination.Path);
        var temporary = Directory.CreateDirectory(Path.Combine(destination.Path, BackupFolders.TemporaryPrefix + jobId.ToString("D")));
        try
        {
            var (created, name) = await FreeNameAsync(destination.Path, cancellationToken).ConfigureAwait(false);

            progress(BackupPhase.CopyingDatabase, 0);
            var databaseCopy = Path.Combine(temporary.FullName, BackupManifest.DatabaseEntry);
            await SqliteOnlineBackup.CopyAsync(
                SqliteDatabase.FilePath(options.DataPath),
                databaseCopy,
                (done, total) => progress(BackupPhase.CopyingDatabase, total == 0 ? 100 : (int)(100L * done / total)),
                cancellationToken).ConfigureAwait(false);

            if (hooks.AfterDatabaseCopy is { } hook)
            {
                await hook(databaseCopy, cancellationToken).ConfigureAwait(false);
            }

            progress(BackupPhase.Archiving, 0);
            var archivePath = Path.Combine(temporary.FullName, name);
            await WriteArchiveAsync(archivePath, databaseCopy, created, kind, progress, cancellationToken).ConfigureAwait(false);

            progress(BackupPhase.Verifying, 0);
            await VerifyAsync(archivePath, temporary.FullName, cancellationToken).ConfigureAwait(false);
            progress(BackupPhase.Verifying, 100);

            var final = Path.Combine(destination.Path, name);
            File.Move(archivePath, final, overwrite: false);
            var size = new FileInfo(final).Length;
            LogCreated(logger, destination.Location, name, size);

            return new CreatedBackup(destination.Location, name, size);
        }
        catch (BackupVerificationException exception)
        {
            LogVerificationFailed(logger, exception.Message);
            throw;
        }
        finally
        {
            try
            {
                temporary.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Removed at the next start instead.
                LogTemporaryLeft(logger, exception);
            }
        }
    }

    /// <summary>
    /// The archive name for <paramref name="created"/>: <c>n8tracks-backup-&lt;UTC yyyyMMdd-HHmmss&gt;-v&lt;version&gt;.zip</c>.
    /// </summary>
    public static string ArchiveName(DateTimeOffset created, string applicationVersion)
    {
        ArgumentNullException.ThrowIfNull(applicationVersion);

        var safeVersion = new string([.. applicationVersion.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-')]);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{BackupFolders.ArchivePrefix}{created.UtcDateTime:yyyyMMdd-HHmmss}-v{safeVersion}{BackupFolders.ArchiveExtension}");
    }

    /// <summary>The time and name of the new archive; a name taken within the same second waits for the next one.</summary>
    private async Task<(DateTimeOffset Created, string Name)> FreeNameAsync(string folder, CancellationToken cancellationToken)
    {
        while (true)
        {
            var created = time.GetUtcNow();
            var name = ArchiveName(created, version.Value);
            if (!File.Exists(Path.Combine(folder, name)))
            {
                return (created, name);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteArchiveAsync(
        string archivePath,
        string databaseCopy,
        DateTimeOffset created,
        BackupKind kind,
        Action<BackupPhase, int> progress,
        CancellationToken cancellationToken)
    {
        string lastMigration;
        byte[] settings;
        try
        {
            (lastMigration, settings) = await ReadCopyAsync(databaseCopy, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new BackupVerificationException(
                $"The backup failed verification: the database copy cannot be read ({exception.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)}).",
                exception);
        }

        var assets = Assets();
        var totalBytes = new FileInfo(databaseCopy).Length + assets.Sum(static asset => asset.File.Length);
        long doneBytes = 0;
        void Advance(long bytes)
        {
            doneBytes += bytes;
            progress(BackupPhase.Archiving, totalBytes == 0 ? 100 : (int)Math.Min(100, 100 * doneBytes / totalBytes));
        }

        var files = new List<BackupManifestFile>();
        var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

            files.Add(await AddFileAsync(archive, BackupManifest.DatabaseEntry, databaseCopy, Advance, cancellationToken).ConfigureAwait(false));
            files.Add(await AddBytesAsync(archive, BackupManifest.SettingsEntry, settings, cancellationToken).ConfigureAwait(false));

            archive.CreateEntry(BackupManifest.AssetsFolder);
            foreach (var (relative, file) in assets)
            {
                files.Add(await AddFileAsync(archive, BackupManifest.AssetsFolder + relative, file.FullName, Advance, cancellationToken).ConfigureAwait(false));
            }

            var manifest = new BackupManifest(
                BackupManifest.CurrentFormatVersion,
                version.Value,
                lastMigration,
                BackupManifest.Time(created),
                BackupKinds.Text(kind),
                files);
            await AddBytesAsync(archive, BackupManifest.EntryName, JsonSerializer.SerializeToUtf8Bytes(manifest, BackupManifest.Json), cancellationToken)
                .ConfigureAwait(false);
        }

        progress(BackupPhase.Archiving, 100);
    }

    /// <summary>
    /// The last applied migration and <c>settings.json</c>, read from the copy with plain SQL (not the
    /// entity model), so the same code works on a database whose schema is older than this build.
    /// </summary>
    private async Task<(string LastMigration, byte[] Settings)> ReadCopyAsync(string databaseCopy, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ReadOnly(databaseCopy));
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            string lastMigration;
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1;";
                lastMigration = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? string.Empty;
            }

            // A database from before the settings table (an upgrade's safety backup) has no settings.
            var settings = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            var hasSettings = connection.CreateCommand();
            await using (hasSettings.ConfigureAwait(false))
            {
                hasSettings.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'settings';";
                if (Convert.ToInt64(await hasSettings.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
                {
                    return (lastMigration, JsonSerializer.SerializeToUtf8Bytes(SettingsDocument(settings), BackupManifest.Json));
                }
            }

            var read = connection.CreateCommand();
            await using (read.ConfigureAwait(false))
            {
                read.CommandText = "SELECT key, value FROM settings;";
                var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var key = reader.GetString(0);
                        if (!SecretSettingKeys.Contains(key))
                        {
                            using var value = JsonDocument.Parse(reader.GetString(1));
                            settings[key] = value.RootElement.Clone();
                        }
                    }
                }
            }

            return (lastMigration, JsonSerializer.SerializeToUtf8Bytes(SettingsDocument(settings), BackupManifest.Json));
        }
    }

    /// <summary>What <c>settings.json</c> holds: the stored settings and the environment-configured ones.</summary>
    private object SettingsDocument(SortedDictionary<string, JsonElement> settings) => new
    {
        settings,
        environment = new
        {
            baseUrl = options.BaseUrl.ToString(),
            timeZone = options.TimeZone.Id,
            logLevel = options.LogLevel.ToString(),
        },
    };

    /// <summary>Every regular file under the managed-assets folder, by its path relative to it; links are not followed.</summary>
    private List<(string Relative, FileInfo File)> Assets()
    {
        var root = new DirectoryInfo(Path.Combine(options.DataPath, AssetsFolderName));
        if (!root.Exists || root.LinkTarget is not null)
        {
            return [];
        }

        var found = new List<(string, FileInfo)>();
        var pending = new Stack<DirectoryInfo>([root]);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo file)
                {
                    found.Add((Path.GetRelativePath(root.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/'), file));
                }
            }
        }

        return [.. found.OrderBy(static asset => asset.Item1, StringComparer.Ordinal)];
    }

    private static async Task<BackupManifestFile> AddFileAsync(
        ZipArchive archive,
        string entryName,
        string path,
        Action<long> advance,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (source.ConfigureAwait(false))
        {
            var target = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (target.ConfigureAwait(false))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long size = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    size += read;
                    advance(read);
                }

                return new BackupManifestFile(entryName, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
        }
    }

    private static async Task<BackupManifestFile> AddBytesAsync(ZipArchive archive, string entryName, byte[] bytes, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        var target = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (target.ConfigureAwait(false))
        {
            await target.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        return new BackupManifestFile(entryName, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>
    /// Reads the finished archive back: the manifest, every checksum and size recomputed from the
    /// archive's own entries, no entry the manifest does not list, and the database, extracted into
    /// the temporary folder, passing an integrity check.
    /// </summary>
    /// <exception cref="BackupVerificationException">Any of it fails; the message says which.</exception>
    private static async Task VerifyAsync(string archivePath, string temporaryFolder, CancellationToken cancellationToken)
    {
        var extracted = Path.Combine(temporaryFolder, "verify-" + BackupManifest.DatabaseEntry);
        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (BackupManifest.Read(archive) is not var (manifest, _))
                {
                    throw new BackupVerificationException("The backup failed verification: its manifest cannot be read.");
                }

                var listed = manifest.Files.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
                var unlisted = archive.Entries.FirstOrDefault(entry =>
                    !entry.FullName.EndsWith('/') && entry.FullName != BackupManifest.EntryName && !listed.Contains(entry.FullName));
                if (unlisted is not null)
                {
                    throw new BackupVerificationException($"The backup failed verification: {unlisted.FullName} is not in its manifest.");
                }

                if (!listed.Contains(BackupManifest.DatabaseEntry))
                {
                    throw new BackupVerificationException("The backup failed verification: it has no database.");
                }

                foreach (var file in manifest.Files)
                {
                    var entry = archive.GetEntry(file.Path)!;
                    var (size, sha256) = await HashAsync(entry, file.Path == BackupManifest.DatabaseEntry ? extracted : null, cancellationToken)
                        .ConfigureAwait(false);
                    if (size != file.Size || !string.Equals(sha256, file.Sha256, StringComparison.Ordinal))
                    {
                        throw new BackupVerificationException($"The backup failed verification: the checksum of {file.Path} does not match.");
                    }
                }
            }

            await CheckIntegrityAsync(extracted, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new BackupVerificationException("The backup failed verification: the archive cannot be read.", exception);
        }
    }

    /// <summary>The entry's size and SHA-256, copying it to <paramref name="copyTo"/> as it is read when given.</summary>
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

    private static async Task CheckIntegrityAsync(string databaseFile, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        try
        {
            var connection = new SqliteConnection(ReadOnly(databaseFile));
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = "PRAGMA integrity_check;";
                    var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            problems.Add(reader.GetString(0));
                        }
                    }
                }
            }
        }
        catch (SqliteException exception)
        {
            throw new BackupVerificationException(
                $"The backup failed verification: the database copy cannot be read ({exception.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)}).",
                exception);
        }

        if (problems is not ["ok"])
        {
            throw new BackupVerificationException(
                $"The backup failed verification: the database copy failed its integrity check ({problems.Count.ToString(CultureInfo.InvariantCulture)} problem(s)).");
        }
    }

    private static string ReadOnly(string databaseFile) =>
        new SqliteConnectionStringBuilder { DataSource = databaseFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup created {BackupLocation} {BackupName} {SizeBytes}")]
    private static partial void LogCreated(ILogger logger, BackupLocation backupLocation, string backupName, long sizeBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup deleted after failing verification: {Reason}")]
    private static partial void LogVerificationFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove a backup's temporary folder; it is removed at the next start")]
    private static partial void LogTemporaryLeft(ILogger logger, Exception exception);
}

/// <summary>Removes what interrupted backups left behind, once, when the server starts.</summary>
internal sealed class BackupStartupCleanup(IBackupStorage storage) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        storage.RemoveLeftoverTemporaryFolders();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
