using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Frontend;

namespace n8Tracks.Api.Tests;

/// <summary>
/// Hosts the real entry point in memory. Each host gets its own environment (never the process
/// environment), its own temporary data path, its own (empty, existing) media path, and its own web
/// root (empty: the frontend is "not built" unless a test writes files there before the first
/// request), all removed when the host is disposed.
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

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
