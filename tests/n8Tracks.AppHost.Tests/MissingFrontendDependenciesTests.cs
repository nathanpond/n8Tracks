using System.Globalization;
using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.TestSupport;

namespace n8Tracks.AppHost.Tests;

/// <summary>
/// The frontend's dependencies are never installed by the AppHost. When they are missing, the frontend
/// resource fails with a message that says what to run, and the app and the gateway still start.
/// <para>
/// The real <c>web/node_modules</c> is never touched: each test points the frontend resource at a
/// folder of its own, and the AppHost's check looks in the resource's working directory.
/// </para>
/// </summary>
[Collection(AppHostCollection.Name)]
public class MissingFrontendDependenciesTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task ByDefaultTheFrontendRunsInTheRepositorysWebFolder()
    {
        var builder = await AppModel.CreateAsync();
        await using var disposal = builder.ConfigureAwait(false);

        var frontend = AppModel.Resource<ExecutableResource>(builder, AppModel.Frontend);

        Assert.Equal(Path.Combine(RepositoryRoot.Find(), "web"), Path.GetFullPath(frontend.WorkingDirectory));
    }

    [Fact]
    public async Task WithoutItsDependenciesTheFrontendIsRefusedWithTheInstallMessage()
    {
        using var folder = new TemporaryFolder();
        var builder = await AppModel.CreateAsync();
        await using var builderDisposal = builder.ConfigureAwait(false);
        var frontend = MoveFrontendTo(builder, folder.Path);

        var app = await builder.BuildAsync();
        await using var appDisposal = app.ConfigureAwait(false);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(frontend, app.Services)));

        Assert.Contains("dependencies are not installed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("`npm install`", exception.Message, StringComparison.Ordinal);
        Assert.Contains(folder.Path, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Complement: the same event with the dependencies in place is let through.</summary>
    [Fact]
    public async Task WithItsDependenciesTheFrontendIsLetThrough()
    {
        using var folder = new TemporaryFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "node_modules", "vite"));
        var builder = await AppModel.CreateAsync();
        await using var builderDisposal = builder.ConfigureAwait(false);
        var frontend = MoveFrontendTo(builder, folder.Path);

        var app = await builder.BuildAsync();
        await using var appDisposal = app.ConfigureAwait(false);

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(frontend, app.Services));
    }

    /// <summary>The whole stack, started for real on the test's own ports and data folder.</summary>
    [Fact]
    public async Task WithoutItsDependenciesTheFrontendFailsToStartWhileTheApiAndTheGatewayBecomeHealthy()
    {
        using var data = new TemporaryFolder();
        using var web = new TemporaryFolder();
        var media = Directory.CreateDirectory(Path.Combine(data.Path, "media")).FullName;

        var builder = await AppModel.CreateAsync(
            $"N8TRACKS_PORT={TestPorts.Next().ToString(CultureInfo.InvariantCulture)}",
            $"N8TRACKS_GATEWAY_PORT={TestPorts.Next().ToString(CultureInfo.InvariantCulture)}",
            $"N8TRACKS_DATA_PATH={data.Path}",
            $"N8TRACKS_MEDIA_PATH={media}");
        await using var builderDisposal = builder.ConfigureAwait(false);
        var frontend = MoveFrontendTo(builder, web.Path);

        using var timeout = new CancellationTokenSource(StartTimeout);

        var app = await builder.BuildAsync(timeout.Token);
        await using var appDisposal = app.ConfigureAwait(false);

        // Listening before the start, so the line cannot be missed.
        var frontendLog = ReadUntilAsync(
            app.Services.GetRequiredService<ResourceLoggerService>(),
            frontend,
            static line => line.Contains("npm install", StringComparison.Ordinal),
            timeout.Token);

        await app.StartAsync(timeout.Token);

        await app.ResourceNotifications.WaitForResourceAsync(AppModel.Frontend, KnownResourceStates.FailedToStart, timeout.Token);

        var line = await frontendLog;
        Assert.Contains("dependencies are not installed", line, StringComparison.Ordinal);
        Assert.Contains("`npm install`", line, StringComparison.Ordinal);

        // The failure is the frontend's alone.
        await app.ResourceNotifications.WaitForResourceHealthyAsync(AppModel.Api, timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(AppModel.Gateway, timeout.Token);

        using var apiClient = app.CreateHttpClient(AppModel.Api, "http");
        using var gatewayClient = app.CreateHttpClient(AppModel.Gateway, "http");
        using var apiHealth = await apiClient.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token);
        using var gatewayHealth = await gatewayClient.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token);
        Assert.True(apiHealth.IsSuccessStatusCode, $"The app's /health answered {(int)apiHealth.StatusCode}.");
        Assert.True(gatewayHealth.IsSuccessStatusCode, $"The gateway's /health answered {(int)gatewayHealth.StatusCode}.");

        // Nothing was installed on the developer's behalf.
        Assert.False(Directory.Exists(Path.Combine(web.Path, "node_modules")));

        await app.StopAsync(timeout.Token);
    }

    /// <summary>Points the frontend resource at <paramref name="folder"/> instead of the repository's <c>web/</c>.</summary>
    private static ExecutableResource MoveFrontendTo(IDistributedApplicationTestingBuilder builder, string folder)
    {
        var frontend = AppModel.Resource<ExecutableResource>(builder, AppModel.Frontend);

        builder.CreateResourceBuilder(frontend).WithWorkingDirectory(folder);
        Assert.Equal(folder, frontend.WorkingDirectory);

        return frontend;
    }

    private static async Task<string> ReadUntilAsync(
        ResourceLoggerService logs,
        IResource resource,
        Func<string, bool> wanted,
        CancellationToken cancellationToken)
    {
        await foreach (var batch in logs.WatchAsync(resource).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var line in batch)
            {
                if (wanted(line.Content))
                {
                    return line.Content;
                }
            }
        }

        throw new InvalidOperationException("The resource's log ended without the expected line.");
    }
}
