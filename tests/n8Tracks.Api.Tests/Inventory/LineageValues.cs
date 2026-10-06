using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Inventory;

/// <summary>
/// Valid lineages (#122) for a Version, and changes of each part, worked out from the Version's
/// <c>inputs</c> as they are, so a test can change every source and file input without the rules
/// refusing it (an image or video only on a Simple-mode Song; an audio file only without an audio
/// action). Every change names a new Suno ID or text, so it is never the value held.
/// </summary>
internal static class LineageValues
{
    /// <summary>A lineage holding every part: a Sample source, an Inspiration playlist, a Voice, and an image.</summary>
    public static VersionLineage Held { get; } = new(
        [new VersionSource(SystemRelationshipTypes.SampleThisSong.Id, SunoActions.Sample, VersionSourceTarget.OfExternal("held-clip"))],
        [],
        new InspirationPlaylist("held-playlist", "Held", ["held-1", "held-2"]),
        new VersionVoice("held-persona", "Held"),
        [new VersionFileInput(VersionFileInputKind.Image, "Held image")]);

    /// <summary>Each part of a lineage changed alone, by name.</summary>
    public static IReadOnlyDictionary<string, Func<VersionLineage, VersionLineage>> EachPartChanged { get; } =
        new Dictionary<string, Func<VersionLineage, VersionLineage>>(StringComparer.Ordinal)
        {
            ["audio sources"] = static lineage => With(lineage, audio: [new VersionSource(SystemRelationshipTypes.SampleThisSong.Id, SunoActions.Sample, VersionSourceTarget.OfExternal(NewId()))]),
            ["audio source order"] = static lineage => With(
                lineage,
                audio:
                [
                    new VersionSource(SystemRelationshipTypes.Mashup.Id, SunoActions.Mashup, VersionSourceTarget.OfExternal("mashup-b")),
                    new VersionSource(SystemRelationshipTypes.Mashup.Id, SunoActions.Mashup, VersionSourceTarget.OfExternal("mashup-a")),
                ]),
            ["continue-at"] = static lineage => With(
                lineage,
                audio: [new VersionSource(SystemRelationshipTypes.Extend.Id, SunoActions.Extend, VersionSourceTarget.OfExternal("held-clip"), 12.5m)]),
            ["inspiration sources"] = static lineage => new VersionLineage(
                lineage.AudioSources,
                [new VersionSource(SystemRelationshipTypes.UseAsInspiration.Id, SunoActions.Inspiration, VersionSourceTarget.OfExternal(NewId()))],
                null,
                lineage.Voice,
                lineage.FileInputs),
            ["playlist"] = static lineage => new VersionLineage(lineage.AudioSources, lineage.InspirationSources, new InspirationPlaylist(NewId(), "Changed", []), lineage.Voice, lineage.FileInputs),
            ["voice"] = static lineage => new VersionLineage(lineage.AudioSources, lineage.InspirationSources, lineage.Playlist, new VersionVoice(NewId(), "Changed"), lineage.FileInputs),
            ["file inputs"] = static lineage => new VersionLineage(
                lineage.AudioSources,
                lineage.InspirationSources,
                lineage.Playlist,
                lineage.Voice,
                [new VersionFileInput(VersionFileInputKind.Video, "Changed " + NewId())]),
        };

    /// <summary>
    /// The body of <c>inputs</c> that gives a new Version every part of a lineage (in Simple mode, so it
    /// may hold an image and a video), each naming <paramref name="tag"/>.
    /// </summary>
    public static string InitialInputsJson(string tag) =>
        new JsonObject
        {
            ["songMode"] = "simple",
            [VersionLineageInputs.SourcesKey] = Sources(SystemRelationshipTypes.SampleThisSong.Id, "held-" + tag),
            [VersionLineageInputs.InspirationKey] = Playlist("held-playlist-" + tag),
            [VersionLineageInputs.VoiceKey] = Voice("held-persona-" + tag),
            [VersionLineageInputs.FileInputsKey] = Files(("image", "Held image " + tag), ("video", "Held video " + tag)),
        }.ToJsonString();

    /// <summary>
    /// The lineage keys to send to change the part <paramref name="key"/> of a Version whose
    /// <c>inputs</c> are <paramref name="inputs"/>: usually that key alone, but an audio file on a
    /// Version that cannot take an image clears its audio sources with it (Suno has one Audio slot).
    /// </summary>
    public static JsonObject Changed(string key, JsonElement inputs)
    {
        var simpleSong = IsSimpleSong(inputs.GetProperty(VersionInputRules.KindKey).GetString(), inputs.GetProperty(VersionInputRules.SongModeKey).GetString());
        return key switch
        {
            VersionLineageInputs.SourcesKey => new JsonObject { [key] = Sources(SystemRelationshipTypes.SampleThisSong.Id, NewId()) },
            VersionLineageInputs.InspirationKey => new JsonObject { [key] = Playlist(NewId()) },
            VersionLineageInputs.VoiceKey => new JsonObject { [key] = Voice(NewId()) },
            VersionLineageInputs.FileInputsKey => simpleSong
                ? new JsonObject { [key] = Files(("image", "Changed " + NewId()), ("video", "Changed " + NewId())) }
                : new JsonObject { [key] = Files(("audio", "Changed " + NewId())), [VersionLineageInputs.SourcesKey] = new JsonArray() },
            _ => throw new ArgumentException($"'{key}' is not a lineage key.", nameof(key)),
        };
    }

    /// <summary>
    /// Every lineage key changed, valid for a Version that will hold <paramref name="options"/> (the
    /// options an edit sends, every one of them): an image and a video only on a Simple-mode Song.
    /// </summary>
    public static JsonObject EveryPartChanged(JsonObject options)
    {
        var simpleSong = IsSimpleSong((string?)options[VersionInputRules.KindKey], (string?)options[VersionInputRules.SongModeKey]);
        return new JsonObject
        {
            [VersionLineageInputs.SourcesKey] = Sources(SystemRelationshipTypes.SampleThisSong.Id, NewId()),
            [VersionLineageInputs.InspirationKey] = Playlist(NewId()),
            [VersionLineageInputs.VoiceKey] = Voice(NewId()),
            [VersionLineageInputs.FileInputsKey] = simpleSong ? Files(("image", "Changed " + NewId())) : new JsonArray(),
        };
    }

    public static JsonArray Sources(Guid typeId, params string[] sunoIds) =>
        new([.. sunoIds.Select(id => (JsonNode?)new JsonObject { ["typeId"] = typeId.ToString(), ["external"] = new JsonObject { ["sunoId"] = id } })]);

    public static JsonObject Playlist(string id) =>
        new() { ["playlist"] = new JsonObject { ["sunoPlaylistId"] = id, ["name"] = "Playlist " + id, ["clipIds"] = new JsonArray("clip-" + id) } };

    public static JsonObject Voice(string id) => new() { ["personaId"] = id, ["name"] = "Persona " + id };

    public static JsonArray Files(params (string Kind, string Description)[] files) =>
        new([.. files.Select(static file => (JsonNode?)new JsonObject { ["kind"] = file.Kind, ["description"] = file.Description })]);

    /// <summary>A short Suno-like ID no other value holds.</summary>
    public static string NewId() => "changed-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>The <paramref name="inputs"/> of a read as an edit's options, without the lineage keys.</summary>
    public static JsonElement OptionsOf(JsonElement inputs)
    {
        var options = new JsonObject();
        foreach (var option in inputs.EnumerateObject().Where(static option => !VersionLineageInputs.IsLineageKey(option.Name)))
        {
            options[option.Name] = JsonNode.Parse(option.Value.GetRawText());
        }

        return JsonDocument.Parse(options.ToJsonString()).RootElement.Clone();
    }

    private static VersionLineage With(VersionLineage lineage, IReadOnlyList<VersionSource> audio) =>
        new(audio, lineage.InspirationSources, lineage.Playlist, lineage.Voice, lineage.FileInputs);

    private static bool IsSimpleSong(string? kind, string? songMode) => kind == "song" && songMode == "simple";
}
