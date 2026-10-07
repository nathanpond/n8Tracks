using n8Tracks.Application.Configuration;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Media;

/// <summary>
/// The only code that touches the media mount (invariant 2), and only to read: it lists directories,
/// looks at files, and opens them with <see cref="FileAccess.Read"/> and <see cref="FileShare.Read"/>.
/// Nothing here creates, writes, renames, moves, or deletes, or sets an attribute or a time. Links are
/// reported, never followed (#205 brings the rules for following one that stays inside the mount).
/// </summary>
internal sealed class MediaMountReader(N8TracksOptions options) : IMediaMount
{
    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    public IReadOnlyList<MediaEntry> List(string relativeDirectory)
    {
        // The root is whatever the operator mounted (it may itself be a link); below it, a link is never listed.
        var directory = new DirectoryInfo(Resolve(relativeDirectory));
        if (relativeDirectory.Length > 0 && directory.LinkTarget is not null)
        {
            throw new IOException("A link is not listed.");
        }

        var entries = new List<MediaEntry>();
        foreach (var entry in directory.EnumerateFileSystemInfos("*", OneLevel))
        {
            var kind = entry.LinkTarget is not null
                ? MediaEntryKind.Link
                : entry is DirectoryInfo ? MediaEntryKind.Directory : MediaEntryKind.File;
            entries.Add(new MediaEntry(entry.Name, kind));
        }

        return entries;
    }

    public MediaFileStat? Stat(string relativePath)
    {
        try
        {
            var file = new FileInfo(Resolve(relativePath));
            if (!file.Exists || file.LinkTarget is not null)
            {
                return null;
            }

            return new MediaFileStat(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Stream OpenRead(string relativePath) =>
        new FileStream(
            Resolve(relativePath),
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
            });

    /// <summary>
    /// The full path of <paramref name="relativePath"/> under the mount root. Refuses an absolute
    /// path, a NUL, a backslash, and any empty, <c>.</c>, or <c>..</c> segment, so nothing named here
    /// is outside the root by its text (#205 adds the check of where links lead).
    /// </summary>
    private string Resolve(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var root = Path.TrimEndingDirectorySeparator(options.MediaPath);
        if (relativePath.Length == 0)
        {
            return root;
        }

        if (relativePath.Contains('\0', StringComparison.Ordinal)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("The path is not a relative path inside the media folder.", nameof(relativePath));
        }

        return Path.Join(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
