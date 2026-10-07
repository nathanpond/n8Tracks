using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Reading a Song clip's creation inputs (#135): the TS-003 fixtures mapped option by option, equality
/// of two clips' inputs, values outside n8Tracks' limits kept and marked, model matching against the
/// model list, and the coverage of the import field map. Pure: no host.
/// </summary>
public sealed class ClipInputMapperTests
{
    private const string Advanced = "feed-v3.songs-advanced.response.json";
    private const string Simple = "feed-v3.songs-simple.response.json";

    /// <summary>The Songs options the story lists, by inventory key: each is read, or marked not returned.</summary>
    private static readonly string[] SongsOptions =
    [
        "model", "simple_prompt", "simple_add_lyrics", "simple_add_styles", "lyrics", "styles", "exclude_styles",
        "vocal_gender", "duration_mode", "duration_seconds", "max_mode", "weirdness", "style_influence", "variety",
        "personalize", "title",
    ];

    /// <summary>
    /// The Advanced fixture has every More Options control off its default, so each option Suno
    /// returns must come back as the clip says and not as the default; the ones the feed does not
    /// return (styles, Vocal Gender, Duration, Personalize, and the Simple form's two sections) are
    /// marked and hold the default.
    /// </summary>
    [Fact]
    public void AnAdvancedClipMapsEveryReturnedOptionAndMarksTheRest()
    {
        var clip = FixtureClip(Advanced);
        var mapped = Map(clip);
        var inputs = mapped.Inputs;
        var metadata = clip["metadata"]!;
        var defaults = VersionInputRules.Defaults(CreateFieldInventory.Embedded, string.Empty);

        Assert.Equal(VersionKind.Song, inputs.Kind);
        Assert.Equal(CreationMode.Advanced, inputs.SongMode);
        Assert.Equal(metadata["prompt"]!.GetValue<string>(), mapped.Lyrics);
        Assert.Equal(string.Empty, mapped.Styles);
        Assert.Equal(metadata["negative_tags"]!.GetValue<string>(), inputs.ExcludeStyles);
        Assert.True(inputs.MaxMode);
        Assert.Equal(70, inputs.Weirdness);
        Assert.Equal(30, inputs.StyleInfluence);
        Assert.Equal("high", inputs.Variety);
        Assert.Equal(clip["title"]!.GetValue<string>(), inputs.Title);
        Assert.Equal(DefaultSunoModels.V6Mini.Name, inputs.Model);
        Assert.Equal(new ClipModel("V6-MINI", "v6-mini"), mapped.Model);
        Assert.Equal(string.Empty, inputs.SimplePrompt);

        // Off the default: a mapper that answered defaults would fail each of these.
        Assert.NotEqual(defaults.ExcludeStyles, inputs.ExcludeStyles);
        Assert.NotEqual(defaults.MaxMode, inputs.MaxMode);
        Assert.NotEqual(defaults.Weirdness, inputs.Weirdness);
        Assert.NotEqual(defaults.StyleInfluence, inputs.StyleInfluence);
        Assert.NotEqual(defaults.Variety, inputs.Variety);
        Assert.NotEqual(defaults.Model, inputs.Model);
        Assert.NotEqual(defaults.Title, inputs.Title);

        Assert.Equal(
            ["simpleLyricsAdded", "simpleStylesAdded", "styles", "vocalGender", "durationMode", "durationSeconds", "personalize"],
            mapped.Marks.NotReturned);
        Assert.Equal(defaults.VocalGender, inputs.VocalGender);
        Assert.Equal(defaults.DurationMode, inputs.DurationMode);
        Assert.Equal(defaults.DurationSeconds, inputs.DurationSeconds);
        Assert.Equal(defaults.Personalize, inputs.Personalize);
        Assert.Equal(defaults.SimpleLyricsAdded, inputs.SimpleLyricsAdded);
        Assert.Equal(defaults.SimpleStylesAdded, inputs.SimpleStylesAdded);
        Assert.Empty(mapped.Marks.OutOfRange);
        Assert.Empty(mapped.Marks.RawValues);
    }

    /// <summary>
    /// A Simple clip (the TS-003 marker: a description prompt and the <c>agentic_thinking</c> task) maps
    /// to Simple mode with its description; the Advanced options it carries are stored too, and a
    /// slider left at its default (Weirdness, absent) is the default.
    /// </summary>
    [Fact]
    public void ASimpleClipMapsItsDescriptionAndTheAdvancedOptionsItCarries()
    {
        var clip = FixtureClip(Simple);
        var mapped = Map(clip);

        Assert.Equal(CreationMode.Simple, mapped.Inputs.SongMode);
        Assert.Equal(clip["metadata"]!["gpt_description_prompt"]!.GetValue<string>(), mapped.Inputs.SimplePrompt);
        Assert.Equal(clip["metadata"]!["prompt"]!.GetValue<string>(), mapped.Lyrics);
        Assert.Equal("max", mapped.Inputs.Variety);
        Assert.Equal(50, mapped.Inputs.Weirdness);
        Assert.Equal(50, mapped.Inputs.StyleInfluence);
        Assert.False(mapped.Inputs.MaxMode);
        Assert.Equal("v6-mini", mapped.Inputs.Model);
        Assert.Contains("styles", mapped.Marks.NotReturned);
    }

    /// <summary>The marker needs both halves: a description prompt with another task is Advanced.</summary>
    [Fact]
    public void ADescriptionPromptWithoutTheAgenticTaskIsAdvanced()
    {
        var clip = FixtureClip(Simple);
        clip["metadata"]!["task"] = "cover";

        Assert.Equal(CreationMode.Advanced, Map(clip).Inputs.SongMode);
    }

    /// <summary>Every Songs option the story lists is read, or marked not returned, and nothing else is read.</summary>
    [Fact]
    public void EverySongsOptionIsReadOrMarkedNotReturned()
    {
        Assert.Equal(SongsOptions.Order(StringComparer.Ordinal), ClipInputMapper.ReadKeys.Order(StringComparer.Ordinal));

        var mapped = Map(FixtureClip(Advanced));
        var names = SongsOptions.Select(static key => key is "lyrics" or "styles" ? key : ApiName(key)).ToList();
        Assert.All(names, name => Assert.True(
            mapped.Compared.ContainsKey(name) || mapped.Marks.NotReturned.Contains(name) || ClipInputMapper.NotCompared.Contains(name),
            $"{name} is neither compared nor marked not returned."));
    }

    /// <summary>The two clips of one Create request have the same inputs, in each mode.</summary>
    [Theory]
    [InlineData(Advanced)]
    [InlineData(Simple)]
    public void TwoClipsOfOneCreateRequestHaveTheSameInputs(string fixture)
    {
        Assert.True(ClipInputMapper.SameInputs(Map(FixtureClip(fixture, 0)), Map(FixtureClip(fixture, 1))));
    }

    /// <summary>Changing only Weirdness makes two clips' inputs differ.</summary>
    [Fact]
    public void ChangingOnlyWeirdnessMakesTheInputsDiffer()
    {
        var other = FixtureClip(Advanced);
        other["metadata"]!["control_sliders"]!["weirdness_constraint"] = 0.71;

        Assert.False(ClipInputMapper.SameInputs(Map(FixtureClip(Advanced)), Map(other)));
    }

    /// <summary>
    /// A difference only in an option Suno does not return (the feed's rewritten styles) leaves the
    /// inputs equal, and so do line endings, trailing whitespace, and Suno's title.
    /// </summary>
    [Fact]
    public void DifferencesOnlyInNotReturnedOptionsLineEndingsOrTheTitleLeaveTheInputsEqual()
    {
        var clip = FixtureClip(Advanced);
        var other = clip.DeepClone();
        other["metadata"]!["tags"] = "a different rewrite of the styles";
        other["metadata"]!["prompt"] = clip["metadata"]!["prompt"]!.GetValue<string>().Replace("\n", "  \r\n", StringComparison.Ordinal) + " \r\n\r\n";
        other["title"] = "Renamed in Suno";

        Assert.True(ClipInputMapper.SameInputs(Map(clip), Map(other)));

        other["metadata"]!["prompt"] = "Other words";
        Assert.False(ClipInputMapper.SameInputs(Map(clip), Map(other)));
    }

    /// <summary>
    /// A clip compared with a Version: equal on every returned option, whatever the Version holds for the
    /// ones not returned; a returned option that differs is a difference, and a raw unknown value is
    /// compared as Suno returned it.
    /// </summary>
    [Fact]
    public void AClipDiffersFromAVersionOnlyOnReturnedOptions()
    {
        var mapped = Map(FixtureClip(Advanced));

        Assert.False(ClipInputMapper.Differs(mapped, mapped.Lyrics, mapped.Styles, mapped.Inputs, mapped.Marks));
        Assert.False(ClipInputMapper.Differs(mapped, mapped.Lyrics, "styles the user wrote", mapped.Inputs with { VocalGender = "female", Personalize = true }, null));
        Assert.True(ClipInputMapper.Differs(mapped, mapped.Lyrics, mapped.Styles, mapped.Inputs with { Weirdness = 71 }, mapped.Marks));
        Assert.True(ClipInputMapper.Differs(mapped, mapped.Lyrics + " more", mapped.Styles, mapped.Inputs, mapped.Marks));

        var unknown = FixtureClip(Advanced);
        unknown["metadata"]!["control_sliders"]!["aug_creativity"] = 7;
        var raw = Map(unknown);
        Assert.False(ClipInputMapper.Differs(raw, raw.Lyrics, raw.Styles, raw.Inputs, raw.Marks));
        Assert.True(ClipInputMapper.Differs(raw, raw.Lyrics, raw.Styles, raw.Inputs, null));
    }

    /// <summary>
    /// A value outside n8Tracks' limits is kept as Suno returned it and marked, never cut or refused: a
    /// 6,000-character lyric whole, an out-of-range percentage as it is, an unknown choice raw with the
    /// option at its default, a fractional percentage raw.
    /// </summary>
    [Fact]
    public void ValuesOutsideTheLimitsAreKeptAndMarked()
    {
        var clip = FixtureClip(Advanced);
        var lyrics = new string('l', 6000);
        clip["metadata"]!["prompt"] = lyrics;
        clip["metadata"]!["control_sliders"]!["weirdness_constraint"] = 1.5;
        clip["metadata"]!["control_sliders"]!["aug_creativity"] = 7;
        clip["metadata"]!["control_sliders"]!["style_weight"] = 0.305;
        clip["title"] = new string('t', 150);

        var mapped = Map(clip);

        Assert.Equal(lyrics, mapped.Lyrics);
        Assert.Equal(150, mapped.Inputs.Weirdness);
        Assert.Equal(150, mapped.Inputs.Title.Length);
        Assert.Equal("normal", mapped.Inputs.Variety);
        Assert.Equal(50, mapped.Inputs.StyleInfluence);
        Assert.Equal(["lyrics", "weirdness", "styleInfluence", "variety", "title"], mapped.Marks.OutOfRange);
        Assert.Equal("7", mapped.Marks.RawValues["variety"]);
        Assert.Equal("0.305", mapped.Marks.RawValues["styleInfluence"]);
        Assert.True(mapped.Marks.HasOutOfRange);

        // The editor's own checks would refuse each of them: the mapper skipped them, nothing else.
        var sent = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["weirdness"] = JsonSerializer.SerializeToElement(mapped.Inputs.Weirdness),
            ["title"] = JsonSerializer.SerializeToElement(mapped.Inputs.Title),
        };
        Assert.Equal(2, VersionInputRules.Errors(CreateFieldInventory.Embedded, ["v6-mini"], sent).Count);
    }

    /// <summary>
    /// The model is the list entry whose reported-as name matches the clip's badge, ignoring case, else
    /// the entry so named; a clip with no badge falls back to <c>major_model_version</c>, then
    /// <c>model_name</c>; an unknown model is proposed as a new entry named as Suno reported it; a clip
    /// reporting none did not return it.
    /// </summary>
    [Fact]
    public void TheModelMatchesTheModelListOrIsProposed()
    {
        var renamed = DefaultSunoModels.V6Mini with { Name = "Mini (renamed)" };
        var list = new[] { DefaultSunoModels.V6, DefaultSunoModels.V6Wild, renamed };

        var clip = FixtureClip(Advanced);
        Assert.Equal(new ClipModel("V6-MINI", "Mini (renamed)"), ClipInputMapper.Map(Element(clip), list).Model);

        clip["metadata"]!["model_badges"]!["songrow"]!["display_name"] = "v6-WILD";
        Assert.Equal(new ClipModel("v6-WILD", "v6-wild"), ClipInputMapper.Map(Element(clip), list).Model);

        clip["metadata"]!["model_badges"]!["songrow"]!["display_name"] = "V7-NEW";
        var proposed = ClipInputMapper.Map(Element(clip), list);
        Assert.Equal(new ClipModel("V7-NEW", null), proposed.Model);
        Assert.True(proposed.Model!.IsNew);
        Assert.Equal("V7-NEW", proposed.Inputs.Model);

        clip["metadata"]!.AsObject().Remove("model_badges");
        clip["major_model_version"] = "v6";
        Assert.Equal(new ClipModel("v6", "v6"), ClipInputMapper.Map(Element(clip), list).Model);

        clip.AsObject().Remove("major_model_version");
        clip["model_name"] = "chirp-goose";
        Assert.Equal(new ClipModel("chirp-goose", null), ClipInputMapper.Map(Element(clip), list).Model);

        clip.AsObject().Remove("model_name");
        var none = ClipInputMapper.Map(Element(clip), list);
        Assert.Null(none.Model);
        Assert.Null(none.Inputs.Model);
        Assert.Contains("model", none.Marks.NotReturned);
        Assert.False(none.Compared.ContainsKey("model"));
    }

    /// <summary>The matching rule itself: reported-as names first, then names, both ignoring case.</summary>
    [Fact]
    public void MatchingPrefersTheReportedAsName()
    {
        var mini = DefaultSunoModels.V6Mini;
        var impostor = new SunoModel(Guid.CreateVersion7(), "V6-MINI-LOOKALIKE", null, 4, Retired: false, Discovered: false, ReportedAs: "v6-mini");

        Assert.Equal(mini, SunoModelRules.Match([impostor, mini], "v6-MINI"));
        Assert.Equal(DefaultSunoModels.V6, SunoModelRules.Match(DefaultSunoModels.All, "V6"));
        Assert.Null(SunoModelRules.Match(DefaultSunoModels.All, "V6-MINI2"));
    }

    /// <summary>
    /// The coverage test: every Songs field of the inventory has a feed mapping or a <c>notReturned</c>
    /// entry, and every mapped one is read by the mapper (or by the lineage or commit story).
    /// </summary>
    [Fact]
    public void TheImportFieldMapCoversEverySongsField()
    {
        Assert.Empty(ClipInputMapper.CoverageGaps(ImportFieldMap.Embedded, CreateFieldInventory.Embedded, ClipInputMapper.ReadKeys));

        // The fields left to others are references and files, not options a Version holds.
        Assert.All(ClipInputMapper.ReadElsewhere, key => Assert.Contains(CreateFieldInventory.Embedded.Get(key).Type, new[] { CreateField.ReferenceType, CreateField.FileType }));
    }

    /// <summary>It bites: a field with its mapping removed fails it, and so does a mapped field the mapper does not read.</summary>
    [Fact]
    public void TheCoverageTestFailsOnAMissingMappingOrAnUnreadField()
    {
        var map = JsonNode.Parse(File.ReadAllText(MapPath()))!;
        map["fields"]!["exclude_styles"]!["paths"]!["feed"] = null;
        var broken = ImportFieldMap.Parse(map.ToJsonString());

        Assert.Equal(
            ["'exclude_styles' has neither a feed path nor a notReturned entry."],
            ClipInputMapper.CoverageGaps(broken, CreateFieldInventory.Embedded, ClipInputMapper.ReadKeys));
        Assert.Throws<InvalidOperationException>(() => ClipInputMapper.Map(Element(FixtureClip(Advanced)), DefaultSunoModels.All, broken, CreateFieldInventory.Embedded));

        var unread = ClipInputMapper.ReadKeys.Where(static key => key != "weirdness").ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            ["'weirdness' is mapped but the mapper does not read it."],
            ClipInputMapper.CoverageGaps(ImportFieldMap.Embedded, CreateFieldInventory.Embedded, unread));
    }

    /// <summary>The embedded map is the committed file, as it is.</summary>
    [Fact]
    public void TheEmbeddedMapIsTheDocsFile()
    {
        var file = ImportFieldMap.Parse(File.ReadAllText(MapPath()));

        Assert.Equal(file.Entries.Select(static entry => entry.Key), ImportFieldMap.Embedded.Entries.Select(static entry => entry.Key));
        Assert.Equal("metadata.control_sliders.weirdness_constraint", ImportFieldMap.Embedded.Find("weirdness")!.FeedPath);
        Assert.True(ImportFieldMap.Embedded.Find("simple_add_lyrics")!.IsNotReturned);
        Assert.True(ImportFieldMap.Embedded.Find("simple_add_styles")!.IsNotReturned);
    }

    private static MappedClipInputs Map(JsonNode clip) => ClipInputMapper.Map(Element(clip), DefaultSunoModels.All);

    private static JsonElement Element(JsonNode clip) => JsonSerializer.SerializeToElement(clip);

    private static JsonNode FixtureClip(string name, int index = 0) => JsonNode.Parse(Clips.FixtureClip(name, index))!;

    private static string ApiName(string inventoryKey) =>
        VersionInputRules.Keys.Single(key => VersionInputRules.InventoryKey(key) == inventoryKey);

    private static string MapPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "suno-import-field-map.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("docs/suno-import-field-map.json is not above the test output.");
    }
}
