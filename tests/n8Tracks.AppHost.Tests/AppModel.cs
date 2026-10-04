using System.Net;
using System.Net.Sockets;
using Aspire.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace n8Tracks.AppHost.Tests;

/// <summary>Builds the AppHost's app model the way <c>dotnet run</c> does, for a test to inspect or start.</summary>
internal static class AppModel
{
    public const string Api = "api";
    public const string Gateway = "gateway";
    public const string Frontend = "frontend";

    /// <summary>
    /// The model as the AppHost declares it. <paramref name="settings"/> are passed as
    /// <c>NAME=value</c> arguments, which the AppHost reads exactly like environment variables.
    /// </summary>
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync(params string[] settings) =>
        DistributedApplicationTestingBuilder.CreateAsync<Projects.n8Tracks_AppHost>(settings, CancellationToken.None);

    /// <summary>Takes the frontend dev server (and its installer) out, so a test needs neither Node nor <c>web/node_modules</c>.</summary>
    public static void RemoveFrontend(IDistributedApplicationTestingBuilder builder)
    {
        foreach (var resource in builder.Resources.Where(static resource => resource.Name.StartsWith(Frontend, StringComparison.Ordinal)).ToList())
        {
            builder.Resources.Remove(resource);
        }
    }

    public static T Resource<T>(IDistributedApplicationTestingBuilder builder, string name)
        where T : IResource =>
        Assert.IsType<T>(Assert.Single(builder.Resources, resource => resource.Name == name), exactMatch: false);

    /// <summary>The environment the AppHost gives a resource, with every reference resolved.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> EnvironmentAsync(IDistributedApplicationTestingBuilder builder, IResource resource)
    {
        // Nothing is started, so nothing has given the endpoints their addresses yet. They are fixed
        // and unproxied, so the address is known: the declared port on localhost.
        foreach (var endpoint in builder.Resources.SelectMany(static other => other.Annotations.OfType<EndpointAnnotation>()))
        {
            endpoint.AllocatedEndpoint ??= new AllocatedEndpoint(endpoint, "localhost", Assert.NotNull(endpoint.Port));
        }

        var configuration = await ExecutionConfigurationBuilder
            .Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Null(configuration.Exception);

        return configuration.EnvironmentVariables.ToDictionary(static variable => variable.Key, static variable => variable.Value, StringComparer.Ordinal);
    }

    public static EndpointAnnotation HttpEndpoint(IResource resource) =>
        Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), static endpoint => endpoint.Name == "http");

    /// <summary>A port nothing listens on right now, so a test never collides with a stack a developer has running.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
