using n8Tracks.Application.Configuration;
using n8Tracks.Application.Logging;
using Serilog.Core;
using Serilog.Events;

namespace n8Tracks.Infrastructure.Logging;

/// <summary>
/// The application log's level while it runs (#234): one switch for every category and one for
/// <c>Microsoft.*</c> and <c>System.*</c>, which stay at Warning (or the level, when that is stricter),
/// Debug included. The one Serilog pipeline reads both, so standard output and the files change together.
/// </summary>
public sealed class LogLevelSwitches : ILogLevelControl
{
    private readonly Lock gate = new();
    private N8TracksLogLevel level;

    public LogLevelSwitches(N8TracksLogLevel initial)
    {
        Minimum = new LoggingLevelSwitch();
        Framework = new LoggingLevelSwitch();
        Set(initial);
    }

    /// <summary>The level of every category but the framework's.</summary>
    public LoggingLevelSwitch Minimum { get; }

    /// <summary>The level of <c>Microsoft.*</c> and <c>System.*</c>.</summary>
    public LoggingLevelSwitch Framework { get; }

    public N8TracksLogLevel Level
    {
        get
        {
            lock (gate)
            {
                return level;
            }
        }
    }

    public void Set(N8TracksLogLevel level)
    {
        lock (gate)
        {
            this.level = level;
            var minimum = ToSerilogLevel(level);
            Minimum.MinimumLevel = minimum;
            Framework.MinimumLevel = minimum > LogEventLevel.Warning ? minimum : LogEventLevel.Warning;
        }
    }

    /// <summary>Serilog's level for <paramref name="level"/>.</summary>
    public static LogEventLevel ToSerilogLevel(N8TracksLogLevel level) => level switch
    {
        N8TracksLogLevel.Trace => LogEventLevel.Verbose,
        N8TracksLogLevel.Debug => LogEventLevel.Debug,
        N8TracksLogLevel.Information => LogEventLevel.Information,
        N8TracksLogLevel.Warning => LogEventLevel.Warning,
        N8TracksLogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };
}
