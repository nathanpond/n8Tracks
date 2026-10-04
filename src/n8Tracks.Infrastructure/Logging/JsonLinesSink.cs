using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace n8Tracks.Infrastructure.Logging;

/// <summary>
/// Writes each event to a text writer as one JSON line (see <see cref="JsonLogFormatter"/>) and
/// flushes it, so nothing is lost on shutdown. Standard output and the tests' capture both use it.
/// </summary>
public sealed class JsonLinesSink(TextWriter output) : ILogEventSink
{
    private readonly JsonLogFormatter formatter = new();
    private readonly Lock gate = new();

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        using var line = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Format(logEvent, line);

        lock (gate)
        {
            output.Write(line.ToString());
            output.Flush();
        }
    }
}
