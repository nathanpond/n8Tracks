using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// The rules of a Version's options, read from the embedded inventory: defaults, what each option
/// accepts, the merge, and which options apply to a kind and mode.
/// </summary>
public sealed class VersionInputRulesTests
{
    private static readonly CreateFieldInventory Inventory = CreateFieldInventory.Embedded;

    private static readonly string[] Models = ["v6", "v6-wild", "v6-mini"];

    [Fact]
    public void TheEmbeddedInventoryIsTheCommittedFile()
    {
        var committed = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "suno-create-field-inventory.json"));

        Assert.Equal(committed, Inventory.Json);
        Assert.Equal(35, Inventory.Fields.Count);
        Assert.Equal(1000, Inventory.Get("speech_prompt").MaxLength);
        Assert.Equal(100, Inventory.Get("weirdness").Max);
        Assert.True(Inventory.Get("vocal_gender").DefaultsToNull);
        Assert.False(Inventory.Get("simple_add_lyrics").HasDefault);
        Assert.Null(Inventory.Find("not_a_field"));
    }

    [Fact]
    public void DefaultsAreTheInventorysWithAnAdvancedSongAndTheSongsTitle()
    {
        var defaults = VersionInputRules.Defaults(Inventory, "Pack");

        Assert.Equal(
            new VersionInputs(
                VersionKind.Song, CreationMode.Advanced, CreationMode.Advanced, null, "", false, false, "", null, "auto", 180, false, 50, 50, "normal", false, "Pack",
                "", "", "", null, true, "normal", null, "", "one_shot", null, "any", null),
            defaults);

        // Each default with one is the inventory's, so a re-captured default changes it.
        var copy = JsonNode.Parse(Inventory.Json)!;
        copy["fields"]!.AsArray().Single(static field => field!["key"]!.GetValue<string>() == "weirdness")!["default"] = 30;
        Assert.Equal(30, VersionInputRules.Defaults(CreateFieldInventory.Parse(copy.ToJsonString()), "Pack").Weirdness);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(300, 100)]
    [InlineData(5, 5)]
    public void SunosTitleIsTheSongsCutToItsLimit(int length, int expected)
    {
        Assert.Equal(expected, VersionInputRules.Defaults(Inventory, new string('t', length)).Title.Length);
    }

    [Fact]
    public void ACutNeverSplitsASurrogatePair()
    {
        Assert.Equal("ab", VersionInputRules.Cut("ab🎸", 3));
        Assert.Equal("ab🎸", VersionInputRules.Cut("ab🎸", 4));
        Assert.Equal("ab🎸", VersionInputRules.Cut("ab🎸c", 4));
    }

    [Fact]
    public void ValuesInsideTheInventorysBoundsAreValidAndOthersAreErrorsNamingTheOption()
    {
        Assert.Empty(Errors("""{"weirdness":0,"styleInfluence":100,"variety":"max","vocalGender":null,"model":"v6-mini","kind":"sound","songMode":"simple","maxMode":true,"title":""}"""));

        var errors = Errors("""{"weirdness":101,"variety":"loud","model":"v5","unknown":1,"personalize":0}""");

        Assert.Equal(
            ["inputs.model", "inputs.personalize", "inputs.unknown", "inputs.variety", "inputs.weirdness"],
            errors.Keys.Order(StringComparer.Ordinal));
        Assert.StartsWith("Weirdness", errors["inputs.weirdness"][0], StringComparison.Ordinal);
        Assert.StartsWith("Personalize (My Taste)", errors["inputs.personalize"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheModelIsCheckedAgainstTheModelListNotTheInventory()
    {
        Assert.Empty(VersionInputRules.Errors(Inventory, ["v7"], Sent("""{"model":"v7"}""")));
        Assert.NotEmpty(VersionInputRules.Errors(Inventory, ["v7"], Sent("""{"model":"v6"}""")));
    }

    [Fact]
    public void AnEditMergesKeyByKeyAndNormalisesLineEndings()
    {
        var current = VersionInputRules.Defaults(Inventory, "Pack");

        var merged = VersionInputRules.Apply(current, Sent("""{"weirdness":80,"simplePrompt":"a\r\nb\rc","vocalGender":"female","kind":"speech"}"""));

        Assert.Equal(current with { Weirdness = 80, SimplePrompt = "a\nb\nc", VocalGender = "female", Kind = VersionKind.Speech }, merged);
        Assert.Same(current, VersionInputRules.Apply(current, Sent("{}")));
    }

    [Fact]
    public void EffectiveInputsHoldOnlyWhatAppliesToTheKindAndMode()
    {
        var advanced = VersionInputRules.Defaults(Inventory, "Pack") with { SimplePrompt = "prompt", DurationSeconds = 200 };

        var effective = VersionInputRules.Effective(Inventory, advanced, "words", "punk");
        Assert.Equal("words", effective["lyrics"]!.GetValue<string>());
        Assert.False(effective.ContainsKey("simplePrompt"));
        Assert.False(effective.ContainsKey("durationSeconds"));
        Assert.Equal(200, VersionInputRules.Effective(Inventory, advanced with { DurationMode = "custom" }, "", "")["durationSeconds"]!.GetValue<int>());

        // Simple: no Advanced-only option; lyrics and styles only when their section is added.
        var simple = advanced with { SongMode = CreationMode.Simple };
        var simpleEffective = VersionInputRules.Effective(Inventory, simple, "words", "punk");
        Assert.Equal(["kind", "songMode", "model", "simplePrompt", "simpleLyricsAdded", "simpleStylesAdded"], simpleEffective.Select(static option => option.Key));
        Assert.Equal(
            ["kind", "songMode", "model", "simplePrompt", "simpleLyricsAdded", "simpleStylesAdded", "styles"],
            VersionInputRules.Effective(Inventory, simple with { SimpleStylesAdded = true }, "words", "punk").Select(static option => option.Key));

        // Speech and Sound: no Song option, and no lyrics or styles.
        Assert.Equal(
            """{"kind":"speech","speechMode":"advanced","speechScript":"","speechTone":"","speechVocalGender":null,"speechBackgroundMusic":true,"speechVariety":"normal"}""",
            VersionInputRules.Effective(Inventory, advanced with { Kind = VersionKind.Speech }, "words", "punk").ToJsonString());
        Assert.Equal(
            """{"kind":"sound","soundsModel":null,"soundDescription":"","soundType":"one_shot","soundBpm":null,"soundKey":"any"}""",
            VersionInputRules.Effective(Inventory, advanced with { Kind = VersionKind.Sound }, "words", "punk").ToJsonString());
    }

    [Fact]
    public void ASpeechInSimpleModeSendsOnlyItsPromptAndInAdvancedModeOnlyItsAdvancedOptions()
    {
        var speech = VersionInputRules.Defaults(Inventory, "Pack") with
        {
            Kind = VersionKind.Speech,
            SpeechPrompt = "A narrator",
            SpeechScript = "Once upon a time",
            SpeechVocalGender = "female",
            VocalGender = "male",
            Variety = "max",
        };

        Assert.Equal(
            """{"kind":"speech","speechMode":"simple","speechPrompt":"A narrator"}""",
            VersionInputRules.Effective(Inventory, speech with { SpeechMode = CreationMode.Simple }, "", "").ToJsonString());

        // A Speech's Vocal Gender and Variety are its own, not the Song's.
        var advanced = VersionInputRules.Effective(Inventory, speech, "", "");
        Assert.Equal("female", advanced["speechVocalGender"]!.GetValue<string>());
        Assert.Equal("normal", advanced["speechVariety"]!.GetValue<string>());
        Assert.False(advanced.ContainsKey("vocalGender"));
        Assert.False(advanced.ContainsKey("speechPrompt"));
    }

    [Fact]
    public void ASoundsScaleIsSentOnlyWithAKeyOtherThanAny()
    {
        var sound = VersionInputRules.Defaults(Inventory, "Pack") with
        {
            Kind = VersionKind.Sound,
            SoundKey = "A",
            SoundScale = "minor",
            SoundBpm = 120,
            SoundType = "loop",
            SoundsModel = "v6-mini",
            Model = "v6",
        };

        Assert.Equal(
            """{"kind":"sound","soundsModel":"v6-mini","soundDescription":"","soundType":"loop","soundBpm":120,"soundKey":"A","soundScale":"minor"}""",
            VersionInputRules.Effective(Inventory, sound, "", "").ToJsonString());

        // Complement: with the key back to Any, the stored scale is kept but not sent.
        var any = sound with { SoundKey = "any" };
        Assert.False(VersionInputRules.Effective(Inventory, any, "", "").ContainsKey("soundScale"));
        Assert.Equal("minor", any.SoundScale);

        // A key with no scale sends the scale as none.
        Assert.Null(VersionInputRules.Effective(Inventory, sound with { SoundScale = null }, "", "")["soundScale"]);
    }

    [Fact]
    public void SpeechAndSoundValuesAreCheckedAgainstTheInventory()
    {
        Assert.Empty(Errors("""{"soundBpm":1,"soundKey":"C#","soundScale":"major","soundType":"loop","soundsModel":"v6","speechVariety":"off","speechVocalGender":null,"speechBackgroundMusic":false}"""));
        Assert.Empty(Errors("""{"soundBpm":300,"soundScale":null,"soundsModel":null}"""));
        Assert.Empty(Errors("""{"soundBpm":null}"""));

        var errors = Errors("""{"soundBpm":0,"soundKey":"H","soundType":"drone","soundScale":"dorian","speechVariety":"loud","speechBackgroundMusic":null,"soundsModel":"v5"}""");

        Assert.Equal(
            ["inputs.soundBpm", "inputs.soundKey", "inputs.soundScale", "inputs.soundType", "inputs.soundsModel", "inputs.speechBackgroundMusic", "inputs.speechVariety"],
            errors.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("BPM is a whole number from 1 to 300, or null for none.", errors["inputs.soundBpm"][0]);
        Assert.NotEmpty(Errors("""{"soundBpm":301}"""));
        Assert.NotEmpty(Errors("""{"soundBpm":120.5}"""));
        Assert.NotEmpty(Errors("""{"soundBpm":"120"}"""));
        Assert.NotEmpty(Errors($$$"""{"speechScript":"{{{new string('x', 5_001)}}}"}"""));
        Assert.Empty(Errors($$$"""{"speechScript":"{{{new string('x', 5_000)}}}"}"""));
        Assert.NotEmpty(Errors($$$"""{"speechTone":"{{{new string('x', 1_001)}}}"}"""));
        Assert.NotEmpty(Errors($$$"""{"speechPrompt":"{{{new string('x', 1_001)}}}"}"""));
        Assert.NotEmpty(Errors($$$"""{"soundDescription":"{{{new string('x', 501)}}}"}"""));

        // A Sound's model, like a Song's, is checked against the model list.
        Assert.Empty(VersionInputRules.Errors(Inventory, ["v7"], Sent("""{"soundsModel":"v7"}""")));
    }

    [Fact]
    public void TheJsonFormIsEveryKeyInCamelCaseAndReadsBackTheSame()
    {
        var inputs = VersionInputRules.Defaults(Inventory, "Pack") with { Kind = VersionKind.Sound, SongMode = CreationMode.Simple };

        var json = VersionInputRules.ToJson(inputs);

        Assert.Equal(VersionInputRules.Keys, json.Select(static option => option.Key));
        Assert.Equal("sound", json["kind"]!.GetValue<string>());
        Assert.Equal(inputs, VersionInputRules.FromJson(json));

        // A document missing an option is refused, not read with a made-up value.
        json.Remove("weirdness");
        Assert.ThrowsAny<JsonException>(() => VersionInputRules.FromJson(json));
    }

    [Fact]
    public void TheOptionsThatTakeADefaultAreTheFormChoosersAndEveryChoiceToggleRangeAndNumber()
    {
        // Read from the inventory: each stored option whose field is not text, a reference, or one
        // of the Simple form's sections takes a default, and nothing else does.
        var expected = VersionInputRules.Keys
            .Where(static key => VersionInputRules.InventoryKey(key) is not { } sunoKey
                || (Inventory.Get(sunoKey).Type is CreateField.ChoiceType or CreateField.ToggleType or CreateField.RangeType or CreateField.NumberType
                    && sunoKey is not ("simple_add_lyrics" or "simple_add_styles")))
            .ToList();

        Assert.Equal(expected, VersionInputRules.DefaultableKeys);
        Assert.Contains("model", VersionInputRules.DefaultableKeys);
        Assert.Contains("soundsModel", VersionInputRules.DefaultableKeys);
        Assert.Contains("speechBackgroundMusic", VersionInputRules.DefaultableKeys);
        Assert.All(
            ["simplePrompt", "excludeStyles", "title", "speechPrompt", "speechScript", "speechTone", "soundDescription", "simpleLyricsAdded", "simpleStylesAdded"],
            static key => Assert.DoesNotContain(key, VersionInputRules.DefaultableKeys));

        Assert.True(VersionInputRules.IsModel("model"));
        Assert.True(VersionInputRules.IsModel("soundsModel"));
        Assert.False(VersionInputRules.IsModel("variety"));
        Assert.False(VersionInputRules.IsModel("kind"));
    }

    private static Dictionary<string, string[]> Errors(string json) => VersionInputRules.Errors(Inventory, Models, Sent(json));

    private static Dictionary<string, JsonElement> Sent(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(static option => option.Name, static option => option.Value.Clone(), StringComparer.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "n8Tracks.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}
