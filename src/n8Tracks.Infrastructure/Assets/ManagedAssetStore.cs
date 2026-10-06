using n8Tracks.Application.Assets;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure.Retention;

namespace n8Tracks.Infrastructure.Assets;

/// <summary>
/// Artwork files under <c>&lt;data&gt;/assets</c> (which backups already archive), named by content
/// hash (<see cref="ArtworkPaths"/>). Every path is resolved through <see cref="ManagedFiles"/>, so
/// nothing is read or written outside that folder or through a link: never under the media mount
/// (invariant 2). A write goes in full to <c>&lt;data&gt;/assets-staging</c> first, outside the
/// folder a backup reads, and is then moved into place; an existing file is never replaced.
/// </summary>
internal sealed class ManagedAssetStore(ManagedFiles files, N8TracksOptions options) : IManagedAssetStore
{
    /// <summary>The staging folder, beside the managed-assets folder under the data path.</summary>
    public const string StagingFolderName = "assets-staging";

    private const string StagingSuffix = ".tmp";

    private string StagingFolder => Path.Combine(options.DataPath, StagingFolderName);

    public bool Exists(string path) =>
        files.Resolve(path, out _) is { } full && new FileInfo(full) is { Exists: true, LinkTarget: null };

    public Stream? OpenRead(string path)
    {
        if (files.Resolve(path, out _) is not { } full || new FileInfo(full) is not { Exists: true, LinkTarget: null })
        {
            return null;
        }

        try
        {
            return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var full = files.Resolve(path, out var error) ?? throw new ArgumentException($"'{path}' cannot be written: {error}.", nameof(path));

        Directory.CreateDirectory(StagingFolder);
        var staged = Path.Combine(StagingFolder, Guid.NewGuid().ToString("N") + StagingSuffix);
        try
        {
            var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            // The folders were just created: resolve again, so none of them can be a link put there meanwhile.
            if (files.Resolve(path, out error) is null)
            {
                throw new IOException($"'{path}' cannot be written: {error}.");
            }

            try
            {
                File.Move(staged, full, overwrite: false);
            }
            catch (IOException) when (File.Exists(full))
            {
                // The same content-addressed file arrived first from another upload: keep that one.
            }
        }
        finally
        {
            File.Delete(staged);
        }
    }

    public IReadOnlyList<string> Files(string folder)
    {
        if (files.Resolve(folder, out _) is not { } full || new DirectoryInfo(full) is not { Exists: true, LinkTarget: null } directory)
        {
            return [];
        }

        try
        {
            return [.. directory.EnumerateFiles()
                .Where(static file => file.LinkTarget is null)
                .Select(file => $"{folder.TrimEnd('/')}/{file.Name}")
                .Order(StringComparer.Ordinal)];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public void RemoveEmptyFolders(string folder)
    {
        if (files.Resolve(folder, out _) is not { } full)
        {
            return;
        }

        for (var directory = new DirectoryInfo(full); directory.FullName.Length > files.Root.Length; directory = directory.Parent!)
        {
            try
            {
                if (directory is not { Exists: true, LinkTarget: null } || directory.EnumerateFileSystemInfos().Any())
                {
                    return;
                }

                directory.Delete(recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Something arrived meanwhile, or it cannot be removed: an empty folder left behind is harmless.
                return;
            }
        }
    }

    public void RemoveStaleStaging(TimeSpan age)
    {
        var staging = new DirectoryInfo(StagingFolder);
        if (staging is not { Exists: true, LinkTarget: null })
        {
            return;
        }

        // File times are the file system's, so they are compared with the real clock, not the app's.
        var before = TimeProvider.System.GetUtcNow().UtcDateTime - age;
        foreach (var file in staging.EnumerateFiles("*" + StagingSuffix))
        {
            try
            {
                if (file.LinkTarget is null && file.LastWriteTimeUtc < before)
                {
                    file.Delete();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Still being written, or not ours to remove: the next sweep looks again.
            }
        }
    }
}
