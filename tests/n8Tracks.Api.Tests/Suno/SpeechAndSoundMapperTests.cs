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
/// Reading a Speech or a Sound clip's creation inputs (#136): the kind told by the import field map's
/// markers, the TS-003 Speech Simple, Speech Advanced, and Sounds fixtures mapped option by option, the
/// same-inputs and out-of-range rules on those tabs, and the fallback to a Song needing the user's
/// attention when the kind cannot be told. Pure: no host.
/// </summary>
public sealed class SpeechAndSoundMapperTests
{
    private const string SpeechSimple = "feed-v3.speech-simple.response.json";
    private const string SpeechAdvanced = "feed-v3.speech-advanced.response.json";
    private const string Sounds = "feed-v3.sounds.response.json";

    /// <summary>Every fixture of Songs clips (feed, Create response, Trash): none is ever a Speech or a Sound.</summary>
    private static readonly string[] SongFixtures =
    [
        "feed-v3.songs-advanced.response.json",
        "feed-v3.songs-simple.response.json",
        "feed-v3.completed-clip.response.json",
        "feed-v3.library-page-1.response.json",
        "feed-v3.library-page-2.response.json",
        "generate-v2-web.songs-advanced.response.json",
        "generate-v2-web.songs-simple.response.json",
        "clips-trashed-v2.response.json",
    ];

    private static readonly VersionInputs Defaults = VersionInputRules.Defaults(CreateFieldInventory.Embedded, string.Empty);

    /// <summary>
    /// A Speech Simple clip (<c>is_speech</c>, and the Simple marker) maps to a Speech Version in Simple
    /// mode with its description and script; Tone, Vocal Gender, and Background music are not returned,
    /// and Variety, which the Simple form does not have, stays at its default.
    /// </summary>
    [Fact]
    public void ASpeechSimpleClipMapsToASpeechVersionInSimpleMode()
    {
        var clip = FixtureClip(SpeechSimple);
        var mapped = Map(clip);
        var inputs = mapped.Inputs;

        Assert.Equal(VersionKind.Speech, inputs.Kind);
        Assert.Equal(CreationMode.Simple, inputs.SpeechMode);
        Assert.Equal(Metadata(clip, "gpt_description_prompt"), inputs.SpeechPrompt);
        Assert.Equal(Metadata(clip, "prompt"), inputs.SpeechScript);
        Assert.NotEqual(Defaults.SpeechPrompt, inputs.SpeechPrompt);
        Assert.NotEqual(Defaults.SpeechScript, inputs.SpeechScript);
        Assert.Equal(Defaults.SpeechVariety, inputs.SpeechVariety);
        Assert.Equal(["speechTone", "speechVocalGender", "speechBackgroundMusic"], mapped.Marks.NotReturned);
        AssertOnlyItsTab(mapped, VersionKind.Speech);
        Assert.False(mapped.KindUnknown);
    }

    /// <summary>
    /// A Speech Advanced clip maps to Advanced mode with its script and Variety (High, off the default);
    /// it has no description prompt, so that stays empty.
    /// </summary>
    [Fact]
    public void ASpeechAdvancedClipMapsItsScriptAndVariety()
    {
        var clip = FixtureClip(SpeechAdvanced);
        var mapped = Map(clip);
        var inputs = mapped.Inputs;

        Assert.Equal(VersionKind.Speech, inputs.Kind);
        Assert.Equal(CreationMode.Advanced, inputs.SpeechMode);
        Assert.Equal(Metadata(clip, "prompt"), inputs.SpeechScript);
        Assert.Equal("high", inputs.SpeechVariety);
        Assert.NotEqual(Defaults.SpeechVariety, inputs.SpeechVariety);
        Assert.Equal(string.Empty, inputs.SpeechPrompt);
        Assert.Equal(Defaults.SpeechTone, inputs.SpeechTone);
        Assert.Equal(Defaults.SpeechVocalGender, inputs.SpeechVocalGender);
        Assert.Equal(Defaults.SpeechBackgroundMusic, inputs.SpeechBackgroundMusic);
        Assert.Equal(["speechTone", "speechVocalGender", "speechBackgroundMusic"], mapped.Marks.NotReturned);
        Assert.Empty(mapped.Marks.OutOfRange);
        AssertOnlyItsTab(mapped, VersionKind.Speech);
    }

    /// <summary>
    /// A Sounds clip (task <c>sound</c>) maps to a Sound Version with its description, Type (Loop), BPM
    /// (the setting, 120, never the measured average), and key and scale from the one <c>Am</c> (never
    /// the measured key); the model is not returned. Every option is off its default.
    /// </summary>
    [Fact]
    public void ASoundsClipMapsToASoundVersionWithItsSettings()
    {
        var clip = FixtureClip(Sounds);
        var mapped = Map(clip);
        var inputs = mapped.Inputs;

        Assert.Equal(VersionKind.Sound, inputs.Kind);
        Assert.Equal(Metadata(clip, "tags"), inputs.SoundDescription);
        Assert.Equal("loop", inputs.SoundType);
        Assert.Equal(120, inputs.SoundBpm);
        Assert.Equal("A", inputs.SoundKey);
        Assert.Equal("minor", inputs.SoundScale);
        Assert.NotEqual(Defaults.SoundDescription, inputs.SoundDescription);
        Assert.NotEqual(Defaults.SoundType, inputs.SoundType);
        Assert.NotEqual(Defaults.SoundBpm, inputs.SoundBpm);
        Assert.NotEqual(Defaults.SoundKey, inputs.SoundKey);
        Assert.NotEqual(Defaults.SoundScale, inputs.SoundScale);
        Assert.Equal(["soundsModel"], mapped.Marks.NotReturned);
        Assert.Null(inputs.SoundsModel);
        Assert.Null(mapped.Model);
        Assert.Empty(mapped.Marks.OutOfRange);
        AssertOnlyItsTab(mapped, VersionKind.Sound);

        // The Songs options of the clip are not read: its empty prompt is no lyric, its model no Song model.
        Assert.Equal(string.Empty, mapped.Lyrics);
        Assert.Equal(Defaults.Model, inputs.Model);
        Assert.Equal(Defaults.Title, inputs.Title);
    }

    /// <summary>Each Speech and Sounds option the story lists is read, or marked not returned.</summary>
    [Theory]
    [InlineData(SpeechAdvanced, "speech_prompt", "speech_script", "speech_tone", "speech_vocal_gender", "speech_background_music", "speech_variety")]
    [InlineData(Sounds, "sounds_model", "sound_description", "sound_type", "sound_bpm", "sound_key", "sound_scale")]
    public void EveryOptionOfTheTabIsReadOrMarkedNotReturned(string fixture, params string[] options)
    {
        var mapped = Map(FixtureClip(fixture));
        var tab = CreateFieldInventory.Embedded.Get(options[0]).Tab;

        Assert.Equal(
            options.Order(StringComparer.Ordinal),
            ClipInputMapper.ReadKeys.Where(key => CreateFieldInventory.Embedded.Get(key).Tab == tab).Order(StringComparer.Ordinal));
        Assert.All(options.Select(ApiName), name => Assert.True(
            mapped.Compared.ContainsKey(name) ^ mapped.Marks.NotReturned.Contains(name),
            $"{name} is not either compared or marked not returned."));
    }

    /// <summary>The two clips of one Create request have the same inputs, on each tab and form.</summary>
    [Theory]
    [InlineData(SpeechSimple)]
    [InlineData(SpeechAdvanced)]
    [InlineData(Sounds)]
    public void TwoClipsOfOneCreateRequestHaveTheSameInputs(string fixture)
    {
        Assert.True(ClipInputMapper.SameInputs(Map(FixtureClip(fixture, 0)), Map(FixtureClip(fixture, 1))));
    }

    /// <summary>
    /// The same-inputs rule as for Songs: a returned option that differs makes the inputs differ (BPM,
    /// Speech Variety, a script), while a difference only in an option Suno does not return (the
    /// rewritten tone), in what is measured (average BPM, key), or in line endings does not.
    /// </summary>
    [Fact]
    public void OnlyReturnedOptionsDecideWhetherTheInputsDiffer()
    {
        var sound = FixtureClip(Sounds);
        var measured = sound.DeepClone();
        measured["metadata"]!["avg_bpm"] = 99.5;
        measured["metadata"]!["key"] = "C_major";
        Assert.True(ClipInputMapper.SameInputs(Map(sound), Map(measured)));

        var faster = sound.DeepClone();
        faster["metadata"]!["sound_configs"]!["user_tempo"] = 121;
        Assert.False(ClipInputMapper.SameInputs(Map(sound), Map(faster)));

        var speech = FixtureClip(SpeechAdvanced);
        var retoned = speech.DeepClone();
        retoned["metadata"]!["tags"] = "a different rewrite of the tone";
        retoned["metadata"]!["prompt"] = Metadata(speech, "prompt").Replace("\n", "\r\n", StringComparison.Ordinal) + "  \r\n";
        Assert.True(ClipInputMapper.SameInputs(Map(speech), Map(retoned)));

        var varied = speech.DeepClone();
        varied["metadata"]!["control_sliders"]!["aug_creativity"] = 4;
        Assert.False(ClipInputMapper.SameInputs(Map(speech), Map(varied)));

        var rewritten = speech.DeepClone();
        rewritten["metadata"]!["prompt"] = "Other words";
        Assert.False(ClipInputMapper.SameInputs(Map(speech), Map(rewritten)));
    }

    /// <summary>
    /// A clip of one kind never has the same inputs as a Version of another: a Speech clip differs from a
    /// Song Version holding the same text, and a Sound clip from a Version with its options but kind Song.
    /// A clip equals the Version it maps to.
    /// </summary>
    [Fact]
    public void AClipDiffersFromAVersionOfAnotherKind()
    {
        foreach (var fixture in new[] { SpeechSimple, SpeechAdvanced, Sounds })
        {
            var mapped = Map(FixtureClip(fixture));
            Assert.False(ClipInputMapper.Differs(mapped, mapped.Lyrics, mapped.Styles, mapped.Inputs, mapped.Marks));
            Assert.True(ClipInputMapper.Differs(mapped, mapped.Lyrics, mapped.Styles, mapped.Inputs with { Kind = VersionKind.Song }, mapped.Marks));
        }

        var speech = Map(FixtureClip(SpeechAdvanced));
        Assert.True(ClipInputMapper.Differs(speech, speech.Lyrics, speech.Styles, speech.Inputs with { SpeechMode = CreationMode.Simple }, speech.Marks));
        Assert.False(ClipInputMapper.Differs(speech, speech.Lyrics, speech.Styles, speech.Inputs with { SpeechTone = "what the user wrote", Variety = "max" }, null));
    }

    /// <summary>
    /// The out-of-range rule as for Songs: a value outside n8Tracks' limits is kept and marked, an
    /// unknown one kept raw with the option at its default. A BPM of 400 is kept; a flat key (<c>Bb</c>,
    /// which the form spells as a sharp) is unknown for both key and scale; a script past its limit is
    /// kept whole; an unknown Speech Variety is kept raw.
    /// </summary>
    [Fact]
    public void ValuesOutsideTheLimitsAreKeptAndMarked()
    {
        var sound = FixtureClip(Sounds);
        sound["metadata"]!["sound_configs"]!["user_tempo"] = 400;
        sound["metadata"]!["sound_configs"]!["user_key"] = "Bb";
        sound["metadata"]!["sound_configs"]!["user_loop"] = "yes";
        var mappedSound = Map(sound);

        Assert.Equal(400, mappedSound.Inputs.SoundBpm);
        Assert.Equal(Defaults.SoundKey, mappedSound.Inputs.SoundKey);
        Assert.Equal(Defaults.SoundScale, mappedSound.Inputs.SoundScale);
        Assert.Equal(Defaults.SoundType, mappedSound.Inputs.SoundType);
        Assert.Equal(["soundType", "soundBpm", "soundKey", "soundScale"], mappedSound.Marks.OutOfRange);
        Assert.Equal("\"Bb\"", mappedSound.Marks.RawValues["soundKey"]);
        Assert.Equal("\"Bb\"", mappedSound.Marks.RawValues["soundScale"]);
        Assert.Equal("\"yes\"", mappedSound.Marks.RawValues["soundType"]);

        var speech = FixtureClip(SpeechAdvanced);
        var script = new string('s', 6000);
        speech["metadata"]!["prompt"] = script;
        speech["metadata"]!["control_sliders"]!["aug_creativity"] = 9;
        var mappedSpeech = Map(speech);

        Assert.Equal(script, mappedSpeech.Inputs.SpeechScript);
        Assert.Equal(Defaults.SpeechVariety, mappedSpeech.Inputs.SpeechVariety);
        Assert.Equal(["speechScript", "speechVariety"], mappedSpeech.Marks.OutOfRange);
        Assert.Equal("9", mappedSpeech.Marks.RawValues["speechVariety"]);
    }

    /// <summary>
    /// A Sound's key and scale as the inventory holds them: a sharp major key is the note with major; no
    /// key at all is Any with the scale unset; One-shot is <c>false</c>; Auto BPM (absent) is null.
    /// </summary>
    [Fact]
    public void ASoundsKeyScaleTypeAndBpmFollowTheInventory()
    {
        var sound = FixtureClip(Sounds);
        sound["metadata"]!["sound_configs"]!["user_key"] = "C#";
        sound["metadata"]!["sound_configs"]!["user_loop"] = false;
        var sharp = Map(sound).Inputs;
        Assert.Equal("C#", sharp.SoundKey);
        Assert.Equal("major", sharp.SoundScale);
        Assert.Equal("one_shot", sharp.SoundType);

        var configs = sound["metadata"]!["sound_configs"]!.AsObject();
        configs.Remove("user_key");
        configs.Remove("user_tempo");
        var any = Map(sound);
        Assert.Equal("any", any.Inputs.SoundKey);
        Assert.Null(any.Inputs.SoundScale);
        Assert.Null(any.Inputs.SoundBpm);
        Assert.Empty(any.Marks.OutOfRange);
    }

    /// <summary>
    /// Kind detection, by the markers the map names. Complement: no clip of any Songs fixture is ever a
    /// Speech or a Sound, and each is determined; the Speech and Sounds fixtures are what they are.
    /// </summary>
    [Fact]
    public void EachFixtureIsTheKindItsMarkersSay()
    {
        foreach (var fixture in SongFixtures)
        {
            var clips = JsonNode.Parse(Clips.Fixture(fixture))!["clips"]!.AsArray();
            Assert.NotEmpty(clips);
            Assert.All(clips, clip => Assert.Equal(new ClipKind(VersionKind.Song, Determined: true), ClipInputMapper.KindOf(Element(clip!))));
        }

        Assert.Equal(new ClipKind(VersionKind.Speech, Determined: true), ClipInputMapper.KindOf(Element(FixtureClip(SpeechSimple))));
        Assert.Equal(new ClipKind(VersionKind.Speech, Determined: true), ClipInputMapper.KindOf(Element(FixtureClip(SpeechAdvanced))));
        Assert.Equal(new ClipKind(VersionKind.Sound, Determined: true), ClipInputMapper.KindOf(Element(FixtureClip(Sounds))));
        Assert.Equal(new ClipKind(VersionKind.Sound, Determined: true), ClipInputMapper.KindOf(Element(FixtureClip("generate-v2-web.sounds.response.json"))));

        // is_speech false is no marker, and neither is a clip with no metadata at all.
        var notSpeech = FixtureClip("feed-v3.songs-advanced.response.json");
        notSpeech["metadata"]!["is_speech"] = false;
        Assert.Equal(new ClipKind(VersionKind.Song, Determined: true), ClipInputMapper.KindOf(Element(notSpeech)));
        Assert.Equal(new ClipKind(VersionKind.Song, Determined: true), ClipInputMapper.KindOf(Element(JsonNode.Parse(Clips.Minimal("bare"))!)));
    }

    /// <summary>
    /// A clip whose kind cannot be told (both markers, or a marker of another type) maps as a Song with
    /// whatever maps, and says so for the review's attention mark; the Songs options it carries are read.
    /// </summary>
    [Fact]
    public void AnUnrecognisedOrConflictingMarkerFallsBackToASongMarkedUnknown()
    {
        var both = FixtureClip(Sounds);
        both["metadata"]!["is_speech"] = true;
        var unrecognised = FixtureClip(SpeechAdvanced);
        unrecognised["metadata"]!["is_speech"] = "yes";
        var numericTask = FixtureClip("feed-v3.songs-advanced.response.json");
        numericTask["metadata"]!["task"] = 7;

        foreach (var clip in new[] { both, unrecognised, numericTask })
        {
            Assert.Equal(new ClipKind(VersionKind.Song, Determined: false), ClipInputMapper.KindOf(Element(clip)));
            var mapped = Map(clip);
            Assert.True(mapped.KindUnknown);
            Assert.Equal(VersionKind.Song, mapped.Inputs.Kind);
        }

        // Whatever maps as a Song still maps: the Speech clip's script comes in as its lyrics.
        Assert.Equal(Metadata(unrecognised, "prompt"), Map(unrecognised).Lyrics);
        Assert.Equal(Map(FixtureClip("feed-v3.songs-advanced.response.json")).Inputs, Map(numericTask).Inputs);
        Assert.False(Map(FixtureClip(Sounds)).KindUnknown);
    }

    /// <summary>
    /// A Speech clip with neither mode marker (a description prompt, but a task other than the Simple
    /// one) is Advanced when it has a script, and Simple when the script is absent or only whitespace.
    /// </summary>
    [Fact]
    public void ASpeechClipWithoutAModeMarkerIsAdvancedWhenItHasAScript()
    {
        var clip = FixtureClip(SpeechSimple);
        clip["metadata"]!["task"] = "cover";
        Assert.Equal(CreationMode.Advanced, Map(clip).Inputs.SpeechMode);

        clip["metadata"]!["prompt"] = " \n\t ";
        Assert.Equal(CreationMode.Simple, Map(clip).Inputs.SpeechMode);

        clip["metadata"]!.AsObject().Remove("prompt");
        Assert.Equal(CreationMode.Simple, Map(clip).Inputs.SpeechMode);
    }

    /// <summary>The map's own checks: a kind marker needs a path and a value, and a pattern exactly one group.</summary>
    [Fact]
    public void TheMapRefusesAMalformedMarkerOrPattern()
    {
        var map = JsonNode.Parse(ImportFieldMapText())!;
        Assert.Equal(
            [VersionKind.Speech, VersionKind.Sound],
            ImportFieldMap.Parse(map.ToJsonString()).KindMarkers.Select(static marker => marker.Kind));

        var noPath = map.DeepClone();
        noPath["kindMarkers"]!["speech"]!.AsObject().Remove("path");
        Assert.Throws<JsonException>(() => ImportFieldMap.Parse(noPath.ToJsonString()));

        var unknownKind = map.DeepClone();
        unknownKind["kindMarkers"]!["video"] = new JsonObject { ["path"] = "metadata.is_video", ["equals"] = true };
        Assert.Throws<JsonException>(() => ImportFieldMap.Parse(unknownKind.ToJsonString()));

        var noGroup = map.DeepClone();
        noGroup["fields"]!["sound_key"]!["pattern"] = "^[A-G]#?m?$";
        Assert.Throws<JsonException>(() => ImportFieldMap.Parse(noGroup.ToJsonString()));

        var noMarkers = map.DeepClone();
        noMarkers.AsObject().Remove("kindMarkers");
        Assert.Throws<JsonException>(() => ImportFieldMap.Parse(noMarkers.ToJsonString()));
    }

    /// <summary>A clip compared on its own tab only: the kind, its mode, and the options of its tab, and nothing of another.</summary>
    private static void AssertOnlyItsTab(MappedClipInputs mapped, VersionKind kind)
    {
        var tab = ClipInputMapper.TabOf(kind);
        Assert.All(
            mapped.Compared.Keys.Concat(mapped.Marks.NotReturned).Where(static key => key is not ("kind" or "songMode" or "speechMode")),
            key => Assert.Equal(tab, CreateFieldInventory.Embedded.Get(VersionInputRules.InventoryKey(key)!).Tab));
        Assert.Equal(kind == VersionKind.Speech, mapped.Compared.ContainsKey("speechMode"));
        Assert.False(mapped.Compared.ContainsKey("songMode"));
    }

    private static MappedClipInputs Map(JsonNode clip) => ClipInputMapper.Map(Element(clip), DefaultSunoModels.All);

    private static JsonElement Element(JsonNode clip) => JsonSerializer.SerializeToElement(clip);

    private static JsonNode FixtureClip(string name, int index = 0) => JsonNode.Parse(Clips.FixtureClip(name, index))!;

    private static string Metadata(JsonNode clip, string name) => clip["metadata"]![name]!.GetValue<string>();

    private static string ApiName(string inventoryKey) =>
        VersionInputRules.Keys.Single(key => VersionInputRules.InventoryKey(key) == inventoryKey);

    private static string ImportFieldMapText()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "suno-import-field-map.json");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("docs/suno-import-field-map.json is not above the test output.");
    }
}
