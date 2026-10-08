using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Logging;
using Serilog.Core;
using Serilog.Events;

namespace n8Tracks.Infrastructure.Logging;

/// <summary>
/// The application log's files (#234): one more sink of the one Serilog pipeline, registered in the
/// container as the standard-output sink is, so every event reaches it after the redaction enricher
/// (invariant 6) and is written by the same <see cref="JsonLogFormatter"/>: a file line and a console
/// line are the same event. Files are <c>logs/n8tracks-YYYYMMDD[_n].jsonl</c> under the data folder,
/// dated in UTC, started afresh each day and whenever the next line would take the file past the roll
/// size (the smaller of 20 MB and a quarter of the cap).
/// <para>
/// Retention goes by the date in the file name: a file more than the retention's days older than today
/// is deleted, one exactly that old is kept. The cap is kept by deleting the oldest files first: at
/// each roll, so the files closed so far and the new one at its roll size fit under it, and at each
/// sweep (startup, hourly, and on a save). Together the files never pass the cap by more than one
/// line. Nothing is deleted until the saved limits have been put in effect (<see cref="Configure"/>),
/// so the defaults never cut a longer retention short. Any other file in the folder, and a link with a
/// log file's name, is ignored: never written through, never deleted.
/// </para>
/// <para>
/// Invariant 2: before the folder is created or a file opened, the folder (or, while it is not there,
/// the data folder that holds it) is compared with the media folder by <see cref="MediaFolderOverlap"/>
/// (#387), which also sees one host folder mounted at both; inside it, nothing is written. When the
/// folder cannot be used for any reason, the application goes on, logging to standard output only,
/// and <see cref="Problem"/> says why (without a path) until a sweep finds it usable again.
/// </para>
/// </summary>
public sealed partial class FileLogging : ILogEventSink, ILogFiles, IDisposable
{
    /// <summary>The folder under the data folder that holds the files.</summary>
    public const string FolderName = "logs";

    /// <summary>The largest a file grows before the next one is started.</summary>
    public const long MaximumRollBytes = 20L * 1024 * 1024;

    private const string Prefix = "n8tracks-";
    private const string Extension = ".jsonl";
    private const string DateFormat = "yyyyMMdd";

    private const string InsideTheMediaFolder = "The log folder is inside the media folder, and n8Tracks never writes there. Logs go to standard output only.";
    private const string NotAllowed = "n8Tracks is not allowed to write to the log folder. Logs go to standard output only.";
    private const string NotWritable = "The log folder could not be written to: it may not be a folder, or the disk may be full. Logs go to standard output only.";

    private readonly string? folder;
    private readonly string? mediaFolder;
    private readonly TimeProvider time;
    private readonly JsonLogFormatter formatter = new();
    private readonly Lock gate = new();

    private LogFileLimits limits = LogFileLimits.Default;
    private bool configured;
    private bool started;
    private bool unavailable;
    private string? problem;
    private FileStream? stream;
    private LogFileName? current;
    private long currentLength;

    /// <summary>The files of the data folder <paramref name="dataFolder"/>, never inside <paramref name="mediaFolder"/>.</summary>
    public FileLogging(string dataFolder, string mediaFolder, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(dataFolder);
        ArgumentNullException.ThrowIfNull(mediaFolder);
        ArgumentNullException.ThrowIfNull(time);

        folder = Path.Join(dataFolder, FolderName);
        this.mediaFolder = mediaFolder;
        this.time = time;
    }

    private FileLogging(TimeProvider time) => this.time = time;

    /// <summary>No files at all: for a start whose settings are invalid, which stops before anything is written.</summary>
    public static FileLogging Off(TimeProvider time) => new(time ?? throw new ArgumentNullException(nameof(time)));

    /// <summary>The folder the files are in; null when there are none.</summary>
    public string? Folder => folder;

    public string? Problem
    {
        get
        {
            lock (gate)
            {
                return problem;
            }
        }
    }

    /// <summary>The size at which the next file is started: the smaller of 20 MB and a quarter of the cap.</summary>
    public long RollBytes
    {
        get
        {
            lock (gate)
            {
                return CurrentRollBytes();
            }
        }
    }

    /// <summary>
    /// Starts writing. Program calls it once it holds the data folder's lock, so a second process
    /// refused that lock never writes here. The first file is opened now, so a folder that cannot be
    /// written is reported from the start.
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            if (folder is null || started)
            {
                return;
            }

            started = true;
            TryUse(() => Roll(time.GetUtcNow()));
        }
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        lock (gate)
        {
            if (!started || unavailable)
            {
                return;
            }
        }

        using var text = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Format(logEvent, text);
        var line = Encoding.UTF8.GetBytes(text.ToString());

        lock (gate)
        {
            if (!started || unavailable)
            {
                return;
            }

            TryUse(() =>
            {
                var now = time.GetUtcNow();
                if (stream is null
                    || current!.Day != Today(now)
                    || (currentLength > 0 && currentLength + line.Length > CurrentRollBytes()))
                {
                    Roll(now);
                    if (stream is null)
                    {
                        return;
                    }
                }

                stream.Write(line);
                stream.Flush();
                currentLength += line.Length;
            });
        }
    }

    public void Configure(LogFileLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        lock (gate)
        {
            this.limits = limits;
            configured = true;
        }
    }

    public LogFileDeletion Preview(LogFileLimits current, LogFileLimits proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        lock (gate)
        {
            if (folder is null)
            {
                return LogFileDeletion.None;
            }

            try
            {
                var files = List();
                var today = Today(time.GetUtcNow());
                var anyway = Doomed(files, current, today, this.current?.Name, reserve: KeepLength(files));
                var extra = Doomed(files, proposed, today, this.current?.Name, reserve: KeepLength(files))
                    .Where(file => !anyway.Contains(file))
                    .ToList();
                return new LogFileDeletion(extra.Count, extra.Sum(static file => file.Length));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return LogFileDeletion.None;
            }
        }
    }

    public LogFileDeletion Sweep()
    {
        lock (gate)
        {
            if (folder is null || !started || !configured)
            {
                return LogFileDeletion.None;
            }

            // Tried again: a folder that could not be written may have been put right.
            unavailable = false;
            problem = null;

            var deleted = LogFileDeletion.None;
            TryUse(() =>
            {
                if (stream is null)
                {
                    Roll(time.GetUtcNow());
                    if (stream is null)
                    {
                        return;
                    }
                }

                var files = List();
                deleted = Delete(Doomed(files, limits, Today(time.GetUtcNow()), current?.Name, reserve: KeepLength(files)));
            });

            return deleted;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            Close();
            started = false;
        }
    }

    /// <summary>
    /// Closes the file and opens the one lines go to now: today's newest when it is not the one just
    /// closed and has room, otherwise the next in today's sequence. Then the oldest files are deleted
    /// until those closed and the new one at its roll size fit under the cap.
    /// </summary>
    private void Roll(DateTimeOffset now)
    {
        var previous = current?.Name;
        Close();
        if (!UseFolder())
        {
            Fail(InsideTheMediaFolder);
            return;
        }

        var day = Today(now);
        var files = List();
        var newest = files.Where(file => file.Day == day).MaxBy(static file => file.Sequence);
        var rollBytes = CurrentRollBytes();
        var next = newest is not null && newest.Name != previous && newest.Length < rollBytes
            ? newest
            : new LogFileName(day, newest is null ? 0 : newest.Sequence + 1, 0);

        // A link under a log file's name is never written through: the next name is taken instead.
        while (new FileInfo(Path.Join(folder, next.Name)).LinkTarget is not null)
        {
            next = next with { Sequence = next.Sequence + 1, Length = 0 };
        }

        stream = new FileStream(Path.Join(folder, next.Name), FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        current = next;
        currentLength = stream.Length;

        if (configured)
        {
            Delete(Doomed(List(), limits, day, next.Name, reserve: rollBytes));
        }
    }

    /// <summary>
    /// False when the folder is in the media folder (checked before it is created, and again after);
    /// otherwise makes sure it is there.
    /// </summary>
    private bool UseFolder()
    {
        if (IsInTheMediaFolder())
        {
            return false;
        }

        Directory.CreateDirectory(folder!);
        return !IsInTheMediaFolder();
    }

    /// <summary>The folder, or while it is not there the data folder that would hold it, is the media folder or inside it.</summary>
    private bool IsInTheMediaFolder() =>
        MediaFolderOverlap.IsInside(Directory.Exists(folder) ? folder! : Path.GetDirectoryName(folder)!, mediaFolder!);

    /// <summary>Runs <paramref name="work"/>; when the folder cannot be used, stops writing until the next sweep and records why.</summary>
    private void TryUse(Action work)
    {
        try
        {
            work();
        }
        catch (UnauthorizedAccessException)
        {
            Fail(NotAllowed);
        }
        catch (IOException)
        {
            Fail(NotWritable);
        }
    }

    /// <summary>Stops writing until the next sweep, and records <paramref name="reason"/>.</summary>
    private void Fail(string reason)
    {
        Close();
        current = null;
        unavailable = true;
        problem = reason;
    }

    private void Close()
    {
        stream?.Dispose();
        stream = null;
    }

    private long CurrentRollBytes() => Math.Min(MaximumRollBytes, limits.MaxBytes / 4);

    /// <summary>The length of the file lines go to, as listed: what a sweep counts toward the cap for it.</summary>
    private long KeepLength(List<LogFileName> files) =>
        files.FirstOrDefault(file => file.Name == current?.Name)?.Length ?? 0;

    /// <summary>The log files in the folder (links and other files left out), with their sizes.</summary>
    private List<LogFileName> List()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var files = new List<LogFileName>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (file.LinkTarget is null && Parse(file.Name, file.Length) is { } name)
            {
                files.Add(name);
            }
        }

        return files;
    }

    /// <summary>
    /// The files, but <paramref name="keep"/>, that <paramref name="limits"/> delete: those older than
    /// the retention, then the oldest of the rest while they and <paramref name="reserve"/> bytes for
    /// <paramref name="keep"/> are over the cap.
    /// </summary>
    private static HashSet<LogFileName> Doomed(List<LogFileName> files, LogFileLimits limits, DateOnly today, string? keep, long reserve)
    {
        var oldestKept = today.AddDays(-limits.RetentionDays);
        var others = files.Where(file => file.Name != keep).ToList();
        var doomed = others.Where(file => file.Day < oldestKept).ToHashSet();

        var remaining = others.Where(file => !doomed.Contains(file)).OrderBy(static file => file.Day).ThenBy(static file => file.Sequence).ToList();
        var total = remaining.Sum(static file => file.Length) + reserve;
        foreach (var file in remaining)
        {
            if (total <= limits.MaxBytes)
            {
                break;
            }

            doomed.Add(file);
            total -= file.Length;
        }

        return doomed;
    }

    private LogFileDeletion Delete(IEnumerable<LogFileName> doomed)
    {
        var count = 0;
        var bytes = 0L;
        foreach (var file in doomed)
        {
            var path = Path.Join(folder, file.Name);
            if (new FileInfo(path).LinkTarget is not null)
            {
                continue;
            }

            File.Delete(path);
            count++;
            bytes += file.Length;
        }

        return new LogFileDeletion(count, bytes);
    }

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    /// <summary>A log file's name read back, or null when it is not one.</summary>
    internal static LogFileName? Parse(string name, long length)
    {
        var match = FileNamePattern().Match(name);
        if (!match.Success
            || !DateOnly.TryParseExact(match.Groups[1].Value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return null;
        }

        var sequence = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return new LogFileName(day, sequence, length);
    }

    [GeneratedRegex(@"^n8tracks-(\d{8})(?:_([1-9]\d{0,8}))?\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    /// <summary>One log file: its day, its place in that day's sequence (0 for the first), and its size.</summary>
    internal sealed record LogFileName(DateOnly Day, int Sequence, long Length)
    {
        public string Name => Prefix + Day.ToString(DateFormat, CultureInfo.InvariantCulture)
            + (Sequence == 0 ? string.Empty : "_" + Sequence.ToString(CultureInfo.InvariantCulture)) + Extension;
    }
}
