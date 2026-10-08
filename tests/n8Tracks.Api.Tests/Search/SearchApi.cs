using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;

namespace n8Tracks.Api.Tests.Search;

/// <summary>Searches the catalog (#223) and puts text into it, as the web UI and the API's clients do.</summary>
internal static class SearchApi
{
    /// <summary>GETs the Songs matching <paramref name="text"/> (with <paramref name="extra"/> appended to the query), asserting 200.</summary>
    public static Task<JsonElement> SearchAsync(HttpClient client, string text, string extra = "") =>
        SongApi.ListAsync(client, $"search={Uri.EscapeDataString(text)}{extra}");

    /// <summary>The shortcodes the search for <paramref name="text"/> finds, in order.</summary>
    public static async Task<List<string>> FoundAsync(HttpClient client, string text) =>
        SongApi.Shortcodes(await SearchAsync(client, text));

    /// <summary>The item for <paramref name="shortcode"/> in a list body.</summary>
    public static JsonElement Item(JsonElement list, string shortcode) =>
        Assert.Single(list.GetProperty("items").EnumerateArray(), item => item.GetProperty("shortcode").GetString() == shortcode);

    /// <summary>A Version, read.</summary>
    public static async Task<JsonElement> VersionAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{reference}", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Edits a Version at the revision it has now, asserting 200.</summary>
    public static async Task EditVersionAsync(HttpClient client, string reference, string json)
    {
        var revision = (await VersionAsync(client, reference)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/versions/{reference}", revision, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Creates a Tag; its ID.</summary>
    public static async Task<string> TagAsync(HttpClient client, string name)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/tags", UriKind.Relative), JsonSerializer.Serialize(new { name }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    /// <summary>Gives the Song with <paramref name="songId"/> these Tags, at the revision it has now.</summary>
    public static async Task TagSongAsync(HttpClient client, string songId, params string[] tagIds)
    {
        var revision = (await SongAsync(client, songId)).GetProperty("revision").GetInt32();
        _ = await SongApi.EditAsync(client, songId, revision, JsonSerializer.Serialize(new { tagIds }));
    }

    /// <summary>A Song, read.</summary>
    public static async Task<JsonElement> SongAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(SongApi.Song(reference));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Creates an Album or Playlist (<paramref name="collection"/> is <c>albums</c> or <c>playlists</c>); its ID.</summary>
    public static async Task<string> CollectionAsync(HttpClient client, string collection, string title)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/{collection}", UriKind.Relative), JsonSerializer.Serialize(new { title }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    /// <summary>Puts the Song on the Album (its tracks) or Playlist (its Songs), at the record's revision now.</summary>
    public static async Task AddToAsync(HttpClient client, string collection, string id, string songId)
    {
        var revision = await RevisionAsync(client, $"/api/v1/{collection}/{id}");
        var path = collection == "albums" ? $"/api/v1/albums/{id}/tracks" : $"/api/v1/playlists/{id}/songs";
        using var response = await SendAsync(client, HttpMethod.Post, path, revision, JsonSerializer.Serialize(new { songId }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The <c>revision</c> of what <paramref name="path"/> answers.</summary>
    public static async Task<int> RevisionAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("revision").GetInt32();
    }

    /// <summary>Keeps a comment on a Generation; the comment.</summary>
    public static async Task<JsonElement> CommentAsync(HttpClient client, string generation, string text)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/generations/{generation}/comments", UriKind.Relative), JsonSerializer.Serialize(new { text }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>A raw Suno clip with the fields search reads, each that is given.</summary>
    public static string Clip(string sunoId, string? title = null, string? tags = null, string? modelVersion = null, string? modelName = null, string? modelLabel = null)
    {
        var metadata = new JsonObject();
        if (tags is not null)
        {
            metadata["tags"] = tags;
        }

        if (modelLabel is not null)
        {
            metadata["model_badges"] = new JsonObject { ["songrow"] = new JsonObject { ["display_name"] = modelLabel } };
        }

        var clip = new JsonObject { ["id"] = sunoId, ["status"] = "complete", ["metadata"] = metadata };
        if (title is not null)
        {
            clip["title"] = title;
        }

        if (modelVersion is not null)
        {
            clip["major_model_version"] = modelVersion;
        }

        if (modelName is not null)
        {
            clip["model_name"] = modelName;
        }

        return clip.ToJsonString();
    }

    /// <summary>Sends <paramref name="json"/> (or nothing) with the anti-forgery header and, when given, the revision as <c>If-Match</c>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int? revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (revision is { } value)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
        }

        return await client.SendAsync(request);
    }

    /// <summary>The fields of an item's matches, in order.</summary>
    public static List<string> Fields(JsonElement item) =>
        [.. item.GetProperty("matches").EnumerateArray().Select(static match => match.GetProperty("field").GetString()!)];
}
