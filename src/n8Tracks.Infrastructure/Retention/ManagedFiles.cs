using n8Tracks.Application.Configuration;
using n8Tracks.Application.Retention;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Infrastructure.Retention;

/// <summary>
/// The managed-assets folder, <c>&lt;data&gt;/assets</c> (which backups already archive). The prune
/// deletes files only here: a path is refused unless it resolves inside the folder without passing
/// through a link, so nothing under the media mount, or anywhere else, is ever deleted (invariant 2).
/// </summary>
internal sealed class ManagedFiles(N8TracksOptions options) : IManagedFiles
{
    public string Root { get; } = Path.GetFullPath(Path.Combine(options.DataPath, BackupWriter.AssetsFolderName));

    public ManagedFileDeletion Delete(string path, out string? error)
    {
        if (Resolve(path, out error) is not { } file)
        {
            return ManagedFileDeletion.Failed;
        }

        try
        {
            var info = new FileInfo(file);
            if (!info.Exists && info.LinkTarget is null)
            {
                return Directory.Exists(file) ? Refuse("it is a folder, not a file", out error) : ManagedFileDeletion.Missing;
            }

            info.Delete();
            return ManagedFileDeletion.Deleted;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return ManagedFileDeletion.Failed;
        }
    }

    /// <summary>
    /// The full path of <paramref name="path"/> inside <see cref="Root"/>, or null (with why) when it
    /// is not a managed path, leaves the folder, or passes through a link on the way.
    /// </summary>
    public string? Resolve(string path, out string? error)
    {
        if (!RetentionService.IsManagedFilePath(path))
        {
            error = "it is not a path inside the managed-assets folder";
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(Root, path));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            error = "it leaves the managed-assets folder";
            return null;
        }

        // The folder itself and each folder on the way must be real folders, not links elsewhere.
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(full)!); ; directory = directory.Parent!)
        {
            if (directory.LinkTarget is not null)
            {
                error = "it passes through a link";
                return null;
            }

            if (directory.FullName.Length <= Root.Length)
            {
                break;
            }
        }

        error = null;
        return full;
    }

    private static ManagedFileDeletion Refuse(string why, out string? error)
    {
        error = why;
        return ManagedFileDeletion.Failed;
    }
}
