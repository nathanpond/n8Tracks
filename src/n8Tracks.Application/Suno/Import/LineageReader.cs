using System.Globalization;
using System.Text;
using System.Text.Json;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// What a clip says it was made from (#137), as an imported Version holds it: its lineage with every
/// source naming its Suno clip by ID, and the "Not imported" external reference each of those IDs
/// becomes unless the clip is already a Generation (<see cref="ExternalReferenceResolver.LinkAsync"/>).
/// </summary>
/// <param name="Lineage">The sources, Inspiration, Voice, and file inputs; every source's target is an external Suno ID.</param>
/// <param name="References">One external reference for each source Suno ID, in the order first named.</param>
/// <param name="Task">The clip's <c>metadata.task</c>; null when it has none (a Reuse Prompt clip).</param>
/// <param name="Rule">The rule that read its audio sources (<see cref="LineageRule.Action"/>), <see cref="LineageReader.RemixRule"/>, or null for none.</param>
public sealed record ImportedLineage(VersionLineage Lineage, IReadOnlyList<ExternalSunoReference> References, string? Task, string? Rule)
{
    /// <summary>A clip that names no source.</summary>
    public static ImportedLineage None { get; } = new(VersionLineage.None, [], null, null);

    public IReadOnlyList<ExternalSunoReference> References { get; } = References ?? throw new ArgumentNullException(nameof(References));

    /// <summary>What two clips' lineages are compared by (<see cref="LineageReader.ComparisonKeyOf"/>).</summary>
    public string ComparisonKey => LineageReader.ComparisonKeyOf(Lineage);

    /// <summary>Whether it holds <paramref name="part"/>.</summary>
    public bool Has(LineagePart part) => part switch
    {
        LineagePart.AudioSources => Lineage.AudioSources.Count > 0,
        LineagePart.Inspiration => Lineage.InspirationSources.Count > 0 || Lineage.Playlist is not null,
        LineagePart.Playlist => Lineage.Playlist is not null,
        LineagePart.Voice => Lineage.Voice is not null,
        LineagePart.AudioFile => Lineage.FileInputs.Any(static file => file.Kind == VersionFileInputKind.Audio),
        LineagePart.ImageFile => Lineage.FileInputs.Any(static file => file.Kind == VersionFileInputKind.Image),
        LineagePart.VideoFile => Lineage.FileInputs.Any(static file => file.Kind == VersionFileInputKind.Video),
        _ => false,
    };
}

/// <summary>The parts of an imported lineage a capture fills (<see cref="LineageReader.Captures"/>).</summary>
public enum LineagePart
{
    AudioSources,
    Inspiration,
    Playlist,
    Voice,
    AudioFile,
    ImageFile,
    VideoFile,
}

/// <summary>
/// How one <c>metadata.task</c> of the TS-002 lineage list is read: the action it stands for and its
/// audio sources, each naming its clip by Suno ID; null <paramref name="AudioSources"/> for a task with
/// no audio action (Inspiration, Voice, a plain generation), whose parts are read from their own fields.
/// </summary>
/// <param name="Task">The <c>metadata.task</c> it reads.</param>
/// <param name="Action">The TS-002 action's name, as the lineage list names it.</param>
/// <param name="AudioSources">The audio sources of a clip with this task, in order; empty when the clip names none.</param>
public sealed record LineageRule(string Task, string Action, Func<JsonElement, IReadOnlyList<VersionSource>>? AudioSources);

/// <summary>
/// Reads a clip's lineage (#137) as <c>docs/spikes/TS-002.md</c> describes it, keyed by
/// <c>metadata.task</c> (<see cref="Rules"/>): Cover's <c>cover_clip_id</c> (else <c>edited_clip_id</c>),
/// Mashup's two <c>mashup_clip_ids</c> in order, Sample's first <c>clip_roots</c> clip, and Extend's
/// <c>edited_clip_id</c> with its <c>history</c> entry's <c>continue_at</c>. Only the direct parent is
/// a source; every other identifier the table lists for the action (Cover's <c>edited_clip_id</c> when it
/// differs, Extend's earlier <c>history</c> entries in order, the <c>clip_roots</c> IDs) is kept on it
/// as a secondary identifier. A clip with a task this list does not know takes its sources from
/// <c>clip_roots</c> under the general Remix type; one with no task (Reuse Prompt, which Suno does not
/// record) or a plain generation's task has none.
/// <para>
/// Inspiration and Voice are read from their own fields whatever the task: a playlist
/// (<c>playlist_id</c> with <c>playlist_clip_ids</c> as its snapshot), or individual sources when Suno
/// reports no playlist; and the Voice (<c>persona_id</c>, named by the top-level <c>persona</c>). A file
/// input is a note "Imported from Suno" where the import field map says where Suno reports it.
/// </para>
/// <para>
/// Every source names its clip by Suno ID, with a "Not imported" external reference for it (its title
/// from <c>clip_roots</c> when there, else "Suno clip" and the ID's first eight characters). Pure: it
/// reads nothing but its arguments, and importing the child never imports the parent.
/// </para>
/// </summary>
public static class LineageReader
{
    /// <summary>The description of a file input read from a clip.</summary>
    public const string FileInputDescription = "Imported from Suno";

    /// <summary>The <see cref="ImportedLineage.Rule"/> of sources read from <c>clip_roots</c> for a task the rules do not know.</summary>
    public const string RemixRule = "Remix";

    /// <summary>A plain generation's task (TS-002): no sources.</summary>
    public const string PlainTask = "agentic_thinking";

    /// <summary>What Suno's Create response puts in <c>playlist_id</c> before the feed has the playlist's real ID (TS-002).</summary>
    private const string CreateInspirationPlaceholder = "inspiration";

    /// <summary>The import field map keys of the file inputs, by kind.</summary>
    private static readonly (VersionFileInputKind Kind, string Key, LineagePart Part)[] FileKeys =
    [
        (VersionFileInputKind.Image, "simple_add_image", LineagePart.ImageFile),
        (VersionFileInputKind.Video, "simple_add_video", LineagePart.VideoFile),
    ];

    private const string AudioKey = "audio";

    /// <summary>The rules, by <c>metadata.task</c>: the six TS-002 actions that leave lineage, and the plain generation.</summary>
    public static IReadOnlyDictionary<string, LineageRule> Rules { get; } = new Dictionary<string, LineageRule>(StringComparer.Ordinal)
    {
        ["cover"] = new("cover", "Cover", Cover),
        ["mashup_condition"] = new("mashup_condition", "Mashup", Mashup),
        ["chop_sample_condition"] = new("chop_sample_condition", "Sample this song", Sample),
        ["extend"] = new("extend", "Extend", Extend),
        ["playlist_condition"] = new("playlist_condition", "Use as Inspiration", null),
        ["vox_playlist_condition"] = new("vox_playlist_condition", "Voice", null),
        [PlainTask] = new(PlainTask, "Plain generation", null),
    };

    /// <summary>
    /// The inventory's reference and file keys this reader captures, and the part each fills, under
    /// <paramref name="map"/>: the audio sources, Inspiration, the playlist, and the Voice always; an
    /// image or video only when the map says where Suno reports it (otherwise the map marks it not returned).
    /// </summary>
    public static IReadOnlyDictionary<string, LineagePart> Captures(ImportFieldMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var captures = new Dictionary<string, LineagePart>(StringComparer.Ordinal)
        {
            [AudioKey] = LineagePart.AudioSources,
            ["inspiration"] = LineagePart.Inspiration,
            ["simple_add_playlist"] = LineagePart.Playlist,
            ["voice"] = LineagePart.Voice,
        };
        foreach (var (_, key, part) in FileKeys)
        {
            if (map.Find(key)?.FeedPath is not null)
            {
                captures[key] = part;
            }
        }

        return captures;
    }

    /// <summary>Reads <paramref name="clip"/>'s lineage by the embedded rules and import field map.</summary>
    public static ImportedLineage Read(JsonElement clip) => Read(clip, Rules, ImportFieldMap.Embedded);

    /// <summary>Reads <paramref name="clip"/>'s lineage by <paramref name="rules"/> and <paramref name="map"/>.</summary>
    public static ImportedLineage Read(JsonElement clip, IReadOnlyDictionary<string, LineageRule> rules, ImportFieldMap map)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(map);

        if (clip.ValueKind != JsonValueKind.Object)
        {
            return ImportedLineage.None;
        }

        var task = ImportFieldMap.Read(clip, "metadata.task") is { ValueKind: JsonValueKind.String } taskValue ? taskValue.GetString() : null;
        IReadOnlyList<VersionSource> audio = [];
        string? rule = null;
        if (task is not null && rules.TryGetValue(task, out var known))
        {
            if (known.AudioSources is not null)
            {
                audio = known.AudioSources(clip);
                rule = known.Action;
            }
        }
        else if (task is not null)
        {
            audio = Remix(clip);
            rule = audio.Count > 0 ? RemixRule : null;
        }

        var (inspiration, playlist) = Inspiration(clip);
        var lineage = new VersionLineage(audio, inspiration, playlist, Voice(clip), FileInputs(clip, map));
        IReadOnlyList<ExternalSunoReference> references =
        [
            .. audio.Concat(inspiration)
                .Select(static source => source.Target.ExternalSunoId)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(id => Reference(clip, id)),
        ];
        return new ImportedLineage(lineage, references, task, rule);
    }

    /// <summary>
    /// What two clips' lineages are compared by: each source's type, the Suno IDs of its targets in
    /// order, and Extend's position; the playlist's ID (not its snapshot); and the persona's ID. A source
    /// read from a clip always names its Suno ID, so the key is the same whether or not it has resolved.
    /// </summary>
    public static string ComparisonKeyOf(VersionLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);

        var key = new StringBuilder();
        void Sources(string group, IReadOnlyList<VersionSource> sources)
        {
            key.Append(group).Append('[');
            foreach (var source in sources)
            {
                key.Append(source.TypeId.ToString("N", CultureInfo.InvariantCulture))
                    .Append(':')
                    .Append(source.Target.ExternalSunoId ?? source.Target.GenerationId?.ToString() ?? source.Target.SongId?.ToString())
                    .Append('@')
                    .Append(source.ContinueAtSeconds?.ToString(CultureInfo.InvariantCulture) ?? "-")
                    .Append(';');
            }

            key.Append(']');
        }

        Sources("audio", lineage.AudioSources);
        Sources("inspiration", lineage.InspirationSources);
        key.Append("playlist[").Append(lineage.Playlist?.SunoPlaylistId).Append(']');
        key.Append("voice[").Append(lineage.Voice?.PersonaId).Append(']');
        return key.ToString();
    }

    private static List<VersionSource> Cover(JsonElement clip)
    {
        var cover = Id(clip, "metadata.cover_clip_id");
        var edited = Id(clip, "metadata.edited_clip_id");
        if ((cover ?? edited) is not { } direct)
        {
            return Remix(clip);
        }

        var secondary = new List<(string, string)>();
        if (edited is not null && edited != direct)
        {
            secondary.Add(("edited_clip_id", edited));
        }

        return [Source(SystemRelationshipTypes.Cover, direct, null, WithRoots(clip, secondary, [direct]))];
    }

    private static List<VersionSource> Mashup(JsonElement clip)
    {
        var ids = Ids(clip, "metadata.mashup_clip_ids");
        if (ids.Count == 0)
        {
            return Remix(clip);
        }

        // Suno takes exactly two; any further ID is kept on the second source rather than lost.
        var direct = ids.Take(2).ToList();
        var further = ids.Skip(2).Select(static (id, index) => ($"mashup_clip_ids[{index + 2}]", id)).ToList();
        return [.. direct.Select((id, index) => Source(
            SystemRelationshipTypes.Mashup,
            id,
            null,
            WithRoots(clip, index == direct.Count - 1 ? further : [], direct)))];
    }

    private static List<VersionSource> Sample(JsonElement clip)
    {
        var roots = Roots(clip);
        if (roots.Count == 0)
        {
            return [];
        }

        var direct = roots[0].Id;
        return [Source(SystemRelationshipTypes.SampleThisSong, direct, null, WithRoots(clip, [], [direct]))];
    }

    private static List<VersionSource> Extend(JsonElement clip)
    {
        var history = new List<(string Id, decimal? ContinueAt)>();
        if (ImportFieldMap.Read(clip, "metadata.history") is { ValueKind: JsonValueKind.Array } entries)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && Id(entry, "id") is { } id)
                {
                    history.Add((id, Seconds(entry, "continue_at")));
                }
            }
        }

        var direct = Id(clip, "metadata.edited_clip_id")
            ?? (history.Count > 0 ? history[^1].Id : null)
            ?? (Roots(clip) is { Count: > 0 } roots ? roots[0].Id : null);
        if (direct is null)
        {
            return [];
        }

        var continueAt = history.LastOrDefault(entry => entry.Id == direct).ContinueAt ?? Seconds(clip, "metadata.continue_at");
        var earlier = history
            .Select(static (entry, index) => (Key: $"history[{index}]", entry.Id))
            .Where(entry => entry.Id != direct)
            .ToList();
        return [Source(SystemRelationshipTypes.Extend, direct, continueAt, WithRoots(clip, earlier, [direct]))];
    }

    /// <summary>Sources for a task the rules do not know (TS-002's fallback): each <c>clip_roots</c> clip, of the general Remix type.</summary>
    private static List<VersionSource> Remix(JsonElement clip) =>
        [.. Roots(clip).Select(static root => new VersionSource(SystemRelationshipTypes.Remix.Id, null, VersionSourceTarget.OfExternal(root.Id)))];

    private static (IReadOnlyList<VersionSource> Sources, InspirationPlaylist? Playlist) Inspiration(JsonElement clip)
    {
        var clipIds = Ids(clip, "metadata.playlist_clip_ids");
        var playlistId = Id(clip, "metadata.playlist_id");
        if (playlistId is not null && playlistId != CreateInspirationPlaceholder)
        {
            return ([], new InspirationPlaylist(playlistId, string.Empty, [.. clipIds.Take(VersionLineageRules.PlaylistClipMaximum)]));
        }

        // No playlist: the clips are individual sources, as many as Suno takes.
        return (
            [.. clipIds.Take(VersionLineageRules.InspirationMaximum).Select(static id => Source(SystemRelationshipTypes.UseAsInspiration, id, null, []))],
            null);
    }

    private static VersionVoice? Voice(JsonElement clip)
    {
        if (Id(clip, "metadata.persona_id") is not { } personaId)
        {
            return null;
        }

        var persona = ImportFieldMap.Read(clip, "persona");
        var name = persona is { ValueKind: JsonValueKind.Object } named
            && (Id(named, "id") is null || Id(named, "id") == personaId)
            && named.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
            ? Line(nameValue.GetString()!, VersionLineageRules.NameMaximumLength)
            : string.Empty;
        return new VersionVoice(personaId, name);
    }

    private static List<VersionFileInput> FileInputs(JsonElement clip, ImportFieldMap map)
    {
        var files = new List<VersionFileInput>();
        if (map.Find(AudioKey)?.FileInput?.FeedPath is { } audioPath && Reported(clip, audioPath))
        {
            files.Add(new VersionFileInput(VersionFileInputKind.Audio, FileInputDescription));
        }

        foreach (var (kind, key, _) in FileKeys)
        {
            if (map.Find(key)?.FeedPath is { } path && Reported(clip, path))
            {
                files.Add(new VersionFileInput(kind, FileInputDescription));
            }
        }

        return files;
    }

    /// <summary>Whether the clip reports a file at <paramref name="path"/>: any value but null, false, or blank text.</summary>
    private static bool Reported(JsonElement clip, string path) =>
        ImportFieldMap.Read(clip, path) is { } value
        && value.ValueKind != JsonValueKind.False
        && !(value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    private static VersionSource Source(RelationshipType type, string sunoId, decimal? continueAt, IReadOnlyList<(string Key, string Id)> secondary) =>
        new(type.Id, type.SunoAction, VersionSourceTarget.OfExternal(sunoId), continueAt, SecondaryIds(secondary));

    /// <summary><paramref name="secondary"/> followed by each <c>clip_roots</c> ID none of <paramref name="direct"/> is.</summary>
    private static List<(string Key, string Id)> WithRoots(JsonElement clip, IReadOnlyList<(string Key, string Id)> secondary, IReadOnlyCollection<string> direct) =>
        [.. secondary, .. Roots(clip)
            .Select(static (root, index) => (Key: $"clip_roots[{index}]", root.Id))
            .Where(root => !direct.Contains(root.Id, StringComparer.Ordinal))];

    /// <summary>
    /// Secondary identifiers as a source stores them: a JSON object of named Suno IDs in order, each
    /// ID once, at most <see cref="VersionLineageRules.SecondaryIdsMaximum"/>; null for none.
    /// </summary>
    private static string? SecondaryIds(IReadOnlyList<(string Key, string Id)> secondary)
    {
        var kept = secondary.DistinctBy(static entry => entry.Id, StringComparer.Ordinal).Take(VersionLineageRules.SecondaryIdsMaximum).ToList();
        if (kept.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, id) in kept)
            {
                writer.WriteString(key, id);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ExternalSunoReference Reference(JsonElement clip, string sunoId)
    {
        var title = Roots(clip).FirstOrDefault(root => root.Id == sunoId).Title is { Length: > 0 } rootTitle
            ? Line(rootTitle, ExternalSunoReferenceRules.TitleMaximumLength)
            : null;
        return new ExternalSunoReference(
            Guid.Empty,
            sunoId,
            ExternalSunoKind.Clip,
            string.IsNullOrWhiteSpace(title) ? "Suno clip " + sunoId[..Math.Min(8, sunoId.Length)] : title,
            ClipFields.PageUrlOf(sunoId),
            ExternalSunoReferenceRules.NotImportedLabel);
    }

    /// <summary>The clips of <c>clip_roots.clips</c> that have a Suno ID, with their titles, in order.</summary>
    private static List<(string Id, string? Title)> Roots(JsonElement clip)
    {
        var roots = new List<(string, string?)>();
        if (ImportFieldMap.Read(clip, "clip_roots.clips") is { ValueKind: JsonValueKind.Array } clips)
        {
            foreach (var root in clips.EnumerateArray())
            {
                if (root.ValueKind == JsonValueKind.Object && Id(root, "id") is { } id)
                {
                    var title = root.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : null;
                    roots.Add((id, title));
                }
            }
        }

        return roots;
    }

    /// <summary>The Suno ID at <paramref name="path"/>; null when absent or not one.</summary>
    private static string? Id(JsonElement element, string path) =>
        ImportFieldMap.Read(element, path) is { ValueKind: JsonValueKind.String } value && ExternalSunoReferenceRules.IsSunoId(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>The Suno IDs in the array at <paramref name="path"/>, in order, each once; empty when absent.</summary>
    private static List<string> Ids(JsonElement clip, string path) =>
        ImportFieldMap.Read(clip, path) is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String && ExternalSunoReferenceRules.IsSunoId(item.GetString()))
                .Select(static item => item.GetString()!)
                .Distinct(StringComparer.Ordinal)]
            : [];

    /// <summary>A position in seconds at <paramref name="path"/>, to the hundredth; null when absent, negative, or not a number.</summary>
    private static decimal? Seconds(JsonElement element, string path) =>
        ImportFieldMap.Read(element, path) is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var seconds) && seconds >= 0
            ? decimal.Round(seconds, 2, MidpointRounding.ToEven)
            : null;

    /// <summary>One line of text: control characters dropped, cut to <paramref name="maximumLength"/>.</summary>
    private static string Line(string text, int maximumLength)
    {
        var line = string.Concat(text.Where(static character => !char.IsControl(character))).Trim();
        return line.Length <= maximumLength ? line : line[..maximumLength];
    }
}
