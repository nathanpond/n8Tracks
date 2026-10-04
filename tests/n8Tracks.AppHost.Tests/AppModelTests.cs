using System.Globalization;

namespace n8Tracks.AppHost.Tests;

/// <summary>What the AppHost declares, read from the app model without starting anything.</summary>
[Collection(AppHostCollection.Name)]
public class AppModelTests
{
    private static readonly string AppHostDirectory = Path.Combine(RepositoryRoot.Find(), "src", "n8Tracks.AppHost");

    [Fact]
    public async Task TheModelHoldsTheApiTheGatewayAndTheFrontendOnTheirFixedPorts()
    {
        var builder = await AppModel.CreateAsync();
        await using var disposal = builder.ConfigureAwait(false);

        AssertFixedUnproxied(AppModel.Resource<ProjectResource>(builder, AppModel.Api), 8787);
        AssertFixedUnproxied(AppModel.Resource<ProjectResource>(builder, AppModel.Gateway), 8788);
        AssertFixedUnproxied(AppModel.Resource<ExecutableResource>(builder, AppModel.Frontend), 5173);
    }

    [Fact]
    public async Task ByDefaultTheApiGetsTheLocalDataAndMediaFoldersAndTheyExist()
    {
        var builder = await AppModel.CreateAsync();
        await using var disposal = builder.ConfigureAwait(false);

        var environment = await AppModel.EnvironmentAsync(builder, AppModel.Resource<ProjectResource>(builder, AppModel.Api));

        var data = Path.Combine(AppHostDirectory, ".localdata");
        var media = Path.Combine(data, "media");

        Assert.Equal(data, environment["N8TRACKS_DATA_PATH"]);
        Assert.Equal(media, environment["N8TRACKS_MEDIA_PATH"]);
        Assert.True(Directory.Exists(data), $"'{data}' was not created.");
        Assert.True(Directory.Exists(media), $"'{media}' was not created.");

        Assert.Equal("8787", environment["N8TRACKS_PORT"]);
        Assert.Equal("Information", environment["N8TRACKS_LOG_LEVEL"]);
    }

    [Fact]
    public async Task ByDefaultTheGatewayAndTheFrontendArePointedAtTheApi()
    {
        var builder = await AppModel.CreateAsync();
        await using var disposal = builder.ConfigureAwait(false);

        var gateway = await AppModel.EnvironmentAsync(builder, AppModel.Resource<ProjectResource>(builder, AppModel.Gateway));
        var frontend = await AppModel.EnvironmentAsync(builder, AppModel.Resource<ExecutableResource>(builder, AppModel.Frontend));

        Assert.Equal("http://localhost:8787", gateway["N8TRACKS_API_URL"]);
        Assert.Equal("8788", gateway["N8TRACKS_GATEWAY_PORT"]);
        Assert.Equal("Information", gateway["N8TRACKS_LOG_LEVEL"]);
        Assert.Equal("http://localhost:8787", frontend["N8TRACKS_API_URL"]);
    }

    /// <summary>
    /// The services are configured the way a container is. Apart from what Aspire itself adds for
    /// every project (telemetry, and the usual .NET development switches), nothing else is set.
    /// </summary>
    [Theory]
    [InlineData(AppModel.Api, new[] { "N8TRACKS_DATA_PATH", "N8TRACKS_LOG_LEVEL", "N8TRACKS_MEDIA_PATH", "N8TRACKS_PORT" })]
    [InlineData(AppModel.Gateway, new[] { "N8TRACKS_API_URL", "N8TRACKS_GATEWAY_PORT", "N8TRACKS_LOG_LEVEL" })]
    public async Task TheAppHostConfiguresAServiceOnlyThroughProductVariables(string name, string[] expected)
    {
        var builder = await AppModel.CreateAsync();
        await using var disposal = builder.ConfigureAwait(false);

        var environment = await AppModel.EnvironmentAsync(builder, AppModel.Resource<ProjectResource>(builder, name));

        var fromAspire = environment.Keys.Where(static key =>
            key.StartsWith("OTEL_", StringComparison.Ordinal)
            || key.StartsWith("ASPNETCORE_", StringComparison.Ordinal)
            || key.StartsWith("DOTNET_", StringComparison.Ordinal)
            || key.StartsWith("LOGGING__", StringComparison.Ordinal)
            || key.StartsWith("ASPIRE_", StringComparison.Ordinal));

        Assert.Equal(expected, environment.Keys.Except(fromAspire).Order(StringComparer.Ordinal));

        // No launch profile: nothing but the product variable says where to listen.
        Assert.DoesNotContain("ASPNETCORE_URLS", environment.Keys);
        Assert.DoesNotContain(
            AppModel.Resource<ProjectResource>(builder, name).Annotations,
            static annotation => annotation.GetType().Name == "LaunchProfileAnnotation");
    }

    [Theory]
    [InlineData(AppModel.Api)]
    [InlineData(AppModel.Gateway)]
    public async Task TelemetryOfAServiceIsPointedAtTheDashboard(string name)
    {
        // The address the launch profile gives the dashboard's collector.
        const string collector = "http://localhost:15188";

        var builder = await AppModel.CreateAsync($"ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL={collector}");
        await using var disposal = builder.ConfigureAwait(false);

        var environment = await AppModel.EnvironmentAsync(builder, AppModel.Resource<ProjectResource>(builder, name));

        Assert.Equal(collector, environment["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    /// <summary>
    /// Aspire gives every project it starts in development two variables that make OpenTelemetry
    /// export query strings as they are. The AppHost takes both away again: neither service is
    /// handed a variable that turns query redaction off.
    /// </summary>
    [Theory]
    [InlineData(AppModel.Api)]
    [InlineData(AppModel.Gateway)]
    public async Task NoServiceIsHandedAVariableThatTurnsQueryRedactionOff(string name)
    {
        var builder = await AppModel.CreateAsync("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:15188");
        await using var disposal = builder.ConfigureAwait(false);

        var environment = await AppModel.EnvironmentAsync(builder, AppModel.Resource<ProjectResource>(builder, name));

        // Complement: what was read is the environment Aspire's telemetry settings are in.
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", environment.Keys);
        Assert.Contains(environment.Keys, static key => key.StartsWith("OTEL_", StringComparison.Ordinal) && key != "OTEL_EXPORTER_OTLP_ENDPOINT");

        foreach (var variable in new[]
        {
            "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION",
            "OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION",
        })
        {
            var value = environment.GetValueOrDefault(variable);
            Assert.False(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase), $"The {name} resource is given {variable}={value}.");
        }

        Assert.DoesNotContain(environment.Keys, static key => key.Contains("QUERY_REDACTION", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Complement of the defaults above: a variable in the developer's own environment wins. This one
    /// is set for real, in this process, which is where the AppHost runs under test.
    /// </summary>
    [Fact]
    public async Task AVariableInTheDevelopersEnvironmentReplacesTheDefault()
    {
        const string variable = "N8TRACKS_LOG_LEVEL";
        var before = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "Debug");

        try
        {
            var builder = await AppModel.CreateAsync();
            await using var disposal = builder.ConfigureAwait(false);

            var api = await AppModel.EnvironmentAsync(builder, AppModel.Resource<ProjectResource>(builder, AppModel.Api));
            var gateway = await AppModel.EnvironmentAsync(builder, AppModel.Resource<ProjectResource>(builder, AppModel.Gateway));

            Assert.Equal("Debug", api[variable]);
            Assert.Equal("Debug", gateway[variable]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, before);
        }
    }

    [Fact]
    public async Task EverySettingCanBeReplacedWithoutEditingCode()
    {
        using var folder = new TemporaryFolder();
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), folder.Path);

        var builder = await AppModel.CreateAsync(
            "N8TRACKS_PORT=18701",
            "N8TRACKS_GATEWAY_PORT=18702",
            $"N8TRACKS_DATA_PATH={relative}",
            $"N8TRACKS_MEDIA_PATH={folder.Path}",
            $"N8TRACKS_BACKUP_PATH={folder.Path}",
            "N8TRACKS_BASE_URL=http://localhost:18701",
            "N8TRACKS_API_URL=http://elsewhere.example:9999",
            "TZ=Europe/Oslo");
        await using var disposal = builder.ConfigureAwait(false);

        var apiResource = AppModel.Resource<ProjectResource>(builder, AppModel.Api);
        var gatewayResource = AppModel.Resource<ProjectResource>(builder, AppModel.Gateway);
        var api = await AppModel.EnvironmentAsync(builder, apiResource);
        var gateway = await AppModel.EnvironmentAsync(builder, gatewayResource);

        AssertFixedUnproxied(apiResource, 18701);
        AssertFixedUnproxied(gatewayResource, 18702);
        Assert.Equal("18701", api["N8TRACKS_PORT"]);

        // A relative folder is the developer's, relative to where the command was run.
        Assert.Equal(folder.Path, api["N8TRACKS_DATA_PATH"]);
        Assert.Equal(folder.Path, api["N8TRACKS_MEDIA_PATH"]);
        Assert.Equal(folder.Path, api["N8TRACKS_BACKUP_PATH"]);
        Assert.Equal("http://localhost:18701", api["N8TRACKS_BASE_URL"]);
        Assert.Equal("Europe/Oslo", api["TZ"]);

        Assert.Equal("18702", gateway["N8TRACKS_GATEWAY_PORT"]);
        Assert.Equal("http://elsewhere.example:9999", gateway["N8TRACKS_API_URL"]);
    }

    private static void AssertFixedUnproxied(IResource resource, int port)
    {
        var endpoint = AppModel.HttpEndpoint(resource);

        Assert.Equal(port.ToString(CultureInfo.InvariantCulture), endpoint.Port?.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(port.ToString(CultureInfo.InvariantCulture), endpoint.TargetPort?.ToString(CultureInfo.InvariantCulture));
        Assert.False(endpoint.IsProxied, $"The {resource.Name} endpoint is proxied.");
        Assert.Equal("http", endpoint.UriScheme);
    }
}
