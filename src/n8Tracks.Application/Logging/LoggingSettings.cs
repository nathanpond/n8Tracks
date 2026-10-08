using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Application.Logging;

/// <summary>
/// How much the application logs and how long its log files are kept (#234), as the administrator
/// sets them in Settings → Diagnostics. The level applies to standard output and the files alike;
/// the retention and the size cap to the files only.
/// </summary>
/// <param name="Level">Error, Warning, Information, or Debug.</param>
/// <param name="RetentionDays">How many days of files are kept: <see cref="MinimumRetentionDays"/> to <see cref="MaximumRetentionDays"/>.</param>
/// <param name="MaxMegabytes">The most the files may take together: <see cref="MinimumMegabytes"/> to <see cref="MaximumMegabytes"/>.</param>
/// <param name="DebugUntil">When a saved Debug level switches itself back to Information; null at any other level.</param>
public sealed record LoggingSettings(N8TracksLogLevel Level, int RetentionDays, int MaxMegabytes, DateTimeOffset? DebugUntil)
{
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 90;
    public const int MinimumMegabytes = 10;
    public const int MaximumMegabytes = 5120;
    public const int DefaultRetentionDays = 14;
    public const int DefaultMegabytes = 200;

    public const string LevelField = "level";
    public const string RetentionDaysField = "retentionDays";
    public const string MaxMegabytesField = "maxMegabytes";

    /// <summary>How long a saved Debug level lasts before it switches itself back to Information.</summary>
    public static readonly TimeSpan DebugDuration = TimeSpan.FromHours(24);

    /// <summary>The levels the administrator can choose, by the name the API uses for each.</summary>
    public static readonly IReadOnlyDictionary<string, N8TracksLogLevel> SettableLevels =
        new Dictionary<string, N8TracksLogLevel>(StringComparer.Ordinal)
        {
            ["error"] = N8TracksLogLevel.Error,
            ["warning"] = N8TracksLogLevel.Warning,
            ["information"] = N8TracksLogLevel.Information,
            ["debug"] = N8TracksLogLevel.Debug,
        };

    /// <summary>The limits of the files.</summary>
    public LogFileLimits Limits => new(RetentionDays, MaxMegabytes);

    /// <summary>The API's name for <paramref name="level"/>: <c>trace</c>, <c>debug</c>, <c>information</c>, <c>warning</c>, <c>error</c>, or <c>critical</c>.</summary>
    public static string LevelName(N8TracksLogLevel level) => level switch
    {
        N8TracksLogLevel.Trace => "trace",
        N8TracksLogLevel.Debug => "debug",
        N8TracksLogLevel.Information => "information",
        N8TracksLogLevel.Warning => "warning",
        N8TracksLogLevel.Error => "error",
        _ => "critical",
    };

    /// <summary>
    /// Reads the settings as a client sends them: <paramref name="level"/> one of the
    /// <see cref="SettableLevels"/> by name, and the two limits whole numbers in range. A missing field
    /// arrives as <see cref="JsonValueKind.Undefined"/>. The errors are keyed by field name; empty when
    /// valid. <c>DebugUntil</c> is left null: the service sets it when the level is Debug.
    /// </summary>
    public static (LoggingSettings? Settings, Dictionary<string, string[]> Errors) Parse(JsonElement level, JsonElement retentionDays, JsonElement maxMegabytes)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var chosen = N8TracksLogLevel.Information;
        if (level.ValueKind != JsonValueKind.String || !SettableLevels.TryGetValue(level.GetString()!, out chosen))
        {
            errors[LevelField] = ["Choose error, warning, information, or debug."];
        }

        var days = WholeNumber(retentionDays, MinimumRetentionDays, MaximumRetentionDays);
        if (days is null)
        {
            errors[RetentionDaysField] = [string.Create(
                CultureInfo.InvariantCulture,
                $"Enter a whole number of days from {MinimumRetentionDays} to {MaximumRetentionDays}.")];
        }

        var megabytes = WholeNumber(maxMegabytes, MinimumMegabytes, MaximumMegabytes);
        if (megabytes is null)
        {
            errors[MaxMegabytesField] = [string.Create(
                CultureInfo.InvariantCulture,
                $"Enter a whole number of megabytes from {MinimumMegabytes} to {MaximumMegabytes:N0} (5 GB).")];
        }

        return errors.Count > 0
            ? (null, errors)
            : (new LoggingSettings(chosen, days!.Value, megabytes!.Value, DebugUntil: null), errors);
    }

    private static int? WholeNumber(JsonElement value, int minimum, int maximum) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= minimum && number <= maximum
            ? number
            : null;
}

/// <summary>The limits of the log files.</summary>
/// <param name="RetentionDays">A file whose name dates it more than this many days before today (UTC) is deleted; one exactly this old is kept.</param>
/// <param name="MaxMegabytes">The files together are kept to this size, the oldest deleted first.</param>
public sealed record LogFileLimits(int RetentionDays, int MaxMegabytes)
{
    /// <summary>What every instance has until the administrator changes it: 14 days, 200 MB.</summary>
    public static readonly LogFileLimits Default = new(LoggingSettings.DefaultRetentionDays, LoggingSettings.DefaultMegabytes);

    /// <summary>The cap in bytes.</summary>
    public long MaxBytes => MaxMegabytes * 1024L * 1024L;
}

/// <summary>The settings as they are stored.</summary>
/// <param name="Revision">0 until they are first saved, then 1, 2, ….</param>
public sealed record StoredLoggingSettings(LoggingSettings Settings, int Revision);

/// <summary>Where the settings are kept: the <c>settings</c> row <c>logging</c>.</summary>
public interface ILoggingSettingsStore
{
    /// <summary>The stored settings, or null when they were never saved.</summary>
    Task<StoredLoggingSettings?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the stored settings.</summary>
    Task WriteAsync(StoredLoggingSettings settings, CancellationToken cancellationToken);
}

/// <summary>Log files a change would delete.</summary>
/// <param name="Files">How many.</param>
/// <param name="Bytes">Their size together.</param>
public sealed record LogFileDeletion(int Files, long Bytes)
{
    public static readonly LogFileDeletion None = new(0, 0);
}

/// <summary>The application's log files (Infrastructure: <c>FileLogging</c>).</summary>
public interface ILogFiles
{
    /// <summary>Why the log folder cannot be written, in words without its path; null while it can be (or there are no files to write).</summary>
    string? Problem { get; }

    /// <summary>The files a sweep under <paramref name="proposed"/> would delete that one under <paramref name="current"/> would keep. Deletes nothing.</summary>
    LogFileDeletion Preview(LogFileLimits current, LogFileLimits proposed);

    /// <summary>Uses <paramref name="limits"/> from now on (the roll size follows the cap), without a sweep.</summary>
    void Configure(LogFileLimits limits);

    /// <summary>
    /// Deletes the files older than the retention and, oldest first, those over the cap; tries the
    /// folder again if it could not be written. Returns what was deleted.
    /// </summary>
    LogFileDeletion Sweep();
}

/// <summary>The level the application log is written at, standard output and files alike, changed while it runs.</summary>
public interface ILogLevelControl
{
    /// <summary>The level now.</summary>
    N8TracksLogLevel Level { get; }

    /// <summary>Writes at <paramref name="level"/> from the next event on.</summary>
    void Set(N8TracksLogLevel level);
}

/// <summary>Where the level in effect comes from.</summary>
public enum LogLevelSource
{
    /// <summary><c>N8TRACKS_LOG_LEVEL</c> (or its default): the settings were never saved.</summary>
    Environment,

    /// <summary>The saved settings.</summary>
    Setting,
}

/// <summary>What Settings → Diagnostics shows: the stored settings and what is in effect.</summary>
/// <param name="Stored">The stored settings; before the first save, the environment's level and the default limits at revision 0.</param>
/// <param name="Source">Where the level comes from.</param>
/// <param name="FolderProblem">Why the log folder cannot be written; null when it can.</param>
public sealed record LoggingView(StoredLoggingSettings Stored, LogLevelSource Source, string? FolderProblem);

/// <summary>How a change of the settings ended.</summary>
public abstract record LoggingSettingsUpdate
{
    private LoggingSettingsUpdate()
    {
    }

    /// <summary>Written and in effect; <paramref name="Deleted"/> is what the new limits deleted.</summary>
    public sealed record Updated(LoggingView Current, LogFileDeletion Deleted) : LoggingSettingsUpdate;

    /// <summary>The revision sent is not the current one; nothing was written.</summary>
    public sealed record Stale(LoggingView Current) : LoggingSettingsUpdate;

    /// <summary>The new limits would delete <paramref name="Deletion"/>; nothing was written until that is confirmed.</summary>
    public sealed record ConfirmationRequired(LogFileDeletion Deletion) : LoggingSettingsUpdate;
}
