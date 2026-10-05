using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;

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

    /// <summary>
    /// PATCHes an edit of the Song with <paramref name="id"/>, with the anti-forgery header and, when
    /// given, <paramref name="ifMatch"/> exactly as written; the caller reads the answer.
    /// <paramref name="json"/> is sent as it is, so a test can leave a field out or send it as null.
    /// </summary>
    public static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string id, string? ifMatch, string json)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Patch, Song(id))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    /// <summary>Edits a Song at <paramref name="revision"/> and returns it, asserting 200.</summary>
    public static async Task<JsonElement> EditAsync(HttpClient client, string id, int revision, string json)
    {
        using var response = await PatchAsync(client, id, Quoted(revision), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>A revision as <c>If-Match</c> carries it.</summary>
    public static string Quoted(int revision) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\"{revision}\"");

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
    /// Sends <paramref name="json"/> as it is with the anti-forgery header, as the web UI does; the
    /// caller reads the answer.
    /// </summary>
    public static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, Uri uri, string json)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Adds a Version to the Song <c>n8-<paramref name="shortcodeNumber"/></c> straight in the
    /// database, with the lyrics and styles given, and returns its ID. The database records its number
    /// as used.
    /// </summary>
    public static Guid AddVersionDirectly(
        string dataPath,
        long shortcodeNumber,
        string number,
        string visibility = VersionRecord.Active,
        string lyrics = "",
        string styles = "")
    {
        var id = Guid.CreateVersion7();
        TestDatabase.Execute(
            dataPath,
            $"""
            INSERT INTO versions (id, song_id, number, number_sort_key, visibility, lyrics, styles, created_utc, updated_utc, revision)
            SELECT '{id.ToString().ToUpperInvariant()}', id, '{number}', '{VersionNumbers.SortKey(number)}', '{visibility}', '{lyrics.Replace("'", "''", StringComparison.Ordinal)}', '{styles.Replace("'", "''", StringComparison.Ordinal)}', '2026-10-03T10:00:00.000Z', '2026-10-03T10:00:00.000Z', 1
            FROM songs WHERE shortcode_number = {shortcodeNumber};
            """);
        return id;
    }

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
