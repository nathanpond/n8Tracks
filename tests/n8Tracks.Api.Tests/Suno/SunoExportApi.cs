using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Calls for the Suno export tests (#131): building exports from the committed TS-003 fixtures, and
/// uploading, completing, and reading them as the extension does (a <c>suno.sync</c> token) or as the
/// signed-in user does (no token).
/// </summary>
internal static class SunoExportApi
{
    public static readonly Uri Exports = new("/api/v1/suno/exports", UriKind.Relative);

    /// <summary>The library clips of the fixture pages, in order: four clips, each in a workspace.</summary>
    public static readonly string[] LibraryFixtures = ["feed-v3.library-page-1.response.json", "feed-v3.library-page-2.response.json"];

    /// <summary>The Trash list fixture: two clips.</summary>
    public const string TrashFixture = "clips-trashed-v2.response.json";

    public static Uri Export(Guid id, string suffix = "") => new($"/api/v1/suno/exports/{id}{suffix}", UriKind.Relative);

    /// <summary>Every clip of the fixture <paramref name="name"/> (<c>clips</c>), each as its own JSON node.</summary>
    public static List<JsonNode> FixtureClips(string name) =>
        [.. JsonNode.Parse(Clips.Fixture(name))!["clips"]!.AsArray().Select(static clip => clip!.DeepClone())];

    /// <summary>The library clips of both fixture pages.</summary>
    public static List<JsonNode> LibraryClips() => [.. LibraryFixtures.SelectMany(FixtureClips)];

    /// <summary>The Suno ID of a clip node.</summary>
    public static string IdOf(JsonNode clip) => clip["id"]!.GetValue<string>();

    /// <summary>An export header as the extension sends it, with the fields given.</summary>
    public static JsonObject Header(
        string scope = "library",
        bool libraryComplete = true,
        bool trashedComplete = true,
        JsonArray? workspaces = null,
        bool workspacesComplete = false,
        JsonArray? playlists = null,
        int formatVersion = 1) =>
        new()
        {
            ["format"] = "n8tracks.suno-export",
            ["formatVersion"] = formatVersion,
            ["extensionVersion"] = "0.1.0",
            ["adapterVersion"] = 1,
            ["capturedAt"] = "2026-10-06T12:00:00.000Z",
            ["scope"] = new JsonObject { ["kind"] = scope, ["ids"] = new JsonArray() },
            ["libraryComplete"] = libraryComplete,
            ["trashedComplete"] = trashedComplete,
            ["workspaces"] = workspaces ?? [],
            ["workspacesComplete"] = workspacesComplete,
            ["playlists"] = playlists ?? [],
        };

    /// <summary>A part's body: its number, library clips, Trash clips, and playlists.</summary>
    public static JsonObject Part(int number, IEnumerable<JsonNode>? clips = null, IEnumerable<JsonNode>? trashed = null, JsonArray? playlists = null)
    {
        var part = new JsonObject
        {
            ["partNumber"] = number,
            ["clips"] = new JsonArray([.. (clips ?? []).Select(static clip => clip.DeepClone())]),
            ["trashedClips"] = new JsonArray([.. (trashed ?? []).Select(static clip => clip.DeepClone())]),
        };
        if (playlists is not null)
        {
            part["playlists"] = playlists.DeepClone();
        }

        return part;
    }

    /// <summary>A playlist entry: <c>{ id, name, clipIds }</c>.</summary>
    public static JsonObject Playlist(string id, string name, params string[] clipIds) =>
        new() { ["id"] = id, ["name"] = name, ["clipIds"] = new JsonArray([.. clipIds.Select(static clip => (JsonNode)clip)]) };

    /// <summary>Sends <paramref name="json"/> with <paramref name="token"/>, or as the signed-in user when null; the caller reads the answer.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string? token, string? json = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(method, uri);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (token is null)
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Creates an export from <paramref name="header"/> and returns its ID, asserting 201.</summary>
    public static async Task<Guid> CreateAsync(HttpClient client, string? token, JsonObject? header = null)
    {
        using var response = await SendAsync(client, HttpMethod.Post, Exports, token, (header ?? Header()).ToJsonString());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Uploads <paramref name="part"/>, asserting 200, and returns the export.</summary>
    public static async Task<JsonElement> PartAsync(HttpClient client, string? token, Guid id, JsonObject part)
    {
        using var response = await SendAsync(client, HttpMethod.Post, Export(id, "/parts"), token, part.ToJsonString());
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Completes the export, asserting 200, and returns it.</summary>
    public static async Task<JsonElement> CompleteAsync(HttpClient client, string? token, Guid id)
    {
        using var response = await SendAsync(client, HttpMethod.Post, Export(id, "/complete"), token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Creates, uploads each part, and completes; returns the ID and the completed export.</summary>
    public static async Task<(Guid Id, JsonElement Export)> UploadAsync(HttpClient client, string? token, JsonObject header, params JsonObject[] parts)
    {
        var id = await CreateAsync(client, token, header);
        foreach (var part in parts)
        {
            await PartAsync(client, token, id, part);
        }

        return (id, await CompleteAsync(client, token, id));
    }

    /// <summary>Reads the export, asserting 200.</summary>
    public static async Task<JsonElement> GetAsync(HttpClient client, string? token, Guid id)
    {
        using var response = await SendAsync(client, HttpMethod.Get, Export(id), token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>A page of the export's records as the signed-in user reads it, asserting 200.</summary>
    public static async Task<JsonElement> RecordsAsync(HttpClient client, Guid id, string query = "")
    {
        using var response = await client.GetAsync(Export(id, "/records" + query));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Every record of the export (one page of 200), by Suno ID.</summary>
    public static async Task<Dictionary<string, JsonElement>> RecordsByIdAsync(HttpClient client, Guid id, string query = "")
    {
        var page = await RecordsAsync(client, id, query.Length == 0 ? "?pageSize=200" : query + "&pageSize=200");
        return page.GetProperty("items").EnumerateArray().ToDictionary(static item => item.GetProperty("sunoId").GetString()!, StringComparer.Ordinal);
    }

    /// <summary>The count of <paramref name="name"/> in an export answer's <c>counts</c>.</summary>
    public static int Count(JsonElement export, string name) => export.GetProperty("counts").GetProperty(name).GetInt32();

    /// <summary>Puts <paramref name="sunoId"/> on the ignore list directly, as the ignore-list story (#143) will at a commit.</summary>
    public static void Ignore(N8TracksApiFactory factory, string sunoId) =>
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO suno_ignored_items (suno_id, title, ignored_utc) VALUES ('{sunoId}', 'Ignored', '2026-10-01T00:00:00.000Z');");

    /// <summary>How many rows each staging table holds for the export.</summary>
    public static (int Parts, int Records, int Memberships) StagedRows(N8TracksApiFactory factory, Guid id)
    {
        var key = id.ToString().ToUpperInvariant();
        int Rows(string table) => int.Parse(
            TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM {table} WHERE upper(export_id) = '{key}';"),
            System.Globalization.CultureInfo.InvariantCulture);
        return (Rows("suno_export_parts"), Rows("suno_export_records"), Rows("suno_export_record_playlists"));
    }

    /// <summary>Calls the staging service in a scope of its own.</summary>
    public static async Task<T> WithServiceAsync<T>(N8TracksApiFactory factory, Func<ExportStagingService, Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(call);

        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await call(scope.ServiceProvider.GetRequiredService<ExportStagingService>());
        }
    }
}
