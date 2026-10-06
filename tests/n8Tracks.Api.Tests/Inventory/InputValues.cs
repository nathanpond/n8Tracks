using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Inventory;

/// <summary>
/// Valid values for a Version's options that differ from the ones it holds, worked out from the
/// option's type and the embedded inventory, so a test can change every option without naming them.
/// </summary>
internal static class InputValues
{
    /// <summary>A valid value of the option the API calls <paramref name="key"/> other than <paramref name="current"/>, as JSON.</summary>
    public static string ChangedJson(string key, JsonElement current)
    {
        switch (key)
        {
            case VersionInputRules.KindKey:
                return current.GetString() == "song" ? "\"speech\"" : "\"song\"";
            case VersionInputRules.SongModeKey or VersionInputRules.SpeechModeKey:
                return current.GetString() == "advanced" ? "\"simple\"" : "\"advanced\"";
        }

        var field = CreateFieldInventory.Embedded.Get(VersionInputRules.InventoryKey(key) ?? throw new ArgumentException($"'{key}' is not an option.", nameof(key)));
        switch (current.ValueKind)
        {
            case JsonValueKind.True:
                return "false";
            case JsonValueKind.False:
                return "true";
            case JsonValueKind.Number:
                var number = current.GetInt32();
                return (number < field.Max ? number + 1 : number - 1).ToString(CultureInfo.InvariantCulture);
            case JsonValueKind.Null when field.Type is CreateField.NumberType or CreateField.RangeType:
                // A number left empty (a Sound's BPM on Auto) changes to the lowest it can be.
                return (field.Min ?? 0).ToString(CultureInfo.InvariantCulture);
        }

        if (field.Values is { } values)
        {
            return JsonSerializer.Serialize(values.First(value => value != current.GetString()));
        }

        // Short and new each time, so it is never the current text and stays within any limit.
        return JsonSerializer.Serialize("changed " + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>
    /// <paramref name="inputs"/> (a Version's <c>inputs</c>) with every option changed, as a JSON
    /// object; the lineage keys (#122) are left out (<see cref="LineageValues"/> changes those).
    /// </summary>
    public static JsonObject EveryOptionChanged(JsonElement inputs)
    {
        var changed = new JsonObject();
        foreach (var option in inputs.EnumerateObject().Where(static option => !VersionLineageInputs.IsLineageKey(option.Name)))
        {
            changed[option.Name] = JsonNode.Parse(ChangedJson(option.Name, option.Value));
        }

        return changed;
    }

    /// <summary><paramref name="inputs"/> with the option the API calls <paramref name="key"/> changed.</summary>
    public static VersionInputs WithChanged(VersionInputs inputs, string key)
    {
        var json = VersionInputRules.ToJson(inputs);
        using var document = JsonDocument.Parse(json.ToJsonString());
        json[key] = JsonNode.Parse(ChangedJson(key, document.RootElement.GetProperty(key)));
        return VersionInputRules.FromJson(json);
    }

    /// <summary>The options a test Version starts with: the defaults for a Song called <paramref name="title"/>.</summary>
    public static VersionInputs Defaults(string title = "Test") => VersionInputRules.Defaults(CreateFieldInventory.Embedded, title);
}
