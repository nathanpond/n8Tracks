using n8Tracks.Api.Endpoints;
using n8Tracks.Application;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure;

namespace n8Tracks.Api.DependencyInjection;

/// <summary>The composition root of a command run in the container, beside the server's own in <c>Program</c>.</summary>
internal static class CommandServices
{
    /// <summary>
    /// The minimal container: the application and infrastructure layers over <paramref name="options"/>,
    /// and nothing that starts on its own. No logging provider is registered, so the database layer
    /// writes no line of its own.
    /// </summary>
    public static ServiceProvider Build(N8TracksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new ApplicationVersion(ProductVersion.Current));
        services.AddApplication();
        services.AddInfrastructure();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
