using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog.Core;
using Serilog.Events;

namespace n8Tracks.Infrastructure.Logging;

/// <summary>
/// Turns a logged <see cref="JsonElement"/>, <see cref="JsonDocument"/>, or <see cref="JsonNode"/>
/// into a property tree, so that its members can be masked by name like any other logged object.
/// Sensitive members are masked here as well, so their values are never copied into the event.
/// </summary>
public sealed class JsonDestructuringPolicy : IDestructuringPolicy
{
    private static readonly ScalarValue Null = new(null);
    private static readonly ScalarValue RedactedValue = new(RedactionPolicy.Redacted);

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        switch (value)
        {
            case JsonElement element:
                result = Convert(element, depth: 1);
                return true;

            case JsonDocument document:
                result = Convert(document.RootElement, depth: 1);
                return true;

            case JsonNode node:
                result = Convert(JsonSerializer.SerializeToElement(node), depth: 1);
                return true;

            default:
                result = null;
                return false;
        }
    }

    private static LogEventPropertyValue Convert(JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    if (depth > RedactionPolicy.MaximumDepth)
                    {
                        return Null;
                    }

                    var properties = new List<LogEventProperty>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var member in element.EnumerateObject())
                    {
                        if (properties.Count >= RedactionPolicy.MaximumCollectionCount)
                        {
                            break;
                        }

                        // A property name must be non-blank, and a repeated name keeps its first value.
                        var name = string.IsNullOrWhiteSpace(member.Name) ? "_" : member.Name;
                        if (!seen.Add(name))
                        {
                            continue;
                        }

                        var memberValue = RedactionPolicy.IsSensitive(name)
                            ? (member.Value.ValueKind == JsonValueKind.Null ? Null : RedactedValue)
                            : Convert(member.Value, depth + 1);

                        properties.Add(new LogEventProperty(name, memberValue));
                    }

                    return new StructureValue(properties);
                }

            case JsonValueKind.Array:
                {
                    if (depth > RedactionPolicy.MaximumDepth)
                    {
                        return Null;
                    }

                    var elements = new List<LogEventPropertyValue>();
                    foreach (var item in element.EnumerateArray())
                    {
                        if (elements.Count >= RedactionPolicy.MaximumCollectionCount)
                        {
                            break;
                        }

                        elements.Add(Convert(item, depth + 1));
                    }

                    return new SequenceValue(elements);
                }

            case JsonValueKind.String:
                {
                    var text = element.GetString() ?? string.Empty;
                    return new ScalarValue(text.Length <= RedactionPolicy.MaximumStringLength
                        ? text
                        : string.Concat(text.AsSpan(0, RedactionPolicy.MaximumStringLength - 1), "…"));
                }

            case JsonValueKind.Number:
                return element.TryGetInt64(out var whole)
                    ? new ScalarValue(whole)
                    : element.TryGetDecimal(out var exact) ? new ScalarValue(exact) : new ScalarValue(element.GetDouble());

            case JsonValueKind.True:
                return new ScalarValue(true);

            case JsonValueKind.False:
                return new ScalarValue(false);

            default:
                return Null;
        }
    }
}
