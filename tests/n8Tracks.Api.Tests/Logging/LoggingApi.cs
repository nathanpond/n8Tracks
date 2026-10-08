using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Logging;
using n8Tracks.Infrastructure.Logging;

namespace n8Tracks.Api.Tests.Logging;

/// <summary>
/// Test helpers for the log settings (#234): a host on a <see cref="TestClock"/>, one look of the
/// monitor made by the test (it is off in test hosts), the setting through the API, and the log files
/// as the host wrote them.
/// </summary>
internal static class LoggingApi
{
    public static readonly Uri Settings = new("/api/v1/settings/logging", UriKind.Relative);

    /// <summary>A host on <paramref name="clock"/>, with <c>N8TRACKS_LOG_LEVEL</c> set when <paramref name="logLevel"/> is.</summary>
    public static N8TracksApiFactory Host(TestClock clock, string? logLevel = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (logLevel is not null)
        {
            variables[EnvironmentOptionsLoader.LogLevel] = logLevel;
        }

        return new N8TracksApiFactory(variables)
        {
            TestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            },
        };
    }

    /// <summary>One look of the monitor, as it makes every 30 seconds (the first one sweeps the folder).</summary>
    public static Task LookAsync(N8TracksApiFactory factory) =>
        factory.Services.GetServices<IHostedService>().OfType<LoggingSettingsMonitor>().Single().LookAsync(CancellationToken.None);

    /// <summary>The settings as the service has them, without a session.</summary>
    public static async Task<LoggingView> ViewAsync(N8TracksApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LoggingSettingsService>().GetAsync(CancellationToken.None);
    }

    /// <summary>The level the log is written at now.</summary>
    public static Application.Configuration.N8TracksLogLevel Level(N8TracksApiFactory factory) =>
        factory.Services.GetRequiredService<ILogLevelControl>().Level;

    public static async Task<JsonElement> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Settings);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Sends <paramref name="body"/> (an object, or raw JSON text) as a PUT with <paramref name="ifMatch"/>.</summary>
    public static async Task<HttpResponseMessage> PutAsync(HttpClient client, string? ifMatch, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Settings)
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

    /// <summary>Sets the settings from their current revision, asserting 200, and returns them.</summary>
    public static async Task<JsonElement> SetAsync(HttpClient client, string level, int retentionDays = 14, int maxMegabytes = 200, bool confirmDelete = false)
    {
        var current = await GetAsync(client);
        using var response = await PutAsync(client, $"\"{current.GetProperty("revision").GetInt32()}\"", new { level, retentionDays, maxMegabytes, confirmDelete });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>The log folder of a host.</summary>
    public static string Folder(N8TracksApiFactory factory) => Path.Combine(factory.DataPath, FileLogging.FolderName);

    /// <summary>Everything in the host's log files, oldest file first, read without disturbing the writer.</summary>
    public static string FilesText(N8TracksApiFactory factory)
    {
        var folder = Folder(factory);
        if (!Directory.Exists(folder))
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder();
        foreach (var path in Directory.EnumerateFiles(folder, "n8tracks-*.jsonl").Order(StringComparer.Ordinal))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    /// <summary>Every line of the host's log files, parsed.</summary>
    public static List<JsonElement> FileLines(N8TracksApiFactory factory) =>
        [.. FilesText(factory).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => JsonSerializer.Deserialize<JsonElement>(line))];

    /// <summary>Waits until a line of the host's log files matches.</summary>
    public static async Task<JsonElement> WaitForFileLineAsync(N8TracksApiFactory factory, Func<JsonElement, bool> matches)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var found = FileLines(factory).Where(matches).ToList();
            if (found.Count > 0)
            {
                return found[0];
            }

            Assert.True(DateTime.UtcNow < deadline, $"The expected line was not in the log files. They hold:\n{FilesText(factory)}");
            await Task.Delay(25);
        }
    }
}
