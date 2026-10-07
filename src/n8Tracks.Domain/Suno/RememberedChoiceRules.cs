using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace n8Tracks.Domain.Suno;

/// <summary>
/// How a declined change and a kept conflict are remembered (#141). Each is a SHA-256, as lower-case hex,
/// over a canonical JSON object, and is stored on the Generation:
/// <list type="bullet">
/// <item>the declined hash covers Suno's incoming value of every compared field
/// (<see cref="SunoExportRules.ComparedFields"/>, in that order; the image address without its query
/// string, as the comparison takes it), so any later change in Suno, to a declined field or another one,
/// gives another hash;</item>
/// <item>the kept hash covers the clip's mapped creation inputs as they are compared (each option's
/// compared text, keys in ordinal order).</item>
/// </list>
/// While a sync brings data with the stored hash, the clip is not Changed (or not Conflict) again. The
/// hashes say nothing readable about the values: they are never shown or logged.
/// </summary>
public static class RememberedChoiceRules
{
    /// <summary>
    /// The value of the compared field <paramref name="field"/> in <paramref name="fields"/>, as the
    /// comparison and the diff take it (text or a number; the image address without its query string);
    /// null when absent.
    /// </summary>
    public static object? ComparedValue(ClipFields fields, string field)
    {
        ArgumentNullException.ThrowIfNull(fields);

        return field switch
        {
            "title" => fields.Title,
            "tags" => fields.StyleTags,
            "duration" => fields.DurationSeconds,
            "modelVersion" => fields.ModelVersion,
            "modelName" => fields.ModelName,
            "minimumBpm" => fields.MinimumBpm,
            "maximumBpm" => fields.MaximumBpm,
            "averageBpm" => fields.AverageBpm,
            "key" => fields.Key,
            "imageUrl" => SunoExportRules.WithoutQuery(fields.ImageUrl),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a compared field."),
        };
    }

    /// <summary>The declined hash of Suno's incoming <paramref name="incoming"/>: over the value of every compared field.</summary>
    public static string DeclinedHash(ClipFields incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        return Hash(writer =>
        {
            foreach (var field in SunoExportRules.ComparedFields)
            {
                writer.WritePropertyName(field);
                switch (ComparedValue(incoming, field))
                {
                    case null:
                        writer.WriteNullValue();
                        break;
                    case double number:
                        writer.WriteStringValue(number.ToString("R", CultureInfo.InvariantCulture));
                        break;
                    case string text:
                        writer.WriteStringValue(text);
                        break;
                    case var other:
                        throw new InvalidOperationException($"A compared value of type {other.GetType().Name} cannot be hashed.");
                }
            }
        });
    }

    /// <summary>The kept hash of a clip's compared creation inputs <paramref name="compared"/> (key: compared text).</summary>
    public static string KeptInputsHash(IReadOnlyDictionary<string, string> compared)
    {
        ArgumentNullException.ThrowIfNull(compared);

        return Hash(writer =>
        {
            foreach (var (key, value) in compared.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(key, value);
            }
        });
    }

    /// <summary>
    /// Whether a sync's clip is still the one the user decided on: <paramref name="stored"/> is set and
    /// equals <paramref name="incoming"/>.
    /// </summary>
    public static bool Remembers(string? stored, string incoming) =>
        stored is not null && string.Equals(stored, incoming, StringComparison.Ordinal);

    private static string Hash(Action<Utf8JsonWriter> members)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            members(writer);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
