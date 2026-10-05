using System.Net;
using System.Text.Json;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// A Version's kind, modes, and Suno options: <c>inputs</c> on <c>GET</c> and <c>PATCH
/// /api/v1/versions/{reference}</c>, merged key by key, with <c>effectiveInputs</c> holding only the
/// ones that apply to the kind and mode. Every option is a creation input: copied by Create New
/// Version From and frozen with the Version.
/// </summary>
public sealed class VersionOptionsEndpointTests
{
    /// <summary>What an Advanced Song with an automatic duration sends to Suno, in the inventory's order.</summary>
    private static readonly string[] AdvancedSong =
    [
        "kind", "songMode", "model", "lyrics", "styles", "excludeStyles", "vocalGender", "durationMode",
        "maxMode", "weirdness", "styleInfluence", "variety", "personalize", "title",
    ];

    [Fact]
    public async Task ANewVersionIsAnAdvancedSongWithSunosDefaultsAndTheSongsTitle()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var title = string.Concat(Enumerable.Repeat("Long title ", 30))[..300];
        var id = VersionId(await SongApi.CreateAsync(client, title));

        var version = await GetAsync(client, id);
        var inputs = version.GetProperty("inputs");

        Assert.Equal(
            """{"kind":"song","songMode":"advanced","speechMode":"advanced","model":null,"simplePrompt":"","simpleLyricsAdded":false,"simpleStylesAdded":false,"excludeStyles":"","vocalGender":null,"durationMode":"auto","durationSeconds":180,"maxMode":false,"weirdness":50,"styleInfluence":50,"variety":"normal","personalize":false,"title":"TITLE","speechPrompt":"","speechScript":"","speechTone":"","speechVocalGender":null,"speechBackgroundMusic":true,"speechVariety":"normal","soundsModel":null,"soundDescription":"","soundType":"one_shot","soundBpm":null,"soundKey":"any","soundScale":null}"""
                .Replace("TITLE", title[..100], StringComparison.Ordinal),
            inputs.GetRawText());
        Assert.Equal(100, inputs.GetProperty("title").GetString()!.Length);

        // effectiveInputs: an Advanced Song, so no Simple prompt, and no custom seconds while Duration is Auto.
        Assert.Equal(AdvancedSong, version.GetProperty("effectiveInputs").EnumerateObject().Select(static option => option.Name));

        // The kind and model have columns of their own, so lists can filter on them.
        Assert.Equal("song|", TestDatabase.Scalar(factory.DataPath, "SELECT kind || '|' || coalesce(model, '') FROM versions;"));
    }

    [Fact]
    public async Task SunosTitleIsIndependentOfTheSongsOnceSet()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Working title");
        var id = VersionId(song);

        await SongApi.EditAsync(client, song.GetProperty("id").GetString()!, 1, """{"title":"Final title"}""");
        Assert.Equal("Working title", (await GetAsync(client, id)).GetProperty("inputs").GetProperty("title").GetString());

        var edited = await EditAsync(client, id, """{"inputs":{"title":"For Suno"}}""");
        Assert.Equal("For Suno", edited.GetProperty("inputs").GetProperty("title").GetString());
        Assert.Equal("Final title", (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")))).GetProperty("title").GetString());

        // A title cut at 100 never splits a surrogate pair.
        var emoji = await SongApi.CreateAsync(client, new string('a', 99) + "🎸 and more");
        Assert.Equal(new string('a', 99), (await GetAsync(client, VersionId(emoji))).GetProperty("inputs").GetProperty("title").GetString());
    }

    [Fact]
    public async Task OptionsAreMergedKeyByKeyAndKeptWhenTheKindOrModeChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Switching"));
        await EditAsync(client, id, """{"lyrics":"[Verse]\nRun","styles":"punk"}""");

        var advanced = await EditAsync(
            client,
            id,
            """{"inputs":{"weirdness":80,"vocalGender":"female","durationMode":"custom","durationSeconds":120,"variety":"high","simplePrompt":"A fast song\r\nabout running","model":"v6-mini"}}""");
        var inputs = advanced.GetProperty("inputs");
        Assert.Equal(80, inputs.GetProperty("weirdness").GetInt32());
        Assert.Equal(50, inputs.GetProperty("styleInfluence").GetInt32());
        Assert.Equal("A fast song\nabout running", inputs.GetProperty("simplePrompt").GetString());
        var effective = advanced.GetProperty("effectiveInputs");
        Assert.Equal(120, effective.GetProperty("durationSeconds").GetInt32());
        Assert.Equal("[Verse]\nRun", effective.GetProperty("lyrics").GetString());
        Assert.False(effective.TryGetProperty("simplePrompt", out _));
        Assert.False(effective.TryGetProperty("simpleLyricsAdded", out _));
        Assert.False(effective.TryGetProperty("speechMode", out _));

        // Simple mode: the model, the prompt, and the section switches; no Advanced-only option, and
        // no lyrics or styles until their section is added.
        var simple = await EditAsync(client, id, """{"inputs":{"songMode":"simple"}}""");
        Assert.Equal(
            ["kind", "songMode", "model", "simplePrompt", "simpleLyricsAdded", "simpleStylesAdded"],
            simple.GetProperty("effectiveInputs").EnumerateObject().Select(static option => option.Name));
        Assert.Equal(80, simple.GetProperty("inputs").GetProperty("weirdness").GetInt32());

        var withLyrics = await EditAsync(client, id, """{"inputs":{"simpleLyricsAdded":true}}""");
        Assert.Equal("[Verse]\nRun", withLyrics.GetProperty("effectiveInputs").GetProperty("lyrics").GetString());
        Assert.False(withLyrics.GetProperty("effectiveInputs").TryGetProperty("styles", out _));
        var withBoth = await EditAsync(client, id, """{"inputs":{"simpleStylesAdded":true}}""");
        Assert.Equal("punk", withBoth.GetProperty("effectiveInputs").GetProperty("styles").GetString());

        // Speech and Sound: no Song option applies, only their own (SpeechAndSoundOptionsEndpointTests has the rest).
        var speech = await EditAsync(client, id, """{"inputs":{"kind":"speech","speechMode":"simple"}}""");
        Assert.Equal("""{"kind":"speech","speechMode":"simple","speechPrompt":""}""", speech.GetProperty("effectiveInputs").GetRawText());
        var sound = await EditAsync(client, id, """{"inputs":{"kind":"sound"}}""");
        Assert.Equal(
            """{"kind":"sound","soundsModel":null,"soundDescription":"","soundType":"one_shot","soundBpm":null,"soundKey":"any"}""",
            sound.GetProperty("effectiveInputs").GetRawText());

        // Back to an Advanced Song: nothing was cleared by switching.
        var back = await EditAsync(client, id, """{"inputs":{"kind":"song","songMode":"advanced"}}""");
        Assert.Equal(
            """{"kind":"song","songMode":"advanced","speechMode":"simple","model":"v6-mini","simplePrompt":"A fast song\nabout running","simpleLyricsAdded":true,"simpleStylesAdded":true,"excludeStyles":"","vocalGender":"female","durationMode":"custom","durationSeconds":120,"maxMode":false,"weirdness":80,"styleInfluence":50,"variety":"high","personalize":false,"title":"Switching","speechPrompt":"","speechScript":"","speechTone":"","speechVocalGender":null,"speechBackgroundMusic":true,"speechVariety":"normal","soundsModel":null,"soundDescription":"","soundType":"one_shot","soundBpm":null,"soundKey":"any","soundScale":null}""",
            back.GetProperty("inputs").GetRawText());
        Assert.Equal("[Verse]\nRun", back.GetProperty("lyrics").GetString());

        // Duration back to Auto keeps the seconds but no longer sends them.
        var auto = await EditAsync(client, id, """{"inputs":{"durationMode":"auto"}}""");
        Assert.Equal(120, auto.GetProperty("inputs").GetProperty("durationSeconds").GetInt32());
        Assert.False(auto.GetProperty("effectiveInputs").TryGetProperty("durationSeconds", out _));
        Assert.Equal(AdvancedSong, auto.GetProperty("effectiveInputs").EnumerateObject().Select(static option => option.Name));
    }

    [Fact]
    public async Task AnOptionOutsideItsRangeListOrLimitIsRefusedWithAFieldErrorNamingItAndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Bounded"));
        var before = (await GetAsync(client, id)).GetRawText();

        foreach (var (json, field, message) in new[]
        {
            ("""{"inputs":{"weirdness":101}}""", "inputs.weirdness", "Weirdness is a whole number from 0 to 100."),
            ("""{"inputs":{"weirdness":-1}}""", "inputs.weirdness", "Weirdness"),
            ("""{"inputs":{"styleInfluence":50.5}}""", "inputs.styleInfluence", "Style Influence"),
            ("""{"inputs":{"durationSeconds":9}}""", "inputs.durationSeconds", "from 10 to 360"),
            ("""{"inputs":{"durationSeconds":"120"}}""", "inputs.durationSeconds", "Duration (custom)"),
            ("""{"inputs":{"variety":"loud"}}""", "inputs.variety", "Choose Variety: off, normal, high, extra, max."),
            ("""{"inputs":{"vocalGender":"other"}}""", "inputs.vocalGender", "Choose Vocal Gender: male, female, or null for none."),
            ("""{"inputs":{"durationMode":null}}""", "inputs.durationMode", "Choose Duration: auto, custom."),
            ("""{"inputs":{"model":"v7"}}""", "inputs.model", "the model list"),
            ("""{"inputs":{"maxMode":"yes"}}""", "inputs.maxMode", "Max Mode: send true or false."),
            ("""{"inputs":{"simpleLyricsAdded":"write_new"}}""", "inputs.simpleLyricsAdded", "send true or false"),
            ($$$"""{"inputs":{"simplePrompt":"{{{new string('x', 1_001)}}}"}}""", "inputs.simplePrompt", "Use at most 1,000 characters."),
            ($$$"""{"inputs":{"excludeStyles":"{{{new string('x', 1_001)}}}"}}""", "inputs.excludeStyles", "Use at most 1,000 characters."),
            ($$$"""{"inputs":{"title":"{{{new string('x', 101)}}}"}}""", "inputs.title", "Use at most 100 characters."),
            ("""{"inputs":{"excludeStyles":null}}""", "inputs.excludeStyles", "Exclude styles: send text."),
            ("""{"inputs":{"title":"a\u0000b"}}""", "inputs.title", "U+0000"),
            ("""{"inputs":{"simplePrompt":"\ud83c"}}""", "inputs.simplePrompt", "unpaired surrogate"),
            ("""{"inputs":{"kind":"video"}}""", "inputs.kind", "Choose the kind: song, speech, sound."),
            ("""{"inputs":{"songMode":"expert"}}""", "inputs.songMode", "Choose the mode: simple, advanced."),
            ("""{"inputs":{"lyrics":"Not here"}}""", "inputs.lyrics", "'lyrics' is not an option of a Version."),
            ("""{"inputs":{"Weirdness":60}}""", "inputs.Weirdness", "not an option"),
            ("""{"inputs":["weirdness"]}""", "inputs", "Send an object of options, each one to change."),
            ("""{"inputs":null}""", "inputs", "Send an object"),
        })
        {
            using var response = await PatchAsync(client, id, await IfMatchAsync(client, id), json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
            Assert.Contains(message, problem.GetProperty("errors").GetProperty(field)[0].GetString(), StringComparison.Ordinal);
        }

        // One wrong option refuses the whole edit, other fields included.
        using (var mixed = await PatchAsync(client, id, "\"1\"", """{"name":"Renamed","inputs":{"weirdness":60,"variety":"loud"}}"""))
        {
            var problem = await SetupApi.ProblemAsync(mixed, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal(["inputs.variety"], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
        }

        Assert.Equal(before, (await GetAsync(client, id)).GetRawText());

        // Complement: the edges are taken, and null is accepted where the inventory's default is null.
        var edges = await EditAsync(
            client,
            id,
            $$$"""{"inputs":{"weirdness":0,"styleInfluence":100,"durationSeconds":360,"vocalGender":"male","model":"v6","title":"{{{new string('x', 100)}}}"}}""");
        Assert.Equal(0, edges.GetProperty("inputs").GetProperty("weirdness").GetInt32());
        var cleared = await EditAsync(client, id, """{"inputs":{"vocalGender":null,"model":null}}""");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("inputs").GetProperty("vocalGender").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("inputs").GetProperty("model").ValueKind);
    }

    [Fact]
    public async Task OptionsSentUnchangedChangeNothingAndAnEmptyObjectIsNoEdit()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Steady"));

        var same = await EditAsync(client, id, """{"inputs":{"weirdness":50,"kind":"song","title":"Steady"}}""");
        Assert.Equal(1, same.GetProperty("revision").GetInt32());
        var empty = await EditAsync(client, id, """{"inputs":{}}""");
        Assert.Equal(1, empty.GetProperty("revision").GetInt32());

        var changed = await EditAsync(client, id, """{"inputs":{"weirdness":51}}""");
        Assert.Equal(2, changed.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task CreatingAVersionFromAnotherCopiesEveryOptionIncludingTheOnesThatDoNotApply()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Branching");
        var id = VersionId(song);
        var source = await EditAsync(
            client,
            id,
            """{"inputs":{"songMode":"simple","speechMode":"simple","simplePrompt":"Prompt","weirdness":12,"durationSeconds":99,"title":"Suno title","model":"v6-wild"}}""");

        // A frozen source too: its options are copied into a Version that can change them.
        await SongApi.AttachGenerationAsync(factory, id.ToString());
        using var created = await SongApi.SendJsonAsync(
            client,
            HttpMethod.Post,
            new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative),
            """{"sourceVersionId":"n8-1-v1","number":"2"}""");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var copyId = (await SetupApi.JsonAsync(created)).GetProperty("id").GetGuid();

        var copy = await GetAsync(client, copyId);
        Assert.Equal(source.GetProperty("inputs").GetRawText(), copy.GetProperty("inputs").GetRawText());
        Assert.False(copy.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(13, (await EditAsync(client, copyId, """{"inputs":{"weirdness":13}}""")).GetProperty("inputs").GetProperty("weirdness").GetInt32());
    }

    [Fact]
    public async Task EachOptionIsRefusedOnAFrozenVersionAndTheSameWritesSucceedOnAMutableOne()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var frozen = VersionId(await SongApi.CreateAsync(client, "Frozen"));
        var mutable = VersionId(await SongApi.CreateAsync(client, "Mutable"));
        await SongApi.AttachGenerationAsync(factory, frozen.ToString());
        var stored = TestDatabase.Scalar(factory.DataPath, $"SELECT kind || '|' || coalesce(model, '') || '|' || inputs FROM versions WHERE id = '{frozen.ToString().ToUpperInvariant()}';");

        foreach (var option in (await GetAsync(client, frozen)).GetProperty("inputs").EnumerateObject())
        {
            var json = $$$"""{"name":"Renamed","inputs":{"{{{option.Name}}}":{{{Inventory.InputValues.ChangedJson(option.Name, option.Value)}}}}}""";

            using var refused = await PatchAsync(client, frozen, await IfMatchAsync(client, frozen), json);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "version_frozen");
            Assert.Equal(frozen, problem.GetProperty("versionId").GetGuid());

            using var accepted = await PatchAsync(client, mutable, await IfMatchAsync(client, mutable), json);
            Assert.True(accepted.StatusCode == HttpStatusCode.OK, $"{option.Name}: {await accepted.Content.ReadAsStringAsync()}");
        }

        Assert.Equal(stored, TestDatabase.Scalar(factory.DataPath, $"SELECT kind || '|' || coalesce(model, '') || '|' || inputs FROM versions WHERE id = '{frozen.ToString().ToUpperInvariant()}';"));
        Assert.Null((await GetAsync(client, frozen)).GetProperty("name").GetString());

        // Sending a frozen Version's options unchanged is a metadata edit, which is allowed.
        var unchanged = (await GetAsync(client, frozen)).GetProperty("inputs").GetRawText();
        var renamed = await EditAsync(client, frozen, $$"""{"name":"Renamed","inputs":{{unchanged}}}""");
        Assert.Equal("Renamed", renamed.GetProperty("name").GetString());

        // The database refuses a raw write of any option column too.
        foreach (var column in new[] { "kind = 'sound'", "model = 'v6'", "inputs = '{}'" })
        {
            var error = Assert.ThrowsAny<Exception>(() => TestDatabase.Execute(factory.DataPath, $"UPDATE versions SET {column} WHERE id = '{frozen.ToString().ToUpperInvariant()}';"));
            Assert.Contains("A frozen Version's inputs never change.", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ACredentialsEditOfOnlyOptionsTakesNoSnapshot()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Tooling"));
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.VersionsWrite);

        using var tool = factory.CreateClient();
        using (var request = new HttpRequestMessage(HttpMethod.Patch, Version(id))
        {
            Content = new StringContent("""{"inputs":{"weirdness":70}}""", System.Text.Encoding.UTF8, "application/json"),
        })
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", "\"1\""));
            using var response = await tool.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The history covers lyrics and styles only.
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM editor_revisions;"));
        Assert.Equal(70, (await GetAsync(client, id)).GetProperty("inputs").GetProperty("weirdness").GetInt32());
    }

    private static Guid VersionId(JsonElement song) => song.GetProperty("currentVersion").GetProperty("id").GetGuid();

    private static Uri Version(Guid id) => new($"/api/v1/versions/{id}", UriKind.Relative);

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(Version(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<string> IfMatchAsync(HttpClient client, Guid id) =>
        SongApi.Quoted((await GetAsync(client, id)).GetProperty("revision").GetInt32());

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, string ifMatch, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, Version(id))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        return await client.SendAsync(request);
    }

    /// <summary>Edits a Version at its current revision and returns it, asserting 200.</summary>
    private static async Task<JsonElement> EditAsync(HttpClient client, Guid id, string json)
    {
        using var response = await PatchAsync(client, id, await IfMatchAsync(client, id), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }
}
