using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Health;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Persistence;
using n8Tracks.Application.Setup;
using n8Tracks.Application.Songs;
using n8Tracks.Infrastructure.Health;
using n8Tracks.Infrastructure.Jobs;
using n8Tracks.Infrastructure.Persistence;
using n8Tracks.Infrastructure.Security;
using n8Tracks.Infrastructure.Setup;

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
        services.AddScoped<IDatabaseSchemaCheck, DatabaseSchemaCheck>();
        services.AddSingleton<IMediaMountProbe, MediaMountProbe>();
        services.AddSingleton<IHealthService, HealthService>();

        services.AddScoped<IAdministratorStore, AdministratorStore>();
        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<ISetupChecks, SetupChecks>();

        services.AddScoped<SessionStore>();
        services.AddScoped<ISessionStore>(static provider => provider.GetRequiredService<SessionStore>());
        services.AddScoped<ISignInAccounts>(static provider => provider.GetRequiredService<SessionStore>());
        services.AddScoped<IAccountPasswords>(static provider => provider.GetRequiredService<SessionStore>());
        services.AddScoped<ISignInThrottleStore, SignInThrottleStore>();
        services.AddScoped<IPasswordResetRecord, PasswordResetRecordStore>();
        services.AddScoped<IExclusiveTransaction, ExclusiveTransaction>();
        services.AddScoped<ICredentialStore, CredentialStore>();
        services.AddScoped<IJobStore, JobStore>();
        services.AddScoped<ISongStore, SongStore>();
        services.AddScoped<IVersionStore, VersionStore>();
        services.AddScoped<IWorkflowStateStore, WorkflowStateStore>();

        return services;
    }

    /// <summary>
    /// Adds the background worker that runs queued jobs. Only the server adds it: a command run in
    /// the container starts nothing on its own.
    /// </summary>
    public static IServiceCollection AddJobWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new JobWorkerOptions());
        services.AddHostedService<JobWorker>();

        return services;
    }

    /// <summary>How long a graceful shutdown gives the running job to stop.</summary>
    public static TimeSpan JobShutdownGrace => JobWorkerOptions.DefaultShutdownGrace;
}
