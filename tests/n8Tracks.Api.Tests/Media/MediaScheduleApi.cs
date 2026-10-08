using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Media;
using n8Tracks.Infrastructure.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Test helpers for scans nobody asked for (#204): a media host on a <see cref="TestClock"/>, one
/// look of the scheduler made by the test, the <c>media-scan</c> jobs as the store holds them, and
/// the schedule setting through the API.
/// </summary>
internal static class MediaScheduleApi
{
    public static readonly Uri Schedule = new("/api/v1/settings/media-scan", UriKind.Relative);

    /// <summary>
    /// A media host (<see cref="MediaApi.Host"/>) on <paramref name="clock"/>. The scheduler itself
    /// stays off unless <paramref name="checkInterval"/> is given, in which case it runs and looks that often.
    /// </summary>
    public static N8TracksApiFactory Host(TestClock clock, TimeSpan? checkInterval = null) =>
        MediaApi.Host(services: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            if (checkInterval is { } interval)
            {
                services.RemoveAll<MediaScanSchedulerOptions>();
                services.AddSingleton(new MediaScanSchedulerOptions { CheckInterval = interval });
            }
        });

    /// <summary>One look of the scheduler, as it makes every 30 seconds.</summary>
    public static async Task<MediaScanScheduleAction> TickAsync(N8TracksApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediaScanScheduleService>().TickAsync(CancellationToken.None);
    }

    /// <summary>Every <c>media-scan</c> job in the jobs table, oldest first.</summary>
    public static async Task<List<JobSummary>> ScanJobsAsync(N8TracksApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = await scope.ServiceProvider.GetRequiredService<IJobStore>().ListRecentAsync(10_000, CancellationToken.None);
        return [.. jobs.Where(static job => job.Type == MediaScanService.JobType).Reverse()];
    }

    /// <summary>Waits until no <c>media-scan</c> job is queued or running.</summary>
    public static async Task SettleAsync(N8TracksApiFactory factory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                if (await scope.ServiceProvider.GetRequiredService<IJobStore>().FindActiveAsync(MediaScanService.JobType, CancellationToken.None) is null)
                {
                    return;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "A media scan never finished.");
            await Task.Delay(25);
        }
    }

    /// <summary>The trigger a finished scan's job recorded in its result.</summary>
    public static string? Trigger(JobSummary job) =>
        job.Result is { ValueKind: JsonValueKind.Object } result && result.TryGetProperty("trigger", out var trigger) ? trigger.GetString() : null;

    public static async Task<JsonElement> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Schedule);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Sends <paramref name="body"/> (an object, or raw JSON text) as a PUT with <paramref name="ifMatch"/>.</summary>
    public static async Task<HttpResponseMessage> PutAsync(HttpClient client, string? ifMatch, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Schedule)
        {
            Content = body is string json ? new StringContent(json, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Sets the schedule from its current revision, asserting 200, and returns it.</summary>
    public static async Task<JsonElement> SetAsync(HttpClient client, bool enabled, int intervalMinutes)
    {
        var current = await GetAsync(client);
        using var response = await PutAsync(client, $"\"{current.GetProperty("revision").GetInt32()}\"", new { enabled, intervalMinutes });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>A signed-in client of a host whose first look has queued the startup scan, which has finished.</summary>
    public static async Task<HttpClient> StartedAsync(N8TracksApiFactory factory)
    {
        var client = await SessionApi.SignedInClientAsync(factory);
        Assert.Equal(MediaScanScheduleAction.Startup, await TickAsync(factory));
        await SettleAsync(factory);
        return client;
    }

    /// <summary>Waits until <paramref name="condition"/> holds.</summary>
    public static Task UntilAsync(Func<bool> condition, string what) => TestJobs.WaitUntilAsync(condition, what);
}
