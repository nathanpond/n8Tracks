using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Songs;

/// <summary>
/// A Version's lineage (#122) as the API spells it, beside the Suno options in <c>inputs</c>: four
/// keys, each written whole (<c>sources</c>, <c>inspiration</c>, <c>voice</c>, and
/// <c>fileInputs</c>). In an edit, a key left out is unchanged and <c>null</c> or an empty array
/// clears it. A source names its target by <c>generation</c> or <c>song</c> (an ID or shortcode, as
/// text or as the object a read answers with) or by <c>external</c> (a Suno clip's ID, with its title
/// and address when known); what a read adds (shortcodes, titles, <c>missing</c>, <c>sunoAction</c>)
/// is ignored when sent back, so a read can be sent back as it is.
/// </summary>
public static class VersionLineageInputs
{
    public const string SourcesKey = "sources";
    public const string InspirationKey = "inspiration";
    public const string VoiceKey = "voice";
    public const string FileInputsKey = "fileInputs";

    /// <summary>The four lineage keys of <c>inputs</c>, in the order a read lists them after the options.</summary>
    public static IReadOnlyList<string> Keys { get; } = [SourcesKey, InspirationKey, VoiceKey, FileInputsKey];

    /// <summary>
    /// The inventory fields (<c>docs/suno-create-field-inventory.json</c>) each lineage key stores: the
    /// declared mapping the inventory coverage test reads.
    /// </summary>
    public static IReadOnlyDictionary<string, string> InventoryFields { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["audio"] = SourcesKey,
        ["inspiration"] = InspirationKey,
        ["simple_add_playlist"] = InspirationKey,
        ["voice"] = VoiceKey,
        ["simple_add_image"] = FileInputsKey,
        ["simple_add_video"] = FileInputsKey,
    };

    /// <summary>Whether <paramref name="key"/> of <c>inputs</c> is a lineage key rather than a Suno option.</summary>
    public static bool IsLineageKey(string key) => Keys.Contains(key, StringComparer.Ordinal);

    /// <summary>The lineage as a read shows it, under its four keys of <c>inputs</c>.</summary>
    public static JsonObject ToJson(VersionLineageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var lineage = view.Lineage;
        var json = new JsonObject
        {
            [SourcesKey] = new JsonArray([.. lineage.AudioSources.Select((source, index) => (JsonNode?)SourceJson(source, view.AudioTargets.ElementAtOrDefault(index), withType: true))]),
        };
        if (lineage.Playlist is { } playlist)
        {
            json[InspirationKey] = new JsonObject { ["playlist"] = PlaylistJson(playlist) };
        }
        else if (lineage.InspirationSources.Count > 0)
        {
            json[InspirationKey] = new JsonObject
            {
                [SourcesKey] = new JsonArray([.. lineage.InspirationSources.Select((source, index) => (JsonNode?)SourceJson(source, view.InspirationTargets.ElementAtOrDefault(index), withType: false))]),
            };
        }
        else
        {
            json[InspirationKey] = null;
        }

        json[VoiceKey] = lineage.Voice is { } voice ? new JsonObject { ["personaId"] = voice.PersonaId, ["name"] = voice.Name } : null;
        json[FileInputsKey] = new JsonArray([.. lineage.FileInputs.Select(static file => (JsonNode?)new JsonObject
        {
            ["kind"] = FileKindName(file.Kind),
            ["description"] = file.Description,
        })]);
        return json;
    }

    /// <summary>
    /// What of the lineage applies to the Version's kind and mode, under the same keys, for
    /// <c>effectiveInputs</c>: a key is left out when nothing of it applies. Audio sources and the Voice
    /// apply to a Song; individual Inspiration sources to an Advanced-mode Song, a playlist to a Song in
    /// either mode; an audio file to a Song, an image or video to a Simple-mode Song. Sources of the
    /// general Remix type are never sent to Suno.
    /// </summary>
    public static JsonObject Effective(VersionLineageView view, VersionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(inputs);

        var all = ToJson(view);
        var lineage = view.Lineage;
        var effective = new JsonObject();
        if (!VersionLineageRules.AppliesToSong(inputs.Kind))
        {
            return effective;
        }

        var audio = lineage.AudioSources
            .Select((source, index) => (source, index))
            .Where(static pair => !VersionLineageRules.IsRemix(pair.source))
            .Select(pair => all[SourcesKey]![pair.index]!.DeepClone())
            .ToArray();
        if (audio.Length > 0)
        {
            effective[SourcesKey] = new JsonArray(audio);
        }

        if (lineage.Playlist is not null
            || (lineage.InspirationSources.Count > 0 && VersionLineageRules.Applies(VersionSourceGroup.Inspiration, inputs.Kind, inputs.SongMode)))
        {
            effective[InspirationKey] = all[InspirationKey]!.DeepClone();
        }

        if (lineage.Voice is not null)
        {
            effective[VoiceKey] = all[VoiceKey]!.DeepClone();
        }

        var files = lineage.FileInputs
            .Select((file, index) => (file, index))
            .Where(pair => VersionLineageRules.Applies(pair.file.Kind, inputs.Kind, inputs.SongMode))
            .Select(pair => all[FileInputsKey]![pair.index]!.DeepClone())
            .ToArray();
        if (files.Length > 0)
        {
            effective[FileInputsKey] = new JsonArray(files);
        }

        return effective;
    }

    /// <summary>
    /// The lineage keys an edit sends, read but not looked up. A value of the wrong shape is an error
    /// in <paramref name="errors"/>, keyed by the field, and also in <paramref name="rules"/> when it
    /// breaks a named rule (a second Voice, a source with more than one target).
    /// </summary>
    public static LineageEdit Parse(
        IReadOnlyDictionary<string, JsonElement> sent,
        Dictionary<string, string[]> errors,
        Dictionary<string, string[]> rules)
    {
        ArgumentNullException.ThrowIfNull(sent);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(rules);

        var reader = new Reader(errors, rules);
        var edit = new LineageEdit();
        if (sent.TryGetValue(SourcesKey, out var sources))
        {
            edit = edit with { SourcesSent = true, Sources = reader.Sources(sources, VersionLineageRules.SourcesField, withType: true) };
        }

        if (sent.TryGetValue(InspirationKey, out var inspiration))
        {
            var (individual, playlist) = reader.Inspiration(inspiration);
            edit = edit with { InspirationSent = true, InspirationSources = individual, Playlist = playlist };
        }

        if (sent.TryGetValue(VoiceKey, out var voice))
        {
            edit = edit with { VoiceSent = true, Voice = reader.Voice(voice) };
        }

        if (sent.TryGetValue(FileInputsKey, out var files))
        {
            edit = edit with { FileInputsSent = true, FileInputs = reader.FileInputs(files) };
        }

        return edit;
    }

    /// <summary>A file input kind as the API spells it.</summary>
    public static string FileKindName(VersionFileInputKind kind) => kind switch
    {
        VersionFileInputKind.Audio => "audio",
        VersionFileInputKind.Image => "image",
        _ => "video",
    };

    private static JsonObject SourceJson(VersionSource source, SourceTargetView? target, bool withType)
    {
        var json = new JsonObject();
        if (withType)
        {
            json["typeId"] = source.TypeId.ToString();
            json["sunoAction"] = source.SunoAction;
        }

        if (source.Target.GenerationId is { } generationId)
        {
            json["generation"] = new JsonObject
            {
                ["id"] = generationId.ToString(),
                ["shortcode"] = target?.GenerationShortcode,
                ["songId"] = target?.GenerationSongId?.ToString(),
                ["songShortcode"] = target?.SongShortcode,
                ["songTitle"] = target?.SongTitle,
                ["missing"] = target?.Missing ?? false,
            };
        }
        else if (source.Target.SongId is { } songId)
        {
            json["song"] = new JsonObject
            {
                ["id"] = songId.ToString(),
                ["shortcode"] = target?.SongShortcode,
                ["title"] = target?.SongTitle,
                ["missing"] = target?.Missing ?? false,
            };
        }
        else
        {
            var external = target?.External;
            json["external"] = new JsonObject
            {
                ["sunoId"] = source.Target.ExternalSunoId,
                ["title"] = external?.Title,
                ["address"] = external?.Address,
                ["label"] = external?.Label,
            };
        }

        if (withType)
        {
            json["continueAtSeconds"] = source.ContinueAtSeconds is { } seconds ? JsonValue.Create(seconds) : null;
            json["secondaryIds"] = source.SecondaryIds is { } ids ? JsonNode.Parse(ids) : null;
        }

        return json;
    }

    private static JsonObject PlaylistJson(InspirationPlaylist playlist) => new()
    {
        ["sunoPlaylistId"] = playlist.SunoPlaylistId,
        ["name"] = playlist.Name,
        ["clipIds"] = new JsonArray([.. playlist.ClipIds.Select(static id => (JsonNode?)id)]),
    };

    /// <summary>Reads the lineage keys of an edit, collecting what is wrong.</summary>
    private sealed class Reader(Dictionary<string, string[]> errors, Dictionary<string, string[]> rules)
    {
        public IReadOnlyList<SourceRequest> Sources(JsonElement value, string field, bool withType)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                Error(field, "Send an array of sources, or null to clear them.");
                return [];
            }

            var sources = new List<SourceRequest>();
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var itemField = string.Create(CultureInfo.InvariantCulture, $"{field}[{index++}]");
                if (Source(item, itemField, withType) is { } source)
                {
                    sources.Add(source);
                }
            }

            return sources;
        }

        public (IReadOnlyList<SourceRequest> Sources, InspirationPlaylist? Playlist) Inspiration(JsonElement value)
        {
            const string field = VersionLineageRules.InspirationField;
            if (value.ValueKind == JsonValueKind.Null)
            {
                return ([], null);
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                Error(field, "Send an object with sources (up to four) or a playlist, or null to clear it.");
                return ([], null);
            }

            var sources = value.TryGetProperty(SourcesKey, out var individual) ? Sources(individual, field + ".sources", withType: false) : [];
            InspirationPlaylist? playlist = null;
            if (value.TryGetProperty("playlist", out var sentPlaylist) && sentPlaylist.ValueKind != JsonValueKind.Null)
            {
                playlist = Playlist(sentPlaylist, field + ".playlist");
            }

            return (sources, playlist);
        }

        public VersionVoice? Voice(JsonElement value)
        {
            const string field = VersionLineageRules.VoiceField;
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                    return null;
                case JsonValueKind.Array when value.GetArrayLength() > 1:
                    Rule(field, VersionLineageRules.OneVoice, "A Version has one Voice.");
                    return null;
                case JsonValueKind.Object:
                    var personaId = Text(value, "personaId", field + ".personaId", required: true);
                    var name = Text(value, "name", field + ".name", required: false) ?? string.Empty;
                    return personaId is null ? null : new VersionVoice(personaId, name.Trim());
                default:
                    Error(field, "Send an object with the persona's ID and name, or null to clear it.");
                    return null;
            }
        }

        public IReadOnlyList<VersionFileInput> FileInputs(JsonElement value)
        {
            const string field = VersionLineageRules.FileInputsField;
            if (value.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                Error(field, "Send an array of file inputs, or null to clear them.");
                return [];
            }

            var files = new List<VersionFileInput>();
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var itemField = string.Create(CultureInfo.InvariantCulture, $"{field}[{index++}]");
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Error(itemField, "Send an object with a kind and a description.");
                    continue;
                }

                VersionFileInputKind? kind = Text(item, "kind", itemField + ".kind", required: true) switch
                {
                    "audio" => VersionFileInputKind.Audio,
                    "image" => VersionFileInputKind.Image,
                    "video" => VersionFileInputKind.Video,
                    null => null,
                    _ => null,
                };
                if (kind is null)
                {
                    errors.TryAdd(itemField + ".kind", ["Choose the kind: audio, image, or video."]);
                }

                var description = Text(item, "description", itemField + ".description", required: true);
                if (kind is { } fileKind && description is not null)
                {
                    files.Add(new VersionFileInput(fileKind, VersionRules.NormaliseInput(description).Trim()));
                }
            }

            // One per kind, so the order they are sent in is not a change.
            return [.. files.OrderBy(static file => file.Kind)];
        }

        private SourceRequest? Source(JsonElement item, string field, bool withType)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                Error(field, "Send an object for each source.");
                return null;
            }

            Guid? typeId = null;
            if (withType)
            {
                if (!item.TryGetProperty("typeId", out var type) || type.ValueKind != JsonValueKind.String || !Guid.TryParse(type.GetString(), out var parsed))
                {
                    Error(field + ".typeId", "Choose the source's relationship type by its ID.");
                    return null;
                }

                typeId = parsed;
            }

            var targets = new[] { "generation", "song", "external" }
                .Where(name => item.TryGetProperty(name, out var target) && target.ValueKind != JsonValueKind.Null)
                .ToList();
            if (targets.Count != 1)
            {
                Rule(field, VersionLineageRules.OneTarget, "A source points at exactly one Generation, Song, or Suno clip.");
                return null;
            }

            var target = item.GetProperty(targets[0]);
            string? generation = null;
            string? song = null;
            ExternalSunoReference? external = null;
            switch (targets[0])
            {
                case "generation":
                    generation = Reference(target, field + ".generation");
                    if (generation is null)
                    {
                        return null;
                    }

                    break;
                case "song":
                    song = Reference(target, field + ".song");
                    if (song is null)
                    {
                        return null;
                    }

                    break;
                default:
                    external = External(target, field + ".external");
                    if (external is null)
                    {
                        return null;
                    }

                    break;
            }

            decimal? continueAt = null;
            if (item.TryGetProperty("continueAtSeconds", out var position) && position.ValueKind != JsonValueKind.Null)
            {
                if (position.ValueKind != JsonValueKind.Number || !position.TryGetDecimal(out var seconds))
                {
                    Error(field + ".continueAtSeconds", "Send the position in seconds, or null.");
                    return null;
                }

                continueAt = seconds;
            }

            string? secondaryIds = null;
            if (item.TryGetProperty("secondaryIds", out var ids) && ids.ValueKind != JsonValueKind.Null)
            {
                if (ids.ValueKind != JsonValueKind.Object || ids.EnumerateObject().Any(static property => property.Value.ValueKind != JsonValueKind.String))
                {
                    Rule(field + ".secondaryIds", VersionLineageRules.SecondaryIdsInvalid, "Secondary identifiers are an object of named Suno IDs.");
                    return null;
                }

                // Keys in order, so the same identifiers sent in another order are no change.
                var ordered = new JsonObject();
                foreach (var property in ids.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    ordered[property.Name] = property.Value.GetString();
                }

                secondaryIds = ordered.ToJsonString();
            }

            return new SourceRequest(typeId, generation, song, external, continueAt, secondaryIds, field);
        }

        private InspirationPlaylist? Playlist(JsonElement value, string field)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                Error(field, "Send the playlist's Suno ID, name, and clip IDs.");
                return null;
            }

            var id = Text(value, "sunoPlaylistId", field + ".sunoPlaylistId", required: true);
            var name = Text(value, "name", field + ".name", required: false) ?? string.Empty;
            var clipIds = new List<string>();
            if (value.TryGetProperty("clipIds", out var clips) && clips.ValueKind != JsonValueKind.Null)
            {
                if (clips.ValueKind != JsonValueKind.Array || clips.EnumerateArray().Any(static clip => clip.ValueKind != JsonValueKind.String))
                {
                    Error(field + ".clipIds", "Send the playlist's clip IDs as an array of text.");
                    return null;
                }

                clipIds.AddRange(clips.EnumerateArray().Select(static clip => clip.GetString()!));
            }

            return id is null ? null : new InspirationPlaylist(id, name.Trim(), clipIds);
        }

        private ExternalSunoReference? External(JsonElement value, string field)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                Error(field, "Send the clip's Suno ID, and its title and address when known.");
                return null;
            }

            var sunoId = Text(value, "sunoId", field + ".sunoId", required: true);
            var title = Text(value, "title", field + ".title", required: false);
            var address = Text(value, "address", field + ".address", required: false);
            if (sunoId is null)
            {
                return null;
            }

            if (!ExternalSunoReferenceRules.IsDescription(title?.Trim(), ExternalSunoReferenceRules.TitleMaximumLength))
            {
                Error(field + ".title", string.Create(CultureInfo.InvariantCulture, $"A title is one line of up to {ExternalSunoReferenceRules.TitleMaximumLength} characters."));
                return null;
            }

            if (!ExternalSunoReferenceRules.IsAddress(address))
            {
                Error(field + ".address", "An address is an http or https address.");
                return null;
            }

            return new ExternalSunoReference(Guid.Empty, sunoId, ExternalSunoKind.Clip, string.IsNullOrWhiteSpace(title) ? null : title.Trim(), address, Label: null);
        }

        /// <summary>A Generation or Song as sent: its ID or shortcode as text, or an object read back with its <c>id</c>.</summary>
        private string? Reference(JsonElement value, string field)
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            if (value.ValueKind == JsonValueKind.Object
                && (value.TryGetProperty("id", out var id) || value.TryGetProperty("shortcode", out id))
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } reference)
            {
                return reference;
            }

            Error(field, "Name it by its ID or shortcode.");
            return null;
        }

        private string? Text(JsonElement value, string property, string field, bool required)
        {
            if (!value.TryGetProperty(property, out var text) || text.ValueKind == JsonValueKind.Null)
            {
                if (required)
                {
                    Error(field, "This is required.");
                }

                return null;
            }

            try
            {
                if (text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }
            }
            catch (InvalidOperationException)
            {
                // Half a surrogate pair: refused below like any other wrong value.
            }

            Error(field, "Send text.");
            return null;
        }

        private void Error(string field, string message) => errors.TryAdd(field, [message]);

        private void Rule(string field, string rule, string message)
        {
            Error(field, message);
            rules.TryAdd(field, [rule]);
        }
    }
}

/// <summary>
/// The lineage keys an edit sent, read but not looked up: each part with whether it was sent (a part
/// not sent is kept as it is).
/// </summary>
public sealed record LineageEdit
{
    public bool SourcesSent { get; init; }

    public IReadOnlyList<SourceRequest> Sources { get; init; } = [];

    public bool InspirationSent { get; init; }

    public IReadOnlyList<SourceRequest> InspirationSources { get; init; } = [];

    public InspirationPlaylist? Playlist { get; init; }

    public bool VoiceSent { get; init; }

    public VersionVoice? Voice { get; init; }

    public bool FileInputsSent { get; init; }

    public IReadOnlyList<VersionFileInput> FileInputs { get; init; } = [];

    /// <summary>Whether any lineage key was sent.</summary>
    public bool IsSent => SourcesSent || InspirationSent || VoiceSent || FileInputsSent;
}

/// <summary>
/// One source as an edit sent it: its type (an audio source's; an Inspiration source's is implied),
/// its target as a reference to look up (a Generation or Song) or a Suno clip, Extend's position,
/// and its secondary identifiers (a JSON object, keys in order). <paramref name="Field"/> is where
/// errors about it are reported.
/// </summary>
public sealed record SourceRequest(
    Guid? TypeId,
    string? Generation,
    string? Song,
    ExternalSunoReference? External,
    decimal? ContinueAtSeconds,
    string? SecondaryIds,
    string Field);
