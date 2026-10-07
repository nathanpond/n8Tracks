using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Setup;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>Calls for the review page's reads and its change by filter (#139), as the signed-in user makes them.</summary>
internal static class ImportReviewApi
{
    public static readonly Uri Current = new("/api/v1/suno/exports/current", UriKind.Relative);

    /// <summary>A change of <paramref name="choice"/> for the records matching <paramref name="filter"/>, except <paramref name="except"/>.</summary>
    public static string ByFilter(JsonObject filter, JsonNode choice, params string[] except)
    {
        var change = new JsonObject { ["filter"] = filter.DeepClone(), ["choice"] = choice.DeepClone() };
        if (except.Length > 0)
        {
            change["except"] = new JsonArray([.. except.Select(static id => (JsonNode)id)]);
        }

        return change.ToJsonString();
    }

    /// <summary>The export's summary, asserting 200.</summary>
    public static async Task<JsonElement> SummaryAsync(HttpClient client, Guid exportId)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(SunoExportApi.Export(exportId, "/summary"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Where <paramref name="sunoId"/> may go, for <paramref name="query"/> (<c>?song=…</c>), asserting 200.</summary>
    public static async Task<JsonElement> TargetsAsync(HttpClient client, Guid exportId, string sunoId, string query)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(SunoExportApi.Export(exportId, $"/records/{sunoId}/targets{query}"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>The export waiting for review and the last one, asserting 200.</summary>
    public static async Task<JsonElement> CurrentAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(Current);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>The numbers in a targets answer, in order.</summary>
    public static string[] Numbers(JsonElement targets) =>
        [.. targets.GetProperty("numbers").EnumerateArray().Select(static number => number.GetProperty("number").GetString()!)];

    /// <summary>The shortcodes of a list of Versions in a targets answer.</summary>
    public static string[] Shortcodes(JsonElement targets, string list) =>
        [.. targets.GetProperty(list).EnumerateArray().Select(static version => version.GetProperty("shortcode").GetString()!)];
}
