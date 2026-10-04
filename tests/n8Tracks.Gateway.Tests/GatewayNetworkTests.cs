using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using n8Tracks.Gateway.Health;
using n8Tracks.TestSupport;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// The gateway's real network handler against a real upstream on the loopback interface: what a fake
/// handler cannot show (redirects, sockets).
/// </summary>
public sealed class GatewayNetworkTests
{
    [Fact]
    public async Task ARealUpstreamIsProbedUnderItsSubPath()
    {
        var hits = 0;
        await using var upstream = await StartUpstream(app => app.MapGet("/sub/health", () =>
        {
            Interlocked.Increment(ref hits);
            return Results.Json(new { status = "unhealthy", version = ProductVersion.Current }, statusCode: 503);
        }));
        using var factory = new GatewayFactory(upstream: null, ("N8TRACKS_API_URL", Address(upstream) + "/sub"));

        var body = await Health(factory);

        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal("reachable", body.GetProperty("upstream").GetString());
        Assert.Equal(JsonValueKind.True, body.GetProperty("compatible").ValueKind);
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task ARedirectFromARealUpstreamIsNotFollowed()
    {
        var followed = 0;
        await using var upstream = await StartUpstream(app =>
        {
            app.MapGet("/health", () => Results.Redirect("/elsewhere"));
            app.MapGet("/elsewhere", () =>
            {
                Interlocked.Increment(ref followed);
                return Results.Json(new { version = ProductVersion.Current });
            });
        });
        using var factory = new GatewayFactory(upstream: null, ("N8TRACKS_API_URL", Address(upstream)));

        var body = await Health(factory);

        Assert.Equal("reachable", body.GetProperty("upstream").GetString());
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("compatible").ValueKind);
        Assert.Equal(0, followed);
    }

    [Fact]
    public async Task NothingListeningIsUnreachableAndTheLogDoesNotNameTheAddress()
    {
        var port = TestPorts.Next();
        using var factory = new GatewayFactory(upstream: null, ("N8TRACKS_API_URL", $"http://127.0.0.1:{port}"));

        var body = await Health(factory);

        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal("unreachable", body.GetProperty("upstream").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("compatible").ValueKind);

        Assert.Equal(LogLevel.Warning, Assert.Single(factory.GatewayLog).Level);
        Assert.All(factory.Log, entry =>
        {
            Assert.DoesNotContain("127.0.0.1", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(port.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Message, StringComparison.Ordinal);
        });
    }

    private static async Task<JsonElement> Health(GatewayFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<WebApplication> StartUpstream(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication upstream) =>
        upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}
