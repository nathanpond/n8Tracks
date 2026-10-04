using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Configuration;

namespace n8Tracks.Api.Tests;

/// <summary>
/// Hosts the real entry point in memory. Each host gets its own environment (never the process
/// environment) and its own temporary data path, removed when the host is disposed.
/// </summary>
public class N8TracksApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string> variables;

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
    }

    public string DataPath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<EnvironmentSnapshot>();
            services.AddSingleton(new EnvironmentSnapshot(variables, DataPath));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(DataPath))
        {
            Directory.Delete(DataPath, recursive: true);
        }
    }
}
