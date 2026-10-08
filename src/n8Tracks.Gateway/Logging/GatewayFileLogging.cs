using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using n8Tracks.Gateway.Configuration;

namespace n8Tracks.Gateway.Logging;

/// <summary>
/// The gateway's log files (#234), when <c>N8TRACKS_GATEWAY_LOG_PATH</c> is set: a logging provider
/// beside the console that writes the same lines, by the same JSON console formatter, filtered by the
/// same rules (<c>N8TRACKS_LOG_LEVEL</c>, framework categories at Warning). The gateway may share no
/// code with the application, so this is its own small copy of the application's file rules: files
/// <c>n8tracks-gateway-YYYYMMDD[_n].jsonl</c> in that folder, dated in UTC, started afresh each day
/// and when the next line would take a file past the roll size (the smaller of 20 MB and a quarter
/// of the cap); a file more than the retention's days older than today (by its name) is deleted, and
/// the oldest are deleted first to keep the files under the cap, at each roll and at the hourly sweep.
/// Other files in the folder, and links, are never written through or deleted. When the folder
/// cannot be written the gateway logs to the console only, <see cref="Problem"/> says why (Program
/// writes it as a Warning), and the next sweep tries again. The gateway's startup-failure lines and the
/// <c>--healthcheck</c> command write to the console only.
/// </summary>
internal sealed partial class GatewayFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    /// <summary>The largest a file grows before the next one is started.</summary>
    public const long MaximumRollBytes = 20L * 1024 * 1024;

    private const string Prefix = "n8tracks-gateway-";
    private const string Extension = ".jsonl";
    private const string DateFormat = "yyyyMMdd";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly GatewayLogFiles? settings;
    private readonly string? folder;
    private readonly ConsoleFormatter? formatter;
    private readonly TimeProvider time;
    private readonly ITimer? sweeps;
    private readonly Lock gate = new();

    private IExternalScopeProvider? scopes;
    private bool unavailable;
    private string? problem;
    private FileStream? stream;
    private LogFile? current;
    private long currentLength;
    private bool disposed;

    public GatewayFileLoggerProvider(EnvironmentSnapshot environment, IEnumerable<ConsoleFormatter> formatters, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(formatters);
        ArgumentNullException.ThrowIfNull(time);

        this.time = time;
        settings = GatewayOptionsLoader.LogFilesOrDefault(environment);
        if (settings is null)
        {
            return;
        }

        folder = Path.GetFullPath(settings.Folder);
        formatter = formatters.First(static candidate => candidate.Name == ConsoleFormatterNames.Json);

        lock (gate)
        {
            TryUse(() => Roll(time.GetUtcNow()));
        }

        sweeps = time.CreateTimer(static state => ((GatewayFileLoggerProvider)state!).Sweep(), this, SweepInterval, SweepInterval);
    }

    /// <summary>Why the folder cannot be written, without its path; null while it can be or when there are no files.</summary>
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

    public ILogger CreateLogger(string categoryName) =>
        formatter is null ? NullLogger.Instance : new FileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    /// <summary>
    /// Deletes the files older than the retention and, oldest first, those over the cap, and tries
    /// a folder that could not be written again. Runs hourly; tests call it directly.
    /// </summary>
    public void Sweep()
    {
        lock (gate)
        {
            if (settings is null || disposed)
            {
                return;
            }

            unavailable = false;
            problem = null;
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
                Delete(Doomed(files, settings, Today(time.GetUtcNow()), current?.Name, files.FirstOrDefault(file => file.Name == current?.Name)?.Length ?? 0));
            });
        }
    }

    public void Dispose()
    {
        sweeps?.Dispose();
        lock (gate)
        {
            disposed = true;
            stream?.Dispose();
            stream = null;
        }
    }

    private void Write(string text)
    {
        var line = Encoding.UTF8.GetBytes(text);
        lock (gate)
        {
            if (disposed || unavailable)
            {
                return;
            }

            TryUse(() =>
            {
                var now = time.GetUtcNow();
                if (stream is null
                    || current!.Day != Today(now)
                    || (currentLength > 0 && currentLength + line.Length > RollBytes()))
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

    private long RollBytes() => Math.Min(MaximumRollBytes, settings!.MaxBytes / 4);

    /// <summary>Opens today's newest file when it is not the one just closed and has room, otherwise the next; then keeps the cap.</summary>
    private void Roll(DateTimeOffset now)
    {
        var previous = current?.Name;
        stream?.Dispose();
        stream = null;
        Directory.CreateDirectory(folder!);

        var day = Today(now);
        var files = List();
        var newest = files.Where(file => file.Day == day).MaxBy(static file => file.Sequence);
        var next = newest is not null && newest.Name != previous && newest.Length < RollBytes()
            ? newest
            : new LogFile(day, newest is null ? 0 : newest.Sequence + 1, 0);
        while (new FileInfo(Path.Join(folder, next.Name)).LinkTarget is not null)
        {
            next = next with { Sequence = next.Sequence + 1, Length = 0 };
        }

        stream = new FileStream(Path.Join(folder, next.Name), FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        current = next;
        currentLength = stream.Length;

        Delete(Doomed(List(), settings!, day, next.Name, RollBytes()));
    }

    private void TryUse(Action work)
    {
        try
        {
            work();
        }
        catch (UnauthorizedAccessException)
        {
            Fail("the gateway is not allowed to write to the folder.");
        }
        catch (IOException)
        {
            Fail("the folder could not be written to: it may not be a folder, or the disk may be full.");
        }
    }

    private void Fail(string reason)
    {
        stream?.Dispose();
        stream = null;
        current = null;
        unavailable = true;
        problem = reason;
    }

    private List<LogFile> List()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var files = new List<LogFile>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (file.LinkTarget is null && Parse(file.Name, file.Length) is { } parsed)
            {
                files.Add(parsed);
            }
        }

        return files;
    }

    /// <summary>The files but <paramref name="keep"/> to delete: past the retention, then the oldest while they and <paramref name="reserve"/> are over the cap.</summary>
    private static List<LogFile> Doomed(List<LogFile> files, GatewayLogFiles limits, DateOnly today, string? keep, long reserve)
    {
        var oldestKept = today.AddDays(-limits.RetentionDays);
        var others = files.Where(file => file.Name != keep).OrderBy(static file => file.Day).ThenBy(static file => file.Sequence).ToList();
        var doomed = others.Where(file => file.Day < oldestKept).ToList();
        var total = others.Except(doomed).Sum(static file => file.Length) + reserve;
        foreach (var file in others.Except(doomed).ToList())
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

    private void Delete(List<LogFile> doomed)
    {
        foreach (var file in doomed)
        {
            var path = Path.Join(folder, file.Name);
            if (new FileInfo(path).LinkTarget is null)
            {
                File.Delete(path);
            }
        }
    }

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    private static LogFile? Parse(string name, long length)
    {
        var match = FileNamePattern().Match(name);
        return match.Success && DateOnly.TryParseExact(match.Groups[1].Value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? new LogFile(day, match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0, length)
            : null;
    }

    [GeneratedRegex(@"^n8tracks-gateway-(\d{8})(?:_([1-9]\d{0,8}))?\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    private sealed record LogFile(DateOnly Day, int Sequence, long Length)
    {
        public string Name => Prefix + Day.ToString(DateFormat, CultureInfo.InvariantCulture)
            + (Sequence == 0 ? string.Empty : "_" + Sequence.ToString(CultureInfo.InvariantCulture)) + Extension;
    }

    /// <summary>Formats an entry as the JSON console does and hands the line to the provider.</summary>
    private sealed class FileLogger(GatewayFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider.scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            using var text = new StringWriter(CultureInfo.InvariantCulture);
            var entry = new LogEntry<TState>(logLevel, category, eventId, state, exception, formatter);
            provider.formatter!.Write(in entry, provider.scopes, text);
            if (text.GetStringBuilder().Length > 0)
            {
                provider.Write(text.ToString());
            }
        }
    }
}
