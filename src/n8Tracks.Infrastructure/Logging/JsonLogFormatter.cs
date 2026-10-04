using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;

namespace n8Tracks.Infrastructure.Logging;

/// <summary>
/// Writes one log event as one line of JSON with the keys <c>timestamp</c> (UTC, milliseconds,
/// <c>Z</c>), <c>level</c> (Microsoft's level names), <c>message</c> (the rendered text),
/// <c>properties</c>, and, when the event carries one, <c>exception</c> with <c>type</c>,
/// <c>message</c>, <c>stackTrace</c>, and a nested <c>innerException</c>.
/// </summary>
public sealed class JsonLogFormatter : ITextFormatter
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const int MaximumInnerExceptions = 5;

    // The lines go to a log, not into HTML, so quotes and non-ASCII text are written as they are.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("timestamp", logEvent.Timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            json.WriteString("level", LevelName(logEvent.Level));
            json.WriteString("message", RenderMessage(logEvent));

            json.WriteStartObject("properties");
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in logEvent.Properties)
            {
                var name = CamelCase(property.Key);
                if (written.Add(name))
                {
                    json.WritePropertyName(name);
                    WriteValue(json, property.Value);
                }
            }

            json.WriteEndObject();

            if (logEvent.Exception is not null)
            {
                json.WritePropertyName("exception");
                WriteException(json, logEvent.Exception, depth: 0);
            }

            json.WriteEndObject();
        }

        output.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        output.Write('\n');
    }

    /// <summary>The level under the name Microsoft's logging and <c>N8TRACKS_LOG_LEVEL</c> use.</summary>
    internal static string LevelName(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "Trace",
        LogEventLevel.Debug => "Debug",
        LogEventLevel.Information => "Information",
        LogEventLevel.Warning => "Warning",
        LogEventLevel.Error => "Error",
        _ => "Critical",
    };

    /// <summary>The message with its placeholders filled in; text values are written without quotes.</summary>
    private static string RenderMessage(LogEvent logEvent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);

        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken { Format: null } propertyToken
                && logEvent.Properties.TryGetValue(propertyToken.PropertyName, out var value)
                && value is ScalarValue { Value: string text })
            {
                writer.Write(text);
            }
            else
            {
                token.Render(logEvent.Properties, writer, CultureInfo.InvariantCulture);
            }
        }

        return writer.ToString();
    }

    private static void WriteException(Utf8JsonWriter json, Exception exception, int depth)
    {
        json.WriteStartObject();
        json.WriteString("type", exception.GetType().FullName ?? exception.GetType().Name);
        json.WriteString("message", RedactionPolicy.ScrubText(exception.Message));

        // Frames only: Exception.ToString() would repeat the unscrubbed messages.
        json.WriteString("stackTrace", exception.StackTrace ?? string.Empty);

        if (exception.InnerException is not null && depth < MaximumInnerExceptions)
        {
            json.WritePropertyName("innerException");
            WriteException(json, exception.InnerException, depth + 1);
        }

        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                WriteScalar(json, scalar.Value);
                break;

            case SequenceValue sequence:
                json.WriteStartArray();
                foreach (var element in sequence.Elements)
                {
                    WriteValue(json, element);
                }

                json.WriteEndArray();
                break;

            case StructureValue structure:
                {
                    json.WriteStartObject();
                    var written = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in structure.Properties)
                    {
                        var name = CamelCase(property.Name);
                        if (written.Add(name))
                        {
                            json.WritePropertyName(name);
                            WriteValue(json, property.Value);
                        }
                    }

                    json.WriteEndObject();
                    break;
                }

            case DictionaryValue dictionary:
                {
                    json.WriteStartObject();
                    var written = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var element in dictionary.Elements)
                    {
                        // Keys are data, not names: written as they are.
                        var key = Convert.ToString(element.Key.Value, CultureInfo.InvariantCulture) ?? "null";
                        if (written.Add(key))
                        {
                            json.WritePropertyName(key);
                            WriteValue(json, element.Value);
                        }
                    }

                    json.WriteEndObject();
                    break;
                }

            default:
                json.WriteStringValue(value.ToString(null, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case string text:
                json.WriteStringValue(text);
                break;
            case bool flag:
                json.WriteBooleanValue(flag);
                break;
            case int or long or short or sbyte or byte or ushort or uint:
                json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong number:
                json.WriteNumberValue(number);
                break;
            case decimal number:
                json.WriteNumberValue(number);
                break;
            case double number when double.IsFinite(number):
                json.WriteNumberValue(number);
                break;
            case float number when float.IsFinite(number):
                json.WriteNumberValue(number);
                break;
            case DateTime dateTime:
                json.WriteStringValue(dateTime);
                break;
            case DateTimeOffset dateTimeOffset:
                json.WriteStringValue(dateTimeOffset);
                break;
            case IFormattable formattable:
                json.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                json.WriteStringValue(value.ToString());
                break;
        }
    }

    private static string CamelCase(string name) =>
        name.Length > 0 && char.IsUpper(name[0])
            ? string.Concat(char.ToLowerInvariant(name[0]).ToString(), name.AsSpan(1))
            : name;
}
