using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Calls for the proposal tests (#138): clips built from a committed TS-003 library clip with the Suno ID,
/// workspace, creation time, <c>batch_index</c>, lyrics, and title a test needs, an export of them, and
/// changes of choices as the signed-in user sends them.
/// </summary>
internal static class ProposalApi
{
    /// <summary>When the first Create request of a test was made.</summary>
    public static readonly DateTimeOffset At = new(2026, 10, 1, 17, 52, 8, 520, TimeSpan.Zero);

    /// <summary>
    /// A library clip (an Advanced Song with no lineage) as Suno would list it with these fields: in
    /// <paramref name="workspace"/> (none when null), made at <paramref name="created"/>, with
    /// <paramref name="batchIndex"/> (none when null), and with <paramref name="lyrics"/> as its lyrics when given.
    /// </summary>
    public static JsonNode Clip(string id, string? workspace, DateTimeOffset created, int? batchIndex, string? lyrics = null, string title = "A Suno title")
    {
        var clip = SunoExportApi.LibraryClips()[0];
        clip["id"] = id;
        clip["title"] = title;
        clip["created_at"] = created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        clip["batch_index"] = batchIndex;
        if (batchIndex is null)
        {
            clip.AsObject().Remove("batch_index");
        }

        if (workspace is null)
        {
            clip.AsObject().Remove("project");
        }
        else
        {
            clip["project"]!["id"] = workspace;
        }

        if (lyrics is not null)
        {
            clip["metadata"]!["prompt"] = lyrics;
        }

        return clip;
    }

    /// <summary>Uploads <paramref name="clips"/> as one export, completes it, and returns its ID and every record by Suno ID.</summary>
    public static async Task<(Guid Id, Dictionary<string, JsonElement> Records)> ExportAsync(HttpClient client, string token, params JsonNode[] clips)
    {
        var (id, export) = await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, clips));
        Assert.Equal("ready", export.GetProperty("state").GetString());
        return (id, await SunoExportApi.RecordsByIdAsync(client, id));
    }

    /// <summary>A record's proposed choice.</summary>
    public static JsonElement Proposed(JsonElement record) => record.GetProperty("proposal").GetProperty("choice");

    /// <summary>A record's proposed target.</summary>
    public static JsonElement Target(JsonElement record) => Proposed(record).GetProperty("target");

    /// <summary>A text member of a JSON object.</summary>
    public static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();

    /// <summary>PATCHes a change of choices with <paramref name="ifMatch"/> (none when null); the caller reads the answer.</summary>
    public static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid exportId, string? ifMatch, string json, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Patch, SunoExportApi.Export(exportId, "/records"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (token is null)
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    /// <summary>The ID of the Version <paramref name="shortcode"/>.</summary>
    public static async Task<Guid> VersionIdAsync(HttpClient client, string shortcode)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(new Uri("/api/v1/versions/" + shortcode, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Deletes the Generation <paramref name="shortcode"/> as the signed-in user, asserting 200.</summary>
    public static async Task DeleteGenerationAsync(HttpClient client, string shortcode)
    {
        ArgumentNullException.ThrowIfNull(client);

        var uri = new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative);
        var generation = await SetupApi.JsonAsync(await client.GetAsync(uri));
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", $"\"{generation.GetProperty("revision").GetInt32()}\""));
        using var deleted = await client.SendAsync(request);
        Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
    }

    /// <summary>A change of <paramref name="choice"/> for <paramref name="sunoIds"/>.</summary>
    public static string Change(JsonNode choice, params string[] sunoIds) =>
        new JsonObject { ["sunoIds"] = new JsonArray([.. sunoIds.Select(static id => (JsonNode)id)]), ["choice"] = choice }.ToJsonString();

    /// <summary>An import to <paramref name="target"/>.</summary>
    public static JsonObject Import(JsonObject target) => new() { ["action"] = "import", ["target"] = target };

    /// <summary>Changes the choices at <paramref name="revision"/>, asserting 200, and returns the export.</summary>
    public static async Task<JsonElement> ChangedAsync(HttpClient client, Guid exportId, int revision, string json)
    {
        using var response = await PatchAsync(client, exportId, $"\"{revision}\"", json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Changes the choices at <paramref name="revision"/>, asserting 422 <c>invalid_choices</c>, and returns the reasons by Suno ID.</summary>
    public static async Task<Dictionary<string, string[]>> RefusedAsync(HttpClient client, Guid exportId, int revision, string json)
    {
        using var response = await PatchAsync(client, exportId, $"\"{revision}\"", json);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
        var problem = await SetupApi.JsonAsync(response);
        Assert.Equal("invalid_choices", problem.GetProperty("code").GetString());
        return problem.GetProperty("records").EnumerateObject().ToDictionary(
            static pair => pair.Name,
            static pair => pair.Value.EnumerateArray().Select(static reason => reason.GetString()!).ToArray(),
            StringComparer.Ordinal);
    }
}
