using System.IO.Compression;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>
/// The backup mount and the <c>backups</c> folder under the data path. Only regular files named
/// <c>n8tracks-backup-*.zip</c> directly inside either are candidates; a name from a request is
/// matched against the folder's own listing, never joined to a path, so no name can reach anything
/// else. A folder inside the media mount is never used, read, or written (invariant 2).
/// </summary>
internal sealed partial class BackupFolders(N8TracksOptions options, ILogger<BackupFolders> logger) : IBackupStorage
{
    public const string FallbackFolderName = "backups";
    public const string ArchivePrefix = "n8tracks-backup-";
    public const string ArchiveExtension = ".zip";
    public const string TemporaryPrefix = ".tmp-";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>The fallback folder under the data path.</summary>
    public string FallbackPath { get; } = Path.Combine(options.DataPath, FallbackFolderName);

    public BackupDestination ResolveDestination() =>
        MountIsUsable() && IsWritable(options.BackupPath)
            ? new BackupDestination(BackupLocation.Mount, options.BackupPath)
            : new BackupDestination(BackupLocation.Data, FallbackPath);

    public string FolderPath(BackupLocation location) => location == BackupLocation.Mount ? options.BackupPath : FallbackPath;

    public Task<IReadOnlyList<BackupArchive>> ListAsync(CancellationToken cancellationToken)
    {
        var archives = new List<BackupArchive>();
        foreach (var (location, folder) in Folders())
        {
            foreach (var file in Candidates(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Describe(location, file) is { } archive)
                {
                    archives.Add(archive);
                }
            }
        }

        IReadOnlyList<BackupArchive> ordered = [.. archives
            .OrderByDescending(static archive => archive.CreatedUtc)
            .ThenBy(static archive => archive.Name, StringComparer.Ordinal)];
        return Task.FromResult(ordered);
    }

    public Task<BackupArchive?> FindAsync(BackupLocation location, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        var folder = Folders().Where(pair => pair.Location == location).Select(static pair => pair.Path).FirstOrDefault();
        var file = folder is null || !IsCandidateName(name)
            ? null
            : Candidates(folder).FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

        return Task.FromResult(file is null ? null : Describe(location, file));
    }

    public Stream? OpenRead(BackupArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        try
        {
            // Shared with a delete: a download in progress does not block one.
            return new FileStream(
                PathOf(archive),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public bool Delete(BackupArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var path = PathOf(archive);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        LogDeleted(logger, archive.Location, archive.Name);
        return true;
    }

    public void RemoveLeftoverTemporaryFolders()
    {
        foreach (var (_, folder) in Folders())
        {
            IEnumerable<DirectoryInfo> leftovers;
            try
            {
                leftovers = [.. new DirectoryInfo(folder).EnumerateDirectories(TemporaryPrefix + "*", SearchOption.TopDirectoryOnly)
                    .Where(static directory => directory.LinkTarget is null
                        && Guid.TryParseExact(directory.Name[TemporaryPrefix.Length..], "D", out _))];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogCleanupFailed(logger, exception);
                continue;
            }

            foreach (var leftover in leftovers)
            {
                try
                {
                    leftover.Delete(recursive: true);
                    LogRemovedLeftover(logger, leftover.Name);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    LogCleanupFailed(logger, exception);
                }
            }
        }
    }

    public int DeleteForRetention(IEnumerable<BackupArchive> archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        var deleted = 0;
        foreach (var archive in archives)
        {
            try
            {
                if (Delete(archive))
                {
                    deleted++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogRetentionDeleteFailed(logger, exception, archive.Location, archive.Name);
            }
        }

        return deleted;
    }

    /// <summary>Whether <paramref name="name"/> could name an archive: the prefix and extension, and nothing that is a path.</summary>
    public static bool IsCandidateName(string name) =>
        name.StartsWith(ArchivePrefix, StringComparison.Ordinal)
        && name.EndsWith(ArchiveExtension, StringComparison.Ordinal)
        && name.Length <= 255
        && name.IndexOfAny(['/', '\\', '\0']) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.Any(char.IsControl);

    /// <summary>
    /// Whether the backup mount can be used at all: it exists and is neither the media mount nor
    /// inside it. Never written to for the check.
    /// </summary>
    private bool MountIsUsable() => Directory.Exists(options.BackupPath) && !IsInside(options.BackupPath, options.MediaPath);

    /// <summary>The folders to list: the mount when usable, and the fallback when it exists and is a different folder.</summary>
    private List<(BackupLocation Location, string Path)> Folders()
    {
        var folders = new List<(BackupLocation, string)>();
        if (MountIsUsable())
        {
            folders.Add((BackupLocation.Mount, options.BackupPath));
        }

        if (Directory.Exists(FallbackPath)
            && !string.Equals(Normalise(FallbackPath), Normalise(options.BackupPath), PathComparison)
            && !IsInside(FallbackPath, options.MediaPath))
        {
            folders.Add((BackupLocation.Data, FallbackPath));
        }

        return folders;
    }

    private string PathOf(BackupArchive archive) =>
        Path.Combine(archive.Location == BackupLocation.Mount ? options.BackupPath : FallbackPath, archive.Name);

    private IEnumerable<FileInfo> Candidates(string folder)
    {
        try
        {
            return [.. new DirectoryInfo(folder).EnumerateFiles(ArchivePrefix + "*" + ArchiveExtension, SearchOption.TopDirectoryOnly)
                .Where(static file => file.LinkTarget is null && IsCandidateName(file.Name))];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogListFailed(logger, exception);
            return [];
        }
    }

    private static BackupArchive? Describe(BackupLocation location, FileInfo file)
    {
        try
        {
            file.Refresh();
            if (!file.Exists)
            {
                return null;
            }

            var size = file.Length;
            var written = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            try
            {
                using var archive = ZipFile.OpenRead(file.FullName);
                if (BackupManifest.Read(archive) is var (manifest, created))
                {
                    return new BackupArchive(location, file.Name, size, created, manifest.ApplicationVersion, manifest.Kind, manifest.Validity);
                }
            }
            catch (InvalidDataException)
            {
                // Not a ZIP file: listed as invalid below.
            }

            return new BackupArchive(location, file.Name, size, written, null, null, BackupValidity.Invalid);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted while it was being read.
            return null;
        }
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            using var probe = new FileStream(
                Path.Combine(folder, $".n8tracks-write-probe-{Guid.NewGuid():N}"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsInside(string path, string root)
    {
        var normalisedPath = Normalise(path);
        var normalisedRoot = Normalise(root);
        return string.Equals(normalisedPath, normalisedRoot, PathComparison)
            || normalisedPath.StartsWith(normalisedRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted backup {BackupLocation} {BackupName}")]
    private static partial void LogDeleted(ILogger logger, BackupLocation backupLocation, string backupName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the temporary folder {FolderName} an interrupted backup left behind")]
    private static partial void LogRemovedLeftover(ILogger logger, string folderName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove a temporary backup folder")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention could not delete backup {BackupLocation} {BackupName}; it is tried again after the next scheduled backup")]
    private static partial void LogRetentionDeleteFailed(ILogger logger, Exception exception, BackupLocation backupLocation, string backupName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list a backup folder")]
    private static partial void LogListFailed(ILogger logger, Exception exception);
}
