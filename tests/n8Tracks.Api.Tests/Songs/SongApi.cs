using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// Calls the Songs API as the web UI does, and reaches into the test database for what no endpoint
/// does yet. Later catalog stories use <see cref="CreateAsync"/> to get a Song to work on.
/// </summary>
internal static class SongApi
{
    public static readonly Uri Songs = new("/api/v1/songs", UriKind.Relative);
    public static readonly Uri WorkflowStates = new("/api/v1/workflow-states", UriKind.Relative);

    /// <summary>A host whose clock is <paramref name="clock"/> when one is given.</summary>
    public static N8TracksApiFactory Host(TimeProvider? clock = null) =>
        new()
        {
            TestServices = services =>
            {
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }
            },
        };

    /// <summary>A Song's URI, by ID or shortcode.</summary>
    public static Uri Song(string reference) => new($"/api/v1/songs/{reference}", UriKind.Relative);

    /// <summary>POSTs a create request with the anti-forgery header; the caller reads the answer.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, Songs) { Content = JsonContent.Create(body) };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        return await client.SendAsync(request);
    }

    /// <summary>Creates a Song and returns it, asserting it was created.</summary>
    public static async Task<JsonElement> CreateAsync(HttpClient client, string title, string? concept = null)
    {
        using var response = await PostAsync(client, new { title, concept });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>GETs the list with <paramref name="query"/> (without the <c>?</c>) and returns the body, asserting 200.</summary>
    public static async Task<JsonElement> ListAsync(HttpClient client, string query = "")
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(new Uri($"/api/v1/songs?{query}", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>The shortcodes of a list body's items, in order.</summary>
    public static List<string> Shortcodes(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(static song => song.GetProperty("shortcode").GetString()!)];

    /// <summary>
    /// Removes a Song and its Versions straight from the database, as a deletion would once there is
    /// one: the only way, in this story, for a Song to go away.
    /// </summary>
    public static void RemoveDirectly(string dataPath, long shortcodeNumber)
    {
        var id = $"(SELECT id FROM songs WHERE shortcode_number = {shortcodeNumber})";
        TestDatabase.Execute(
            dataPath,
            $"""
            UPDATE songs SET current_version_id = NULL WHERE shortcode_number = {shortcodeNumber};
            DELETE FROM versions WHERE song_id = {id};
            DELETE FROM songs WHERE shortcode_number = {shortcodeNumber};
            """);
    }
}
