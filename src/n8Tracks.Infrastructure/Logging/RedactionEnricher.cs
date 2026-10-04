using Serilog.Core;
using Serilog.Events;

namespace n8Tracks.Infrastructure.Logging;

/// <summary>
/// Masks every property of a log event whose name is sensitive, at any depth, before the event
/// reaches a sink. It must be the last enricher so that it sees properties added by the others
/// (log scopes and the log context included).
/// </summary>
public sealed class RedactionEnricher : ILogEventEnricher
{
    private static readonly ScalarValue RedactedValue = new(RedactionPolicy.Redacted);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        List<LogEventProperty>? replacements = null;

        foreach (var property in logEvent.Properties)
        {
            var redacted = Redact(property.Key, property.Value);
            if (!ReferenceEquals(redacted, property.Value))
            {
                (replacements ??= []).Add(new LogEventProperty(property.Key, redacted));
            }
        }

        if (replacements is null)
        {
            return;
        }

        foreach (var replacement in replacements)
        {
            logEvent.AddOrUpdateProperty(replacement);
        }
    }

    /// <summary>Returns the same instance when nothing under <paramref name="value"/> needed masking.</summary>
    internal static LogEventPropertyValue Redact(string? name, LogEventPropertyValue value)
    {
        if (RedactionPolicy.IsSensitive(name))
        {
            // Null stays null: it shows the value was absent without revealing anything.
            return value is ScalarValue { Value: null } ? value : RedactedValue;
        }

        switch (value)
        {
            case StructureValue structure:
                {
                    var changed = false;
                    var properties = new List<LogEventProperty>(structure.Properties.Count);
                    foreach (var property in structure.Properties)
                    {
                        var redacted = Redact(property.Name, property.Value);
                        changed |= !ReferenceEquals(redacted, property.Value);
                        properties.Add(changed ? new LogEventProperty(property.Name, redacted) : property);
                    }

                    return changed ? new StructureValue(properties, structure.TypeTag) : value;
                }

            case DictionaryValue dictionary:
                {
                    var changed = false;
                    var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                    foreach (var element in dictionary.Elements)
                    {
                        var redacted = Redact(element.Key.Value?.ToString(), element.Value);
                        changed |= !ReferenceEquals(redacted, element.Value);
                        elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(element.Key, redacted));
                    }

                    return changed ? new DictionaryValue(elements) : value;
                }

            case SequenceValue sequence:
                {
                    var changed = false;
                    var elements = new List<LogEventPropertyValue>(sequence.Elements.Count);
                    foreach (var element in sequence.Elements)
                    {
                        var redacted = Redact(null, element);
                        changed |= !ReferenceEquals(redacted, element);
                        elements.Add(redacted);
                    }

                    return changed ? new SequenceValue(elements) : value;
                }

            default:
                return value;
        }
    }
}
