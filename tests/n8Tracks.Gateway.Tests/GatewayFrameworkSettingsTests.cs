using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.TestSupport;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// The gateway is configured through its own environment variables and nothing else: a setting of
/// the .NET host or of ASP.NET Core, from any source a default host reads, has no effect. The keys
/// here stand for the class. On a default host each one alone breaks the request below:
/// <c>AllowedHosts</c> answers 400 for a host name it does not list, the request-line limit answers
/// 414, and a content root that does not exist stops the start.
/// </summary>
public sealed class GatewayFrameworkSettingsTests
{
    [Theory]
    [MemberData(nameof(OtherConfigurationSources.AllForFrameworkSettings), MemberType = typeof(OtherConfigurationSources))]
    public async Task AFrameworkSettingFromAnotherSourceHasNoEffect(string source)
    {
        var port = GatewayNetworkTests.FreePort();
        var otherPort = GatewayNetworkTests.FreePort();

        using var gateway = OtherConfigurationSources.Start(
            typeof(Program).Assembly,
            source,
            [
                ("AllowedHosts", "only.example.invalid"),
                ("Kestrel:Limits:MaxRequestLineSize", "8"),
                ("contentRoot", Path.Combine(Path.GetTempPath(), "n8tracks-test-no-such-content-root")),
                ("urls", $"http://127.0.0.1:{otherPort}"),
            ],
            ("N8TRACKS_GATEWAY_PORT", port.ToString(CultureInfo.InvariantCulture)),

            // Nothing answers there: the gateway runs degraded, which is still a 200.
            ("N8TRACKS_API_URL", $"http://127.0.0.1:{GatewayNetworkTests.FreePort()}"));

        using var client = new HttpClient();
        await gateway.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (_, error) = await gateway.StopAndReadOutput();
        Assert.Equal(string.Empty, error);
    }

    /// <summary>
    /// The host's configuration holds only what the gateway's own code put there. The test host hands
    /// the entry point arguments of its own (an environment name, a content root, and the settings
    /// below), and the machine running the tests has its environment; none of it is in there.
    /// </summary>
    [Fact]
    public void TheHostsConfigurationHoldsOnlyWhatTheGatewaySet()
    {
        using var factory = new GatewayFactory(new StubUpstream())
        {
            HostSettings = [("AllowedHosts", "only.example.invalid"), ("Kestrel:Endpoints:FromArgument:Url", "http://127.0.0.1:1")],
        };

        var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(
            ["applicationName", "contentRoot", "environment"],
            configuration.AsEnumerable().Where(static entry => entry.Value is not null).Select(static entry => entry.Key).Order(StringComparer.Ordinal));
        Assert.Equal("n8Tracks.Gateway", configuration["applicationName"]);
        Assert.Equal("Production", configuration["environment"]);
        Assert.Equal(AppContext.BaseDirectory, configuration["contentRoot"]);
    }
}
