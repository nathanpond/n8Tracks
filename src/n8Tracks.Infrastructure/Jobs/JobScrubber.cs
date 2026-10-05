using System.Text;
using System.Text.Json;
using n8Tracks.Infrastructure.Logging;

namespace n8Tracks.Infrastructure.Jobs;

/// <summary>
/// What a job may store of what its handler reported, threw, or returned: the same scrubbing the
/// log applies (invariant 6), so a job record never holds what a log line could not.
/// </summary>
internal static class JobScrubber
{
    /// <summary>The longest progress message kept; the rest is cut.</summary>
    public const int MessageMaximumLength = 500;

    /// <summary>The longest error kept; the rest is cut.</summary>
    public const int ErrorMaximumLength = 1000;

    /// <summary>The progress text with sensitive values masked and cut to length; null stays null.</summary>
    public static string? Message(string? message) =>
        message is null ? null : Shorten(RedactionPolicy.ScrubText(message), MessageMaximumLength);

    /// <summary>The exception's type and its message with sensitive values masked, cut to length. Never a stack trace.</summary>
    public static string Error(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var type = exception.GetType().FullName ?? exception.GetType().Name;
        return Shorten($"{type}: {RedactionPolicy.ScrubText(exception.Message)}", ErrorMaximumLength);
    }

    /// <summary>
    /// The result as JSON text, with every string value scrubbed like free text and the value of
    /// every property whose name is sensitive replaced, whatever it was, by the redaction marker.
    /// Null and JSON <c>null</c> stay as they are.
    /// </summary>
    public static string? Result(JsonElement? result)
    {
        if (result is not { ValueKind: not JsonValueKind.Undefined } value)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (RedactionPolicy.IsSensitive(property.Name) && property.Value.ValueKind != JsonValueKind.Null)
                    {
                        writer.WriteStringValue(RedactionPolicy.Redacted);
                    }
                    else
                    {
                        Write(writer, property.Value);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(RedactionPolicy.ScrubText(element.GetString()));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Shorten(string text, int maximumLength)
    {
        if (text.Length <= maximumLength)
        {
            return text;
        }

        // Never cut a surrogate pair in two.
        var length = maximumLength - 1;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return string.Concat(text.AsSpan(0, length), "…");
    }
}
