using Microsoft.AspNetCore.Server.Kestrel.Core;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Configuration;

internal static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the environment snapshot and the one <see cref="N8TracksOptions"/> loaded from it, and
    /// makes Kestrel listen on the configured port: plain HTTP, all interfaces.
    /// </summary>
    public static IServiceCollection AddEnvironmentConfiguration(this IServiceCollection services, EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddSingleton(environment);
        services.AddSingleton(provider => EnvironmentOptionsLoader.Load(provider.GetRequiredService<EnvironmentSnapshot>()));

        // The one endpoint. The host reads no other source (see Program), so nothing can add a second.
        services.AddOptions<KestrelServerOptions>()
            .Configure<IServiceProvider>((kestrel, provider) =>
            {
                try
                {
                    kestrel.ListenAnyIP(provider.GetRequiredService<N8TracksOptions>().Port);
                }
                catch (ConfigurationValidationException)
                {
                    // Kestrel's options are built while the host is built. Program reports the invalid
                    // settings and exits before the server starts, so no endpoint is needed here.
                }
            });

        return services;
    }
}
