using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The clip lookup (#215): <c>POST /api/v1/suno/clips/lookup</c>, called by the extension's Download
/// view with its <c>suno.sync</c> token, answers which of the Suno clips named n8Tracks already has as
/// Generations (archived ones included), with the Song's primary Artist, and which were deleted in
/// n8Tracks. It changes nothing.
/// </summary>
public sealed class SunoClipLookupEndpointTests
{
    private static readonly Uri Lookup = new("/api/v1/suno/clips/lookup", UriKind.Relative);

    [Fact]
    public async Task AnswersEachClipInTheOrderSentWithItsGenerationArtistAndDeletedState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Held");
        var songRef = song.GetProperty("shortcode").GetString()!;
        var held = await SongApi.AttachGenerationAsync(factory, $"{songRef}-v1", Clips.Minimal("clip-held"));
        var archived = await SongApi.AttachGenerationAsync(factory, $"{songRef}-v1", Clips.Minimal("clip-archived"));
        TestDatabase.Execute(factory.DataPath, "UPDATE generations SET state = 'archived' WHERE suno_id = 'clip-archived';");
        var artist = await ArtistAsync(client, "The Credited");
        await CreditAsync(client, songRef, artist);
        TestDatabase.Execute(factory.DataPath, "INSERT INTO provider_tombstones (suno_id, kind, deleted_utc, title) VALUES ('clip-deleted', 'clip', '2026-10-01T00:00:00.000Z', 'Gone');");
        TestDatabase.Execute(factory.DataPath, "INSERT INTO suno_ignored_items (suno_id, title, ignored_utc) VALUES ('clip-ignored', 'Ignored', '2026-10-01T00:00:00.000Z');");
        Assert.Equal("archived", TestDatabase.Scalar(factory.DataPath, "SELECT state FROM generations WHERE suno_id = 'clip-archived';"));
        var before = RestoreApiFingerprint(factory);

        var items = await LookupAsync(client, token, "clip-new", "clip-held", "clip-deleted", "clip-held", "clip-ignored", "clip-archived");

        Assert.Equal(["clip-new", "clip-held", "clip-deleted", "clip-ignored", "clip-archived"], items.Select(static item => item.GetProperty("sunoId").GetString()));
        var byId = items.ToDictionary(static item => item.GetProperty("sunoId").GetString()!);
        var heldRow = byId["clip-held"];
        Assert.Equal(held.Generation.Id, heldRow.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal(held.Shortcode, heldRow.GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal("The Credited", heldRow.GetProperty("artist").GetString());
        Assert.False(heldRow.GetProperty("deleted").GetBoolean());
        Assert.Empty(heldRow.GetProperty("downloadedFormats").EnumerateArray());

        // Archived Generations count as in n8Tracks.
        Assert.Equal(archived.Shortcode, byId["clip-archived"].GetProperty("generation").GetProperty("shortcode").GetString());

        foreach (var (sunoId, deleted) in new[] { ("clip-new", false), ("clip-deleted", true), ("clip-ignored", false) })
        {
            var row = byId[sunoId];
            Assert.Equal(JsonValueKind.Null, row.GetProperty("generation").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("artist").ValueKind);
            Assert.Equal(deleted, row.GetProperty("deleted").GetBoolean());
            Assert.Empty(row.GetProperty("downloadedFormats").EnumerateArray());
        }

        // Complement: the lookup changed nothing in the database.
        Assert.Equal(before, RestoreApiFingerprint(factory));
    }

    [Fact]
    public async Task AGenerationWhoseSongHasNoPrimaryArtistAnswersArtistNull()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Uncredited");
        await SongApi.AttachGenerationAsync(factory, $"{song.GetProperty("shortcode").GetString()}-v1", Clips.Minimal("clip-uncredited"));
        await CreditAsync(client, song.GetProperty("shortcode").GetString()!, null);

        var row = Assert.Single(await LookupAsync(client, token, "clip-uncredited"));

        Assert.Equal(JsonValueKind.Object, row.GetProperty("generation").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("artist").ValueKind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"sunoIds":[]}""")]
    [InlineData("""{"sunoIds":"clip"}""")]
    [InlineData("""{"sunoIds":[1]}""")]
    [InlineData("""{"sunoIds":[""]}""")]
    [InlineData("""{"sunoIds":["  "]}""")]
    [InlineData("""{"sunoIds":[null]}""")]
    public async Task RefusesABodyThatIsNotOneTo500SunoIds(string body)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        using var response = await SendAsync(client, token, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await SetupApi.JsonAsync(response);
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("sunoIds", out _));
    }

    [Fact]
    public async Task TakesExactly500SunoIdsAndRefuses501()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        string Ids(int count) => new JsonObject { ["sunoIds"] = new JsonArray([.. Enumerable.Range(0, count).Select(static i => (JsonNode)$"clip-{i}")]) }.ToJsonString();

        using var full = await SendAsync(client, token, Ids(500));
        using var over = await SendAsync(client, token, Ids(501));

        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(500, (await SetupApi.JsonAsync(full)).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
    }

    [Fact]
    public async Task NeedsSunoSync()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generateOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using var anonymous = factory.CreateClient();

        using var refusedGenerate = await SendAsync(client, generateOnly, """{"sunoIds":["clip"]}""");
        using var refusedReader = await SendAsync(client, reader, """{"sunoIds":["clip"]}""");
        using var noToken = await anonymous.PostAsync(Lookup, new StringContent("""{"sunoIds":["clip"]}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, refusedGenerate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refusedReader.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }

    private static string RestoreApiFingerprint(N8TracksApiFactory factory) =>
        string.Join(
            '\n',
            TestDatabase.Rows(factory.DataPath, "SELECT id || ':' || state || ':' || revision FROM generations ORDER BY id;")
                .Concat(TestDatabase.Rows(factory.DataPath, "SELECT suno_id FROM provider_tombstones ORDER BY suno_id;"))
                .Concat(TestDatabase.Rows(factory.DataPath, "SELECT suno_id FROM suno_ignored_items ORDER BY suno_id;"))
                .Concat(TestDatabase.Rows(factory.DataPath, "SELECT id || ':' || revision FROM songs ORDER BY id;")));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Lookup)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement[]> LookupAsync(HttpClient client, string token, params string[] sunoIds)
    {
        using var response = await SendAsync(client, token, new JsonObject { ["sunoIds"] = new JsonArray([.. sunoIds.Select(static id => (JsonNode)id)]) }.ToJsonString());
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static async Task<Guid> ArtistAsync(HttpClient client, string name)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/artists", UriKind.Relative), JsonSerializer.Serialize(new { name, confirmDuplicate = true }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task CreditAsync(HttpClient client, string reference, Guid? primary)
    {
        using var read = await client.GetAsync(SongApi.Song(reference));
        var revision = (await SetupApi.JsonAsync(read)).GetProperty("revision").GetInt32();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/songs/{reference}/credits", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { primaryArtistId = primary, featuredArtistIds = Array.Empty<Guid>() }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
