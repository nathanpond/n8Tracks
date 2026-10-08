using System.Globalization;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Media;

/// <summary>
/// The only code that touches the media mount (invariant 2, guarded by #205), and only to read: it
/// lists directories, looks at files, and opens them with <see cref="FileAccess.Read"/> and
/// <see cref="FileShare.Read"/>. Nothing here creates, writes, renames, moves, or deletes, or sets an
/// attribute or a time; <c>MediaMountAccessTests</c> fails the build of any change that would.
/// <para>
/// Every path is resolved to its real path at each use, segment by segment, following each link
/// (at most <see cref="MaximumLinkHops"/> of them) the way the operating system would, and compared
/// with the mount root's real path: one that does not end under it is refused. The root itself may be
/// a link (it is whatever the operator mounted). On Linux an opened file's path is read back from
/// <c>/proc/self/fd</c> and checked again, which narrows the window between the check and the open;
/// other systems skip that second check.
/// </para>
/// <para>
/// What it hands out is never its <see cref="FileStream"/> but a <see cref="ReadOnlyContent"/> over it
/// (#386): no caller can write through it, and none can learn the file's absolute path from it (a
/// <c>FileStream</c>'s <c>Name</c>), so the mount's path stays here.
/// </para>
/// </summary>
internal sealed class MediaMountReader(N8TracksOptions options) : IMediaMount
{
    /// <summary>How many links one path may pass through before it counts as going round (Linux's own limit).</summary>
    internal const int MaximumLinkHops = 40;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    public bool Probe()
    {
        var root = Root();
        if (!Directory.Exists(root))
        {
            return false;
        }

        // Asking for the first entry is what proves the directory can be read; an empty one is fine.
        using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
        _ = entries.MoveNext();

        return true;
    }

    public IReadOnlyList<MediaEntry> List(string relativeDirectory)
    {
        var realRoot = RealRoot();
        var real = ResolveInside(relativeDirectory, realRoot);
        var realRelative = Relative(real, realRoot);

        var entries = new List<MediaEntry>();
        foreach (var entry in new DirectoryInfo(real).EnumerateFileSystemInfos("*", OneLevel))
        {
            if (entry.LinkTarget is null)
            {
                entries.Add(entry is DirectoryInfo
                    ? new MediaEntry(entry.Name, MediaEntryKind.Directory) { RealPath = Join(realRelative, entry.Name) }
                    : new MediaEntry(entry.Name, MediaEntryKind.File));
                continue;
            }

            entries.Add(Link(real, entry.Name, realRoot));
        }

        return entries;
    }

    public MediaFileStat? Stat(string relativePath)
    {
        try
        {
            var file = new FileInfo(ResolveInside(relativePath, RealRoot()));
            if (!file.Exists)
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

    public Stream OpenRead(string relativePath) => new ReadOnlyContent(Open(relativePath));

    public OpenedMediaFile OpenWithStat(string relativePath)
    {
        var stream = Open(relativePath);

        // Asked of the handle, never of the path: what is sent is what was opened.
        return new OpenedMediaFile(
            new ReadOnlyContent(stream),
            () => new MediaFileStat(stream.Length, new DateTimeOffset(File.GetLastWriteTimeUtc(stream.SafeFileHandle), TimeSpan.Zero)));
    }

    /// <summary>Opens the file for reading only, after the real-path check, and checks again where the open handle is.</summary>
    private FileStream Open(string relativePath)
    {
        var realRoot = RealRoot();
        var stream = new FileStream(
            ResolveInside(relativePath, realRoot),
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
            });
        try
        {
            if (!OpenedInside(stream, realRoot))
            {
                // Something swapped a link in between the check and the open: not one byte is read.
                throw new MediaPathOutsideException();
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether the file <paramref name="stream"/> has open is under <paramref name="realRoot"/>,
    /// asked of the kernel (<c>/proc/self/fd</c>) on Linux. Elsewhere there is no such place, and the
    /// answer is yes: the check before the open is all there is (<c>CLAUDE.md</c> names the gap).
    /// </summary>
    internal static bool OpenedInside(FileStream stream, string realRoot)
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt64();
        var opened = new FileInfo("/proc/self/fd/" + descriptor.ToString(CultureInfo.InvariantCulture)).LinkTarget;
        return opened is not null && IsInside(opened, realRoot);
    }

    /// <summary>
    /// The real path <paramref name="path"/> resolves to, link by link from the file system root, or
    /// null when its links go round more than <see cref="MaximumLinkHops"/> times. A segment that is not
    /// there ends the resolving of links: the rest is taken as written.
    /// </summary>
    internal static string? RealPath(string path) =>
        Walk(Path.GetPathRoot(path) ?? throw new ArgumentException("The path is not absolute.", nameof(path)), path.Split(Separators));

    /// <summary>The listed link <paramref name="name"/> in the real directory <paramref name="directory"/>, as what it leads to.</summary>
    private static MediaEntry Link(string directory, string name, string realRoot)
    {
        string? resolved;
        try
        {
            resolved = Walk(directory, [name]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new MediaEntry(name, MediaEntryKind.DanglingLink);
        }

        if (resolved is null)
        {
            return new MediaEntry(name, MediaEntryKind.LoopingLink);
        }

        if (!IsInside(resolved, realRoot))
        {
            return new MediaEntry(name, MediaEntryKind.EscapingLink);
        }

        if (Directory.Exists(resolved))
        {
            return new MediaEntry(name, MediaEntryKind.Directory) { RealPath = Relative(resolved, realRoot), ViaLink = true };
        }

        return File.Exists(resolved)
            ? new MediaEntry(name, MediaEntryKind.File) { ViaLink = true }
            : new MediaEntry(name, MediaEntryKind.DanglingLink);
    }

    /// <summary>
    /// Walks <paramref name="segments"/> from the real directory <paramref name="start"/>: a link's
    /// target replaces it (from the file system root when the target is absolute, else from the
    /// directory holding the link), and <c>..</c> steps up from where the walk really is, as the
    /// operating system resolves a path. Null when more than <see cref="MaximumLinkHops"/> links are met.
    /// </summary>
    private static string? Walk(string start, IEnumerable<string> segments)
    {
        var pending = new Stack<string>(segments.Reverse());
        var current = start;
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

            if (++hops > MaximumLinkHops)
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

    private string Root() => Path.TrimEndingDirectorySeparator(options.MediaPath);

    /// <summary>The mount root's real path, resolved again at each use (the mount may change under a running app).</summary>
    private string RealRoot() =>
        RealPath(Root()) is { } real
            ? Path.TrimEndingDirectorySeparator(real)
            : throw new IOException("The media folder's path never resolves.");

    /// <summary>
    /// The real path of <paramref name="relativePath"/> under <paramref name="realRoot"/>. Refuses, by
    /// its text, an absolute path, a NUL, a backslash, and any empty, <c>.</c>, or <c>..</c> segment
    /// (<see cref="ArgumentException"/>); then, once every link on the way is followed, anything that
    /// does not end under the root (<see cref="MediaPathOutsideException"/>).
    /// </summary>
    private static string ResolveInside(string relativePath, string realRoot)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (relativePath.Length == 0)
        {
            return realRoot;
        }

        if (relativePath.Contains('\0', StringComparison.Ordinal)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("The path is not a relative path inside the media folder.", nameof(relativePath));
        }

        var real = Walk(realRoot, relativePath.Split('/'));
        return real is not null && IsInside(real, realRoot) ? real : throw new MediaPathOutsideException();
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or under it (both real; compared ordinally, so a link spelled in another case counts as outside).</summary>
    private static bool IsInside(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) || path.StartsWith(Prefix(root), StringComparison.Ordinal);

    private static string Prefix(string root) => Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

    /// <summary><paramref name="real"/> relative to <paramref name="realRoot"/>, with <c>/</c> between segments; the root is the empty string.</summary>
    private static string Relative(string real, string realRoot) =>
        string.Equals(real, realRoot, StringComparison.Ordinal) ? string.Empty : string.Join('/', real[Prefix(realRoot).Length..].Split(Separators));

    private static string Join(string relativeDirectory, string name) => relativeDirectory.Length == 0 ? name : $"{relativeDirectory}/{name}";

    /// <summary>
    /// An opened file as callers get it: reads and seeks pass through to the reader's own stream; it
    /// cannot write, flush data, or change its length, and it has no name or handle to give out.
    /// </summary>
    private sealed class ReadOnlyContent(FileStream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override int ReadByte() => inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
            // Nothing is ever written, so there is nothing to flush.
        }

        public override void SetLength(long value) => throw new NotSupportedException("A media file is opened for reading only.");

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("A media file is opened for reading only.");

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
