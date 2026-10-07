using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Reading a clip's lineage (#137): each TS-002 example (<c>lineage-metadata.examples.json</c>) read
/// into sources, Inspiration, and Voice, the identifiers it keeps besides the direct source, the
/// clips that yield none, the same-inputs comparison, and the two coverage tests (the seven rows of
/// the TS-002 lineage list, and the six reference and file keys of the inventory). Pure: no host.
/// </summary>
public sealed class LineageReaderTests
{
    private const string Source1 = "00000000-0000-4000-8000-000000000011";
    private const string Source2 = "00000000-0000-4000-8000-000000000012";
    private const string PlaylistId = "00000000-0000-4000-8000-000000000021";
    private const string PersonaId = "00000000-0000-4000-8000-000000000031";

    /// <summary>The six reference and file keys of the inventory the lineage reader owns (#135's <see cref="ClipInputMapper.ReadElsewhere"/>, less the workspace).</summary>
    private static readonly string[] InventoryKeys = ["audio", "voice", "inspiration", "simple_add_playlist", "simple_add_image", "simple_add_video"];

    [Fact]
    public void ACoverIsItsCoverClipWithANotImportedReference()
    {
        var read = Read("cover");

        var source = Assert.Single(read.Lineage.AudioSources);
        Assert.Equal(SystemRelationshipTypes.Cover.Id, source.TypeId);
        Assert.Equal(SunoActions.Cover, source.SunoAction);
        Assert.Equal(VersionSourceTarget.OfExternal(Source1), source.Target);
        Assert.Null(source.ContinueAtSeconds);
        Assert.Null(source.SecondaryIds); // edited_clip_id is the same clip, and there is no clip_roots
        Assert.Equal("Cover", read.Rule);

        var reference = Assert.Single(read.References);
        Assert.Equal(Source1, reference.SunoId);
        Assert.Equal(ExternalSunoKind.Clip, reference.Kind);
        Assert.Equal("Suno clip 00000000", reference.Title);
        Assert.Equal("https://suno.com/song/" + Source1, reference.Address);
        Assert.Equal(ExternalSunoReferenceRules.NotImportedLabel, reference.Label);
    }

    [Fact]
    public void ACoverKeepsADifferentEditedClipAndFallsBackToIt()
    {
        var differing = Example("cover");
        differing["metadata"]!["edited_clip_id"] = Source2;
        var source = Assert.Single(Read(differing).Lineage.AudioSources);
        Assert.Equal(VersionSourceTarget.OfExternal(Source1), source.Target);
        Assert.Equal($$"""{"edited_clip_id":"{{Source2}}"}""", source.SecondaryIds);

        var editedOnly = Example("cover");
        editedOnly["metadata"]!.AsObject().Remove("cover_clip_id");
        Assert.Equal(VersionSourceTarget.OfExternal(Source1), Assert.Single(Read(editedOnly).Lineage.AudioSources).Target);
    }

    [Fact]
    public void AMashupIsItsTwoClipsInOrderTitledFromClipRoots()
    {
        var read = Read("mashup");

        Assert.Equal([Source1, Source2], read.Lineage.AudioSources.Select(static source => source.Target.ExternalSunoId));
        Assert.All(read.Lineage.AudioSources, static source => Assert.Equal(SystemRelationshipTypes.Mashup.Id, source.TypeId));
        Assert.All(read.Lineage.AudioSources, static source => Assert.Null(source.SecondaryIds)); // clip_roots names only the two
        Assert.Equal(["<redacted title>", "<redacted title>"], read.References.Select(static reference => reference.Title));

        // The order is mashup_clip_ids', not clip_roots'.
        var swapped = Example("mashup");
        swapped["metadata"]!["mashup_clip_ids"] = new JsonArray(Source2, Source1);
        Assert.Equal([Source2, Source1], Read(swapped).Lineage.AudioSources.Select(static source => source.Target.ExternalSunoId));
    }

    [Fact]
    public void ASampleIsItsClipRoot()
    {
        var source = Assert.Single(Read("sample").Lineage.AudioSources);

        Assert.Equal(SystemRelationshipTypes.SampleThisSong.Id, source.TypeId);
        Assert.Equal(SunoActions.Sample, source.SunoAction);
        Assert.Equal(VersionSourceTarget.OfExternal(Source1), source.Target);
    }

    [Fact]
    public void AnExtendIsItsEditedClipAtItsHistoryPosition()
    {
        var source = Assert.Single(Read("extend").Lineage.AudioSources);

        Assert.Equal(SystemRelationshipTypes.Extend.Id, source.TypeId);
        Assert.Equal(VersionSourceTarget.OfExternal(Source1), source.Target);
        Assert.Equal(125m, source.ContinueAtSeconds);
        Assert.Null(source.SecondaryIds);
    }

    [Fact]
    public void AnExtendOfAnExtensionKeepsTheEarlierHistoryInOrderAndTheClipRoots()
    {
        var clip = Example("extend");
        const string Root = "00000000-0000-4000-8000-000000000041";
        const string Middle = "00000000-0000-4000-8000-000000000042";
        clip["metadata"]!["edited_clip_id"] = Source2;
        clip["metadata"]!["history"] = new JsonArray(
            new JsonObject { ["id"] = Root, ["continue_at"] = 30 },
            new JsonObject { ["id"] = Middle, ["continue_at"] = 61.257 },
            new JsonObject { ["id"] = Source2, ["continue_at"] = 92.5 });
        clip["clip_roots"]!["clips"]![0]!["id"] = Root;
        clip["clip_roots"]!["clips"]!.AsArray().Add(new JsonObject { ["id"] = "00000000-0000-4000-8000-000000000043", ["title"] = "Other root" });

        var source = Assert.Single(Read(clip).Lineage.AudioSources);

        Assert.Equal(VersionSourceTarget.OfExternal(Source2), source.Target);
        Assert.Equal(92.5m, source.ContinueAtSeconds);
        Assert.Equal(
            $$"""{"history[0]":"{{Root}}","history[1]":"{{Middle}}","clip_roots[1]":"00000000-0000-4000-8000-000000000043"}""",
            source.SecondaryIds);
    }

    [Fact]
    public void InspirationIsThePlaylistWithItsSnapshotOrIndividualSourcesWithoutOne()
    {
        var read = Read("inspiration");
        Assert.Empty(read.Lineage.AudioSources);
        Assert.Empty(read.Lineage.InspirationSources);
        Assert.Equal(new InspirationPlaylist(PlaylistId, string.Empty, [Source1]), read.Lineage.Playlist);
        Assert.Empty(read.References);

        // No playlist reported (or Create's literal placeholder): the clips are individual sources.
        foreach (var playlist in new JsonNode?[] { null, "inspiration" })
        {
            var clip = Example("inspiration");
            clip["metadata"]!["playlist_id"] = playlist;
            clip["metadata"]!["playlist_clip_ids"] = new JsonArray(Source1, Source2);
            var individual = Read(clip);
            Assert.Null(individual.Lineage.Playlist);
            Assert.Equal([Source1, Source2], individual.Lineage.InspirationSources.Select(static source => source.Target.ExternalSunoId));
            Assert.All(individual.Lineage.InspirationSources, static source => Assert.Equal(SystemRelationshipTypes.UseAsInspiration.Id, source.TypeId));
            Assert.Equal([Source1, Source2], individual.References.Select(static reference => reference.SunoId));
        }
    }

    [Fact]
    public void AVoiceIsItsPersonaAndKeepsItsInspiration()
    {
        var read = Read("voice");

        Assert.Equal(new VersionVoice(PersonaId, "<redacted name>"), read.Lineage.Voice);
        Assert.Equal(new InspirationPlaylist(PlaylistId, string.Empty, [Source1]), read.Lineage.Playlist);
        Assert.Empty(read.Lineage.AudioSources);
    }

    [Fact]
    public void APlainGenerationAndAReusePromptClipYieldNoSources()
    {
        Assert.True(Read("plain_generation").Lineage.IsEmpty);
        Assert.Empty(Read("plain_generation").References);

        var reuse = Read("reuse_prompt");
        Assert.True(reuse.Lineage.IsEmpty);
        Assert.Null(reuse.Task);

        // Even with clip_roots, a clip with no task is not a Remix: Reuse Prompt leaves nothing.
        var withRoots = Example("reuse_prompt");
        withRoots["clip_roots"] = Example("sample")["clip_roots"]!.DeepClone();
        Assert.True(Read(withRoots).Lineage.IsEmpty);
    }

    [Fact]
    public void AnUnknownTaskTakesRemixSourcesFromClipRootsAndWithoutThemNone()
    {
        var clip = Example("mashup");
        clip["metadata"]!["task"] = "a_task_from_the_future";
        var read = Read(clip);

        Assert.Equal([Source1, Source2], read.Lineage.AudioSources.Select(static source => source.Target.ExternalSunoId));
        Assert.All(read.Lineage.AudioSources, static source => Assert.True(VersionLineageRules.IsRemix(source)));
        Assert.All(read.Lineage.AudioSources, static source => Assert.Null(source.SunoAction));
        Assert.Equal(LineageReader.RemixRule, read.Rule);

        var bare = Example("plain_generation");
        bare["metadata"]!["task"] = "a_task_from_the_future";
        Assert.True(Read(bare).Lineage.IsEmpty);
    }

    /// <summary>Every lineage read from an example is one an import may store: complete, and valid as imported.</summary>
    [Fact]
    public void EveryExampleReadsAsACompleteImportedLineage()
    {
        foreach (var name in Examples().Select(static example => example.Key))
        {
            var read = Read(name);
            var errors = VersionLineageRules.Errors(read.Lineage, VersionKind.Song, CreationMode.Advanced, LineageCheck.Complete, LineageOrigin.Import);
            Assert.True(errors.Count == 0, $"{name}: {string.Join("; ", errors.Select(static error => error.Rule))}");
            Assert.Equal(
                read.Lineage.AudioSources.Concat(read.Lineage.InspirationSources).Select(static source => source.Target.ExternalSunoId).Distinct(),
                read.References.Select(static reference => reference.SunoId));
        }
    }

    [Fact]
    public void AFileInputIsANoteWhereTheMapSaysSunoReportsItAndNothingOtherwise()
    {
        // The shipped map: no file input is reported, so none is read.
        Assert.Empty(Read("cover").Lineage.FileInputs);
        Assert.True(ImportFieldMap.Embedded.Find("simple_add_image")!.IsNotReturned);
        Assert.True(ImportFieldMap.Embedded.Find("simple_add_video")!.IsNotReturned);
        Assert.True(ImportFieldMap.Embedded.Find("audio")!.FileInput!.IsNotReturned);

        // A map that says where Suno reports them: each is a note, described as imported.
        var map = MapJson();
        map["fields"]!["simple_add_image"]!["paths"]!["feed"] = "metadata.uploaded_image";
        map["fields"]!["simple_add_video"]!["paths"]!["feed"] = "metadata.uploaded_video";
        map["fields"]!["audio"]!["fileInput"] = new JsonObject { ["paths"] = new JsonObject { ["feed"] = "metadata.uploaded_audio" } };
        var reporting = ImportFieldMap.Parse(map.ToJsonString());
        var clip = Example("plain_generation");
        clip["metadata"]!["uploaded_image"] = "image";
        clip["metadata"]!["uploaded_video"] = "video";
        clip["metadata"]!["uploaded_audio"] = "audio";

        var files = LineageReader.Read(Element(clip), LineageReader.Rules, reporting).Lineage.FileInputs;
        Assert.Equal(
            [
                new VersionFileInput(VersionFileInputKind.Audio, "Imported from Suno"),
                new VersionFileInput(VersionFileInputKind.Image, "Imported from Suno"),
                new VersionFileInput(VersionFileInputKind.Video, "Imported from Suno"),
            ],
            files);
        Assert.Contains("simple_add_image", LineageReader.Captures(reporting).Keys);

        // Complement: a blank value is no file.
        clip["metadata"]!["uploaded_image"] = " ";
        Assert.DoesNotContain(LineageReader.Read(Element(clip), LineageReader.Rules, reporting).Lineage.FileInputs, static file => file.Kind == VersionFileInputKind.Image);
    }

    [Fact]
    public void TwoClipsWithTheSameSettingsAndDifferentSourcesHaveDifferentInputs()
    {
        var clip = JsonNode.Parse(Clips.FixtureClip("feed-v3.songs-advanced.response.json"))!;
        clip["metadata"]!["task"] = "cover";
        clip["metadata"]!["cover_clip_id"] = Source1;
        var same = clip.DeepClone();
        var other = clip.DeepClone();
        other["metadata"]!["cover_clip_id"] = Source2;

        Assert.True(ClipInputMapper.SameInputs(ImportedVersions.Map(clip), ImportedVersions.Map(same)));
        Assert.False(ClipInputMapper.SameInputs(ImportedVersions.Map(clip), ImportedVersions.Map(other)));

        // Each part of the key: Extend's position, the playlist (not its snapshot), and the persona.
        var extend = Example("extend");
        var later = Example("extend");
        later["metadata"]!["history"]![0]!["continue_at"] = 126;
        Assert.NotEqual(Read(extend).ComparisonKey, Read(later).ComparisonKey);

        var voice = Example("voice");
        var resnapshot = Example("voice");
        resnapshot["metadata"]!["playlist_clip_ids"] = new JsonArray(Source2);
        Assert.Equal(Read(voice).ComparisonKey, Read(resnapshot).ComparisonKey);
        var otherPersona = Example("voice");
        otherPersona["metadata"]!["persona_id"] = "00000000-0000-4000-8000-000000000032";
        Assert.NotEqual(Read(voice).ComparisonKey, Read(otherPersona).ComparisonKey);
        var otherPlaylist = Example("voice");
        otherPlaylist["metadata"]!["playlist_id"] = "00000000-0000-4000-8000-000000000022";
        Assert.NotEqual(Read(voice).ComparisonKey, Read(otherPlaylist).ComparisonKey);

        // A plain clip and its copy compare as before (#135): no lineage on either.
        Assert.True(ClipInputMapper.SameInputs(ImportedVersions.Map(Example("plain_generation")), ImportedVersions.Map(Example("plain_generation"))));
    }

    /// <summary>
    /// The first coverage test: every row of the TS-002 lineage list (<c>docs/spikes/TS-002.lineage.json</c>,
    /// checked against the finding's own table) has a capture rule and a fixture that the rule reads as
    /// the row says, and Reuse Prompt, which Suno does not record, yields nothing.
    /// </summary>
    [Fact]
    public void EveryRowOfTheLineageListHasACaptureRuleAndAFixtureTest()
    {
        var rows = LineageRows();
        Assert.Equal(7, rows.Count);
        Assert.Equal(FindingTable(), rows.Select(static row => (row.Action, row.Task)));

        Assert.Empty(LineageListGaps(rows, LineageReader.Rules));
    }

    /// <summary>It bites: with the Extend rule removed, the Extend row fails, and only it.</summary>
    [Fact]
    public void TheLineageListCoverageFailsWithoutTheExtendRule()
    {
        var withoutExtend = LineageReader.Rules.Where(static rule => rule.Key != "extend").ToDictionary(StringComparer.Ordinal);

        var gaps = LineageListGaps(LineageRows(), withoutExtend);

        Assert.Equal(
            ["Extend: no capture rule for task 'extend'.", "Extend: the 'extend' fixture does not read as audio (extend)."],
            gaps);
    }

    /// <summary>
    /// The second coverage test: each of the six reference and file keys of the inventory has a capture
    /// rule with a fixture that fills its part, or a <c>notReturned</c> entry in the import field map;
    /// and the audio slot's file input, which no fixture shows, is marked not returned.
    /// </summary>
    [Fact]
    public void EveryReferenceAndFileKeyHasACaptureRuleWithAFixtureOrIsNotReturned()
    {
        Assert.Equal(
            ClipInputMapper.ReadElsewhere.Where(static key => key != "workspace").Order(StringComparer.Ordinal),
            InventoryKeys.Order(StringComparer.Ordinal));
        Assert.Empty(InventoryGaps(LineageReader.Captures(ImportFieldMap.Embedded), ImportFieldMap.Embedded));
        Assert.True(ImportFieldMap.Embedded.Find("audio")!.FileInput!.IsNotReturned);
    }

    /// <summary>It bites: the image key with neither its capture rule nor its <c>notReturned</c> entry fails.</summary>
    [Fact]
    public void TheInventoryCoverageFailsWithoutTheImageRuleAndItsNotReturnedEntry()
    {
        var map = MapJson();
        map["fields"]!["simple_add_image"]!.AsObject().Remove("notReturned");
        var broken = ImportFieldMap.Parse(map.ToJsonString());
        var captures = LineageReader.Captures(broken).Where(static capture => capture.Key != "simple_add_image").ToDictionary(StringComparer.Ordinal);

        Assert.Equal(["'simple_add_image' has neither a capture rule nor a notReturned entry."], InventoryGaps(captures, broken));

        // And a capture whose part no fixture shows fails too: a map pointing at an image field no example has.
        map["fields"]!["simple_add_image"]!["paths"]!["feed"] = "metadata.uploaded_image";
        var pointing = ImportFieldMap.Parse(map.ToJsonString());
        Assert.Equal(["'simple_add_image' has a capture rule (ImageFile) but no fixture it reads."], InventoryGaps(LineageReader.Captures(pointing), pointing));
    }

    private static List<string> LineageListGaps(IReadOnlyList<LineageRow> rows, IReadOnlyDictionary<string, LineageRule> rules)
    {
        var examples = Examples();
        var gaps = new List<string>();
        foreach (var row in rows)
        {
            if (row.Captured && (row.Task is null || !rules.ContainsKey(row.Task)))
            {
                gaps.Add($"{row.Action}: no capture rule for task '{row.Task}'.");
            }

            if (!examples.ContainsKey(row.Fixture))
            {
                gaps.Add($"{row.Action}: no fixture '{row.Fixture}'.");
                continue;
            }

            var read = LineageReader.Read(Element(Example(row.Fixture)), rules, ImportFieldMap.Embedded);
            var holds = row.Yields switch
            {
                "audio" => read.Lineage.AudioSources.Count > 0 && read.Lineage.AudioSources.All(source => source.SunoAction == row.SunoAction),
                "inspiration" => read.Has(LineagePart.Inspiration),
                "voice" => read.Has(LineagePart.Voice),
                "none" => read.Lineage.IsEmpty,
                _ => false,
            };
            if (!holds)
            {
                gaps.Add($"{row.Action}: the '{row.Fixture}' fixture does not read as {row.Yields} ({row.SunoAction}).");
            }
        }

        return gaps;
    }

    private static List<string> InventoryGaps(IReadOnlyDictionary<string, LineagePart> captures, ImportFieldMap map)
    {
        var reads = Examples().Select(example => LineageReader.Read(Element(Example(example.Key)), LineageReader.Rules, map)).ToList();
        var gaps = new List<string>();
        foreach (var key in InventoryKeys)
        {
            if (captures.TryGetValue(key, out var part))
            {
                if (!reads.Any(read => read.Has(part)))
                {
                    gaps.Add($"'{key}' has a capture rule ({part}) but no fixture it reads.");
                }
            }
            else if (map.Find(key)?.IsNotReturned != true)
            {
                gaps.Add($"'{key}' has neither a capture rule nor a notReturned entry.");
            }
        }

        return gaps;
    }

    /// <summary>The rows of the finding's lineage list as its Markdown table has them: the action and the first <c>metadata.task</c> named.</summary>
    private static List<(string Action, string? Task)> FindingTable()
    {
        var lines = File.ReadAllLines(RepositoryFile("docs", "spikes", "TS-002.md"));
        var start = Array.FindIndex(lines, static line => line.StartsWith("## The lineage list", StringComparison.Ordinal));
        return [.. lines.Skip(start)
            .SkipWhile(static line => !line.StartsWith("| Suno action", StringComparison.Ordinal))
            .Skip(2)
            .TakeWhile(static line => line.StartsWith('|'))
            .Select(static line => line.Split('|', StringSplitOptions.TrimEntries))
            .Select(static cells => (cells[1], cells[2].StartsWith('`') ? cells[2].Split('`')[1] : null))];
    }

    private static List<LineageRow> LineageRows()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile("docs", "spikes", "TS-002.lineage.json")));
        return [.. document.RootElement.GetProperty("rows").EnumerateArray().Select(static row => new LineageRow(
            row.GetProperty("action").GetString()!,
            row.GetProperty("task").GetString(),
            row.GetProperty("captured").GetBoolean(),
            row.GetProperty("fixture").GetString()!,
            row.GetProperty("yields").GetString()!,
            row.GetProperty("sunoAction").GetString()!))];
    }

    private static ImportedLineage Read(string example) => Read(Example(example));

    private static ImportedLineage Read(JsonNode clip) => LineageReader.Read(Element(clip));

    private static JsonElement Element(JsonNode clip) => JsonSerializer.SerializeToElement(clip);

    /// <summary>The TS-002 examples by name (the <c>_about</c> note left out).</summary>
    private static Dictionary<string, JsonNode> Examples() =>
        JsonNode.Parse(Clips.Fixture("lineage-metadata.examples.json"))!.AsObject()
            .Where(static example => !example.Key.StartsWith('_'))
            .ToDictionary(static example => example.Key, static example => example.Value!, StringComparer.Ordinal);

    /// <summary>The example <paramref name="name"/> as a clip: its top-level fields, an ID and title, and its <c>metadata</c>.</summary>
    private static JsonNode Example(string name)
    {
        var example = Examples()[name];
        var clip = example["clip"]!.DeepClone().AsObject();
        clip["id"] = "00000000-0000-4000-8000-0000000000aa";
        clip["status"] = "complete";
        clip["title"] = "Example " + name;
        clip["metadata"] = example["metadata"]!.DeepClone();
        return clip;
    }

    private static JsonNode MapJson() => JsonNode.Parse(File.ReadAllText(RepositoryFile("docs", "suno-import-field-map.json")))!;

    private static string RepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, Path.Combine(parts));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{string.Join('/', parts)} is not above the test output.");
    }

    private sealed record LineageRow(string Action, string? Task, bool Captured, string Fixture, string Yields, string SunoAction);
}
