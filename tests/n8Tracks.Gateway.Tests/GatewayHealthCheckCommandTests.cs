using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using n8Tracks.Gateway.Configuration;
using n8Tracks.Gateway.Health;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// The <c>--healthcheck</c> mode of the gateway binary, which the gateway image's <c>HEALTHCHECK</c>
/// runs: exit code 0 only when the gateway answers 200 at its own port, whatever it says about n8Tracks.
/// The mode is run as its own process, the way Docker runs it.
/// </summary>
public sealed class GatewayHealthCheckCommandTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void TheDefaultTargetIsLoopbackAtPort8788()
    {
        Assert.Equal(new Uri("http://127.0.0.1:8788/health"), HealthCheckCommand.Target(Snapshot()));
    }

    [Fact]
    public void TheTargetFollowsTheGatewayPortAndNoOtherSetting()
    {
        var target = HealthCheckCommand.Target(Snapshot(
            ("N8TRACKS_GATEWAY_PORT", "9123"),
            ("N8TRACKS_PORT", "8787"),
            ("N8TRACKS_API_URL", "https://nas.example/n8tracks"),
            ("ASPNETCORE_HTTP_PORTS", "8080")));

        Assert.Equal(new Uri("http://127.0.0.1:9123/health"), target);
    }

    [Fact]
    public void OnlyTheExactArgumentRequestsTheCheck()
    {
        Assert.True(HealthCheckCommand.IsRequested(["--healthcheck"]));
        Assert.True(HealthCheckCommand.IsRequested(["--urls", "http://*:1", "--healthcheck"]));
        Assert.False(HealthCheckCommand.IsRequested([]));
        Assert.False(HealthCheckCommand.IsRequested(["--HealthCheck"]));
        Assert.False(HealthCheckCommand.IsRequested(["healthcheck"]));
    }

    [Fact]
    public async Task ItPassesAgainstAHealthyGateway()
    {
        await using var upstream = await StartStub(app => app.MapGet("/health", () => Results.Json(new { status = "healthy", version = ProductVersion.Current })));
        var port = Port(GatewayNetworkTests.FreePort());

        using var gateway = GatewayProcessTests.Start([], ("N8TRACKS_API_URL", Address(upstream)), ("N8TRACKS_GATEWAY_PORT", port));
        try
        {
            using var client = new HttpClient();
            var health = await GatewayProcessTests.WaitForHealth(client, int.Parse(port, CultureInfo.InvariantCulture));
            Assert.Equal("healthy", health.GetProperty("status").GetString());

            var (exitCode, lines, _) = await Check(("N8TRACKS_GATEWAY_PORT", port));

            Assert.Equal(0, exitCode);
            AssertPassed(Assert.Single(lines));
        }
        finally
        {
            gateway.Process.Kill(entireProcessTree: true);
        }

        await gateway.Completion.WaitAsync(ExitTimeout);
    }

    /// <summary>A degraded gateway is still running: the container must stay healthy while n8Tracks is away.</summary>
    [Fact]
    public async Task ItPassesAgainstADegradedGateway()
    {
        var port = Port(GatewayNetworkTests.FreePort());

        using var gateway = GatewayProcessTests.Start(
            [],
            ("N8TRACKS_API_URL", $"http://127.0.0.1:{GatewayNetworkTests.FreePort()}"),
            ("N8TRACKS_GATEWAY_PORT", port));
        try
        {
            using var client = new HttpClient();
            var health = await GatewayProcessTests.WaitForHealth(client, int.Parse(port, CultureInfo.InvariantCulture));
            Assert.Equal("degraded", health.GetProperty("status").GetString());

            var (exitCode, lines, _) = await Check(("N8TRACKS_GATEWAY_PORT", port));

            Assert.Equal(0, exitCode);
            AssertPassed(Assert.Single(lines));
        }
        finally
        {
            gateway.Process.Kill(entireProcessTree: true);
        }

        await gateway.Completion.WaitAsync(ExitTimeout);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(404)]
    [InlineData(302)]
    [InlineData(204)]
    public async Task ItFailsWhenTheAnswerIsNot200(int statusCode)
    {
        await using var stub = await StartStub(app => app.MapGet("/health", () => Results.StatusCode(statusCode)));

        var (exitCode, lines, _) = await Check(("N8TRACKS_GATEWAY_PORT", Port(stub)));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        Assert.Equal("Error", line.GetProperty("LogLevel").GetString());
        Assert.Equal(statusCode, line.GetProperty("State").GetProperty("StatusCode").GetInt32());
    }

    [Fact]
    public async Task ItAsksForHealthOnceAndNeedsNoApiUrl()
    {
        var paths = new ConcurrentQueue<string>();
        await using var stub = await StartStub(app => app.Run(context =>
        {
            paths.Enqueue(context.Request.Method + " " + context.Request.Path);
            return Task.CompletedTask;
        }));

        // No N8TRACKS_API_URL: the gateway itself would refuse to start, the check does not read it.
        var (exitCode, lines, _) = await Check(("N8TRACKS_GATEWAY_PORT", Port(stub)));

        Assert.Equal(0, exitCode);
        AssertPassed(Assert.Single(lines));
        Assert.Equal(["GET /health"], paths);
    }

    [Fact]
    public async Task ItFailsWithOneErrorLineWhenNothingAnswers()
    {
        var (exitCode, lines, _) = await Check(("N8TRACKS_GATEWAY_PORT", Port(GatewayNetworkTests.FreePort())));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        Assert.Equal("Error", line.GetProperty("LogLevel").GetString());
        Assert.Contains("did not answer", line.GetProperty("Message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("70000")]
    [InlineData("http")]
    public async Task ItFailsNamingTheVariableWhenThePortIsInvalid(string value)
    {
        var (exitCode, lines, _) = await Check(("N8TRACKS_GATEWAY_PORT", value));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        Assert.Equal("Error", line.GetProperty("LogLevel").GetString());
        Assert.Equal("N8TRACKS_GATEWAY_PORT", line.GetProperty("State").GetProperty("Variable").GetString());
    }

    /// <summary>The line is written even when the gateway's own log is set to the quietest level.</summary>
    [Fact]
    public async Task TheResultLineIgnoresTheLogLevelSetting()
    {
        await using var stub = await StartStub(app => app.MapGet("/health", () => Results.Ok()));

        var (exitCode, lines, _) = await Check(("N8TRACKS_GATEWAY_PORT", Port(stub)), ("N8TRACKS_LOG_LEVEL", "Critical"));

        Assert.Equal(0, exitCode);
        AssertPassed(Assert.Single(lines));
    }

    private static Task<(int ExitCode, IReadOnlyList<JsonElement> Lines, string Output)> Check(params (string Name, string Value)[] variables) =>
        GatewayProcessTests.RunToExit([HealthCheckCommand.Argument], variables);

    private static void AssertPassed(JsonElement line)
    {
        Assert.Equal("Information", line.GetProperty("LogLevel").GetString());
        Assert.Equal("n8Tracks.Gateway.HealthCheck", line.GetProperty("Category").GetString());
        Assert.Equal(200, line.GetProperty("State").GetProperty("StatusCode").GetInt32());
    }

    private static EnvironmentSnapshot Snapshot(params (string Name, string Value)[] variables) =>
        new(variables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.Ordinal));

    private static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);

    private static string Port(WebApplication stub) => Port(new Uri(Address(stub)).Port);

    private static string Address(WebApplication stub) =>
        stub.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    /// <summary>Something that answers on a loopback port: a stand-in for the gateway, or for n8Tracks.</summary>
    private static async Task<WebApplication> StartStub(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }
}
