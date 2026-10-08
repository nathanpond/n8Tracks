using System.Text;

namespace n8Tracks.Application.Configuration;

/// <summary>
/// Whether a folder n8Tracks writes to is the media folder or lies inside it (#387, invariant 2): the
/// startup check that refuses such a data or backup folder before anything is written there. It
/// compares real paths, every link followed (as <c>MediaMountReader</c> resolves them), and on Linux
/// also where each folder really is: the device and the path within that filesystem, read from
/// <c>/proc/self/mountinfo</c>. That second comparison is what sees one host folder mounted twice
/// (<c>-v music:/media:ro -v music/sub:/backup</c>), which no path comparison inside the container
/// can. It reads paths and the mount table only; nothing under either folder is opened.
/// </summary>
public static class MediaFolderOverlap
{
    private const string MountInfoPath = "/proc/self/mountinfo";

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Whether the existing folder <paramref name="folder"/> is the existing folder
    /// <paramref name="mount"/> or inside it. False when either is not there: a folder that does not
    /// exist holds nothing, and a mount that is not there has nothing to protect.
    /// </summary>
    public static bool IsInside(string folder, string mount)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(mount);

        if (!Directory.Exists(folder) || !Directory.Exists(mount))
        {
            return false;
        }

        var realFolder = Real(folder);
        var realMount = Real(mount);
        if (Within(realFolder, realMount, PathComparison))
        {
            return true;
        }

        if (!OperatingSystem.IsLinux() || !File.Exists(MountInfoPath))
        {
            return false;
        }

        var mountInfo = File.ReadAllText(MountInfoPath);
        return Locate(realFolder, mountInfo) is { } where
            && Locate(realMount, mountInfo) is { } media
            && string.Equals(where.Device, media.Device, StringComparison.Ordinal)
            && Within(where.Path, media.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the real path <paramref name="realPath"/> is, by <paramref name="mountInfo"/> (the text
    /// of <c>/proc/self/mountinfo</c>): the device (<c>major:minor</c>) of the mount it is under (the
    /// longest mount point that holds it; the later of two at the same point, which covers the
    /// earlier) and its path within that filesystem. Null when no line holds it.
    /// </summary>
    public static (string Device, string Path)? Locate(string realPath, string mountInfo)
    {
        ArgumentNullException.ThrowIfNull(realPath);
        ArgumentNullException.ThrowIfNull(mountInfo);

        (string Device, string Root, string Point)? best = null;
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // id parent major:minor root mount-point options [optional fields] - type source options
            var fields = line.Split(' ');
            if (fields.Length < 5)
            {
                continue;
            }

            var point = Unescape(fields[4]);
            if (Within(realPath, point, StringComparison.Ordinal) && (best is null || point.Length >= best.Value.Point.Length))
            {
                best = (fields[2], Unescape(fields[3]), point);
            }
        }

        if (best is not { } mount)
        {
            return null;
        }

        var rest = mount.Point == "/" ? realPath : realPath[mount.Point.Length..];
        var path = mount.Root == "/" ? rest : mount.Root + rest;
        return (mount.Device, path.Length == 0 ? "/" : path);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or under it (both absolute, without a trailing separator but the root's own).</summary>
    public static bool Within(string path, string root, StringComparison comparison) =>
        root == "/"
        || string.Equals(path, root, comparison)
        || path.StartsWith(root + "/", comparison);

    /// <summary>The real path of an existing folder, without a trailing separator; the path as given when its links go round.</summary>
    private static string Real(string folder)
    {
        var full = Path.GetFullPath(folder);
        var real = RealPath(full) ?? full;
        return real.Length > 1 ? Path.TrimEndingDirectorySeparator(real) : real;
    }

    /// <summary>
    /// <paramref name="path"/> with every link on the way followed, segment by segment, as the
    /// operating system resolves it (the rule of <c>MediaMountReader.RealPath</c>); null when the
    /// links go round more than 40 times. A segment that is not there ends the following of links.
    /// </summary>
    private static string? RealPath(string path)
    {
        var current = Path.GetPathRoot(path)!;
        var pending = new Stack<string>(path[current.Length..].Split(Separators).Reverse());
        var hops = 0;
        while (pending.TryPop(out var segment))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var next = Path.Join(current, segment);
            var target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > 40)
            {
                return null;
            }

            if (Path.IsPathRooted(target))
            {
                current = Path.GetPathRoot(target)!;
            }

            foreach (var part in target.Split(Separators).Reverse())
            {
                pending.Push(part);
            }
        }

        return current;
    }

    /// <summary>A mountinfo field with its octal escapes (<c>\040</c> for a space, and the like) read back.</summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
        {
            return field;
        }

        var text = new StringBuilder(field.Length);
        for (var index = 0; index < field.Length; index++)
        {
            if (field[index] == '\\' && index + 3 < field.Length
                && field[(index + 1)..(index + 4)].All(static digit => digit is >= '0' and <= '7'))
            {
                text.Append((char)Convert.ToInt32(field[(index + 1)..(index + 4)], 8));
                index += 3;
                continue;
            }

            text.Append(field[index]);
        }

        return text.ToString();
    }
}
