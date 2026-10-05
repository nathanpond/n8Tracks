using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Suno;

namespace n8Tracks.Api.Tests.Inventory;

/// <summary>
/// The coverage test: it walks every field of Suno's Create-screen inventory
/// (<c>docs/suno-create-field-inventory.json</c>, the copy embedded in the build), whatever its tab,
/// and fails when a field has no option stored on a Version, is not round-tripped by the API, or has a
/// limit, range, value list, or default that disagrees with the inventory. Fields deferred to a later
/// milestone are left out only by name, in the lists below, which are themselves asserted; a new field
/// of any type that is on neither list fails. The check reads the inventory and the API alone, so it
/// knows nothing of how n8Tracks stores an option: an option's API name is the inventory key in
/// camelCase, apart from the two renamed below.
/// </summary>
public sealed class InventoryCoverageTests
{
    /// <summary>Reference and file inputs, whose mechanics belong to M4 (spike TS-002).</summary>
    private static readonly string[] DeferredToM4 =
        ["simple_add_playlist", "simple_add_image", "simple_add_video", "audio", "voice", "inspiration", "workspace"];

    /// <summary>The Speech and Sounds fields, until their story (#113) stores them and empties this list.</summary>
    private static readonly string[] SpeechAndSoundsUntil113 =
    [
        "speech_prompt", "speech_script", "speech_tone", "speech_vocal_gender", "speech_background_music", "speech_variety",
        "sounds_model", "sound_description", "sound_type", "sound_bpm", "sound_key", "sound_scale",
    ];

    /// <summary>
    /// The Simple form's "add a section" fields, a two-value choice in the inventory (write new or use
    /// existing, both of which add the section), are held as whether the section is added; the section's
    /// text is the Version's lyrics or styles. With no default in the inventory, they start not added.
    /// </summary>
    private static readonly Dictionary<string, string> AddedSections = new(StringComparer.Ordinal)
    {
        ["simple_add_lyrics"] = "simpleLyricsAdded",
        ["simple_add_styles"] = "simpleStylesAdded",
    };

    /// <summary>Kept on the Version itself, beside <c>inputs</c>.</summary>
    private static readonly string[] TopLevel = ["lyrics", "styles"];

    /// <summary>The options that choose the form, which no inventory field describes.</summary>
    private static readonly string[] FormChoosers = ["kind", "songMode", "speechMode"];

    /// <summary>Suno's title starts as the Song's title, not as the inventory's empty default.</summary>
    private const string TitleKey = "title";

    private const string SongTitle = "Coverage";

    [Fact]
    public void TheExclusionListsNameOnlyFieldsTheInventoryHasAndDeferred()
    {
        var inventory = CreateFieldInventory.Embedded;

        Assert.Equal(
            ["simple_add_playlist", "simple_add_image", "simple_add_video", "audio", "voice", "inspiration", "workspace"],
            DeferredToM4);
        Assert.All(DeferredToM4, key => Assert.Contains(
            inventory.Get(key).Type,
            new[] { CreateField.ReferenceType, CreateField.FileType }));
        Assert.All(SpeechAndSoundsUntil113, key => Assert.Contains(inventory.Get(key).Tab, new[] { "speech", "sounds" }));
        Assert.Empty(DeferredToM4.Intersect(SpeechAndSoundsUntil113, StringComparer.Ordinal));

        // Every Speech and Sounds field is on the list, so #113 removes the list rather than trimming it.
        Assert.Equal(
            inventory.Fields.Where(static field => field.Tab != "songs").Select(static field => field.Key).Order(StringComparer.Ordinal),
            SpeechAndSoundsUntil113.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryInventoryFieldIsStoredRoundTrippedAndBoundedAsTheInventorySays()
    {
        var problems = await CheckAsync(CreateFieldInventory.Embedded, [.. DeferredToM4, .. SpeechAndSoundsUntil113]);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>It bites: a field the inventory gains, of any type, fails until n8Tracks stores it.</summary>
    [Theory]
    [InlineData("""{"key":"made_up_toggle","label":"Made up","tab":"songs","modes":["advanced"],"type":"toggle","default":false}""")]
    [InlineData("""{"key":"made_up_reference","label":"Made up","tab":"songs","modes":["simple"],"type":"reference"}""")]
    [InlineData("""{"key":"made_up_speech","label":"Made up","tab":"speech","modes":["advanced"],"type":"text","maxLength":10,"default":""}""")]
    public async Task AFieldTheInventoryGainsFailsTheCheck(string field)
    {
        var copy = JsonNode.Parse(CreateFieldInventory.Embedded.Json)!;
        copy["fields"]!.AsArray().Add(JsonNode.Parse(field));
        var key = JsonNode.Parse(field)!["key"]!.GetValue<string>();

        var problems = await CheckAsync(CreateFieldInventory.Parse(copy.ToJsonString()), [.. DeferredToM4, .. SpeechAndSoundsUntil113]);

        Assert.Contains(problems, problem => problem.StartsWith(key + ":", StringComparison.Ordinal));
    }

    /// <summary>It bites: a key taken off an exclusion list fails, since nothing stores it yet.</summary>
    [Theory]
    [InlineData("audio")]
    [InlineData("simple_add_image")]
    [InlineData("speech_script")]
    [InlineData("sound_bpm")]
    public async Task AKeyTakenOffAnExclusionListFailsTheCheck(string key)
    {
        var excluded = DeferredToM4.Concat(SpeechAndSoundsUntil113).Where(excludedKey => excludedKey != key).ToArray();

        var problems = await CheckAsync(CreateFieldInventory.Embedded, excluded);

        Assert.Contains(problems, problem => problem.StartsWith(key + ":", StringComparison.Ordinal));
    }

    /// <summary>It bites: a limit, range, list, or default that disagrees with the inventory fails.</summary>
    [Theory]
    [InlineData("weirdness", "max", "99")]
    [InlineData("duration_seconds", "min", "5")]
    [InlineData("simple_prompt", "maxLength", "1200")]
    [InlineData("variety", "values", """["off","normal","high","extra","max","ludicrous"]""")]
    [InlineData("variety", "default", "\"high\"")]
    [InlineData("max_mode", "default", "true")]
    [InlineData("lyrics", "maxLength", "4000")]
    public async Task AFieldThatDisagreesWithTheInventoryFailsTheCheck(string key, string property, string value)
    {
        var copy = JsonNode.Parse(CreateFieldInventory.Embedded.Json)!;
        copy["fields"]!.AsArray().Single(field => field!["key"]!.GetValue<string>() == key)![property] = JsonNode.Parse(value);

        var problems = await CheckAsync(CreateFieldInventory.Parse(copy.ToJsonString()), [.. DeferredToM4, .. SpeechAndSoundsUntil113]);

        Assert.Contains(problems, problem => problem.StartsWith(key + ":", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every way <paramref name="inventory"/> and the API disagree, each line starting with the field's
    /// key, or with the API's name for an option no field describes.
    /// </summary>
    private static async Task<List<string>> CheckAsync(CreateFieldInventory inventory, IReadOnlyCollection<string> excluded)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var problems = new List<string>();

        foreach (var field in inventory.Fields.Where(field => !excluded.Contains(field.Key, StringComparer.Ordinal)))
        {
            var version = new CoveredVersion(client, (await SongApi.CreateAsync(client, SongTitle)).GetProperty("currentVersion").GetProperty("id").GetGuid());
            await new FieldCheck(field, version, problems).RunAsync();
        }

        // The other way round: every stored option is a field of the inventory, or one that chooses the form.
        var any = new CoveredVersion(client, (await SongApi.CreateAsync(client, SongTitle)).GetProperty("currentVersion").GetProperty("id").GetGuid());
        var names = inventory.Fields.Select(static field => ApiName(field.Key)).ToHashSet(StringComparer.Ordinal);
        foreach (var option in (await any.ReadAsync()).GetProperty("inputs").EnumerateObject())
        {
            if (!names.Contains(option.Name) && !FormChoosers.Contains(option.Name, StringComparer.Ordinal))
            {
                problems.Add($"{option.Name}: stored, but no inventory field describes it.");
            }
        }

        return problems;
    }

    /// <summary>The one conversion from an inventory key to the API's name for its option: camelCase, apart from the renamed sections.</summary>
    private static string ApiName(string key)
    {
        if (AddedSections.TryGetValue(key, out var renamed))
        {
            return renamed;
        }

        var parts = key.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    /// <summary>The checks of one field against one fresh Version.</summary>
    private sealed class FieldCheck(CreateField field, CoveredVersion version, List<string> problems)
    {
        private readonly string name = ApiName(field.Key);
        private readonly bool topLevel = TopLevel.Contains(field.Key, StringComparer.Ordinal);

        public async Task RunAsync()
        {
            if (await StoredAsync() is not { } stored)
            {
                Fail($"no option is stored (expected {(topLevel ? "a top-level field" : "inputs")}.{name}).");
                return;
            }

            if (AddedSections.ContainsKey(field.Key))
            {
                await CheckSectionAsync(stored);
                return;
            }

            switch (field.Type)
            {
                case CreateField.TextType:
                    await CheckTextAsync(stored);
                    break;
                case CreateField.ChoiceType:
                    await CheckChoiceAsync(stored);
                    break;
                case CreateField.ToggleType:
                    CheckDefault(stored);
                    await AcceptsAsync("true", "false");
                    await RefusesAsync("\"yes\"", "1", "null");
                    break;
                case CreateField.RangeType or CreateField.NumberType:
                    await CheckNumberAsync(stored);
                    break;
                default:
                    Fail($"type '{field.Type}' has no check: a new kind of field needs one here.");
                    break;
            }
        }

        private async Task CheckSectionAsync(JsonElement stored)
        {
            if (stored.ValueKind != JsonValueKind.False)
            {
                Fail($"a section starts not added (false), but is {stored.GetRawText()}.");
            }

            await AcceptsAsync("true", "false");
            await RefusesAsync("\"write_new\"", "null");
        }

        private async Task CheckTextAsync(JsonElement stored)
        {
            var expected = field.Key == TitleKey ? SongTitle : field.HasDefault ? field.Default.GetString() : null;
            if (stored.ValueKind != JsonValueKind.String || stored.GetString() != expected)
            {
                Fail($"starts as {stored.GetRawText()}, not {JsonSerializer.Serialize(expected)}.");
            }

            if (field.MaxLength is not { } limit)
            {
                Fail("the inventory gives no limit.");
                return;
            }

            await AcceptsAsync(JsonSerializer.Serialize(new string('x', limit)), "\"\"", "\"Two\\nlines\"");
            await RefusesAsync(JsonSerializer.Serialize(new string('x', limit + 1)), "5", "null");
        }

        private async Task CheckChoiceAsync(JsonElement stored)
        {
            CheckDefault(stored);
            if (field.Values is not { Count: > 0 } values)
            {
                Fail("the inventory gives no values.");
                return;
            }

            await AcceptsAsync([.. values.Select(static value => JsonSerializer.Serialize(value))]);
            await RefusesAsync("\"not-one-of-them\"", "3");
            if (field.DefaultsToNull)
            {
                await AcceptsAsync("null");
            }
            else
            {
                await RefusesAsync("null");
            }
        }

        private async Task CheckNumberAsync(JsonElement stored)
        {
            CheckDefault(stored);
            if (field is not { Min: { } minimum, Max: { } maximum })
            {
                Fail("the inventory gives no range.");
                return;
            }

            await AcceptsAsync(Number(minimum), Number(maximum), Number((minimum + maximum) / 2));
            await RefusesAsync(Number(minimum - 1), Number(maximum + 1), "1.5", JsonSerializer.Serialize(Number(minimum)));
            if (field.DefaultsToNull)
            {
                await AcceptsAsync("null");
            }
            else
            {
                await RefusesAsync("null");
            }
        }

        private void CheckDefault(JsonElement stored)
        {
            if (!field.HasDefault)
            {
                Fail("the inventory gives no default.");
            }
            else if (!JsonElement.DeepEquals(stored, field.Default))
            {
                Fail($"starts as {stored.GetRawText()}, not the inventory's default {field.Default.GetRawText()}.");
            }
        }

        /// <summary>Each value is stored and read back as sent.</summary>
        private async Task AcceptsAsync(params string[] values)
        {
            foreach (var value in values)
            {
                using var response = await version.PatchAsync(Body(value));
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Fail($"{value} is refused ({(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}).");
                    continue;
                }

                var expected = JsonDocument.Parse(value).RootElement;
                var read = await StoredAsync();
                if (read is not { } stored || !JsonElement.DeepEquals(stored, expected))
                {
                    Fail($"{value} is not read back as sent ({read?.GetRawText()}).");
                }
            }
        }

        /// <summary>Each value is refused with a field error naming the option, and nothing changes.</summary>
        private async Task RefusesAsync(params string[] values)
        {
            foreach (var value in values)
            {
                var before = (await version.ReadAsync()).GetRawText();
                using var response = await version.PatchAsync(Body(value));
                if (response.StatusCode != HttpStatusCode.UnprocessableEntity)
                {
                    Fail($"{value} is not refused ({(int)response.StatusCode}).");
                    continue;
                }

                var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
                var errorKey = topLevel ? name : "inputs." + name;
                if (!problem.GetProperty("errors").TryGetProperty(errorKey, out _))
                {
                    Fail($"{value} is refused without an error for {errorKey} ({problem.GetProperty("errors").GetRawText()}).");
                }

                if ((await version.ReadAsync()).GetRawText() != before)
                {
                    Fail($"{value} was refused but changed the Version.");
                }
            }
        }

        private string Body(string value) => topLevel ? $$"""{"{{name}}":{{value}}}""" : $$$"""{"inputs":{"{{{name}}}":{{{value}}}}}""";

        private async Task<JsonElement?> StoredAsync()
        {
            var read = await version.ReadAsync();
            var holder = topLevel ? read : read.GetProperty("inputs");
            return holder.TryGetProperty(name, out var value) ? value : null;
        }

        private void Fail(string problem) => problems.Add($"{field.Key}: {problem}");

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A Version the check reads and edits, always at its current revision.</summary>
    private sealed class CoveredVersion(HttpClient client, Guid id)
    {
        private readonly Uri uri = new($"/api/v1/versions/{id}", UriKind.Relative);

        public async Task<JsonElement> ReadAsync()
        {
            using var response = await client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await SetupApi.JsonAsync(response);
        }

        public async Task<HttpResponseMessage> PatchAsync(string json)
        {
            var revision = (await ReadAsync()).GetProperty("revision").GetInt32();
            using var request = new HttpRequestMessage(HttpMethod.Patch, uri)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
            return await client.SendAsync(request);
        }
    }
}
