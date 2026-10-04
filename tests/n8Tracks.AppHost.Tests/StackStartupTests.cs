using System.Globalization;
using System.Text.Json;
using Aspire.Hosting;
using n8Tracks.TestSupport;

namespace n8Tracks.AppHost.Tests;

/// <summary>
/// Starts the app model for real (the app and the gateway as processes, without the frontend) and
/// asks both for their health. The ports and the data folder are the test's own, given the way a
/// developer would override them, so a stack that is already running locally is left alone.
/// </summary>
[Collection(AppHostCollection.Name)]
public class StackStartupTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task TheApiIsHealthyAndTheGatewayReachesIt()
    {
        using var folder = new TemporaryFolder();
        var media = Directory.CreateDirectory(Path.Combine(folder.Path, "media")).FullName;
        var apiPort = TestPorts.Next();
        var gatewayPort = TestPorts.Next();

        var builder = await AppModel.CreateAsync(
            $"N8TRACKS_PORT={apiPort.ToString(CultureInfo.InvariantCulture)}",
            $"N8TRACKS_GATEWAY_PORT={gatewayPort.ToString(CultureInfo.InvariantCulture)}",
            $"N8TRACKS_DATA_PATH={folder.Path}",
            $"N8TRACKS_MEDIA_PATH={media}");
        await using var builderDisposal = builder.ConfigureAwait(false);

        AppModel.RemoveFrontend(builder);
        Assert.DoesNotContain(builder.Resources, static resource => resource.Name.StartsWith(AppModel.Frontend, StringComparison.Ordinal));
        Assert.Contains(builder.Resources, static resource => resource.Name == AppModel.Api);
        Assert.Contains(builder.Resources, static resource => resource.Name == AppModel.Gateway);

        using var timeout = new CancellationTokenSource(StartTimeout);

        var app = await builder.BuildAsync(timeout.Token);
        await using var appDisposal = app.ConfigureAwait(false);
        await app.StartAsync(timeout.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(AppModel.Api, timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(AppModel.Gateway, timeout.Token);

        using var apiClient = app.CreateHttpClient(AppModel.Api, "http");
        using var gatewayClient = app.CreateHttpClient(AppModel.Gateway, "http");

        // The clients go to the ports the test chose: the endpoints are not proxied.
        Assert.Equal(apiPort, apiClient.BaseAddress!.Port);
        Assert.Equal(gatewayPort, gatewayClient.BaseAddress!.Port);

        using var apiHealth = await GetJsonAsync(apiClient, timeout.Token);
        Assert.Equal("healthy", apiHealth.RootElement.GetProperty("status").GetString());
        Assert.Equal("healthy", apiHealth.RootElement.GetProperty("components").GetProperty("media").GetProperty("status").GetString());

        using var gatewayHealth = await GetJsonAsync(gatewayClient, timeout.Token);
        Assert.Equal("reachable", gatewayHealth.RootElement.GetProperty("upstream").GetString());
        Assert.True(gatewayHealth.RootElement.GetProperty("compatible").GetBoolean());

        // The app really used the folder it was given.
        Assert.True(File.Exists(Path.Combine(folder.Path, "n8tracks.db")));

        await app.StopAsync(timeout.Token);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"GET {client.BaseAddress}health answered {(int)response.StatusCode}.");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
