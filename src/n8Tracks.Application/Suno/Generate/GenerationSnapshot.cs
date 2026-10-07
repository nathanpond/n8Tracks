using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Suno.Generate;

/// <summary>
/// The adapter's field map (<c>docs/suno-adapter-field-map.md</c>, the extension's
/// <c>adapter/fieldMap.ts</c>): every entry Generate on Suno knows, <c>&lt;tab&gt;.&lt;mode&gt;.&lt;field&gt;</c>.
/// A test fails if this list and the document differ.
/// </summary>
public static class AdapterFieldMap
{
    /// <summary>Every entry, in the document's order.</summary>
    public static IReadOnlyList<string> Entries { get; } =
    [
        "songs.simple.model",
        "songs.simple.simple_prompt",
        "songs.simple.simple_add_lyrics",
        "songs.simple.simple_add_styles",
        "songs.simple.simple_add_playlist",
        "songs.simple.simple_add_image",
        "songs.simple.simple_add_video",
        "songs.simple.audio",
        "songs.simple.voice",
        "songs.simple.workspace",
        "songs.advanced.model",
        "songs.advanced.audio",
        "songs.advanced.voice",
        "songs.advanced.inspiration",
        "songs.advanced.lyrics",
        "songs.advanced.styles",
        "songs.advanced.exclude_styles",
        "songs.advanced.vocal_gender",
        "songs.advanced.duration_mode",
        "songs.advanced.duration_seconds",
        "songs.advanced.max_mode",
        "songs.advanced.weirdness",
        "songs.advanced.style_influence",
        "songs.advanced.variety",
        "songs.advanced.personalize",
        "songs.advanced.title",
        "speech.simple.speech_prompt",
        "speech.advanced.speech_script",
        "speech.advanced.speech_tone",
        "speech.advanced.speech_vocal_gender",
        "speech.advanced.speech_background_music",
        "speech.advanced.speech_variety",
        "sounds.single.sounds_model",
        "sounds.single.sound_description",
        "sounds.single.sound_type",
        "sounds.single.sound_bpm",
        "sounds.single.sound_key",
        "sounds.single.sound_scale",
    ];

    private static readonly HashSet<string> Known = new(Entries, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="entry"/> is on the map.</summary>
    public static bool Contains(string entry) => Known.Contains(entry);
}

/// <summary>A source the Version needs that cannot be used now, as a blocked request names it.</summary>
/// <param name="Group"><c>audio</c> or <c>inspiration</c>.</param>
/// <param name="Position">Its place in the group, from 1.</param>
/// <param name="Title">What it is called: the Generation's or Song's title, or the clip's; null when nothing is known.</param>
/// <param name="Shortcode">The Generation's or Song's shortcode, when it has one.</param>
/// <param name="Availability"><c>deleted</c>, <c>trashed</c>, or <c>missing</c>.</param>
public sealed record UnavailableSource(string Group, int Position, string? Title, string? Shortcode, string Availability);

/// <summary>
/// What a generation request carries (#144): built from the Version's <c>effectiveInputs</c>
/// (<see cref="VersionEffectiveInputs"/>), so the extension fills from exactly what the Version shows,
/// and keyed by the adapter's field map so the extension needs no knowledge of n8Tracks' model:
/// <c>{ schemaVersion: 1, kind, mode, entries: [{ key, value }], sources, fileInputs, workspace:
/// { sunoId, name, state } | null, song: { title, shortcode }, version: { shortcode }, unsupported }</c>.
/// The workspace's <c>state</c> (<c>available</c> or <c>unavailable</c>) tells the extension whether to
/// look for it in Suno or to offer the user a replacement (#145).
/// A value with no entry on the map is listed under <c>unsupported</c>, keyed as it would have been.
/// </summary>
public static class GenerationSnapshot
{
    public const int SchemaVersion = 1;

    /// <summary>The availabilities that block a request: the source can no longer be used in Suno.</summary>
    private static readonly HashSet<string> Blocking = new(StringComparer.Ordinal) { "deleted", "trashed", "missing" };

    /// <summary>The Simple form's two "add a section" options, and the text each adds.</summary>
    private static readonly Dictionary<string, string> AddedSections = new(StringComparer.Ordinal)
    {
        ["simple_add_lyrics"] = VersionInputRules.LyricsField,
        ["simple_add_styles"] = VersionInputRules.StylesField,
    };

    /// <summary>The keys of <c>effectiveInputs</c> that are not values to fill.</summary>
    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        VersionInputRules.KindKey,
        VersionInputRules.SongModeKey,
        VersionInputRules.SpeechModeKey,
        VersionLineageInputs.SourcesKey,
        VersionLineageInputs.InspirationKey,
        VersionLineageInputs.VoiceKey,
        VersionLineageInputs.FileInputsKey,
        VersionEffectiveInputs.WorkspaceKey,
    };

    /// <summary>
    /// The snapshot of <paramref name="version"/> of the Song titled <paramref name="songTitle"/>, with
    /// each source's Suno clip ID from <paramref name="sunoIds"/> (by Generation ID) or its external
    /// reference.
    /// </summary>
    public static JsonObject Build(VersionDetail version, string songTitle, long songShortcodeNumber, IReadOnlyDictionary<Guid, string> sunoIds)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(sunoIds);

        var effective = VersionEffectiveInputs.Of(version);
        var kind = (string)effective[VersionInputRules.KindKey]!;
        var mode = (string?)effective[VersionInputRules.SongModeKey] ?? (string?)effective[VersionInputRules.SpeechModeKey] ?? VersionInputRules.SingleMode;
        var tab = TabOf(version.Inputs.Kind);
        var prefix = tab + "." + mode + ".";

        var entries = new JsonArray();
        var unsupported = new JsonArray();
        void Add(string field, JsonNode? value)
        {
            var key = prefix + field;
            (AdapterFieldMap.Contains(key) ? entries : unsupported).Add(new JsonObject { ["key"] = key, ["value"] = value?.DeepClone() });
        }

        var simple = mode == "simple";
        foreach (var (name, value) in effective)
        {
            if (Structural.Contains(name))
            {
                continue;
            }

            var field = VersionInputRules.InventoryKey(name) ?? name;
            if (AddedSections.TryGetValue(field, out var text))
            {
                // The section's entry fills it from the Version's text: the text when added, null when not.
                Add(field, value is JsonValue && (bool?)value == true ? effective[text]?.DeepClone() : null);
                continue;
            }

            if (simple && AddedSections.ContainsValue(field))
            {
                // Simple mode's lyrics and styles travel in their section's entry above.
                continue;
            }

            Add(field, value);
        }

        var sources = new JsonArray();
        AddSources(sources, effective[VersionLineageInputs.SourcesKey] as JsonArray, "audio", prefix + "audio", sunoIds);
        if (effective[VersionLineageInputs.InspirationKey] is JsonObject inspiration)
        {
            if (inspiration["playlist"] is JsonObject playlist)
            {
                Add(simple ? "simple_add_playlist" : "inspiration", playlist);
            }

            AddSources(sources, inspiration[VersionLineageInputs.SourcesKey] as JsonArray, "inspiration", prefix + "inspiration", sunoIds);
        }

        if (effective[VersionLineageInputs.VoiceKey] is JsonObject voice)
        {
            Add("voice", voice);
        }

        var fileInputs = new JsonArray();
        if (effective[VersionLineageInputs.FileInputsKey] is JsonArray files)
        {
            foreach (var file in files.OfType<JsonObject>())
            {
                var fileKind = (string?)file["kind"];
                fileInputs.Add(new JsonObject
                {
                    ["key"] = prefix + (fileKind == "audio" ? "audio" : "simple_add_" + fileKind),
                    ["kind"] = fileKind,
                    ["description"] = file["description"]?.DeepClone(),
                });
            }
        }

        var workspace = effective[VersionEffectiveInputs.WorkspaceKey] is JsonObject chosen
            ? new JsonObject { ["sunoId"] = chosen["id"]?.DeepClone(), ["name"] = chosen["name"]?.DeepClone(), ["state"] = chosen["state"]?.DeepClone() }
            : null;

        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["kind"] = kind,
            ["mode"] = mode,
            ["entries"] = entries,
            ["sources"] = sources,
            ["fileInputs"] = fileInputs,
            ["workspace"] = workspace,
            ["song"] = new JsonObject { ["title"] = songTitle, ["shortcode"] = Shortcodes.ForSong(songShortcodeNumber) },
            ["version"] = new JsonObject { ["shortcode"] = version.Summary.Shortcode },
            ["unsupported"] = unsupported,
        };
    }

    /// <summary>
    /// The parts of a snapshot an edit of the Version changes, as one hash: kind, mode, entries,
    /// unsupported values, file inputs, and each source's action, target, and position. What is not
    /// the Version's own (its title, the Song's workspace, a source's availability or title) is left
    /// out, so a change to those does not make a request stale.
    /// </summary>
    public static string ContentKey(JsonObject snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var content = new JsonObject
        {
            ["kind"] = snapshot["kind"]?.DeepClone(),
            ["mode"] = snapshot["mode"]?.DeepClone(),
            ["entries"] = snapshot["entries"]?.DeepClone(),
            ["unsupported"] = snapshot["unsupported"]?.DeepClone(),
            ["fileInputs"] = snapshot["fileInputs"]?.DeepClone(),
            ["sources"] = new JsonArray([.. (snapshot["sources"] as JsonArray ?? []).OfType<JsonObject>().Select(static source => (JsonNode?)new JsonObject
            {
                ["key"] = source["key"]?.DeepClone(),
                ["group"] = source["group"]?.DeepClone(),
                ["typeId"] = source["typeId"]?.DeepClone(),
                ["sunoAction"] = source["sunoAction"]?.DeepClone(),
                ["target"] = source["target"]?.DeepClone(),
                ["continueAtSeconds"] = source["continueAtSeconds"]?.DeepClone(),
                ["secondaryIds"] = source["secondaryIds"]?.DeepClone(),
            })]),
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToJsonString())));
    }

    /// <summary>
    /// Every source of <paramref name="snapshot"/> that cannot be used now (deleted, in Suno's Trash, or
    /// no longer listed by Suno), in order; a Suno clip never imported does not block.
    /// </summary>
    public static IReadOnlyList<UnavailableSource> Unavailable(JsonObject snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return [.. (snapshot["sources"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(static source => Blocking.Contains((string?)source["availability"] ?? string.Empty))
            .Select(static source => new UnavailableSource(
                (string)source["group"]!,
                (int)source["position"]!,
                (string?)source["title"],
                (string?)source["shortcode"],
                (string)source["availability"]!))];
    }

    private static string TabOf(VersionKind kind) => kind switch
    {
        VersionKind.Song => "songs",
        VersionKind.Speech => "speech",
        _ => "sounds",
    };

    /// <summary>
    /// Each effective source of <paramref name="group"/> as the extension needs it: its entry, place,
    /// action, the target (<c>{ kind, id, sunoId }</c>), what to call it, and its availability now.
    /// </summary>
    private static void AddSources(JsonArray into, JsonArray? sources, string group, string key, IReadOnlyDictionary<Guid, string> sunoIds)
    {
        if (sources is null)
        {
            return;
        }

        var position = 0;
        foreach (var source in sources.OfType<JsonObject>())
        {
            position++;
            JsonObject target;
            string? title;
            string? shortcode;
            if (source["generation"] is JsonObject generation)
            {
                var id = (string)generation["id"]!;
                target = new JsonObject
                {
                    ["kind"] = "generation",
                    ["id"] = id,
                    ["sunoId"] = sunoIds.TryGetValue(Guid.Parse(id, CultureInfo.InvariantCulture), out var sunoId) ? sunoId : null,
                };
                title = (string?)generation["title"] ?? (string?)generation["songTitle"];
                shortcode = (string?)generation["shortcode"];
            }
            else if (source["song"] is JsonObject song)
            {
                target = new JsonObject { ["kind"] = "song", ["id"] = song["id"]?.DeepClone(), ["sunoId"] = null };
                title = (string?)song["title"];
                shortcode = (string?)song["shortcode"];
            }
            else
            {
                var external = source["external"] as JsonObject;
                target = new JsonObject { ["kind"] = "external", ["id"] = null, ["sunoId"] = external?["sunoId"]?.DeepClone() };
                title = (string?)external?["title"];
                shortcode = null;
            }

            into.Add(new JsonObject
            {
                ["key"] = key,
                ["group"] = group,
                ["position"] = position,
                ["typeId"] = source["typeId"]?.DeepClone(),
                ["sunoAction"] = source["sunoAction"]?.DeepClone(),
                ["target"] = target,
                ["title"] = title,
                ["shortcode"] = shortcode,
                ["availability"] = source["availability"]?.DeepClone(),
                ["continueAtSeconds"] = source["continueAtSeconds"]?.DeepClone(),
                ["secondaryIds"] = source["secondaryIds"]?.DeepClone(),
            });
        }
    }
}
