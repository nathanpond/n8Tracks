using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>
/// The rules of a Version's Suno options (<see cref="VersionInputs"/>), read from Suno's field
/// inventory (<see cref="CreateFieldInventory"/>) instead of restated here: their defaults, what each
/// may hold, a partial edit merged key by key, and which of them apply to the Version's kind and mode
/// (what is later sent to Suno). The API spells each option by its <see cref="VersionInputs"/> property
/// in camelCase: the inventory key in camelCase (<c>exclude_styles</c> is <c>excludeStyles</c>), except
/// the Simple form's two "add a section" fields, which are held as whether the section is added
/// (<c>simpleLyricsAdded</c>, <c>simpleStylesAdded</c>), and the three that choose the form
/// (<c>kind</c>, <c>songMode</c>, <c>speechMode</c>), which have no field of their own.
/// </summary>
public static class VersionInputRules
{
    /// <summary>The field an edit sends the options in, and the prefix of each option's error key.</summary>
    public const string InputsField = "inputs";

    public const string KindKey = "kind";
    public const string SongModeKey = "songMode";
    public const string SpeechModeKey = "speechMode";

    /// <summary>The inventory keys of the two creation inputs kept on the Version itself.</summary>
    public const string LyricsField = "lyrics";
    public const string StylesField = "styles";

    /// <summary>The inventory's word for the form of a tab with no Simple or Advanced switch.</summary>
    public const string SingleMode = "single";

    private const string ModelKey = "model";
    private const string TitleKey = "title";
    private const string DurationModeKey = "duration_mode";
    private const string DurationSecondsKey = "duration_seconds";
    private const string CustomDuration = "custom";

    /// <summary>A field the Simple form shows only when its section is added, and the option that says it is.</summary>
    private static readonly Dictionary<string, string> AddedSections = new(StringComparer.Ordinal)
    {
        [LyricsField] = "simple_add_lyrics",
        [StylesField] = "simple_add_styles",
    };

    private static readonly JsonSerializerOptions Serializer = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static readonly IReadOnlyList<Option> Options =
    [
        .. typeof(VersionInputs).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => new Option(
                JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                property.PropertyType,
                property.GetCustomAttribute<SunoFieldAttribute>()?.Key)),
    ];

    private static readonly Dictionary<string, Option> ByName = Options.ToDictionary(static option => option.Name, StringComparer.Ordinal);

    private static readonly Dictionary<string, Option> BySunoKey = Options
        .Where(static option => option.SunoKey is not null)
        .ToDictionary(static option => option.SunoKey!, StringComparer.Ordinal);

    /// <summary>Every option's API name, in the order <see cref="VersionInputs"/> declares them.</summary>
    public static IReadOnlyList<string> Keys { get; } = [.. Options.Select(static option => option.Name)];

    /// <summary>The inventory key of the option the API calls <paramref name="key"/>; null for the three form choosers or an unknown key.</summary>
    public static string? InventoryKey(string key) => ByName.GetValueOrDefault(key)?.SunoKey;

    /// <summary>What an option's errors are keyed by, as the API spells it: <c>inputs.&lt;key&gt;</c>.</summary>
    public static string FieldName(string key) => InputsField + "." + key;

    /// <summary>The options as the API shows them: every key, in camelCase, choices as the inventory spells them.</summary>
    public static JsonObject ToJson(VersionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        return JsonSerializer.SerializeToNode(inputs, Serializer)!.AsObject();
    }

    /// <summary>The options from their JSON, every key present; throws <see cref="JsonException"/> on anything else.</summary>
    public static VersionInputs FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return json.Deserialize<VersionInputs>(Serializer) ?? throw new JsonException("The options are null.");
    }

    /// <summary>
    /// The options a new Song's first Version starts with: a Song, in Advanced mode for Song and
    /// Speech alike, every option at the inventory's default (a section the Simple form can add is not
    /// added), and Suno's title pre-filled with <paramref name="songTitle"/>, cut to the title's limit.
    /// </summary>
    public static VersionInputs Defaults(CreateFieldInventory inventory, string songTitle)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(songTitle);

        var json = new JsonObject
        {
            [KindKey] = Name(VersionKind.Song),
            [SongModeKey] = Name(CreationMode.Advanced),
            [SpeechModeKey] = Name(CreationMode.Advanced),
        };
        foreach (var option in Options.Where(static option => option.SunoKey is not null))
        {
            var field = inventory.Get(option.SunoKey!);
            json[option.Name] = field.HasDefault
                ? Node(field.Default)
                : option.Type == typeof(bool)
                    ? false
                    : throw new InvalidOperationException($"The Suno field inventory gives '{field.Key}' no default.");
        }

        json[BySunoKey[TitleKey].Name] = Cut(songTitle, inventory.Get(TitleKey).MaxLength ?? int.MaxValue);
        return FromJson(json);
    }

    /// <summary>
    /// The errors of the options an edit sends, keyed by <see cref="FieldName"/>; empty when every one
    /// is valid. A key that is not an option is refused, and so is each value outside its option's
    /// list, range, or limit, as the inventory gives them; the model must be on
    /// <paramref name="models"/>. <c>null</c> is accepted only by an option whose inventory default is null.
    /// </summary>
    public static Dictionary<string, string[]> Errors(
        CreateFieldInventory inventory,
        IReadOnlyCollection<string> models,
        IReadOnlyDictionary<string, JsonElement> sent)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(sent);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (key, value) in sent)
        {
            var problems = ByName.TryGetValue(key, out var option)
                ? OptionErrors(inventory, models, option, value)
                : [$"'{key}' is not an option of a Version."];
            if (problems.Length > 0)
            {
                errors[FieldName(key)] = problems;
            }
        }

        return errors;
    }

    /// <summary>
    /// <paramref name="current"/> with each option <paramref name="sent"/> set, the others kept; the
    /// values must have passed <see cref="Errors"/>. Text has its line endings made <c>\n</c>.
    /// </summary>
    public static VersionInputs Apply(VersionInputs current, IReadOnlyDictionary<string, JsonElement> sent)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(sent);

        if (sent.Count == 0)
        {
            return current;
        }

        var json = ToJson(current);
        foreach (var (key, value) in sent)
        {
            json[key] = value.ValueKind == JsonValueKind.String && ByName[key].Type == typeof(string)
                ? VersionRules.NormaliseInput(value.GetString()!)
                : Node(value);
        }

        return FromJson(json);
    }

    /// <summary>
    /// The options that apply to the Version's kind and mode, which is what is sent to Suno: the kind,
    /// the kind's mode (a Sound has none), and, in the inventory's order, each stored option on the
    /// kind's tab and in its mode whose condition is met. Lyrics and styles are included in Advanced
    /// mode, and in Simple mode only when their section is added; the custom duration only when the
    /// duration is custom.
    /// </summary>
    public static JsonObject Effective(CreateFieldInventory inventory, VersionInputs inputs, string lyrics, string styles)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(inputs);

        var all = ToJson(inputs);
        var effective = new JsonObject { [KindKey] = all[KindKey]!.DeepClone() };
        var (tab, mode) = inputs.Kind switch
        {
            VersionKind.Song => ("songs", Name(inputs.SongMode)),
            VersionKind.Speech => ("speech", Name(inputs.SpeechMode)),
            _ => ("sounds", SingleMode),
        };
        if (inputs.Kind == VersionKind.Song)
        {
            effective[SongModeKey] = mode;
        }
        else if (inputs.Kind == VersionKind.Speech)
        {
            effective[SpeechModeKey] = mode;
        }

        foreach (var field in inventory.Fields.Where(field => field.Tab == tab))
        {
            if (field.Key is LyricsField or StylesField)
            {
                var added = AddedSections[field.Key];
                if (field.Modes.Contains(mode, StringComparer.Ordinal)
                    || (mode == Name(CreationMode.Simple) && BySunoKey.TryGetValue(added, out var section) && (bool)all[section.Name]!))
                {
                    effective[field.Key] = field.Key == LyricsField ? lyrics : styles;
                }

                continue;
            }

            if (!BySunoKey.TryGetValue(field.Key, out var option) || !field.Modes.Contains(mode, StringComparer.Ordinal))
            {
                continue;
            }

            if (field.Key == DurationSecondsKey
                && (!BySunoKey.TryGetValue(DurationModeKey, out var duration) || (string?)all[duration.Name] != CustomDuration))
            {
                continue;
            }

            effective[option.Name] = all[option.Name]?.DeepClone();
        }

        return effective;
    }

    /// <summary>
    /// <paramref name="text"/> cut to <paramref name="maximumLength"/> UTF-16 code units, one fewer
    /// when the cut would split a surrogate pair.
    /// </summary>
    public static string Cut(string text, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= maximumLength)
        {
            return text;
        }

        var cut = text[..maximumLength];
        return cut.Length > 0 && char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }

    private static string[] OptionErrors(CreateFieldInventory inventory, IReadOnlyCollection<string> models, Option option, JsonElement value)
    {
        if (option.Type == typeof(VersionKind))
        {
            return Choice(value, Enum.GetValues<VersionKind>().Select(static kind => Name(kind)).ToList(), allowNull: false, "the kind");
        }

        if (option.Type == typeof(CreationMode))
        {
            return Choice(value, Enum.GetValues<CreationMode>().Select(static mode => Name(mode)).ToList(), allowNull: false, "the mode");
        }

        var field = inventory.Get(option.SunoKey!);
        if (option.Type == typeof(bool))
        {
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? [] : [$"{field.Label}: send true or false."];
        }

        if (option.Type == typeof(int))
        {
            var minimum = field.Min ?? int.MinValue;
            var maximum = field.Max ?? int.MaxValue;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= minimum && number <= maximum
                ? []
                : [string.Create(CultureInfo.InvariantCulture, $"{field.Label} is a whole number from {minimum} to {maximum}.")];
        }

        if (field.Key == ModelKey)
        {
            return Choice(value, models, field.DefaultsToNull, $"{field.Label} (the model list)");
        }

        if (field.Type == CreateField.ChoiceType)
        {
            return Choice(value, field.Values ?? [], field.DefaultsToNull, field.Label);
        }

        if (value.ValueKind == JsonValueKind.Null && field.DefaultsToNull)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return [$"{field.Label}: send text."];
        }

        if (TryGetString(value) is not { } text)
        {
            return [$"{field.Label} cannot contain unpaired surrogate characters."];
        }

        return VersionRules.InputErrors(text, field.MaxLength ?? int.MaxValue, field.Label);
    }

    private static string[] Choice(JsonElement value, IReadOnlyCollection<string> values, bool allowNull, string what)
    {
        if (value.ValueKind == JsonValueKind.Null && allowNull)
        {
            return [];
        }

        return value.ValueKind == JsonValueKind.String && TryGetString(value) is { } chosen && values.Contains(chosen, StringComparer.Ordinal)
            ? []
            : [$"Choose {what}: {string.Join(", ", values)}{(allowNull ? ", or null for none" : string.Empty)}."];
    }

    /// <summary>A JSON string's text, or null when it escapes half a surrogate pair, which .NET cannot read as a string.</summary>
    private static string? TryGetString(JsonElement element)
    {
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonNode? Node(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(element.GetRawText());

    private static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>One option: its API name, its CLR type, and its inventory key (null for the three form choosers).</summary>
    private sealed record Option(string Name, Type Type, string? SunoKey);
}
