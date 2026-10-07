using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Helpers for the observed Create (#149): the TS-003 Create fixtures with fresh IDs, a Version that holds
/// exactly what a fixture submitted, a claimed generation request, and the report itself, as the extension
/// sends it.
/// </summary>
internal static class ObservedCreateApi
{
    /// <summary>The secrets the page's Create request carries, which the extension never forwards.</summary>
    public static readonly string[] Secrets = ["token", "create_session_token", "user_tier"];

    /// <summary>
    /// The Create response of <paramref name="mode"/> (<c>songs-advanced</c>, <c>songs-simple</c>, …) with
    /// Suno's request ID <paramref name="requestId"/> and its clips' IDs <paramref name="clipIds"/> (as many
    /// clips as IDs, copies of the fixture's first ones), each changed by <paramref name="change"/>.
    /// </summary>
    public static JsonObject Response(string mode, string requestId, IReadOnlyList<string> clipIds, Action<JsonObject>? change = null)
    {
        ArgumentNullException.ThrowIfNull(clipIds);

        var response = JsonNode.Parse(Clips.Fixture($"generate-v2-web.{mode}.response.json"))!.AsObject();
        var fixture = response["clips"]!.AsArray();
        var clips = new JsonArray();
        for (var index = 0; index < clipIds.Count; index++)
        {
            var clip = fixture[Math.Min(index, fixture.Count - 1)]!.DeepClone().AsObject();
            clip["id"] = clipIds[index];
            clip["batch_index"] = index;
            change?.Invoke(clip);
            clips.Add(clip);
        }

        response["id"] = requestId;
        response["clips"] = clips;
        return response;
    }

    /// <summary>The Create request of <paramref name="mode"/> as the extension forwards it: the fixture without its secrets.</summary>
    public static JsonObject Request(string mode)
    {
        var request = JsonNode.Parse(Clips.Fixture($"generate-v2-web.{mode}.request.json"))!.AsObject();
        Strip(request);
        return request;
    }

    /// <summary>
    /// The Version edit (PATCH body) that makes a Version hold what <paramref name="response"/>'s first clip
    /// and <paramref name="request"/> submitted, as the observed Create maps them: the options read, lyrics,
    /// and styles; the rest are taken from the Version anyway.
    /// </summary>
    public static string MatchingEdit(JsonObject response, JsonObject? request)
    {
        ArgumentNullException.ThrowIfNull(response);

        using var clip = JsonDocument.Parse(response["clips"]![0]!.ToJsonString());
        using var sent = request is null ? null : JsonDocument.Parse(request.ToJsonString());
        var mapped = ClipInputMapper.MapCreate(clip.RootElement, sent?.RootElement, []);
        var all = VersionInputRules.ToJson(mapped.Inputs);
        var inputs = new JsonObject();
        foreach (var key in mapped.Compared.Keys.Where(static key => key != VersionInputRules.LyricsField && key != VersionInputRules.StylesField))
        {
            inputs[key] = all[key]?.DeepClone();
        }

        return new JsonObject { ["lyrics"] = mapped.Lyrics, ["styles"] = mapped.Styles, ["inputs"] = inputs }.ToJsonString();
    }

    /// <summary>A request made from the Version <paramref name="versionId"/>, claimed with a new <c>suno.generate</c> token and reported waiting: its ID and the token.</summary>
    public static async Task<(Guid Id, string Token)> WaitingRequestAsync(N8TracksApiFactory factory, HttpClient client, Guid versionId)
    {
        ArgumentNullException.ThrowIfNull(client);

        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/versions/{versionId}/generation-requests", UriKind.Relative), "{}");
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await SetupApi.JsonAsync(created)).GetProperty("id").GetGuid();
        using var claimed = await CredentialApi.SendAsync(client, HttpMethod.Post, new Uri($"/api/v1/suno/generation-requests/{id}/claim", UriKind.Relative), token);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        using var waiting = await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/suno/generation-requests/{id}", UriKind.Relative), token, """{"state":"waiting","step":"review and create"}""");
        Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
        return (id, token);
    }

    /// <summary>Reports an observed Create of <paramref name="response"/> and <paramref name="request"/> on the request <paramref name="id"/>.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string? token, Guid id, JsonObject response, JsonObject? request) =>
        SendAsync(
            client,
            HttpMethod.Post,
            Path(id),
            token,
            new JsonObject { ["response"] = response.DeepClone(), ["request"] = request?.DeepClone() }.ToJsonString());

    /// <summary>Reports an observed Create and expects it recorded: the request answered.</summary>
    public static async Task<JsonElement> RecordAsync(HttpClient client, string token, Guid id, JsonObject response, JsonObject? request)
    {
        using var answer = await PostAsync(client, token, id, response, request);
        Assert.True(answer.StatusCode == HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(answer);
    }

    public static Uri Path(Guid id) => new($"/api/v1/suno/generation-requests/{id}/observed-create", UriKind.Relative);

    /// <summary>Sends <paramref name="json"/> with <paramref name="token"/>, or as the session (with the anti-forgery header) when it is null.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string? token, string json)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var message = new HttpRequestMessage(method, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token is null)
        {
            message.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        }
        else
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(message);
    }

    private static void Strip(JsonNode? node)
    {
        if (node is JsonObject item)
        {
            foreach (var secret in Secrets)
            {
                item.Remove(secret);
            }

            foreach (var (_, member) in item.ToList())
            {
                Strip(member);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var member in array)
            {
                Strip(member);
            }
        }
    }
}
