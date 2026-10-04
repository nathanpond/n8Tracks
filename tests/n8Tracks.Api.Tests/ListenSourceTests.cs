using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using n8Tracks.Api.Configuration;
using n8Tracks.TestSupport;

namespace n8Tracks.Api.Tests;

/// <summary>
/// <c>N8TRACKS_PORT</c> is the only source of the listen address. The app runs as a real process with
/// the other ways ASP.NET Core accepts an address each naming a port of its own: the host's variables
/// (<c>ASPNETCORE_URLS</c>, <c>ASPNETCORE_HTTP_PORTS</c>, <c>ASPNETCORE_HTTPS_PORTS</c>,
/// <c>DOTNET_URLS</c>), the <c>--urls</c> argument, <c>urls</c> in <c>appsettings.json</c>, and an
/// endpoint under Kestrel's <c>Kestrel:Endpoints</c> configuration section, as a variable (with the
/// <c>ASPNETCORE_</c> or <c>DOTNET_</c> prefix and without one), an argument, and in <c>appsettings.json</c>.
/// </summary>
public sealed class ListenSourceTests
{
    private const string UrlsVariable = "ASPNETCORE_URLS";
    private const string HttpPortsVariable = "ASPNETCORE_HTTP_PORTS";
    private const string HttpsPortsVariable = "ASPNETCORE_HTTPS_PORTS";
    private const string DotNetUrlsVariable = "DOTNET_URLS";
    private const string UrlsArgument = "--urls";
    private const string SettingsFile = "appsettings.json";
    private const string KestrelVariable = "ASPNETCORE_Kestrel__Endpoints__FromVariable__Url";
    private const string KestrelDotNetVariable = "DOTNET_Kestrel__Endpoints__FromDotNetVariable__Url";
    private const string KestrelUnprefixedVariable = "Kestrel__Endpoints__FromUnprefixedVariable__Url";
    private const string KestrelArgument = "--Kestrel:Endpoints:FromArgument:Url";
    private const string KestrelSettingsFile = "appsettings.json (Kestrel:Endpoints)";

    private static readonly string[] Sources =
    [
        UrlsVariable, HttpPortsVariable, HttpsPortsVariable, DotNetUrlsVariable, UrlsArgument, SettingsFile,
        KestrelVariable, KestrelDotNetVariable, KestrelUnprefixedVariable, KestrelArgument, KestrelSettingsFile,
    ];

    [Theory]
    [InlineData(UrlsVariable)]
    [InlineData(HttpPortsVariable)]
    [InlineData(HttpsPortsVariable)]
    [InlineData(DotNetUrlsVariable)]
    [InlineData(UrlsArgument)]
    [InlineData(SettingsFile)]
    [InlineData(KestrelVariable)]
    [InlineData(KestrelDotNetVariable)]
    [InlineData(KestrelUnprefixedVariable)]
    [InlineData(KestrelArgument)]
    [InlineData(KestrelSettingsFile)]
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
    /// that port, or an address from any other source: had it been, the start would warn that it is
    /// overridden.
    /// </summary>
    [Fact]
    public async Task NoSourceLeavesAWarningInTheLog()
    {
        var output = await AssertOnlyTheProductPortListens(Sources);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();

        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, static line => line.GetProperty("level").GetString() is "Warning" or "Error" or "Critical");
    }

    /// <summary>Starts the app with each of <paramref name="sources"/> naming a port of its own; returns its standard output.</summary>
    private static async Task<string> AssertOnlyTheProductPortListens(params string[] sources)
    {
        var port = TestPorts.Next();
        var others = sources.ToDictionary(static source => source, static _ => TestPorts.Next(), StringComparer.Ordinal);

        using var data = new TemporaryDirectory();
        using var media = new TemporaryDirectory();

        List<(string Name, string Value)> variables =
        [
            (EnvironmentOptionsLoader.Port, Text(port)),
            (EnvironmentOptionsLoader.DataPath, data.Path),
            (EnvironmentOptionsLoader.MediaPath, media.Path),
        ];

        foreach (var (source, other) in others)
        {
            switch (source)
            {
                case UrlsVariable or DotNetUrlsVariable or KestrelVariable or KestrelDotNetVariable or KestrelUnprefixedVariable:
                    variables.Add((source, Url(other)));
                    break;
                case HttpPortsVariable or HttpsPortsVariable:
                    variables.Add((source, Text(other)));
                    break;
            }
        }

        using var app = ServiceProcess.Start(
            typeof(Program).Assembly,
            settingsFile: SettingsFileFor(others),
            arguments: [.. new[] { UrlsArgument, KestrelArgument }.Where(others.ContainsKey).SelectMany(argument => new[] { argument, Url(others[argument]) })],
            variables);

        using var client = new HttpClient();
        await app.WaitUntilItAnswers(client, new Uri($"{Url(port)}/health"));

        using (var health = await client.GetAsync(new Uri($"{Url(port)}/health")))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        foreach (var (source, other) in others)
        {
            Assert.False(await Accepts(other), $"The app listens on port {Text(other)}, which only {source} named.");
        }

        var (output, error) = await app.StopAndReadOutput();
        Assert.Equal(string.Empty, error);

        return output;
    }

    /// <summary>The <c>appsettings.json</c> that names a port under <c>urls</c>, under <c>Kestrel:Endpoints</c>, both, or null for no file.</summary>
    private static string? SettingsFileFor(Dictionary<string, int> others)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (others.TryGetValue(SettingsFile, out var inUrls))
        {
            settings["urls"] = Url(inUrls);
        }

        if (others.TryGetValue(KestrelSettingsFile, out var inKestrel))
        {
            settings["Kestrel:Endpoints:FromFile:Url"] = Url(inKestrel);
        }

        return settings.Count == 0 ? null : JsonSerializer.Serialize(settings);
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
}
