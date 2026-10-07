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

/// <summary>What kind of Version a clip is (#136), and whether its kind markers said so.</summary>
/// <param name="Kind">Song, Speech, or Sound; Song when the markers do not tell.</param>
/// <param name="Determined">False when the markers conflict or are unrecognised, so the clip is taken as a Song and needs the user's attention.</param>
public sealed record ClipKind(VersionKind Kind, bool Determined);

/// <summary>
/// A clip's creation inputs, as an imported Version holds them (#135, #136).
/// </summary>
/// <param name="Lyrics">The Version's lyrics, as Suno returned them; empty for a Speech or a Sound.</param>
/// <param name="Styles">The Version's styles: empty, since the feed returns Suno's rewrite of them, not what was sent.</param>
/// <param name="Inputs">Its kind, mode, and every option, the ones Suno does not return at the n8Tracks default.</param>
/// <param name="Marks">Which options are not returned, out of range, or unknown choices kept raw.</param>
/// <param name="Model">The model it reports, or null when it reports none.</param>
/// <param name="Compared">What two clips' inputs are compared by: the kind, its mode, and each option of its tab Suno returned, normalised.</param>
/// <param name="KindUnknown">Whether the kind could not be determined, so the clip mapped as a Song (<see cref="ClipKind.Determined"/>).</param>
/// <param name="Lineage">
/// What the clip says it was made from (#137, <see cref="LineageReader"/>), its sources naming their clips
/// by Suno ID; null only for inputs not read from a clip, which then compare as having none.
/// </param>
public sealed record MappedClipInputs(
    string Lyrics,
    string Styles,
    VersionInputs Inputs,
    ImportedInputMarks Marks,
    ClipModel? Model,
    IReadOnlyDictionary<string, string> Compared,
    bool KindUnknown = false,
    ImportedLineage? Lineage = null);

/// <summary>
/// Turns what Suno says about a clip into the creation inputs of an n8Tracks Version (#135, #136), by the
/// import field map (<see cref="ImportFieldMap"/>): the clip's kind is told by the map's kind markers,
/// and every field of that kind's tab in the inventory is read from its <c>paths.feed</c> by its
/// encoding, or, when the map says Suno does not return it, left at the n8Tracks default and listed as
/// not returned (never guessed). Fields of the other tabs are not read; they stay in the raw clip. The
/// mapper has no code for any one field beyond the encodings, except the model, which is matched
/// against the model list.
/// <para>
/// A returned value outside n8Tracks' limits (longer text, an out-of-range number, an unknown choice)
/// is kept as Suno returned it and listed as out of range: nothing is refused or cut. An unknown choice
/// is kept in the raw values, its option at the default. The result is the same typed inputs the
/// editor writes, only without its range checks; it is pure and reads nothing but its arguments.
/// </para>
/// <para>
/// Kind (<see cref="KindOf(JsonElement, ImportFieldMap)"/>): a Speech or a Sound by its marker, a
/// Song when neither marks it, and a Song needing the user's attention when the markers conflict or
/// are unrecognised. Mode (TS-003): Simple when <c>metadata.gpt_description_prompt</c> is present and
/// <c>metadata.task</c> is <c>agentic_thinking</c>; Advanced when the description prompt is absent. A
/// Song with the prompt but another task is Advanced; a Speech is then Advanced when it has a script
/// that is not blank, else Simple. A Sound has no mode.
/// </para>
/// </summary>
public static class ClipInputMapper
{
    /// <summary>
    /// The fields this mapper leaves to other stories, all on the Songs tab, though the map says where they are: the
    /// references and files (sources, Voice, Inspiration, playlist, image, video) are read by
    /// <see cref="LineageReader"/> (#137, <see cref="LineageReader.Captures"/>), and the workspace by the commit (#140).
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

    /// <summary>The inventory tab of each kind.</summary>
    public const string SongsTab = "songs";
    public const string SpeechTab = "speech";
    public const string SoundsTab = "sounds";

    private const string AgenticTask = "agentic_thinking";
    private const string SpeechScriptKey = "speech_script";

    /// <summary>The fields naming a Suno model (a Song's and a Sound's), matched against the model list.</summary>
    private static readonly HashSet<string> ModelKeys = new(StringComparer.Ordinal) { "model", "sounds_model" };

    /// <summary>The API name of each option, by its inventory key (lyrics and styles are the Version's own).</summary>
    private static readonly Dictionary<string, string> ApiNames = BuildApiNames();

    /// <summary>The inventory keys this mapper reads: every field, on every tab, with a place in a Version.</summary>
    public static IReadOnlySet<string> ReadKeys { get; } = new HashSet<string>(
        CreateFieldInventory.Embedded.Fields
            .Where(static field => ApiNames.ContainsKey(field.Key))
            .Select(static field => field.Key),
        StringComparer.Ordinal);

    /// <summary>The inventory tab whose fields a Version of <paramref name="kind"/> holds.</summary>
    public static string TabOf(VersionKind kind) => kind switch
    {
        VersionKind.Speech => SpeechTab,
        VersionKind.Sound => SoundsTab,
        _ => SongsTab,
    };

    /// <summary>What kind <paramref name="clip"/> is, by the embedded map's kind markers.</summary>
    public static ClipKind KindOf(JsonElement clip) => KindOf(clip, ImportFieldMap.Embedded);

    /// <summary>
    /// What kind <paramref name="clip"/> is, by <paramref name="map"/>'s kind markers: the one kind whose
    /// marker it carries, or a Song when it carries none. When more than one marker matches, or a marker
    /// holds a value of another JSON type than the map's (an <c>is_speech</c> of <c>"yes"</c>), the kind
    /// is not determined and the clip is taken as a Song.
    /// </summary>
    public static ClipKind KindOf(JsonElement clip, ImportFieldMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var marked = new List<VersionKind>();
        foreach (var marker in map.KindMarkers)
        {
            if (ImportFieldMap.Read(clip, marker.Path) is not { } value)
            {
                continue;
            }

            if (TypeOf(value) != TypeOf(marker.Value))
            {
                return new ClipKind(VersionKind.Song, Determined: false);
            }

            if (JsonElement.DeepEquals(value, marker.Value))
            {
                marked.Add(marker.Kind);
            }
        }

        return marked.Count switch
        {
            0 => new ClipKind(VersionKind.Song, Determined: true),
            1 => new ClipKind(marked[0], Determined: true),
            _ => new ClipKind(VersionKind.Song, Determined: false),
        };
    }

    /// <summary>Maps <paramref name="clip"/>, one clip object, by the embedded map and inventory.</summary>
    public static MappedClipInputs Map(JsonElement clip, IReadOnlyCollection<SunoModel> models) =>
        Map(clip, models, ImportFieldMap.Embedded, CreateFieldInventory.Embedded);

    /// <summary>Maps <paramref name="clip"/>, one clip object, by <paramref name="map"/> and <paramref name="inventory"/>.</summary>
    public static MappedClipInputs Map(JsonElement clip, IReadOnlyCollection<SunoModel> models, ImportFieldMap map, CreateFieldInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(inventory);

        var kind = KindOf(clip, map);
        var tab = TabOf(kind.Kind);
        var json = VersionInputRules.ToJson(VersionInputRules.Defaults(inventory, string.Empty));
        json[VersionInputRules.KindKey] = Camel(kind.Kind.ToString());
        var modeKey = kind.Kind switch
        {
            VersionKind.Song => VersionInputRules.SongModeKey,
            VersionKind.Speech => VersionInputRules.SpeechModeKey,
            _ => null,
        };
        if (modeKey is not null)
        {
            var mode = kind.Kind == VersionKind.Speech ? SpeechModeOf(clip, map) : IsSimple(clip) ? CreationMode.Simple : CreationMode.Advanced;
            json[modeKey] = Camel(mode.ToString());
        }

        var lyrics = string.Empty;
        var styles = string.Empty;
        var notReturned = new List<string>();
        var outOfRange = new List<string>();
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        var compared = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VersionInputRules.KindKey] = json[VersionInputRules.KindKey]!.ToJsonString(),
        };
        if (modeKey is not null)
        {
            compared[modeKey] = json[modeKey]!.ToJsonString();
        }

        ClipModel? model = null;

        foreach (var field in inventory.Fields.Where(field => field.Tab == tab && ApiNames.ContainsKey(field.Key)))
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
            if (ModelKeys.Contains(field.Key) && value is null)
            {
                // A clip with no model at all (no badge, version, or name) did not return it.
                notReturned.Add(name);
                continue;
            }

            if (ModelKeys.Contains(field.Key))
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
            compared,
            KindUnknown: !kind.Determined,
            Lineage: LineageReader.Read(clip, LineageReader.Rules, map));
    }

    /// <summary>
    /// Whether two clips have the same inputs: every option both returned is equal once text has its
    /// line endings made <c>\n</c> and trailing whitespace removed (each line's and the end's), and so
    /// are their lineages' comparison keys (#137, <see cref="LineageReader.ComparisonKeyOf"/>), so two
    /// clips with the same settings made from different sources differ. An option either does not
    /// return takes no part, and neither does Suno's title (<see cref="NotCompared"/>).
    /// </summary>
    public static bool SameInputs(MappedClipInputs first, MappedClipInputs second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return SameOn(first.Compared, second.Compared)
            && string.Equals(
                (first.Lineage ?? ImportedLineage.None).ComparisonKey,
                (second.Lineage ?? ImportedLineage.None).ComparisonKey,
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="clip"/>'s inputs differ from a Version's (its lyrics, styles, options,
    /// and any import marks), compared as <see cref="SameInputs"/> does: on each option the clip returned
    /// and the Version does not list as not returned. What decides a <c>conflict</c> in a sync review.
    /// The lineage takes no part here: a linked clip whose lineage differs from its Version's is the
    /// diff story's Conflict (#137's discretion), compared against the Version's stored sources.
    /// </summary>
    public static bool Differs(MappedClipInputs clip, string lyrics, string styles, VersionInputs inputs, ImportedInputMarks? marks) =>
        DifferingInputs(clip, lyrics, styles, inputs, marks).Count > 0;

    /// <summary>
    /// The creation inputs in which <paramref name="clip"/> differs from a Version (#141, a Conflict's
    /// diff), compared as <see cref="Differs"/> does: each with the Version's value and the clip's, as
    /// compared (JSON text; a value the Version kept raw starts <c>raw:</c>), in the clip's key order.
    /// </summary>
    public static IReadOnlyList<(string Key, string Version, string Clip)> DifferingInputs(MappedClipInputs clip, string lyrics, string styles, VersionInputs inputs, ImportedInputMarks? marks)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(lyrics);
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(inputs);

        var json = VersionInputRules.ToJson(inputs);
        var differing = new List<(string Key, string Version, string Clip)>();
        foreach (var key in clip.Compared.Keys.Where(key => marks?.NotReturned.Contains(key, StringComparer.Ordinal) != true))
        {
            var version = marks?.RawValues.TryGetValue(key, out var rawValue) == true ? "raw:" + rawValue
                : key == VersionInputRules.LyricsField ? Compare(JsonValue.Create(lyrics))
                : key == VersionInputRules.StylesField ? Compare(JsonValue.Create(styles))
                : Compare(json[key]);
            if (!string.Equals(clip.Compared[key], version, StringComparison.Ordinal))
            {
                differing.Add((key, version, clip.Compared[key]));
            }
        }

        return differing;
    }

    /// <summary>
    /// What the import field map lacks for the fields of <paramref name="inventory"/>, on every tab: a field with
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
        foreach (var field in inventory.Fields)
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

    /// <summary>
    /// A Speech's mode: Simple or Advanced by the Songs markers; with a description prompt but another
    /// task (neither marker), Advanced when its script is not blank, else Simple.
    /// </summary>
    private static CreationMode SpeechModeOf(JsonElement clip, ImportFieldMap map)
    {
        if (IsSimple(clip))
        {
            return CreationMode.Simple;
        }

        if (ImportFieldMap.Read(clip, "metadata.gpt_description_prompt") is not { ValueKind: JsonValueKind.String })
        {
            return CreationMode.Advanced;
        }

        var scriptPath = map.Find(SpeechScriptKey)?.FeedPath;
        return scriptPath is not null
            && ImportFieldMap.Read(clip, scriptPath) is { ValueKind: JsonValueKind.String } script
            && !string.IsNullOrWhiteSpace(script.GetString())
            ? CreationMode.Advanced
            : CreationMode.Simple;
    }

    /// <summary>A JSON value's type for marker checks, with <c>true</c> and <c>false</c> one type.</summary>
    private static JsonValueKind TypeOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.False ? JsonValueKind.True : value.ValueKind;

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
                var returned = value;
                if (entry.Pattern is not null)
                {
                    // Part of a text: the pattern's group is what the table is looked up by (a Sound's "Am").
                    if (value.ValueKind != JsonValueKind.String || ImportFieldMap.Capture(entry.Pattern, value.GetString()!) is not { } part)
                    {
                        return Decoded.Unknown(value);
                    }

                    returned = JsonSerializer.SerializeToElement(part);
                }

                var choice = entry.Values.FirstOrDefault(pair => JsonElement.DeepEquals(pair.Value, returned)).Key;
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
