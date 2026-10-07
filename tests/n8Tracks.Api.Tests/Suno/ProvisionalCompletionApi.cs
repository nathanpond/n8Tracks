using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Helpers for the completion of observed Generations (#154): a request whose observed Create made two
/// Generations, the TS-001 finished clip with a chosen Suno ID and status, and the report as the extension
/// sends it.
/// </summary>
internal static class ProvisionalCompletionApi
{
    private const string Mode = "songs-advanced";

    /// <summary>
    /// A Song whose Version a Generate on Suno request was made from, claimed with a <c>suno.generate</c>
    /// token, and an observed Create of <paramref name="clipIds"/> recorded on it: the Generations are
    /// still generating (Suno's status <c>submitted</c>).
    /// </summary>
    public static async Task<Observed> ObservedAsync(N8TracksApiFactory factory, HttpClient client, string title, params string[] clipIds)
    {
        var song = await SongApi.CreateAsync(client, title);
        var versionId = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        var response = ObservedCreateApi.Response(Mode, $"request-{clipIds[0]}", clipIds);
        var request = ObservedCreateApi.Request(Mode);
        await EditAsync(client, versionId, ObservedCreateApi.MatchingEdit(response, request));
        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);
        var recorded = await ObservedCreateApi.RecordAsync(client, token, id, response, request);
        var generations = recorded.GetProperty("observed")[0].GetProperty("generations").EnumerateArray()
            .ToDictionary(static item => item.GetProperty("sunoId").GetString()!, static item => (item.GetProperty("id").GetGuid(), item.GetProperty("shortcode").GetString()!), StringComparer.Ordinal);
        return new Observed(song, versionId, id, token, generations);
    }

    /// <summary>
    /// Suno's finished clip (TS-001, <c>feed-v3.completed-clip</c>) as clip <paramref name="sunoId"/>, with
    /// <paramref name="status"/>: what the extension reads from the feed once Suno has finished it.
    /// </summary>
    public static JsonObject Finished(string sunoId, string status = "complete")
    {
        var clip = JsonNode.Parse(Clips.FixtureClip("feed-v3.completed-clip.response.json"))!.AsObject();
        clip["id"] = sunoId;
        clip["status"] = status;
        return clip;
    }

    /// <summary>Reports <paramref name="clip"/> as finished on the request <paramref name="id"/>.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string? token, Guid id, JsonNode? clip) =>
        ObservedCreateApi.SendAsync(client, HttpMethod.Post, Path(id), token, new JsonObject { ["clip"] = clip?.DeepClone() }.ToJsonString());

    /// <summary>Reports <paramref name="clip"/> and expects the Generation completed: the answer.</summary>
    public static async Task<JsonElement> CompleteAsync(HttpClient client, string token, Guid id, JsonObject clip)
    {
        using var answer = await PostAsync(client, token, id, clip);
        Assert.True(answer.StatusCode == HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(answer);
    }

    public static Uri Path(Guid id) => new($"/api/v1/suno/generation-requests/{id}/clips", UriKind.Relative);

    /// <summary>The Generation <paramref name="shortcode"/> as the API answers it.</summary>
    public static async Task<JsonElement> GenerationAsync(HttpClient client, string shortcode)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Expects <paramref name="response"/> to be a problem with <paramref name="status"/> and <paramref name="code"/>.</summary>
    public static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, body);
            Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        }
    }

    private static async Task EditAsync(HttpClient client, Guid versionId, string json)
    {
        using var current = await client.GetAsync(new Uri($"/api/v1/versions/{versionId}", UriKind.Relative));
        var revision = (await SetupApi.JsonAsync(current)).GetProperty("revision").GetInt32();
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/versions/{versionId}", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// What <see cref="ObservedAsync"/> made: the Song, its Version, the request and its token, and each
    /// clip's Generation (ID and shortcode) by Suno ID.
    /// </summary>
    public sealed record Observed(
        JsonElement Song,
        Guid VersionId,
        Guid RequestId,
        string Token,
        IReadOnlyDictionary<string, (Guid Id, string Shortcode)> Generations);
}
