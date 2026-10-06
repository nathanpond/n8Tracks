using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Inventory;

/// <summary>
/// The coverage test: it walks every field of Suno's Create-screen inventory
/// (<c>docs/suno-create-field-inventory.json</c>, the copy embedded in the build), whatever its tab,
/// and fails when a field has no option stored on a Version, is not round-tripped by the API, or has a
/// limit, range, value list, or default that disagrees with the inventory. Fields deferred to a later
/// milestone are left out only by name, in the list below, which is itself asserted; a new field
/// of any type that is not on it fails. The check reads the inventory and the API alone, so it
/// knows nothing of how n8Tracks stores an option: an option's API name is the inventory key in
/// camelCase, apart from the two renamed below. The reference and file fields (#122) are not options
/// but parts of a Version's lineage: each is stored under the lineage key the declared mapping
/// (<see cref="VersionLineageInputs.InventoryFields"/>) names, and checked by round-tripping a value of
/// that field and refusing a wrong one there.
/// </summary>
public sealed class InventoryCoverageTests
{
    /// <summary>
    /// The fields stored on the Song rather than its Version, each with the Song PATCH field that sets
    /// it: the workspace a result is saved to is the Song's association with a Suno workspace (#129).
    /// </summary>
    private static readonly Dictionary<string, string> SongAssociations = new(StringComparer.Ordinal)
    {
        ["workspace"] = SongService.SunoWorkspaceIdField,
    };

    /// <summary>
    /// For each reference or file field, a value of it as its lineage key takes one (sent in Simple mode
    /// when the field is Simple-only), what reading it back must contain, and a value its rules refuse.
    /// </summary>
    private static readonly Dictionary<string, (string Accepted, string ReadBack, string Refused)> LineageSamples = new(StringComparer.Ordinal)
    {
        ["audio"] = ($$$"""[{"typeId":"{{{SystemRelationshipTypes.Cover.Id}}}","external":{"sunoId":"coverage-audio"}}]""", "coverage-audio", $$$"""[{"typeId":"{{{SystemRelationshipTypes.Cover.Id}}}"}]"""),
        ["inspiration"] = ("""{"sources":[{"external":{"sunoId":"coverage-inspo"}}]}""", "coverage-inspo", """{"sources":[{"external":{"sunoId":"a"}},{"external":{"sunoId":"b"}},{"external":{"sunoId":"c"}},{"external":{"sunoId":"d"}},{"external":{"sunoId":"e"}}]}"""),
        ["simple_add_playlist"] = ("""{"playlist":{"sunoPlaylistId":"coverage-playlist","name":"Coverage","clipIds":["one"]}}""", "coverage-playlist", """{"playlist":{"sunoPlaylistId":"has space"}}"""),
        ["voice"] = ("""{"personaId":"coverage-persona","name":"Coverage"}""", "coverage-persona", """[{"personaId":"a"},{"personaId":"b"}]"""),
        ["simple_add_image"] = ("""[{"kind":"image","description":"coverage image"}]""", "coverage image", $$$"""[{"kind":"image","description":"{{{new string('x', 501)}}}"}]"""),
        ["simple_add_video"] = ("""[{"kind":"video","description":"coverage video"}]""", "coverage video", """[{"kind":"video","description":" "}]"""),
    };

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

    /// <summary>
    /// The model fields start as the first model the model list offers (#114), not as the inventory's
    /// null, which records only that Suno's form remembers the last choice.
    /// </summary>
    private static readonly string[] ModelKeys = ["model", "sounds_model"];

    [Fact]
    public void TheWorkspaceIsTheSongsAssociationAndEveryReferenceFieldIsMapped()
    {
        var inventory = CreateFieldInventory.Embedded;

        // The workspace is no longer left out (#129): it maps to the Song's association.
        Assert.Equal(["workspace"], SongAssociations.Keys);
        Assert.Equal(CreateField.ReferenceType, inventory.Get("workspace").Type);
        Assert.Equal("sunoWorkspaceId", SongAssociations["workspace"]);

        // The six reference and file fields #111 left out are each stored by a part of the lineage (#122).
        Assert.Equal(
            ["audio", "inspiration", "simple_add_image", "simple_add_playlist", "simple_add_video", "voice"],
            VersionLineageInputs.InventoryFields.Keys.Order(StringComparer.Ordinal));
        Assert.All(VersionLineageInputs.InventoryFields.Keys, key => Assert.Contains(
            inventory.Get(key).Type,
            new[] { CreateField.ReferenceType, CreateField.FileType }));
        Assert.All(VersionLineageInputs.InventoryFields.Values, value => Assert.Contains(value, VersionLineageInputs.Keys));
        Assert.Equal(VersionLineageInputs.InventoryFields.Keys.Order(StringComparer.Ordinal), LineageSamples.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryInventoryFieldIsStoredRoundTrippedAndBoundedAsTheInventorySays()
    {
        var problems = await CheckAsync(CreateFieldInventory.Embedded, SongAssociations, VersionLineageInputs.InventoryFields);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>It bites: a reference or file field whose mapping is taken away fails, since no option stores it.</summary>
    [Theory]
    [InlineData("audio")]
    [InlineData("voice")]
    [InlineData("inspiration")]
    [InlineData("simple_add_playlist")]
    [InlineData("simple_add_image")]
    [InlineData("simple_add_video")]
    public async Task AFieldWhoseMappingIsDeletedFailsTheCheck(string key)
    {
        var mapping = VersionLineageInputs.InventoryFields.Where(pair => pair.Key != key).ToDictionary(StringComparer.Ordinal);

        var problems = await CheckAsync(CreateFieldInventory.Embedded, SongAssociations, mapping);

        Assert.Contains(problems, problem => problem.StartsWith(key + ":", StringComparison.Ordinal));
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

        var problems = await CheckAsync(CreateFieldInventory.Parse(copy.ToJsonString()), SongAssociations, VersionLineageInputs.InventoryFields);

        Assert.Contains(problems, problem => problem.StartsWith(key + ":", StringComparison.Ordinal));
    }

    /// <summary>
    /// It bites: the workspace without its mapping to the Song's association fails, since no Version
    /// option stores it; and with a mapping to a Song field that does not store it, it fails too.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("concept")]
    public async Task TheWorkspaceWithoutItsSongAssociationFailsTheCheck(string? field)
    {
        const string key = "workspace";
        var associations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (field is not null)
        {
            associations[key] = field;
        }

        var problems = await CheckAsync(CreateFieldInventory.Embedded, associations, VersionLineageInputs.InventoryFields);

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
    [InlineData("speech_script", "maxLength", "4000")]
    [InlineData("speech_background_music", "default", "false")]
    [InlineData("sound_bpm", "max", "200")]
    [InlineData("sound_key", "values", """["any","C","H"]""")]
    [InlineData("sound_type", "default", "\"loop\"")]
    public async Task AFieldThatDisagreesWithTheInventoryFailsTheCheck(string key, string property, string value)
    {
        var copy = JsonNode.Parse(CreateFieldInventory.Embedded.Json)!;
        copy["fields"]!.AsArray().Single(field => field!["key"]!.GetValue<string>() == key)![property] = JsonNode.Parse(value);

        var problems = await CheckAsync(CreateFieldInventory.Parse(copy.ToJsonString()), SongAssociations, VersionLineageInputs.InventoryFields);

        Assert.Contains(problems, problem => problem.StartsWith(key + ":", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every way <paramref name="inventory"/> and the API disagree, each line starting with the field's
    /// key, or with the API's name for an option no field describes.
    /// </summary>
    private static async Task<List<string>> CheckAsync(CreateFieldInventory inventory, IReadOnlyDictionary<string, string> songAssociations, IReadOnlyDictionary<string, string> lineage)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var problems = new List<string>();

        foreach (var field in inventory.Fields)
        {
            var song = await SongApi.CreateAsync(client, SongTitle);
            var version = new CoveredVersion(client, song.GetProperty("currentVersion").GetProperty("id").GetGuid());
            if (songAssociations.TryGetValue(field.Key, out var songField))
            {
                await CheckSongAssociationAsync(factory, client, field, songField, song.GetProperty("id").GetString()!, version, problems);
            }
            else if (lineage.TryGetValue(field.Key, out var lineageKey))
            {
                await CheckLineageAsync(field, lineageKey, version, problems);
            }
            else
            {
                await new FieldCheck(field, version, problems).RunAsync();
            }
        }

        // The other way round: every stored option is a field of the inventory, or one that chooses the
        // form, and every lineage key stores a field of the mapping.
        var any = new CoveredVersion(client, (await SongApi.CreateAsync(client, SongTitle)).GetProperty("currentVersion").GetProperty("id").GetGuid());
        var names = inventory.Fields.Select(static field => ApiName(field.Key)).ToHashSet(StringComparer.Ordinal);
        foreach (var option in (await any.ReadAsync()).GetProperty("inputs").EnumerateObject())
        {
            if (!names.Contains(option.Name) && !FormChoosers.Contains(option.Name, StringComparer.Ordinal) && !lineage.Values.Contains(option.Name, StringComparer.Ordinal))
            {
                problems.Add($"{option.Name}: stored, but no inventory field describes it.");
            }
        }

        return problems;
    }

    /// <summary>
    /// A reference or file field stored by a part of the lineage: the part is read back under its key
    /// of <c>inputs</c>, a value of the field is stored and read back, and a wrong one is refused with
    /// a field error under that key and a rule named, changing nothing. A field the inventory offers in
    /// Simple mode only is sent in Simple mode.
    /// </summary>
    private static async Task CheckLineageAsync(CreateField field, string lineageKey, CoveredVersion version, List<string> problems)
    {
        void Fail(string problem) => problems.Add($"{field.Key}: {problem}");

        if (!(await version.ReadAsync()).GetProperty("inputs").TryGetProperty(lineageKey, out _))
        {
            Fail($"no option is stored (expected inputs.{lineageKey}).");
            return;
        }

        if (!LineageSamples.TryGetValue(field.Key, out var sample))
        {
            Fail("no sample value: add one to the lineage samples.");
            return;
        }

        if (field.Modes.SequenceEqual(["simple"]))
        {
            using var simple = await version.PatchAsync("""{"inputs":{"songMode":"simple"}}""");
            Assert.Equal(HttpStatusCode.OK, simple.StatusCode);
        }

        using (var accepted = await version.PatchAsync($$$"""{"inputs":{"{{{lineageKey}}}":{{{sample.Accepted}}}}}"""))
        {
            if (accepted.StatusCode != HttpStatusCode.OK)
            {
                Fail($"{sample.Accepted} is refused ({(int)accepted.StatusCode}: {await accepted.Content.ReadAsStringAsync()}).");
                return;
            }
        }

        var stored = (await version.ReadAsync()).GetProperty("inputs").GetProperty(lineageKey).GetRawText();
        if (!stored.Contains(sample.ReadBack, StringComparison.Ordinal))
        {
            Fail($"{sample.Accepted} is not read back ({stored}).");
        }

        var before = (await version.ReadAsync()).GetRawText();
        using var refused = await version.PatchAsync($$$"""{"inputs":{"{{{lineageKey}}}":{{{sample.Refused}}}}}""");
        if (refused.StatusCode != HttpStatusCode.UnprocessableEntity)
        {
            Fail($"{sample.Refused} is not refused ({(int)refused.StatusCode}).");
            return;
        }

        var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        var prefix = "inputs." + lineageKey;
        if (!problem.GetProperty("errors").EnumerateObject().Any(error => error.Name.StartsWith(prefix, StringComparison.Ordinal))
            || !problem.TryGetProperty("rules", out var rules)
            || !rules.EnumerateObject().Any(rule => rule.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            Fail($"{sample.Refused} is refused without a rule named under {prefix} ({problem.GetRawText()}).");
        }

        if ((await version.ReadAsync()).GetRawText() != before)
        {
            Fail($"{sample.Refused} was refused but changed the Version.");
        }
    }

    /// <summary>
    /// A field stored on the Song (#129): the workspace. A workspace is reported as the extension
    /// reports one (a <c>suno.sync</c> token), the Song's <paramref name="songField"/> set to its Suno ID
    /// is read back on the Song (<c>sunoWorkspace</c>) and in the Version's <c>effectiveInputs</c> under
    /// the field's key, a workspace that does not exist is refused with a field error changing nothing,
    /// and null clears it.
    /// </summary>
    private static async Task CheckSongAssociationAsync(N8TracksApiFactory factory, HttpClient client, CreateField field, string songField, string songId, CoveredVersion version, List<string> problems)
    {
        void Fail(string problem) => problems.Add($"{field.Key}: {problem}");

        async Task<JsonElement> SongAsync() => await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(songId)));

        if (!(await SongAsync()).TryGetProperty("sunoWorkspace", out _))
        {
            Fail("no association is stored on the Song (expected sunoWorkspace).");
            return;
        }

        var sunoId = "coverage-" + Guid.NewGuid().ToString("N");
        await Suno.SunoWorkspaceApi.ReportAsync(client, await Suno.SunoWorkspaceApi.ExtensionTokenAsync(factory), complete: false, Suno.SunoWorkspaceApi.Project(sunoId, "Coverage"));

        async Task<HttpResponseMessage> PatchAsync(JsonNode? value) => await SongApi.PatchAsync(
            client,
            songId,
            SongApi.Quoted((await SongAsync()).GetProperty("revision").GetInt32()),
            new JsonObject { [songField] = value }.ToJsonString());

        using (var accepted = await PatchAsync(sunoId))
        {
            if (accepted.StatusCode != HttpStatusCode.OK)
            {
                Fail($"the Song's {songField} refuses a reported workspace ({(int)accepted.StatusCode}: {await accepted.Content.ReadAsStringAsync()}).");
                return;
            }
        }

        if ((await SongAsync()).GetProperty("sunoWorkspace") is not { ValueKind: JsonValueKind.Object } stored || stored.GetProperty("id").GetString() != sunoId)
        {
            Fail($"{songField} is not read back as the Song's sunoWorkspace.");
        }

        if (!(await version.ReadAsync()).GetProperty("effectiveInputs").TryGetProperty(field.Key, out var effective)
            || effective.ValueKind != JsonValueKind.Object
            || effective.GetProperty("id").GetString() != sunoId)
        {
            Fail($"the Version's effectiveInputs does not report the Song's workspace under {field.Key}.");
        }

        var before = (await SongAsync()).GetRawText();
        using (var refused = await PatchAsync("coverage-unknown"))
        {
            if (refused.StatusCode != HttpStatusCode.UnprocessableEntity
                || !(await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode)).GetProperty("errors").TryGetProperty(songField, out _))
            {
                Fail($"a workspace that does not exist is not refused with an error for {songField}.");
            }
        }

        if ((await SongAsync()).GetRawText() != before)
        {
            Fail("a workspace that does not exist was refused but changed the Song.");
        }

        using var cleared = await PatchAsync(null);
        if (cleared.StatusCode != HttpStatusCode.OK || (await SongAsync()).GetProperty("sunoWorkspace").ValueKind != JsonValueKind.Null)
        {
            Fail($"null in {songField} does not clear the association.");
        }
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
            if (ModelKeys.Contains(field.Key))
            {
                if (stored.GetString() != DefaultSunoModels.All[0].Name)
                {
                    Fail($"starts as {stored.GetRawText()}, not the first model the list offers.");
                }
            }
            else
            {
                CheckDefault(stored);
            }

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
