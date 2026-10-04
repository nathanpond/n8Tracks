using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Health;
using n8Tracks.Application.Persistence;
using n8Tracks.Infrastructure.Health;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure;

/// <summary>Registers the infrastructure layer: persistence and other adapters behind the application layer.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<N8TracksDbContext>(static (provider, options) =>
            options.UseN8TracksSqlite(SqliteDatabase.FilePath(provider.GetRequiredService<N8TracksOptions>().DataPath)));

        services.AddSingleton<MigrationStateHolder>();
        services.AddSingleton<IMigrationStateProvider>(static provider => provider.GetRequiredService<MigrationStateHolder>());

        services.AddSingleton<IDatabaseConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<IMediaMountProbe, MediaMountProbe>();
        services.AddSingleton<IHealthService, HealthService>();

        return services;
    }
}
