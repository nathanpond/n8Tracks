using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Calls for the Suno workspace tests (#129): reporting workspaces as the extension does (a
/// <c>suno.sync</c> token), listing them, associating a Song, and moving Songs in bulk.
/// </summary>
internal static class SunoWorkspaceApi
{
    public static readonly Uri Workspaces = new("/api/v1/suno/workspaces", UriKind.Relative);
    public static readonly Uri Discovered = new("/api/v1/suno/workspaces/discovered", UriKind.Relative);

    public static Uri MoveSongs(string sunoId) => new($"/api/v1/suno/workspaces/{Uri.EscapeDataString(sunoId)}/move-songs", UriKind.Relative);

    /// <summary>A token holding only <c>suno.sync</c>, as the extension pairs with.</summary>
    public static Task<string> ExtensionTokenAsync(N8TracksApiFactory factory) => CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);

    /// <summary>A raw project as Suno's workspace list sends it, with only the fields given (null leaves one out).</summary>
    public static JsonObject Project(string id, string? name = null, bool? trashed = null, string? description = null)
    {
        var project = new JsonObject { ["id"] = id, ["clip_count"] = 0, ["shared"] = false };
        if (name is not null)
        {
            project["name"] = name;
        }

        if (description is not null)
        {
            project["description"] = description;
        }

        if (trashed is not null)
        {
            project["is_trashed"] = trashed;
        }

        return project;
    }

    /// <summary>The <c>projects</c> of a captured workspace list page (<c>project-me.*</c>).</summary>
    public static JsonArray FixtureProjects(string name) => JsonNode.Parse(Clips.Fixture(name))!["projects"]!.AsArray();

    /// <summary>PUTs a report with the token; the caller reads the answer.</summary>
    public static async Task<HttpResponseMessage> SendReportAsync(HttpClient client, string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Discovered)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>Reports <paramref name="projects"/> and returns the answer, asserting 200.</summary>
    public static async Task<JsonElement> ReportAsync(HttpClient client, string token, bool complete, params JsonNode[] projects)
    {
        var body = new JsonObject { ["complete"] = complete, ["workspaces"] = new JsonArray([.. projects.Select(static project => project.DeepClone())]) };
        using var response = await SendReportAsync(client, token, body.ToJsonString());
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Every workspace as the list answers it.</summary>
    public static async Task<JsonElement[]> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Workspaces);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    /// <summary>The workspace with Suno ID <paramref name="id"/> as the list answers it.</summary>
    public static async Task<JsonElement> OneAsync(HttpClient client, string id) =>
        (await ListAsync(client)).Single(item => item.GetProperty("id").GetString() == id);

    /// <summary>Sets the Song's workspace (Suno ID, or null) through its PATCH; the caller reads the answer.</summary>
    public static async Task<HttpResponseMessage> AssociateAsync(HttpClient client, string song, string? sunoId)
    {
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song)))).GetProperty("revision").GetInt32();
        return await SongApi.PatchAsync(client, song, SongApi.Quoted(revision), new JsonObject { ["sunoWorkspaceId"] = sunoId }.ToJsonString());
    }

    /// <summary>Sets the Song's workspace and returns the Song, asserting 200.</summary>
    public static async Task<JsonElement> AssociatedAsync(HttpClient client, string song, string? sunoId)
    {
        using var response = await AssociateAsync(client, song, sunoId);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>POSTs a bulk move as the signed-in user; the caller reads the answer.</summary>
    public static Task<HttpResponseMessage> MoveAsync(HttpClient client, string from, string json) =>
        SongApi.SendJsonAsync(client, HttpMethod.Post, MoveSongs(from), json);
}
