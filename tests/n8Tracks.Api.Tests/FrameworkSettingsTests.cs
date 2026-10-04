using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Configuration;
using n8Tracks.TestSupport;

namespace n8Tracks.Api.Tests;

/// <summary>
/// The app is configured through its documented environment variables and nothing else: a setting of
/// the .NET host or of ASP.NET Core, from any source a default host reads, has no effect. The keys
/// here stand for the class. On a default host each one alone breaks the request below:
/// <c>AllowedHosts</c> answers 400 for a host name it does not list, the request-line limit answers
/// 414, and a content root that does not exist stops the start.
/// </summary>
public sealed class FrameworkSettingsTests
{
    private const string OpenApiDocument = "/openapi/v1.json";

    [Theory]
    [MemberData(nameof(OtherConfigurationSources.AllForFrameworkSettings), MemberType = typeof(OtherConfigurationSources))]
    public async Task AFrameworkSettingFromAnotherSourceHasNoEffect(string source)
    {
        var status = await StatusOf(
            "/health",
            source,
            [
                ("AllowedHosts", "only.example.invalid"),
                ("Kestrel:Limits:MaxRequestLineSize", "8"),
                ("contentRoot", Path.Combine(Path.GetTempPath(), "n8tracks-test-no-such-content-root")),
                ("urls", $"http://127.0.0.1:{FreePort()}"),
            ]);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>
    /// The environment name is the one thing the app takes from the .NET host's own variables, and
    /// only from <c>ASPNETCORE_ENVIRONMENT</c>. <c>Development</c> is seen from outside by the OpenAPI
    /// document, which no other environment has.
    /// </summary>
    [Theory]
    [InlineData(OtherConfigurationSources.CommandLine, "environment")]
    [InlineData(OtherConfigurationSources.DotNetPrefix, "ENVIRONMENT")]
    [InlineData(OtherConfigurationSources.Variable, "environment")]
    [InlineData(OtherConfigurationSources.Variable, "ENVIRONMENT")]
    [InlineData(OtherConfigurationSources.SettingsFile, "environment")]
    public async Task NoOtherSourceNamesTheEnvironment(string source, string key)
    {
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(OpenApiDocument, source, [(key, "Development")]));
    }

    /// <summary>Complement: the document is there when the variable says <c>Development</c>, and only then.</summary>
    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    [InlineData(" ", HttpStatusCode.NotFound)]
    public async Task TheEnvironmentVariableNamesTheEnvironment(string value, HttpStatusCode expected)
    {
        Assert.Equal(expected, await StatusOf(OpenApiDocument, OtherConfigurationSources.AspNetCorePrefix, [("ENVIRONMENT", value)]));
    }

    [Fact]
    public async Task WithoutTheVariableTheEnvironmentIsProduction()
    {
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(OpenApiDocument, OtherConfigurationSources.CommandLine, []));
    }

    /// <summary>
    /// The host's configuration holds only what the app's own code put there. The test host hands the
    /// entry point arguments of its own (an environment name, a content root, and the settings
    /// below), and the machine running the tests has its environment; none of it is in there.
    /// </summary>
    [Fact]
    public void TheHostsConfigurationHoldsOnlyWhatTheAppSet()
    {
        using var factory = new N8TracksApiFactory
        {
            EnvironmentName = "Staging",
            HostSettings = [("AllowedHosts", "only.example.invalid"), ("Kestrel:Endpoints:FromArgument:Url", "http://127.0.0.1:1")],
        };

        var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(
            ["applicationName", "contentRoot", "environment"],
            configuration.AsEnumerable().Where(static entry => entry.Value is not null).Select(static entry => entry.Key).Order(StringComparer.Ordinal));
        Assert.Equal("n8Tracks.Api", configuration["applicationName"]);

        // The variable's value, not the "Development" the test host passes as an argument.
        Assert.Equal("Staging", configuration["environment"]);

        // The working directory, not the project directory the test host passes as an argument.
        Assert.Equal(Directory.GetCurrentDirectory(), configuration["contentRoot"]);
    }

    /// <summary>Starts the app as a process with <paramref name="settings"/> arriving through <paramref name="source"/> and asks it for <paramref name="path"/>.</summary>
    private static async Task<HttpStatusCode> StatusOf(string path, string source, IReadOnlyList<(string Name, string Value)> settings)
    {
        var port = FreePort();
        using var data = new TemporaryDirectory();
        using var media = new TemporaryDirectory();

        using var app = OtherConfigurationSources.Start(
            typeof(Program).Assembly,
            source,
            settings,
            (EnvironmentOptionsLoader.Port, port.ToString(CultureInfo.InvariantCulture)),
            (EnvironmentOptionsLoader.DataPath, data.Path),
            (EnvironmentOptionsLoader.MediaPath, media.Path));

        using var client = new HttpClient();
        await app.WaitUntilItAnswers(client, new Uri($"http://127.0.0.1:{port}/health"));

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}{path}"));

        var (_, error) = await app.StopAndReadOutput();
        Assert.Equal(string.Empty, error);

        return response.StatusCode;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
