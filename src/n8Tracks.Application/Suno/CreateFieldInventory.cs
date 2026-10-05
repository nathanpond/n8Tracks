using System.Text.Json;
using System.Text.RegularExpressions;

namespace n8Tracks.Application.Suno;

/// <summary>
/// Suno's Create-screen field inventory, <c>docs/suno-create-field-inventory.json</c>, embedded in this
/// assembly when it is built: the one source of each option's limits, ranges, value lists, defaults,
/// and the tabs and modes it belongs to. Code reads them from here instead of restating them, so a
/// re-captured inventory changes what n8Tracks accepts, and the inventory coverage test says what a
/// new field still lacks.
/// </summary>
public sealed class CreateFieldInventory
{
    /// <summary>The embedded resource's name.</summary>
    public const string ResourceName = "n8Tracks.Application.Suno.suno-create-field-inventory.json";

    private static readonly Lazy<CreateFieldInventory> EmbeddedInventory = new(LoadEmbedded);

    private readonly Dictionary<string, CreateField> byKey;

    private CreateFieldInventory(string json, IReadOnlyList<CreateField> fields)
    {
        Json = json;
        Fields = fields;
        byKey = fields.ToDictionary(static field => field.Key, StringComparer.Ordinal);
    }

    /// <summary>The inventory this build embeds.</summary>
    public static CreateFieldInventory Embedded => EmbeddedInventory.Value;

    /// <summary>The inventory file's text, as captured.</summary>
    public string Json { get; }

    /// <summary>Every field, in the inventory's order (Suno's own order on each tab).</summary>
    public IReadOnlyList<CreateField> Fields { get; }

    /// <summary>The field with <paramref name="key"/>, or null when the inventory has none.</summary>
    public CreateField? Find(string key) => byKey.GetValueOrDefault(key);

    /// <summary>The field with <paramref name="key"/>; throws when the inventory has none, which is a build defect.</summary>
    public CreateField Get(string key) =>
        Find(key) ?? throw new InvalidOperationException($"The Suno field inventory has no field '{key}'.");

    /// <summary>
    /// Reads an inventory from its JSON text. Throws <see cref="JsonException"/> when the text is not
    /// an inventory: every field needs a unique key, a tab, modes, and a type.
    /// </summary>
    public static CreateFieldInventory Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The inventory has no fields array.");
        }

        var read = fields.EnumerateArray().Select(ReadField).ToList();
        var duplicate = read.GroupBy(static field => field.Key, StringComparer.Ordinal).FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new JsonException($"The inventory names the field '{duplicate.Key}' twice.");
        }

        return new CreateFieldInventory(json, read);
    }

    private static CreateField ReadField(JsonElement field)
    {
        var key = RequiredString(field, "key");
        var hasDefault = field.TryGetProperty("default", out var defaultValue);

        return new CreateField(
            key,
            OptionalString(field, "label") ?? key,
            RequiredString(field, "tab"),
            field.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Array
                ? [.. modes.EnumerateArray().Select(static mode => mode.GetString() ?? throw new JsonException("A mode is not text."))]
                : throw new JsonException($"The field '{key}' has no modes."),
            RequiredString(field, "type"),
            field.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
                ? [.. values.EnumerateArray().Select(static value => value.GetString() ?? throw new JsonException("A value is not text."))]
                : null,
            hasDefault,
            hasDefault ? defaultValue.Clone() : default,
            OptionalInt(field, "maxLength"),
            OptionalInt(field, "min"),
            OptionalInt(field, "max"),
            OptionalString(field, "condition"),
            OptionalString(field, "notes"));
    }

    private static string RequiredString(JsonElement field, string name) =>
        OptionalString(field, name) ?? throw new JsonException($"An inventory field has no '{name}'.");

    private static string? OptionalString(JsonElement field, string name) =>
        field.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? OptionalInt(JsonElement field, string name) =>
        field.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static CreateFieldInventory LoadEmbedded()
    {
        using var stream = typeof(CreateFieldInventory).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The Suno field inventory is not embedded as '{ResourceName}'.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

/// <summary>One field of Suno's Create screen, as the inventory records it.</summary>
/// <param name="Key">Its unique key, such as <c>style_influence</c>.</param>
/// <param name="Label">What Suno labels it.</param>
/// <param name="Tab">The tab it is on: <c>songs</c>, <c>speech</c>, or <c>sounds</c>.</param>
/// <param name="Modes">The forms it appears in: <c>simple</c>, <c>advanced</c>, or <c>single</c> (a tab with no modes).</param>
/// <param name="Type"><c>text</c>, <c>choice</c>, <c>toggle</c>, <c>range</c>, <c>number</c>, <c>reference</c>, or <c>file</c>.</param>
/// <param name="Values">A choice's values; null when the field has no list.</param>
/// <param name="HasDefault">Whether the inventory records a default (it may be <c>null</c>).</param>
/// <param name="Default">The default, when <paramref name="HasDefault"/>.</param>
/// <param name="MaxLength">A text field's limit, in characters.</param>
/// <param name="Min">A range's or number's lowest value.</param>
/// <param name="Max">A range's or number's highest value.</param>
/// <param name="Condition">When it is shown, in words.</param>
/// <param name="Notes">Suno's explanation and the capture's remarks.</param>
public sealed partial record CreateField(
    string Key,
    string Label,
    string Tab,
    IReadOnlyList<string> Modes,
    string Type,
    IReadOnlyList<string>? Values,
    bool HasDefault,
    JsonElement Default,
    int? MaxLength,
    int? Min,
    int? Max,
    string? Condition,
    string? Notes)
{
    public const string TextType = "text";
    public const string ChoiceType = "choice";
    public const string ToggleType = "toggle";
    public const string RangeType = "range";
    public const string NumberType = "number";
    public const string ReferenceType = "reference";
    public const string FileType = "file";

    /// <summary>Whether its default is recorded as <c>null</c>: nothing chosen, which a Version may then hold too.</summary>
    public bool DefaultsToNull => HasDefault && Default.ValueKind == JsonValueKind.Null;

    /// <summary>
    /// Suno's own explanation of the field, as its notes quote it (<c>Tooltip: '...'</c>); a tooltip
    /// Suno shows only at one value (<c>Tooltip at Max: '...'</c>) is prefixed with that value
    /// (<c>At Max: ...</c>). Null when the capture recorded none: the notes' other remarks are the
    /// capture's, not Suno's.
    /// </summary>
    public string? Help
    {
        get
        {
            if (Notes is null)
            {
                return null;
            }

            var tooltips = TooltipPattern().Matches(Notes)
                .Select(static match => match.Groups["at"].Success
                    ? $"At {match.Groups["at"].Value}: {match.Groups["text"].Value}"
                    : match.Groups["text"].Value)
                .ToList();
            return tooltips.Count == 0 ? null : string.Join(" ", tooltips);
        }
    }

    [GeneratedRegex(@"Tooltip(?: at (?<at>\w+))?: '(?<text>[^']+)'", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TooltipPattern();
}
