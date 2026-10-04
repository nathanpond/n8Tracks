using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Frontend;
using n8Tracks.TestSupport;

namespace n8Tracks.Api.Tests;

/// <summary>
/// Hosts the real entry point in memory. Each host gets its own environment (never the process
/// environment), its own temporary data path, its own (empty, existing) media path, and its own web
/// root (empty: the frontend is "not built" unless a test writes files there before the first
/// request), all removed when the host is disposed. Two things the entry point takes from the process
/// environment: the telemetry switch and the environment name. The host is built with both set as
/// the test asks: telemetry export is off whatever the machine running the tests has in
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, unless <see cref="OtlpEndpoint"/> is set, and the environment
/// is <see cref="EnvironmentName"/>. The entry point takes nothing from the arguments the test host
/// passes it (an environment name, a content root, and <see cref="HostSettings"/>).
/// </summary>
public class N8TracksApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string> variables;
    private readonly string temporaryMediaPath;

    public N8TracksApiFactory()
        : this(new Dictionary<string, string>(StringComparer.Ordinal))
    {
    }

    /// <summary>A host with the given variables. Internal: xunit allows a fixture one public constructor.</summary>
    internal N8TracksApiFactory(IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        DataPath = Directory.CreateTempSubdirectory("n8tracks-test-").FullName;
        this.variables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
        this.variables.TryAdd(EnvironmentOptionsLoader.DataPath, DataPath);

        // Kept apart from the data path, which a test may point at a directory of its own.
        temporaryMediaPath = Directory.CreateTempSubdirectory("n8tracks-test-media-").FullName;
        this.variables.TryAdd(EnvironmentOptionsLoader.MediaPath, temporaryMediaPath);
    }

    public string DataPath { get; }

    /// <summary>The directory the host serves the frontend from, never the project's own <c>wwwroot</c>.</summary>
    public string WebRootPath { get; } = Directory.CreateTempSubdirectory("n8tracks-test-webroot-").FullName;

    /// <summary>The media path the host was given: the temporary one unless the variables named another.</summary>
    public string MediaPath => variables[EnvironmentOptionsLoader.MediaPath];

    /// <summary>Changes the host's services after the application's own registrations. Set before the first request.</summary>
    internal Action<IServiceCollection>? TestServices { get; init; }

    /// <summary>
    /// The collector the host exports telemetry to (OTLP over HTTP), or null for no export. Reaches the
    /// host as the environment variable, the only switch there is. Set before the first request.
    /// </summary>
    internal string? OtlpEndpoint { get; init; }

    /// <summary>
    /// The value of <c>ASPNETCORE_ENVIRONMENT</c> the host is built with, or null for none (which
    /// is <c>Production</c>, as in the image). Set before the first request.
    /// </summary>
    internal string? EnvironmentName { get; init; } = Environments.Development;

    /// <summary>
    /// Settings handed to the entry point as command-line arguments (<c>--name=value</c>), which is
    /// how the test host passes them on: not the environment. Set before the first request.
    /// </summary>
    internal IReadOnlyList<(string Name, string Value)> HostSettings { get; init; } = [];

    protected override IHost CreateHost(IHostBuilder builder) =>
        TelemetryEnvironment.BuildHost(OtlpEndpoint, () => base.CreateHost(builder), EnvironmentName);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var (name, value) in HostSettings)
        {
            builder.UseSetting(name, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<EnvironmentSnapshot>();
            services.AddSingleton(new EnvironmentSnapshot(variables, DataPath));
            services.RemoveAll<FrontendFiles>();
            services.AddSingleton<PhysicalFileProvider>(_ => new PhysicalFileProvider(WebRootPath));
            services.AddSingleton(provider => new FrontendFiles(provider.GetRequiredService<PhysicalFileProvider>()));
            TestServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        foreach (var directory in new[] { DataPath, temporaryMediaPath, WebRootPath })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
