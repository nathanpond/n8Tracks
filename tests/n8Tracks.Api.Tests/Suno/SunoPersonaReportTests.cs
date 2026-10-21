using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The user's Suno voices as a sync reads them (#439): <c>PUT /api/v1/suno/personas/discovered</c>
/// (<c>suno.sync</c>) keeps each persona of the voice list in <c>suno_personas</c> by its Suno ID, with its
/// name, description, trashed and owned flags and nothing else of Suno's object; only a complete list
/// changes availability; nothing is ever deleted; and the personas list answers what it knows.
/// </summary>
public sealed class SunoPersonaReportTests
{
    private static readonly Uri Discovered = new("/api/v1/suno/personas/discovered", UriKind.Relative);
    private static readonly Uri Personas = new("/api/v1/suno/personas", UriKind.Relative);

    /// <summary>What a voice list's owner fields hold here: never stored, answered, or logged.</summary>
    private const string HandleSentinel = "handle-sentinel-7f3a";
    private const string DisplayNameSentinel = "display-sentinel-7f3a";

    [Fact]
    public async Task ACapturedVoiceListIsKeptByIdWithOnlyTheFieldsReadAndListedByName()
    {
        using var factory = new LoggingApiFactory("Debug");
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var captured = JsonNode.Parse(Clips.Fixture("persona-get-personas.response.json"))!["personas"]!.AsArray();
        JsonNode[] voices = [.. captured.Select(static persona => persona!.DeepClone())];
        foreach (var voice in voices)
        {
            voice["user_handle"] = HandleSentinel;
            voice["user_display_name"] = DisplayNameSentinel;
        }

        var answer = await ReportAsync(client, token, complete: true, voices);

        Assert.Equal(voices.Select(static voice => (string?)voice["id"]), answer.GetProperty("added").EnumerateArray().Select(static id => id.GetString()));
        var items = await ListAsync(client);
        Assert.Equal(answer.GetProperty("items").EnumerateArray().Select(static item => item.GetRawText()), items.Select(static item => item.GetRawText()));
        Assert.Equal(captured.Count, items.Length);
        Assert.All(items, static item =>
        {
            Assert.Equal("available", item.GetProperty("state").GetString());
            Assert.True(item.GetProperty("owned").GetBoolean());
            Assert.Equal(["description", "id", "name", "owned", "state"], item.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        });
        var first = items.Single(item => item.GetProperty("id").GetString() == (string?)captured[0]!["id"]);
        Assert.Equal((string?)captured[0]!["name"], first.GetProperty("name").GetString());

        // Nothing else of Suno's persona is kept: no owner's handle or display name, image, or clip.
        var rows = string.Join('\n', TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || name || '|' || description || '|' || state || '|' || coalesce(is_owned, 'null') || '|' || last_seen_utc FROM suno_personas;"));
        Assert.DoesNotContain(HandleSentinel, rows, StringComparison.Ordinal);
        Assert.DoesNotContain(DisplayNameSentinel, rows, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn2.suno.ai", rows, StringComparison.Ordinal);
        Assert.Equal(
            ["description", "is_owned", "last_seen_utc", "name", "state", "suno_id"],
            TestDatabase.Rows(factory.DataPath, "SELECT name FROM pragma_table_info('suno_personas');").Order(StringComparer.Ordinal));

        // Invariant 6: the report is logged by its counts only.
        Assert.Contains("Suno voices reported: 2 named, complete True", factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(HandleSentinel, factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(DisplayNameSentinel, factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain((string)captured[0]!["name"]!, factory.CapturedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportingNeedsSunoSyncAndASignedOutCallerIsRefused()
    {
        using var factory = SongApi.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.SunoSync)]);
        var generate = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var sync = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var body = Body(complete: false, Voice("pe-1", "Velvet Alto"));

        foreach (var token in new[] { others, generate })
        {
            using var refused = await SendAsync(client, token, body);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SunoSync, problem.GetProperty("requiredScope").GetString());
        }

        using (var signedOut = await client.PutAsync(Discovered, new StringContent(body, System.Text.Encoding.UTF8, "application/json")))
        {
            await SetupApi.ProblemAsync(signedOut, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_personas;"));

        // Complement: suno.sync reports.
        using var reported = await SendAsync(client, sync, body);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_personas;"));
    }

    [Fact]
    public async Task AKnownVoiceTakesItsNewNameDescriptionAndOwnershipAndABlankNameNeverOverwrites()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        // A voice an import saw (#153): known by its ID and name only.
        TestDatabase.Execute(factory.DataPath, "INSERT INTO suno_personas (suno_id, name, last_seen_utc) VALUES ('pe-1', 'From a clip', '2026-09-01T00:00:00.000Z');");
        Assert.Equal("""{"id":"pe-1","name":"From a clip","description":"","state":"available","owned":null}""", (await OneAsync(client, "pe-1")).GetRawText());

        clock.Advance(TimeSpan.FromMinutes(5));
        var answer = await ReportAsync(client, token, complete: false, Voice("pe-1", "Velvet Alto", description: "Warm and low", owned: true));

        Assert.Empty(answer.GetProperty("added").EnumerateArray());
        Assert.Equal(["pe-1"], answer.GetProperty("renamed").EnumerateArray().Select(static id => id.GetString()));
        Assert.Equal("""{"id":"pe-1","name":"Velvet Alto","description":"Warm and low","state":"available","owned":true}""", (await OneAsync(client, "pe-1")).GetRawText());
        Assert.NotEqual("2026-09-01T00:00:00.000Z", TestDatabase.Scalar(factory.DataPath, "SELECT last_seen_utc FROM suno_personas WHERE suno_id = 'pe-1';"));

        // A blank name and a missing description and ownership keep what is known; a repeated ID keeps the last.
        await ReportAsync(client, token, complete: false, Voice("pe-1", "first"), Voice("pe-1", "  "));
        Assert.Equal("""{"id":"pe-1","name":"Velvet Alto","description":"Warm and low","state":"available","owned":true}""", (await OneAsync(client, "pe-1")).GetRawText());

        // A voice of the same name and another ID is another voice, and the list is by name.
        await ReportAsync(client, token, complete: false, Voice("pe-2", "Velvet Alto", owned: false), Voice("pe-3", "airy Tenor"));
        Assert.Equal(["pe-3", "pe-1", "pe-2"], (await ListAsync(client)).Select(static item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task AnIncompleteListNeverMarksAnythingUnavailable()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await ReportAsync(client, token, complete: true, Voice("pe-1", "One"), Voice("pe-2", "Two"));

        // Neither the one left out nor the one named trashed changes.
        var answer = await ReportAsync(client, token, complete: false, Voice("pe-2", "Two", trashed: true));

        Assert.Empty(answer.GetProperty("becameUnavailable").EnumerateArray());
        Assert.All(await ListAsync(client), static item => Assert.Equal("available", item.GetProperty("state").GetString()));

        // An Unavailable one stays so too until a complete list shows it.
        await ReportAsync(client, token, complete: true, Voice("pe-2", "Two"));
        await ReportAsync(client, token, complete: false, Voice("pe-1", "One"));
        Assert.Equal("unavailable", (await OneAsync(client, "pe-1")).GetProperty("state").GetString());

        // A voice first seen in an incomplete list is Available, and one first seen trashed is not.
        await ReportAsync(client, token, complete: false, Voice("pe-new", "New"), Voice("pe-gone", "Gone", trashed: true));
        Assert.Equal("available", (await OneAsync(client, "pe-new")).GetProperty("state").GetString());
        Assert.Equal("unavailable", (await OneAsync(client, "pe-gone")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AVoiceMissingFromACompleteListBecomesUnavailableNothingIsDeletedAndItComesBack()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        TestDatabase.Execute(factory.DataPath, "INSERT INTO suno_personas (suno_id, name, last_seen_utc) VALUES ('pe-clip', 'Seen in a clip', '2026-09-01T00:00:00.000Z');");
        await ReportAsync(client, token, complete: true, Voice("pe-1", "Going"), Voice("pe-2", "Staying"), Voice("pe-3", "Trashed"), Voice("pe-clip", "Seen in a clip"));
        var counts = Counts(factory);

        var answer = await ReportAsync(client, token, complete: true, Voice("pe-2", "Staying"), Voice("pe-3", "Trashed", trashed: true));

        Assert.Equal(["pe-1", "pe-3", "pe-clip"], answer.GetProperty("becameUnavailable").EnumerateArray().Select(static id => id.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal("available", (await OneAsync(client, "pe-2")).GetProperty("state").GetString());
        Assert.All(["pe-1", "pe-3", "pe-clip"], id => Assert.Equal("unavailable", TestDatabase.Scalar(factory.DataPath, $"SELECT state FROM suno_personas WHERE suno_id = '{id}';")));

        // Nothing is deleted: every persona is still listed with its name, and the catalog is as it was.
        Assert.Equal(4, (await ListAsync(client)).Length);
        Assert.Equal("Going", (await OneAsync(client, "pe-1")).GetProperty("name").GetString());
        Assert.Equal(counts, Counts(factory));

        // Listed again (and no longer trashed), each is Available by itself.
        var back = await ReportAsync(client, token, complete: true, Voice("pe-1", "Going"), Voice("pe-2", "Staying"), Voice("pe-3", "Trashed", trashed: false), Voice("pe-clip", "Seen in a clip"));
        Assert.Equal(["pe-1", "pe-3", "pe-clip"], back.GetProperty("becameAvailable").EnumerateArray().Select(static id => id.GetString()));
        Assert.All(await ListAsync(client), static item => Assert.Equal("available", item.GetProperty("state").GetString()));

        // An empty complete list makes every voice Unavailable, and still deletes none.
        await ReportAsync(client, token, complete: true);
        Assert.All(await ListAsync(client), static item => Assert.Equal("unavailable", item.GetProperty("state").GetString()));
        Assert.Equal("4", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_personas;"));
    }

    [Theory]
    [InlineData("""{"complete":true,"voices":[{"id":"pe-1","name":"One"},{"name":"No ID"}]}""", "voices[1].id")]
    [InlineData("""{"complete":true,"voices":[{"id":"","name":"Blank"}]}""", "voices[0].id")]
    [InlineData("""{"complete":true,"voices":[{"id":5}]}""", "voices[0].id")]
    [InlineData("""{"complete":true,"voices":["pe-1"]}""", "voices[0]")]
    [InlineData("""{"complete":true,"voices":{"id":"pe-1"}}""", "voices")]
    [InlineData("""{"complete":"yes","voices":[{"id":"pe-1"}]}""", "complete")]
    [InlineData("""{"voices":[{"id":"pe-1"}]}""", "complete")]
    public async Task AReportThatCannotBeReadStoresNothing(string body, string field)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await ReportAsync(client, token, complete: true, Voice("pe-0", "Known"));
        var before = Rows(factory);

        using var response = await SendAsync(client, token, body);

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.GetRawText());
        Assert.Equal(before, Rows(factory));
    }

    [Fact]
    public async Task ANameOrDescriptionTooLongRefusesTheReport()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        using var response = await SendAsync(client, token, Body(complete: true, Voice("pe-1", new string('n', 501)), Voice("pe-2", "Fine", description: new string('d', 5_001))));

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("voices[0].name", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("voices[1].description", out _));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_personas;"));
    }

    /// <summary>A persona object as Suno's voice list sends it, with only the fields given (null leaves one out).</summary>
    private static JsonObject Voice(string id, string? name = null, bool? trashed = null, string? description = null, bool? owned = null)
    {
        var voice = new JsonObject { ["id"] = id, ["image_s3_id"] = "https://cdn2.suno.ai/image.jpeg", ["user_handle"] = HandleSentinel, ["user_display_name"] = DisplayNameSentinel };
        if (name is not null)
        {
            voice["name"] = name;
        }

        if (description is not null)
        {
            voice["description"] = description;
        }

        if (trashed is not null)
        {
            voice["is_trashed"] = trashed;
        }

        if (owned is not null)
        {
            voice["is_owned"] = owned;
        }

        return voice;
    }

    private static string Body(bool complete, params JsonNode[] voices) =>
        new JsonObject { ["complete"] = complete, ["voices"] = new JsonArray([.. voices.Select(static voice => voice.DeepClone())]) }.ToJsonString();

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Discovered)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReportAsync(HttpClient client, string token, bool complete, params JsonNode[] voices)
    {
        using var response = await SendAsync(client, token, Body(complete, voices));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Personas);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static async Task<JsonElement> OneAsync(HttpClient client, string id) =>
        (await ListAsync(client)).Single(item => item.GetProperty("id").GetString() == id);

    private static List<string> Rows(N8TracksApiFactory factory) =>
        TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || name || '|' || description || '|' || state || '|' || last_seen_utc FROM suno_personas ORDER BY suno_id;");

    private static string Counts(N8TracksApiFactory factory) =>
        TestDatabase.Scalar(factory.DataPath, "SELECT (SELECT count(*) FROM songs) || '|' || (SELECT count(*) FROM versions) || '|' || (SELECT count(*) FROM generations) || '|' || (SELECT count(*) FROM version_voices);");
}
