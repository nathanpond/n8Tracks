using Microsoft.Extensions.DependencyInjection;

namespace n8Tracks.Application;

/// <summary>Registers the application-service layer: the one place business rules live.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services;
    }
}
