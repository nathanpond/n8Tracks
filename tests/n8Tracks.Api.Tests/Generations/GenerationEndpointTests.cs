using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Generations;

/// <summary>
/// Reading Generations (#117): <c>GET /api/v1/versions/{reference}/generations</c>,
/// <c>GET /api/v1/songs/{reference}/generations</c>, and <c>GET /api/v1/generations/{reference}</c>
/// (<c>catalog.read</c>), and <c>GET /api/v1/generations/{reference}/provider-record</c> (session only).
/// The raw clip is answered by the last alone, byte for byte; nothing answers anything about events.
/// </summary>
public sealed class GenerationEndpointTests
{
    private const string Secret = "sentinel-raw-clip-prompt-5e2a";

    [Fact]
    public async Task AVersionsAndASongsGenerationsAreListedInOrderAndEachIsReadByIdOrShortcode()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Generated");
        await SongApi.CreateAsync(client, "Bystander");
        await NewVersionAsync(client, "n8-1", "n8-1-v1", "2");
        await NewVersionAsync(client, "n8-1", "n8-1-v1", "1.1");

        // Attached out of tree order: Version 2 first, then 1.1, then two on Version 1.
        var completed = Clips.FixtureClip("feed-v3.completed-clip.response.json");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", Clips.Minimal("clip-on-two"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1.1", null);
        var first = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", completed);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("clip-two", "submitted"));
        await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal("someone-elses"));

        // The Song's list: by Version number in tree order, then ordinal; other Songs' are not in it.
        foreach (var reference in new[] { song.GetProperty("id").GetString()!, "n8-1", "N8-1" })
        {
            var list = await ListAsync(client, $"songs/{reference}/generations");
            Assert.Equal(["n8-1-v1-g1", "n8-1-v1-g2", "n8-1-v1.1-g1", "n8-1-v2-g1"], Shortcodes(list));
        }

        // A Version's list, in ordinal order, by its ID or shortcode.
        var versionId = song.GetProperty("currentVersion").GetProperty("id").GetString()!;
        foreach (var reference in new[] { versionId, "n8-1-v1", "N8-1-V1" })
        {
            Assert.Equal(["n8-1-v1-g1", "n8-1-v1-g2"], Shortcodes(await ListAsync(client, $"versions/{reference}/generations")));
        }

        Assert.Equal(["n8-2-v1-g1"], Shortcodes(await ListAsync(client, "versions/n8-2-v1/generations")));

        // One, by ID or shortcode (any case): the same answer, with its revision as the ETag.
        using var byId = await client.GetAsync(new Uri($"/api/v1/generations/{first.Generation.Id}", UriKind.Relative));
        using var byShortcode = await client.GetAsync(new Uri("/api/v1/generations/N8-1-V1-G1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Equal("\"1\"", byId.Headers.ETag?.Tag);
        Assert.Contains("no-store", byId.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var read = await SetupApi.JsonAsync(byId);
        Assert.Equal(read.GetRawText(), (await SetupApi.JsonAsync(byShortcode)).GetRawText());

        // Everything the AC names: shortcode, ordinal, owners as {id, shortcode}, normalized fields,
        // both states, the revision, and the (not yet real) selection.
        Assert.Equal(first.Generation.Id.ToString(), read.GetProperty("id").GetString());
        Assert.Equal("n8-1-v1-g1", read.GetProperty("shortcode").GetString());
        Assert.Equal(1, read.GetProperty("ordinal").GetInt32());
        Assert.Equal(song.GetProperty("id").GetString(), read.GetProperty("song").GetProperty("id").GetString());
        Assert.Equal("n8-1", read.GetProperty("song").GetProperty("shortcode").GetString());
        Assert.Equal(versionId, read.GetProperty("version").GetProperty("id").GetString());
        Assert.Equal("n8-1-v1", read.GetProperty("version").GetProperty("shortcode").GetString());
        Assert.Equal("00000000-0000-4000-8000-000000000003", read.GetProperty("sunoId").GetString());
        Assert.Equal("https://suno.com/song/00000000-0000-4000-8000-000000000003", read.GetProperty("sunoUrl").GetString());
        Assert.Equal("complete", read.GetProperty("providerStatus").GetString());
        Assert.Equal("active", read.GetProperty("state").GetString());
        Assert.Equal("present", read.GetProperty("remoteState").GetString());
        Assert.Equal("<redacted title>", read.GetProperty("title").GetString());
        Assert.Equal(143.52, read.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("v6", read.GetProperty("modelVersion").GetString());
        Assert.Equal("chirp-goose", read.GetProperty("modelName").GetString());
        Assert.Equal("<redacted display_name>", read.GetProperty("modelLabel").GetString());
        Assert.Equal("<redacted tags>", read.GetProperty("styleTags").GetString());
        Assert.Equal(68.18, read.GetProperty("minimumBpm").GetDouble());
        Assert.Equal(157.89, read.GetProperty("maximumBpm").GetDouble());
        Assert.Equal(130.05, read.GetProperty("averageBpm").GetDouble());
        Assert.Equal("C_major", read.GetProperty("key").GetString());
        Assert.Equal("2026-10-03T14:19:53.697Z", read.GetProperty("sunoCreatedAt").GetString());
        Assert.Equal("https://studio-api.prod.suno.com/api/forbidden", read.GetProperty("audioUrl").GetString());
        Assert.Equal("https://cdn2.suno.ai/image_00000000-0000-4000-8000-000000000003.jpeg", read.GetProperty("imageUrl").GetString());
        Assert.Equal("00000000-0000-4000-8000-000000000004", read.GetProperty("workspaceId").GetString());
        Assert.Equal(1, read.GetProperty("batchIndex").GetInt32());
        Assert.False(read.GetProperty("isSelected").GetBoolean());
        Assert.Equal(1, read.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.String, read.GetProperty("createdAt").ValueKind);

        // A Generation with no Suno data has none of it.
        var seeded = await ReadAsync(client, "n8-1-v1.1-g1");
        Assert.Equal(JsonValueKind.Null, seeded.GetProperty("sunoId").ValueKind);
        Assert.Equal(JsonValueKind.Null, seeded.GetProperty("sunoUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, seeded.GetProperty("providerStatus").ValueKind);
        Assert.Equal("active", seeded.GetProperty("state").GetString());
        Assert.Equal("present", seeded.GetProperty("remoteState").GetString());
    }

    [Fact]
    public async Task AnUnknownOrDeletedOwnerOrGenerationIsNotFound()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Owner");
        await SongApi.CreateAsync(client, "Gone");
        await NewVersionAsync(client, "n8-1", "n8-1-v1", "2");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("kept"));

        foreach (var path in new[]
        {
            "versions/n8-1-v9/generations", "versions/n8-1/generations", $"versions/{Guid.CreateVersion7()}/generations",
            "songs/n8-9/generations", "songs/n8-1-v1/generations", $"songs/{Guid.CreateVersion7()}/generations",
            "generations/n8-1-v1-g2", "generations/n8-1-v1", "generations/nonsense", $"generations/{Guid.CreateVersion7()}",
            "generations/n8-1-v1-g2/provider-record", $"generations/{Guid.CreateVersion7()}/provider-record",
        })
        {
            using var response = await client.GetAsync(new Uri("/api/v1/" + path, UriKind.Relative));
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // A deleted Version or Song says so, as its own endpoints do.
        var version2 = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v2", UriKind.Relative)));
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v2", version2.GetProperty("revision").GetInt32()))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        await SetupApi.ProblemAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v2/generations", UriKind.Relative)), HttpStatusCode.NotFound, "version_deleted");
        var gone = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-2")));
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-2", gone.GetProperty("revision").GetInt32(), """{"confirmTitle":"Gone"}"""))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await SetupApi.ProblemAsync(await client.GetAsync(new Uri("/api/v1/songs/n8-2/generations", UriKind.Relative)), HttpStatusCode.NotFound, "song_deleted");

        // Complement: a Version with no Generations is an empty list, not a 404.
        Assert.Equal(["n8-1-v1-g1"], Shortcodes(await ListAsync(client, "songs/n8-1/generations")));
        await NewVersionAsync(client, "n8-1", "n8-1-v1", "3");
        Assert.Empty(Shortcodes(await ListAsync(client, "versions/n8-1-v3/generations")));
    }

    [Fact]
    public async Task TheRawClipIsAnsweredByTheProviderRecordAloneExactlyAsStored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Raw");
        var raw = Clips.Handwritten("handwritten-clip", Secret);
        var attached = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", raw);

        // The provider record, by ID and by shortcode: the text as received, byte for byte.
        foreach (var reference in new[] { attached.Generation.Id.ToString(), "n8-1-v1-g1" })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/generations/{reference}/provider-record", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(raw), bytes);

            // And so equal after JSON canonicalisation, the field the reader does not know included.
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(raw), JsonNode.Parse(bytes)));
            Assert.Contains("a_field_from_the_future", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }

        // Every other answer leaves the raw clip out: neither the secret in its prompt nor a field
        // only the raw clip has.
        foreach (var path in new[] { "generations/n8-1-v1-g1", "versions/n8-1-v1/generations", "songs/n8-1/generations", "versions/n8-1-v1", "songs/n8-1", "resolve/n8-1-v1-g1" })
        {
            var body = await (await client.GetAsync(new Uri("/api/v1/" + path, UriKind.Relative))).Content.ReadAsStringAsync();
            Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("a_field_from_the_future", body, StringComparison.Ordinal);
            Assert.DoesNotContain("unknown_to_the_reader", body, StringComparison.Ordinal);
        }

        // A Generation with no Suno data has no provider record.
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null);
        await SetupApi.ProblemAsync(
            await client.GetAsync(new Uri("/api/v1/generations/n8-1-v1-g2/provider-record", UriKind.Relative)),
            HttpStatusCode.NotFound,
            GenerationsEndpoints.NoProviderRecordCode);
    }

    [Fact]
    public async Task ATokenReadsGenerationsWithCatalogReadAndNeverTheProviderRecord()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Scoped");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("scoped-clip"));
        using var tool = factory.CreateClient();
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);
        var everything = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        foreach (var path in new[] { "versions/n8-1-v1/generations", "songs/n8-1/generations", "generations/n8-1-v1-g1" })
        {
            var uri = new Uri("/api/v1/" + path, UriKind.Relative);
            using (var allowed = await CredentialApi.SendAsync(tool, HttpMethod.Get, uri, reader))
            {
                Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            }

            using var refused = await CredentialApi.SendAsync(tool, HttpMethod.Get, uri, others);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        // The provider record is a signed-in session's only: a token with every scope is refused.
        using var record = await CredentialApi.SendAsync(tool, HttpMethod.Get, new Uri("/api/v1/generations/n8-1-v1-g1/provider-record", UriKind.Relative), everything);
        await SetupApi.ProblemAsync(record, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
    }

    [Fact]
    public async Task NoAnswerSaysAnythingAboutGenerationEvents()
    {
        const string RequestId = "provider-request-sentinel-91d3";
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Batched");
        var one = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("batched-clip-a"));
        var two = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("batched-clip-b"));
        var recorded = await GenerationServiceTests.RecordAsync(
            factory,
            new GenerationEventRequest(RequestId, GenerationEventSource.Observed, GenerationEventConfidence.High, 2, DateTimeOffset.UnixEpoch, [one.Generation.Id, two.Generation.Id]));
        Assert.IsType<GenerationEventOutcome.Recorded>(recorded);

        foreach (var path in new[]
        {
            "generations/n8-1-v1-g1", "generations/n8-1-v1-g2", "versions/n8-1-v1/generations", "songs/n8-1/generations",
            "resolve/n8-1-v1-g1", "versions/n8-1-v1", "songs/n8-1",
        })
        {
            using var response = await client.GetAsync(new Uri("/api/v1/" + path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(!body.Contains("event", StringComparison.OrdinalIgnoreCase), $"{path}: {body}");
            Assert.DoesNotContain(RequestId, body, StringComparison.Ordinal);
            Assert.DoesNotContain("requestId", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnAttachRefusalIsAnsweredAsItsProblem()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var existing = new GenerationSummary(new Generation(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 2, DateTimeOffset.UnixEpoch), 7, "1.1");

        var exists = GenerationsEndpoints.AttachRefusal(context, new GenerationAttachOutcome.SunoIdExists(existing));
        Assert.Equal(StatusCodes.Status409Conflict, exists.StatusCode);
        Assert.Equal(GenerationService.SunoIdExistsCode, exists.ProblemDetails.Extensions["code"]);
        Assert.Equal("n8-7-v1.1-g2", exists.ProblemDetails.Extensions["shortcode"]);
        Assert.Equal(existing.Generation.Id, exists.ProblemDetails.Extensions["generationId"]);

        // #130: a clip deleted from n8Tracks names its Suno ID and when it was deleted.
        var deletedUtc = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var tombstoned = GenerationsEndpoints.AttachRefusal(
            context,
            new GenerationAttachOutcome.SunoIdTombstoned(new n8Tracks.Domain.Suno.ProviderTombstone("clip-gone", n8Tracks.Domain.Suno.ProviderTombstoneKind.Clip, deletedUtc, "Gone")));
        Assert.Equal(StatusCodes.Status409Conflict, tombstoned.StatusCode);
        Assert.Equal(GenerationService.SunoIdTombstonedCode, tombstoned.ProblemDetails.Extensions["code"]);
        Assert.Equal("clip-gone", tombstoned.ProblemDetails.Extensions["sunoId"]);
        Assert.Equal(deletedUtc.UtcDateTime, tombstoned.ProblemDetails.Extensions["deletedAt"]);

        var invalid = GenerationsEndpoints.AttachRefusal(context, new GenerationAttachOutcome.InvalidClip("The clip has no Suno ID."));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(GenerationService.InvalidClipCode, invalid.ProblemDetails.Extensions["code"]);
        Assert.Equal(StatusCodes.Status404NotFound, GenerationsEndpoints.AttachRefusal(context, new GenerationAttachOutcome.VersionNotFound()).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, GenerationsEndpoints.AttachRefusal(context, new GenerationAttachOutcome.EventNotFound()).StatusCode);
        Assert.Throws<ArgumentException>(() => GenerationsEndpoints.AttachRefusal(context, new GenerationAttachOutcome.Attached(existing)));
    }

    private static async Task NewVersionAsync(HttpClient client, string song, string source, string number)
    {
        using var created = await SongApi.SendJsonAsync(
            client,
            HttpMethod.Post,
            new Uri($"/api/v1/songs/{song}/versions", UriKind.Relative),
            $$"""{"sourceVersionId":"{{source}}","number":"{{number}}"}""");
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/" + path, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {await response.Content.ReadAsStringAsync()}");
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/generations/{reference}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static List<string?> Shortcodes(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("shortcode").GetString())];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri("/api/v1/" + path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }
}
