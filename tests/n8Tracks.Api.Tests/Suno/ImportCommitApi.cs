using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>Confirming a sync review (#140): the commit request and its job's result.</summary>
internal static class ImportCommitApi
{
    /// <summary>The commit of the export.</summary>
    public static Uri Commit(Guid exportId) => SunoExportApi.Export(exportId, "/commit");

    /// <summary>POSTs the commit with <paramref name="ifMatch"/> (none when null), as the signed-in user or with <paramref name="token"/>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Guid exportId, string? ifMatch, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, Commit(exportId));
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

    /// <summary>The export's revision as the signed-in user reads it, quoted for If-Match.</summary>
    public static async Task<string> IfMatchAsync(HttpClient client, Guid exportId) =>
        $"\"{(await SunoExportApi.GetAsync(client, null, exportId)).GetProperty("revision").GetInt32()}\"";

    /// <summary>Starts the commit at the export's current revision, asserting 202; returns the export answered.</summary>
    public static async Task<JsonElement> StartAsync(HttpClient client, Guid exportId)
    {
        using var response = await SendAsync(client, exportId, await IfMatchAsync(client, exportId));
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Commits the export and waits for the job; returns the job's result, asserting it succeeded and the export is committed.</summary>
    public static async Task<JsonElement> CommitAsync(HttpClient client, Guid exportId)
    {
        var started = await StartAsync(client, exportId);
        Assert.Equal("committing", started.GetProperty("state").GetString());
        var job = await TestJobs.WaitForAsync(
            client,
            started.GetProperty("jobId").GetGuid(),
            static job => job.GetProperty("status").GetString() is "succeeded" or "failed");
        Assert.True(job.GetProperty("status").GetString() == "succeeded", job.ToString());
        Assert.Equal("committed", (await SunoExportApi.GetAsync(client, null, exportId)).GetProperty("state").GetString());
        return job.GetProperty("result");
    }

    /// <summary>Stages a small PNG with the export's record <paramref name="sunoId"/>, as the extension does, asserting 200.</summary>
    public static async Task StageImageAsync(HttpClient client, string token, Guid exportId, string sunoId)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Assets.ArtworkImages.Solid(SkiaSharp.SKEncodedImageFormat.Png, 32, 32, Assets.ArtworkImages.Blue)), "file", "cover.png");
        using var request = new HttpRequestMessage(HttpMethod.Put, SunoExportApi.Export(exportId, "/artwork/" + sunoId)) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var staged = await client.SendAsync(request);
        Assert.True(staged.StatusCode == HttpStatusCode.OK, await staged.Content.ReadAsStringAsync());
    }

    /// <summary>Rates the Generation <paramref name="shortcode"/> and comments on it, as the signed-in user.</summary>
    public static async Task RateAndCommentAsync(HttpClient client, string shortcode, int rating, string comment)
    {
        ArgumentNullException.ThrowIfNull(client);

        var uri = new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative);
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(uri))).GetProperty("revision").GetInt32();
        using (var request = new HttpRequestMessage(HttpMethod.Patch, uri) { Content = new StringContent($$"""{"rating":{{rating}}}""", System.Text.Encoding.UTF8, "application/json") })
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\""));
            using var rated = await client.SendAsync(request);
            Assert.True(rated.StatusCode == HttpStatusCode.OK, await rated.Content.ReadAsStringAsync());
        }

        using var commented = await Songs.SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/generations/{shortcode}/comments", UriKind.Relative), JsonSerializer.Serialize(new { text = comment }));
        Assert.True(commented.StatusCode == HttpStatusCode.Created, await commented.Content.ReadAsStringAsync());
    }

    /// <summary>Gives the Generation <paramref name="shortcode"/> an image, as the signed-in user.</summary>
    public static async Task UploadImageAsync(HttpClient client, string shortcode)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Assets.ArtworkImages.Solid(SkiaSharp.SKEncodedImageFormat.Png, 32, 32, Assets.ArtworkImages.Blue));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "cover.png");
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/generations/{shortcode}/artwork", UriKind.Relative)) { Content = form };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        using var uploaded = await client.SendAsync(request);
        Assert.True(uploaded.StatusCode == HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync());
    }

    /// <summary>Each record of a result by Suno ID.</summary>
    public static Dictionary<string, JsonElement> Records(JsonElement result) =>
        result.GetProperty("records").EnumerateArray().ToDictionary(static record => record.GetProperty("sunoId").GetString()!, StringComparer.Ordinal);

    /// <summary>A record's outcome.</summary>
    public static string Outcome(JsonElement record) => record.GetProperty("outcome").GetString()!;

    /// <summary>A record's reason, or null.</summary>
    public static string? Reason(JsonElement record) => record.TryGetProperty("reason", out var reason) ? reason.GetString() : null;

    /// <summary>How many live Generations hold <paramref name="sunoId"/>.</summary>
    public static int GenerationCount(N8TracksApiFactory factory, string sunoId) =>
        int.Parse(TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM generations WHERE suno_id = '{sunoId}';"), System.Globalization.CultureInfo.InvariantCulture);
}
