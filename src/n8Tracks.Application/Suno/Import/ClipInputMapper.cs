using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// The model a clip reports and the model-list entry it is (#135).
/// </summary>
/// <param name="Reported">What Suno reported: the row badge (<c>V6-MINI</c>), else <c>major_model_version</c>, else <c>model_name</c>.</param>
/// <param name="Matched">The name of the model-list entry it matches; null when none does, and a new entry named <paramref name="Reported"/> is proposed, to be added on commit.</param>
public sealed record ClipModel(string Reported, string? Matched)
{
    /// <summary>Whether no entry matches, so committing the clip adds one.</summary>
    public bool IsNew => Matched is null;
}

/// <summary>
/// A clip's creation inputs, as an imported Version holds them (#135).
/// </summary>
/// <param name="Lyrics">The Version's lyrics, as Suno returned them.</param>
/// <param name="Styles">The Version's styles: empty, since the feed returns Suno's rewrite of them, not what was sent.</param>
/// <param name="Inputs">Its kind, mode, and every option, the ones Suno does not return at the n8Tracks default.</param>
/// <param name="Marks">Which options are not returned, out of range, or unknown choices kept raw.</param>
/// <param name="Model">The model it reports, or null when it reports none.</param>
/// <param name="Compared">What two clips' inputs are compared by: each option Suno returned, normalised.</param>
public sealed record MappedClipInputs(
    string Lyrics,
    string Styles,
    VersionInputs Inputs,
    ImportedInputMarks Marks,
    ClipModel? Model,
    IReadOnlyDictionary<string, string> Compared);

/// <summary>
/// Turns what Suno says about a clip into the creation inputs of an n8Tracks Version (#135), by the
/// import field map (<see cref="ImportFieldMap"/>): every field of the inventory's Songs tab is read
/// from its <c>paths.feed</c> by its encoding, or, when the map says Suno does not return it, left at
/// the n8Tracks default and listed as not returned (never guessed). The mapper has no code for any one
/// field beyond the encodings, except the model, which is matched against the model list.
/// <para>
/// A returned value outside n8Tracks' limits (longer text, an out-of-range number, an unknown choice)
/// is kept as Suno returned it and listed as out of range: nothing is refused or cut. An unknown choice
/// is kept in the raw values, its option at the default. The result is the same typed inputs the
/// editor writes, only without its range checks; it is pure and reads nothing but its arguments.
/// </para>
/// <para>
/// Mode (TS-003): Simple when <c>metadata.gpt_description_prompt</c> is present and
/// <c>metadata.task</c> is <c>agentic_thinking</c>; otherwise Advanced. Until the Speech and Sounds
/// story (#136), every clip maps as a Song.
/// </para>
/// </summary>
public static class ClipInputMapper
{
    /// <summary>
    /// The Songs fields this mapper leaves to other stories, though the map says where they are: the
    /// references and files (sources, Voice, Inspiration, playlist) are read by the lineage story
    /// (#137), and the workspace by the commit (#140).
    /// </summary>
    public static IReadOnlySet<string> ReadElsewhere { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "simple_add_playlist",
        "simple_add_image",
        "simple_add_video",
        "audio",
        "voice",
        "inspiration",
        "workspace",
    };

    /// <summary>
    /// The options read but never compared: Suno's title, which the user may rename in Suno after
    /// creation (a <c>changed</c> clip, not a <c>conflict</c>), and which Suno writes itself when it
    /// was left blank, so two clips of one Create request may differ there (TS-003).
    /// </summary>
    public static IReadOnlySet<string> NotCompared { get; } = new HashSet<string>(StringComparer.Ordinal) { "title" };

    /// <summary>The inventory tab this mapper reads.</summary>
    public const string SongsTab = "songs";

    private const string ModelKey = "model";
    private const string AgenticTask = "agentic_thinking";

    /// <summary>The API name of each option, by its inventory key (lyrics and styles are the Version's own).</summary>
    private static readonly Dictionary<string, string> ApiNames = BuildApiNames();

    /// <summary>The inventory keys this mapper reads: every Songs field with a place in a Version.</summary>
    public static IReadOnlySet<string> ReadKeys { get; } = new HashSet<string>(
        CreateFieldInventory.Embedded.Fields
            .Where(static field => field.Tab == SongsTab && ApiNames.ContainsKey(field.Key))
            .Select(static field => field.Key),
        StringComparer.Ordinal);

    /// <summary>Maps <paramref name="clip"/>, one clip object, by the embedded map and inventory.</summary>
    public static MappedClipInputs Map(JsonElement clip, IReadOnlyCollection<SunoModel> models) =>
        Map(clip, models, ImportFieldMap.Embedded, CreateFieldInventory.Embedded);

    /// <summary>Maps <paramref name="clip"/>, one clip object, by <paramref name="map"/> and <paramref name="inventory"/>.</summary>
    public static MappedClipInputs Map(JsonElement clip, IReadOnlyCollection<SunoModel> models, ImportFieldMap map, CreateFieldInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(inventory);

        var json = VersionInputRules.ToJson(VersionInputRules.Defaults(inventory, string.Empty));
        json[VersionInputRules.KindKey] = Camel(nameof(VersionKind.Song));
        json[VersionInputRules.SongModeKey] = Camel(IsSimple(clip) ? nameof(CreationMode.Simple) : nameof(CreationMode.Advanced));
        var lyrics = string.Empty;
        var styles = string.Empty;
        var notReturned = new List<string>();
        var outOfRange = new List<string>();
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        var compared = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VersionInputRules.KindKey] = json[VersionInputRules.KindKey]!.ToJsonString(),
            [VersionInputRules.SongModeKey] = json[VersionInputRules.SongModeKey]!.ToJsonString(),
        };
        ClipModel? model = null;

        foreach (var field in inventory.Fields.Where(static field => field.Tab == SongsTab && ApiNames.ContainsKey(field.Key)))
        {
            var name = ApiNames[field.Key];
            var entry = map.Find(field.Key) ?? throw new InvalidOperationException($"The import field map has no entry for '{field.Key}'.");
            if (entry.FeedPath is null)
            {
                if (!entry.IsNotReturned)
                {
                    throw new InvalidOperationException($"The import field map neither maps '{field.Key}' nor says Suno does not return it.");
                }

                notReturned.Add(name);
                continue;
            }

            var value = ImportFieldMap.Read(clip, entry.FeedPath)
                ?? entry.FeedFallbacks.Select(path => ImportFieldMap.Read(clip, path)).FirstOrDefault(static found => found is not null);
            Decoded decoded;
            if (field.Key == ModelKey && value is null)
            {
                // A clip with no model at all (no badge, version, or name) did not return it.
                notReturned.Add(name);
                continue;
            }

            if (field.Key == ModelKey)
            {
                (decoded, model) = DecodeModel(value!.Value, models);
            }
            else if (value is null)
            {
                decoded = Decoded.Default;
            }
            else
            {
                decoded = Decode(entry, field, value.Value);
            }

            if (decoded.Raw is not null)
            {
                raw[name] = decoded.Raw;
                outOfRange.Add(name);
                if (!NotCompared.Contains(name))
                {
                    compared[name] = "raw:" + decoded.Raw;
                }

                continue;
            }

            if (decoded.OutOfRange)
            {
                outOfRange.Add(name);
            }

            if (field.Key == VersionInputRules.LyricsField || field.Key == VersionInputRules.StylesField)
            {
                var text = decoded.IsDefault ? string.Empty : (string)decoded.Value!;
                if (field.Key == VersionInputRules.LyricsField)
                {
                    lyrics = text;
                }
                else
                {
                    styles = text;
                }

                compared[name] = Compare(JsonValue.Create(text));
                continue;
            }

            if (!decoded.IsDefault)
            {
                json[name] = decoded.Value;
            }

            if (!NotCompared.Contains(name))
            {
                compared[name] = Compare(json[name]);
            }
        }

        return new MappedClipInputs(
            lyrics,
            styles,
            VersionInputRules.FromJson(json),
            new ImportedInputMarks(notReturned, outOfRange, raw),
            model,
            compared);
    }

    /// <summary>
    /// Whether two clips have the same inputs: every option both returned is equal once text has its
    /// line endings made <c>\n</c> and trailing whitespace removed (each line's and the end's). An
    /// option either does not return takes no part, and neither does Suno's title (<see cref="NotCompared"/>).
    /// </summary>
    public static bool SameInputs(MappedClipInputs first, MappedClipInputs second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return SameOn(first.Compared, second.Compared);
    }

    /// <summary>
    /// Whether <paramref name="clip"/>'s inputs differ from a Version's (its lyrics, styles, options,
    /// and any import marks), compared as <see cref="SameInputs"/> does: on each option the clip returned
    /// and the Version does not list as not returned. What decides a <c>conflict</c> in a sync review.
    /// </summary>
    public static bool Differs(MappedClipInputs clip, string lyrics, string styles, VersionInputs inputs, ImportedInputMarks? marks)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(lyrics);
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(inputs);

        var json = VersionInputRules.ToJson(inputs);
        var version = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in clip.Compared.Keys.Where(key => marks?.NotReturned.Contains(key, StringComparer.Ordinal) != true))
        {
            version[key] = marks?.RawValues.TryGetValue(key, out var rawValue) == true ? "raw:" + rawValue
                : key == VersionInputRules.LyricsField ? Compare(JsonValue.Create(lyrics))
                : key == VersionInputRules.StylesField ? Compare(JsonValue.Create(styles))
                : Compare(json[key]);
        }

        return !SameOn(clip.Compared, version);
    }

    /// <summary>
    /// What the import field map lacks for the Songs fields of <paramref name="inventory"/>: a field with
    /// no entry, an entry with neither a feed path nor a <c>notReturned</c> note, and a mapped field that
    /// neither <paramref name="readKeys"/> (what the mapper reads) nor <see cref="ReadElsewhere"/> covers.
    /// Empty when the map is complete; the coverage test checks it.
    /// </summary>
    public static IReadOnlyList<string> CoverageGaps(ImportFieldMap map, CreateFieldInventory inventory, IReadOnlySet<string> readKeys)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(readKeys);

        var gaps = new List<string>();
        foreach (var field in inventory.Fields.Where(static field => field.Tab == SongsTab))
        {
            var entry = map.Find(field.Key);
            if (entry is null)
            {
                gaps.Add($"'{field.Key}' has no entry in the import field map.");
            }
            else if (entry.FeedPath is null && !entry.IsNotReturned)
            {
                gaps.Add($"'{field.Key}' has neither a feed path nor a notReturned entry.");
            }
            else if (entry.FeedPath is not null && !readKeys.Contains(field.Key) && !ReadElsewhere.Contains(field.Key))
            {
                gaps.Add($"'{field.Key}' is mapped but the mapper does not read it.");
            }
        }

        return gaps;
    }

    /// <summary>What the text normalisation of a comparison makes of <paramref name="text"/>.</summary>
    public static string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return string.Join('\n', lines.Select(static line => line.TrimEnd())).TrimEnd();
    }

    private static bool SameOn(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        first.Keys.Intersect(second.Keys, StringComparer.Ordinal)
            .All(key => string.Equals(first[key], second[key], StringComparison.Ordinal));

    private static bool IsSimple(JsonElement clip) =>
        ImportFieldMap.Read(clip, "metadata.gpt_description_prompt") is { ValueKind: JsonValueKind.String }
        && ImportFieldMap.Read(clip, "metadata.task") is { ValueKind: JsonValueKind.String } task
        && task.GetString() == AgenticTask;

    private static (Decoded Decoded, ClipModel? Model) DecodeModel(JsonElement value, IReadOnlyCollection<SunoModel> models)
    {
        if (value.ValueKind != JsonValueKind.String || SunoModelRules.NameErrors(value.GetString()).Length > 0)
        {
            return (Decoded.Unknown(value), null);
        }

        var reported = SunoModelRules.NormaliseName(value.GetString()!);
        var matched = SunoModelRules.Match(models, reported)?.Name;
        return (Decoded.Of(JsonValue.Create(matched ?? reported), outOfRange: false), new ClipModel(reported, matched));
    }

    private static Decoded Decode(ImportFieldEntry entry, CreateField field, JsonElement value)
    {
        switch (entry.Encoding)
        {
            case ImportFieldMap.TextEncoding when value.ValueKind == JsonValueKind.String:
                var text = value.GetString()!;
                return Decoded.Of(JsonValue.Create(text), text.Length > (field.MaxLength ?? int.MaxValue));

            case ImportFieldMap.BoolEncoding when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return Decoded.Of(JsonValue.Create(value.GetBoolean()), outOfRange: false);

            case ImportFieldMap.PercentEncoding when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var fraction):
                return Whole(fraction / (decimal)entry.Scale!.Value, field, value);

            case ImportFieldMap.SecondsEncoding or ImportFieldMap.NumberEncoding when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number):
                return Whole(number, field, value);

            case ImportFieldMap.EnumEncoding when entry.Values is not null:
                var choice = entry.Values.FirstOrDefault(pair => JsonElement.DeepEquals(pair.Value, value)).Key;
                return choice is not null && (field.Values?.Contains(choice, StringComparer.Ordinal) ?? true)
                    ? Decoded.Of(JsonValue.Create(choice), outOfRange: false)
                    : Decoded.Unknown(value);

            default:
                return Decoded.Unknown(value);
        }
    }

    /// <summary>A whole number in the option's range, a whole number outside it (kept, out of range), or anything else (kept raw).</summary>
    private static Decoded Whole(decimal number, CreateField field, JsonElement value)
    {
        if (decimal.Truncate(number) != number || number < int.MinValue || number > int.MaxValue)
        {
            return Decoded.Unknown(value);
        }

        var whole = (int)number;
        return Decoded.Of(JsonValue.Create(whole), whole < (field.Min ?? int.MinValue) || whole > (field.Max ?? int.MaxValue));
    }

    /// <summary>A value as comparisons see it: text normalised, anything else as its JSON.</summary>
    private static string Compare(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? JsonValue.Create(Normalise(text)).ToJsonString()
            : node?.ToJsonString() ?? "null";

    private static Dictionary<string, string> BuildApiNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VersionInputRules.LyricsField] = VersionInputRules.LyricsField,
            [VersionInputRules.StylesField] = VersionInputRules.StylesField,
        };
        foreach (var key in VersionInputRules.Keys)
        {
            if (VersionInputRules.InventoryKey(key) is { } sunoKey)
            {
                names[sunoKey] = key;
            }
        }

        return names;
    }

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary>What one returned value decoded to.</summary>
    /// <param name="Value">The typed value; null when it stays at the default.</param>
    /// <param name="IsDefault">Whether the option keeps its default (the value was absent).</param>
    /// <param name="OutOfRange">Whether the value is outside n8Tracks' limits, but kept.</param>
    /// <param name="Raw">An unknown value's JSON, kept as Suno returned it; the option keeps its default.</param>
    private sealed record Decoded(JsonNode? Value, bool IsDefault, bool OutOfRange, string? Raw)
    {
        public static Decoded Default { get; } = new(null, IsDefault: true, OutOfRange: false, Raw: null);

        public static Decoded Of(JsonNode? value, bool outOfRange) => new(value, IsDefault: false, outOfRange, Raw: null);

        public static Decoded Unknown(JsonElement value) => new(null, IsDefault: true, OutOfRange: true, value.GetRawText());
    }
}
