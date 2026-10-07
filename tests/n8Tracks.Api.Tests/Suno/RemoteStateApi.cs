using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Setup;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>Following Suno's Trash, restores, and missing clips (#142): the Suno state changes of an export.</summary>
internal static class RemoteStateApi
{
    /// <summary>When the tests' syncs captured the library: after every Generation a test attaches.</summary>
    public const string CapturedLater = "2099-01-01T00:00:00.000Z";

    /// <summary>The Suno state changes of the export.</summary>
    public static Uri RemoteStates(Guid exportId, string query = "") => SunoExportApi.Export(exportId, "/remote-states" + query);

    /// <summary>A header as the extension sends it, captured after every Generation the test attached.</summary>
    public static JsonObject Header(string scope = "library", bool libraryComplete = true, bool trashedComplete = true)
    {
        var header = SunoExportApi.Header(scope, libraryComplete, trashedComplete);
        header["capturedAt"] = CapturedLater;
        return header;
    }

    /// <summary>A clip as Suno lists it: its ID and title (the minimal clip the tests attach).</summary>
    public static JsonNode Clip(string sunoId, string title) =>
        new JsonObject { ["id"] = sunoId, ["status"] = "complete", ["title"] = title };

    /// <summary>Uploads an export of <paramref name="listed"/> and <paramref name="trashed"/> clips and returns its ID once ready.</summary>
    public static async Task<Guid> ExportAsync(HttpClient client, string token, IEnumerable<JsonNode> listed, IEnumerable<JsonNode> trashed, JsonObject? header = null)
    {
        var (id, export) = await SunoExportApi.UploadAsync(client, token, header ?? Header(), SunoExportApi.Part(1, listed, trashed));
        Assert.Equal("ready", export.GetProperty("state").GetString());
        return id;
    }

    /// <summary>The Suno state changes as the signed-in user reads them, asserting 200.</summary>
    public static async Task<JsonElement> ListAsync(HttpClient client, Guid exportId, string query = "")
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(RemoteStates(exportId, query));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Each row by Suno ID.</summary>
    public static async Task<Dictionary<string, JsonElement>> RowsAsync(HttpClient client, Guid exportId) =>
        (await ListAsync(client, exportId, "?pageSize=200")).GetProperty("items").EnumerateArray()
            .ToDictionary(static row => row.GetProperty("sunoId").GetString()!, StringComparer.Ordinal);

    /// <summary>PATCHes a change of rows with <paramref name="ifMatch"/> (none when null); the caller reads the answer.</summary>
    public static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid exportId, string? ifMatch, string json)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Patch, RemoteStates(exportId))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(Auth.SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    /// <summary>Sets the rows of <paramref name="sunoIds"/> to apply or Skip at the export's current revision, asserting 200.</summary>
    public static async Task SetAsync(HttpClient client, Guid exportId, bool apply, params string[] sunoIds)
    {
        using var response = await PatchAsync(client, exportId, await ImportCommitApi.IfMatchAsync(client, exportId), JsonSerializer.Serialize(new { sunoIds, apply }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The remote-state rows of a commit's result, by Suno ID.</summary>
    public static Dictionary<string, JsonElement> Results(JsonElement result) =>
        result.GetProperty("remoteStates").EnumerateArray().ToDictionary(static row => row.GetProperty("sunoId").GetString()!, StringComparer.Ordinal);

    /// <summary>A Generation as the signed-in user reads it.</summary>
    public static async Task<JsonElement> GenerationAsync(HttpClient client, string reference)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(new Uri($"/api/v1/generations/{reference}", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>A Generation's state, remote state, and archiver.</summary>
    public static async Task<(string State, string RemoteState, string? ArchivedBy)> StatesAsync(HttpClient client, string reference)
    {
        var generation = await GenerationAsync(client, reference);
        return (generation.GetProperty("state").GetString()!, generation.GetProperty("remoteState").GetString()!, generation.GetProperty("archivedBy").GetString());
    }

    /// <summary>Archives (or reactivates) a Generation by hand, as the signed-in user, asserting 200.</summary>
    public static async Task SetStateAsync(HttpClient client, string reference, string state)
    {
        ArgumentNullException.ThrowIfNull(client);

        var revision = (await GenerationAsync(client, reference)).GetProperty("revision").GetInt32();
        var uri = new Uri($"/api/v1/generations/{reference}", UriKind.Relative);
        using var request = new HttpRequestMessage(HttpMethod.Patch, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { state }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(Auth.SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\""));
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
