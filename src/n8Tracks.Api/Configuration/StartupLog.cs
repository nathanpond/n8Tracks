using System.Text.Encodings.Web;
using System.Text.Json;

namespace n8Tracks.Api.Configuration;

/// <summary>
/// Writes the lines that precede the application log (invalid settings, unknown variables, a failed
/// bind), one JSON object per line with the application log's keys: <c>timestamp</c>, <c>level</c>,
/// <c>message</c>, <c>properties</c>.
/// </summary>
internal sealed class StartupLog(TextWriter output, TimeProvider timeProvider)
{
    // The lines go to a log, not into HTML, so quotes and non-ASCII text are written as they are.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Warning(string message, string variable, string reason) => Write("Warning", message, variable, reason);

    public void Error(string message, string variable, string reason) => Write("Error", message, variable, reason);

    private void Write(string level, string message, string variable, string reason)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("timestamp", timeProvider.GetUtcNow().UtcDateTime);
            json.WriteString("level", level);
            json.WriteString("message", message);
            json.WriteStartObject("properties");
            json.WriteString("variable", variable);
            json.WriteString("reason", reason);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        output.Flush();
    }
}
