using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Inventory;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The user's defaults for new Versions through <c>/api/v1/settings/version-defaults</c>: read and
/// replaced against one revision, checked against the inventory's ranges and lists and the offered
/// models, applied once to a new Song's Version 1 (an option sent at creation wins), never to a
/// Version created from another or to one that exists, and kept but ignored while no longer valid.
/// </summary>
public sealed class VersionDefaultsEndpointTests
{
    private static readonly Uri Defaults = new("/api/v1/settings/version-defaults", UriKind.Relative);

    [Fact]
    public async Task AtFirstThereAreNoDefaultsAndANewSongStartsWithSunosOwn()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(Defaults);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var read = await SetupApi.JsonAsync(response);
        Assert.Equal(1, read.GetProperty("revision").GetInt32());
        Assert.Empty(read.GetProperty("defaults").EnumerateObject());
        Assert.Empty(read.GetProperty("ignored").EnumerateObject());

        // The options that take a default: the form choosers, both models, and every choice, toggle,
        // range, and number; no text, and not the Simple form's sections.
        Assert.Equal(
            [
                "kind", "songMode", "speechMode", "model", "vocalGender", "durationMode", "durationSeconds", "maxMode", "weirdness",
                "styleInfluence", "variety", "personalize", "speechVocalGender", "speechBackgroundMusic", "speechVariety", "soundsModel",
                "soundType", "soundBpm", "soundKey", "soundScale",
            ],
            read.GetProperty("keys").EnumerateArray().Select(static key => key.GetString()));

        Assert.Equal(["v6", "v6-wild", "v6-mini"], read.GetProperty("models").EnumerateArray().Select(static model => model.GetString()));

        var inputs = await InputsAsync(client, await SongApi.CreateAsync(client, "Plain"));
        Assert.Equal(
            Without(VersionInputRules.ToJson(InputValues.Defaults("Plain")).ToJsonString(), "model", "soundsModel"),
            Without(LineageValues.OptionsOf(inputs).GetRawText(), "model", "soundsModel"));
        Assert.Equal("v6", inputs.GetProperty("model").GetString());
    }

    [Fact]
    public async Task DefaultsAreAppliedToEveryKindOfANewSongsVersionOne()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var saved = await PutAsync(
            client,
            1,
            """{"defaults":{"model":"v6-wild","variety":"high","weirdness":30,"speechBackgroundMusic":false,"songMode":"simple","soundsModel":"v6-mini","soundBpm":120,"soundKey":"D#","soundScale":"minor","vocalGender":null}}""");

        Assert.Equal(2, saved.GetProperty("revision").GetInt32());
        Assert.Equal(10, saved.GetProperty("defaults").EnumerateObject().Count());
        Assert.Empty(saved.GetProperty("ignored").EnumerateObject());

        var inputs = await InputsAsync(client, await SongApi.CreateAsync(client, "Defaulted"));
        Assert.Equal("v6-wild", inputs.GetProperty("model").GetString());
        Assert.Equal("high", inputs.GetProperty("variety").GetString());
        Assert.Equal(30, inputs.GetProperty("weirdness").GetInt32());
        Assert.False(inputs.GetProperty("speechBackgroundMusic").GetBoolean());
        Assert.Equal("simple", inputs.GetProperty("songMode").GetString());
        Assert.Equal("v6-mini", inputs.GetProperty("soundsModel").GetString());
        Assert.Equal(120, inputs.GetProperty("soundBpm").GetInt32());
        Assert.Equal("D#", inputs.GetProperty("soundKey").GetString());
        Assert.Equal("minor", inputs.GetProperty("soundScale").GetString());

        // An option without a default keeps Suno's, and the title is still the Song's.
        Assert.Equal(50, inputs.GetProperty("styleInfluence").GetInt32());
        Assert.Equal("normal", inputs.GetProperty("speechVariety").GetString());
        Assert.Equal("song", inputs.GetProperty("kind").GetString());
        Assert.Equal("Defaulted", inputs.GetProperty("title").GetString());

        // A default kind makes the new Song's Version 1 that kind.
        await PutAsync(client, 2, """{"defaults":{"kind":"sound"}}""");
        var sound = await SongApi.CreateAsync(client, "A sound");
        Assert.Equal("sound", sound.GetProperty("currentVersion").GetProperty("kind").GetString());
        Assert.Equal("normal", (await InputsAsync(client, sound)).GetProperty("variety").GetString());
    }

    [Fact]
    public async Task AnOptionSentAtCreationWinsOverItsDefault()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await PutAsync(client, 1, """{"defaults":{"weirdness":30,"variety":"high","model":"v6-wild"}}""");

        using var response = await SongApi.SendJsonAsync(
            client,
            HttpMethod.Post,
            SongApi.Songs,
            """{"title":"Explicit","inputs":{"weirdness":70,"model":"v6-mini","title":"Suno's own title","simplePrompt":"A prompt"}}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var inputs = await InputsAsync(client, await SetupApi.JsonAsync(response));
        Assert.Equal(70, inputs.GetProperty("weirdness").GetInt32());
        Assert.Equal("v6-mini", inputs.GetProperty("model").GetString());
        Assert.Equal("Suno's own title", inputs.GetProperty("title").GetString());
        Assert.Equal("A prompt", inputs.GetProperty("simplePrompt").GetString());
        Assert.Equal("high", inputs.GetProperty("variety").GetString());
    }

    [Fact]
    public async Task AWrongOptionSentAtCreationIsRefusedAndNoSongIsCreated()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var (body, field) in new[]
        {
            ("""{"title":"Wrong","inputs":{"weirdness":101}}""", "inputs.weirdness"),
            ("""{"title":"Wrong","inputs":{"model":"v99"}}""", "inputs.model"),
            ("""{"title":"Wrong","inputs":{"notAnOption":1}}""", "inputs.notAnOption"),
            ("""{"title":"Wrong","inputs":["weirdness"]}""", "inputs"),
            ("""{"title":"Wrong","inputs":null}""", "inputs"),
        })
        {
            using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, SongApi.Songs, body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        Assert.Equal(0, (await SongApi.ListAsync(client)).GetProperty("total").GetInt32());

        // Complement: the first Song created afterwards still takes the first shortcode number.
        var song = await SongApi.CreateAsync(client, "Right");
        Assert.Equal("n8-1", song.GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task AWrongDefaultIsRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var (body, field) in new[]
        {
            ("""{"defaults":{"title":"A title"}}""", "defaults.title"),
            ("""{"defaults":{"simplePrompt":"A prompt"}}""", "defaults.simplePrompt"),
            ("""{"defaults":{"excludeStyles":"metal"}}""", "defaults.excludeStyles"),
            ("""{"defaults":{"simpleLyricsAdded":true}}""", "defaults.simpleLyricsAdded"),
            ("""{"defaults":{"notAnOption":1}}""", "defaults.notAnOption"),
            ("""{"defaults":{"weirdness":101}}""", "defaults.weirdness"),
            ("""{"defaults":{"durationSeconds":9}}""", "defaults.durationSeconds"),
            ("""{"defaults":{"variety":"wild"}}""", "defaults.variety"),
            ("""{"defaults":{"variety":null}}""", "defaults.variety"),
            ("""{"defaults":{"maxMode":"yes"}}""", "defaults.maxMode"),
            ("""{"defaults":{"kind":"video"}}""", "defaults.kind"),
            ("""{"defaults":{"model":"v99"}}""", "defaults.model"),
            ("""{"defaults":{"soundBpm":301}}""", "defaults.soundBpm"),
            ("""{"defaults":[]}""", "defaults"),
            ("""{}""", "defaults"),
        })
        {
            using var response = await SendAsync(client, 1, body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        // A retired model cannot become a default.
        await RetireAsync(client, DefaultSunoModels.V6Wild.Id, retired: true, listRevision: 1);
        using (var retired = await SendAsync(client, 1, """{"defaults":{"model":"v6-wild"}}"""))
        {
            await SetupApi.ProblemAsync(retired, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        }

        var read = await ReadAsync(client);
        Assert.Equal(1, read.GetProperty("revision").GetInt32());
        Assert.Empty(read.GetProperty("defaults").EnumerateObject());

        // Complement: the edges of each range, and null where Suno's default is null, are taken.
        var edges = await PutAsync(client, 1, """{"defaults":{"weirdness":0,"styleInfluence":100,"durationSeconds":10,"soundBpm":null,"vocalGender":null,"model":null}}""");
        Assert.Equal(6, edges.GetProperty("defaults").EnumerateObject().Count());
    }

    [Fact]
    public async Task ChangesNeedTheRevisionAndAStaleOneIsAnsweredWithTheCurrentDefaults()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var missing = await SendAsync(client, null, """{"defaults":{}}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, "revision_required");
        }

        await PutAsync(client, 1, """{"defaults":{"weirdness":30}}""");
        using (var stale = await SendAsync(client, 1, """{"defaults":{"weirdness":40}}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            var current = problem.GetProperty("current");
            Assert.Equal(2, current.GetProperty("revision").GetInt32());
            Assert.Equal(30, current.GetProperty("defaults").GetProperty("weirdness").GetInt32());
        }

        // The same defaults again change nothing, so the revision stays; leaving a key out removes it.
        var same = await PutAsync(client, 2, """{"defaults":{"weirdness":30}}""");
        Assert.Equal(2, same.GetProperty("revision").GetInt32());
        var cleared = await PutAsync(client, 2, """{"defaults":{}}""");
        Assert.Equal(3, cleared.GetProperty("revision").GetInt32());
        Assert.Empty(cleared.GetProperty("defaults").EnumerateObject());
        Assert.Equal(50, (await InputsAsync(client, await SongApi.CreateAsync(client, "Back to Suno's"))).GetProperty("weirdness").GetInt32());
    }

    [Fact]
    public async Task ADefaultNoLongerValidIsKeptFlaggedAndIgnoredUntilItIsValidAgain()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await PutAsync(client, 1, """{"defaults":{"model":"v6-wild","soundsModel":"v6-wild","weirdness":30}}""");

        await RetireAsync(client, DefaultSunoModels.V6Wild.Id, retired: true, listRevision: 1);

        var read = await ReadAsync(client);
        Assert.Equal("v6-wild", read.GetProperty("defaults").GetProperty("model").GetString());
        Assert.Equal(["model", "soundsModel"], read.GetProperty("ignored").EnumerateObject().Select(static entry => entry.Name));
        Assert.Contains("v6-wild", read.GetProperty("ignored").GetProperty("model").GetString(), StringComparison.Ordinal);
        Assert.Equal(["v6", "v6-mini"], read.GetProperty("models").EnumerateArray().Select(static model => model.GetString()));

        // The next new Song uses the first model offered, and the defaults still valid.
        var inputs = await InputsAsync(client, await SongApi.CreateAsync(client, "After retiring"));
        Assert.Equal("v6", inputs.GetProperty("model").GetString());
        Assert.Equal("v6", inputs.GetProperty("soundsModel").GetString());
        Assert.Equal(30, inputs.GetProperty("weirdness").GetInt32());

        // Saving other changes keeps the retired model as it is.
        var kept = await PutAsync(client, 2, """{"defaults":{"model":"v6-wild","soundsModel":"v6-wild","weirdness":40}}""");
        Assert.Equal("v6-wild", kept.GetProperty("defaults").GetProperty("model").GetString());
        Assert.Equal(2, kept.GetProperty("ignored").EnumerateObject().Count());

        // Restored, it is valid and applied again.
        await RetireAsync(client, DefaultSunoModels.V6Wild.Id, retired: false, listRevision: 2);
        Assert.Empty((await ReadAsync(client)).GetProperty("ignored").EnumerateObject());
        Assert.Equal("v6-wild", (await InputsAsync(client, await SongApi.CreateAsync(client, "Restored"))).GetProperty("model").GetString());
    }

    [Fact]
    public async Task ChangingDefaultsNeverChangesAnExistingVersionAndANewVersionCopiesItsSource()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await PutAsync(client, 1, """{"defaults":{"weirdness":30,"variety":"high"}}""");
        var song = await SongApi.CreateAsync(client, "Existing");
        var before = (await InputsAsync(client, song)).GetRawText();

        await PutAsync(client, 2, """{"defaults":{"weirdness":90,"variety":"off","model":"v6-mini","kind":"speech"}}""");

        Assert.Equal(before, (await InputsAsync(client, song)).GetRawText());

        // A Version created from Version 1 copies it, not the defaults.
        var source = song.GetProperty("currentVersion");
        using var options = await client.GetAsync(new Uri($"/api/v1/versions/{source.GetProperty("id").GetGuid()}/next-numbers", UriKind.Relative));
        var number = (await SetupApi.JsonAsync(options)).GetProperty("options")[0].GetProperty("number").GetString();
        using var created = await SongApi.SendJsonAsync(
            client,
            HttpMethod.Post,
            new Uri($"/api/v1/songs/{song.GetProperty("shortcode").GetString()}/versions", UriKind.Relative),
            $$"""{"sourceVersionId":"{{source.GetProperty("shortcode").GetString()}}","number":"{{number}}"}""");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var branch = await SetupApi.JsonAsync(created);
        using var detail = await client.GetAsync(new Uri($"/api/v1/versions/{branch.GetProperty("id").GetGuid()}", UriKind.Relative));
        Assert.Equal(before, (await SetupApi.JsonAsync(detail)).GetProperty("inputs").GetRawText());
    }

    [Fact]
    public async Task ReadingNeedsCatalogReadAndChangingNeedsASession()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        using var client = factory.CreateClient();

        var everything = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        using (var put = await CredentialApi.SendAsync(client, HttpMethod.Put, Defaults, everything))
        {
            await SetupApi.ProblemAsync(put, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, Defaults, reader))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        // Complement: without catalog.read reading is refused; signed out, so is everything.
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Defaults, writer))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
        }

        using (var signedOut = await client.GetAsync(Defaults))
        {
            await SetupApi.ProblemAsync(signedOut, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        Assert.Equal(1, (await ReadAsync(setUp)).GetProperty("revision").GetInt32());
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Defaults);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> PutAsync(HttpClient client, int revision, string json)
    {
        using var response = await SendAsync(client, revision, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var read = await SetupApi.JsonAsync(response);
        Assert.Equal(SongApi.Quoted(read.GetProperty("revision").GetInt32()), response.Headers.ETag?.Tag);
        return read;
    }

    /// <summary>Sends a PUT with the anti-forgery header and, when given, the revision in <c>If-Match</c>; the caller reads the answer.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, int? revision, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Defaults)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (revision is { } value)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
        }

        return await client.SendAsync(request);
    }

    private static async Task RetireAsync(HttpClient client, Guid id, bool retired, int listRevision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/suno/models/{id}", UriKind.Relative))
        {
            Content = new StringContent(retired ? """{"retired":true}""" : """{"retired":false}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(listRevision)));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> InputsAsync(HttpClient client, JsonElement song)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{song.GetProperty("currentVersion").GetProperty("id").GetGuid()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("inputs");
    }

    /// <summary>A JSON object's text without the given keys, for comparing the rest.</summary>
    private static string Without(string json, params string[] keys)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        foreach (var key in keys)
        {
            node.Remove(key);
        }

        return node.ToJsonString();
    }
}
