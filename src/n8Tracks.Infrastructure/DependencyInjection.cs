using Microsoft.Extensions.DependencyInjection;

namespace n8Tracks.Infrastructure;

/// <summary>Registers the infrastructure layer: persistence and other adapters behind the application layer.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services;
    }
}
