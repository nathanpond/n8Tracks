using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using n8Tracks.Gateway.Configuration;
using n8Tracks.TestSupport;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// <c>N8TRACKS_GATEWAY_PORT</c> is the only source of the listen address. The gateway runs as a real process with
/// the other ways ASP.NET Core accepts an address each naming a port of its own: the host's variables
/// (<c>ASPNETCORE_URLS</c>, <c>ASPNETCORE_HTTP_PORTS</c>, <c>ASPNETCORE_HTTPS_PORTS</c>,
/// <c>DOTNET_URLS</c>), the <c>--urls</c> argument, and <c>urls</c> in <c>appsettings.json</c>.
/// </summary>
public sealed class GatewayListenSourceTests
{
    private const string UrlsVariable = "ASPNETCORE_URLS";
    private const string HttpPortsVariable = "ASPNETCORE_HTTP_PORTS";
    private const string HttpsPortsVariable = "ASPNETCORE_HTTPS_PORTS";
    private const string DotNetUrlsVariable = "DOTNET_URLS";
    private const string UrlsArgument = "--urls";
    private const string SettingsFile = "appsettings.json";

    private static readonly string[] Sources = [UrlsVariable, HttpPortsVariable, HttpsPortsVariable, DotNetUrlsVariable, UrlsArgument, SettingsFile];

    [Theory]
    [InlineData(UrlsVariable)]
    [InlineData(HttpPortsVariable)]
    [InlineData(HttpsPortsVariable)]
    [InlineData(DotNetUrlsVariable)]
    [InlineData(UrlsArgument)]
    [InlineData(SettingsFile)]
    public async Task APortNamedByAnotherSourceIsNotListenedOn(string source)
    {
        await AssertOnlyTheProductPortListens(source);
    }

    /// <summary>What verification did by hand: every source at once, each with a different port.</summary>
    [Fact]
    public async Task WithEverySourceNamingADifferentPortOnlyTheProductPortListens()
    {
        await AssertOnlyTheProductPortListens(Sources);
    }

    /// <summary>
    /// The .NET container image sets <c>ASPNETCORE_HTTP_PORTS</c> itself. The server is never handed
    /// that port (or the HTTPS one): had it been, every start would warn that it is overridden.
    /// </summary>
    [Fact]
    public async Task ThePortVariablesOfTheHostLeaveNoWarningInTheLog()
    {
        var output = await AssertOnlyTheProductPortListens(HttpPortsVariable, HttpsPortsVariable);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();

        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, static line =>
            line.GetProperty("LogLevel").GetString() is "Warning" or "Error" or "Critical"
            && line.GetProperty("Category").GetString()!.StartsWith("Microsoft", StringComparison.Ordinal));
    }

    /// <summary>Starts the gateway with each of <paramref name="sources"/> naming a port of its own; returns its standard output.</summary>
    private static async Task<string> AssertOnlyTheProductPortListens(params string[] sources)
    {
        var port = FreePort();
        var others = sources.ToDictionary(static source => source, static _ => FreePort(), StringComparer.Ordinal);

        List<(string Name, string Value)> variables =
        [
            (GatewayOptionsLoader.Port, Text(port)),

            // Nothing answers there: the gateway runs degraded and says so in one warning of its own.
            (GatewayOptionsLoader.ApiUrl, Url(FreePort())),
        ];

        foreach (var (source, other) in others)
        {
            switch (source)
            {
                case UrlsVariable or DotNetUrlsVariable:
                    variables.Add((source, Url(other)));
                    break;
                case HttpPortsVariable or HttpsPortsVariable:
                    variables.Add((source, Text(other)));
                    break;
            }
        }

        using var gateway = ServiceProcess.Start(
            typeof(Program).Assembly,
            settingsFile: others.TryGetValue(SettingsFile, out var inFile)
                ? JsonSerializer.Serialize(new Dictionary<string, string> { ["urls"] = Url(inFile) })
                : null,
            arguments: others.TryGetValue(UrlsArgument, out var inArgument) ? [UrlsArgument, Url(inArgument)] : [],
            variables);

        using var client = new HttpClient();
        await gateway.WaitUntilItAnswers(client, new Uri($"{Url(port)}/health"));

        using (var health = await client.GetAsync(new Uri($"{Url(port)}/health")))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        foreach (var (source, other) in others)
        {
            Assert.False(await Accepts(other), $"The gateway listens on port {Text(other)}, which only {source} named.");
        }

        var (output, error) = await gateway.StopAndReadOutput();
        Assert.Equal(string.Empty, error);

        return output;
    }

    private static string Text(int port) => port.ToString(CultureInfo.InvariantCulture);

    private static string Url(int port) => $"http://127.0.0.1:{Text(port)}";

    /// <summary>Whether anything accepts a connection on the port, on either loopback address.</summary>
    private static async Task<bool> Accepts(int port)
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, port);
                return true;
            }
            catch (SocketException)
            {
                // Refused, or the address family is not available here.
            }
        }

        return false;
    }

    private static int FreePort()
    {
        using var listener = TcpListener.Create(0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
