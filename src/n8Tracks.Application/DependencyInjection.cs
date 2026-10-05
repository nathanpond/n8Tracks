using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.References;
using n8Tracks.Application.Setup;
using n8Tracks.Application.Songs;

namespace n8Tracks.Application;

/// <summary>Registers the application-service layer: the one place business rules live.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SetupCompletion>();
        services.AddScoped<SetupService>();
        services.AddSingleton<DummyPasswordHash>();
        services.AddScoped<SessionService>();
        services.AddScoped<AccountService>();
        services.AddScoped<CredentialService>();
        services.AddScoped<CredentialVerifier>();
        services.AddSingleton<JobSignal>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<IJobQueue>(static provider => provider.GetRequiredService<JobQueue>());
        services.AddScoped<JobService>();
        services.AddScoped<SongService>();
        services.AddScoped<VersionService>();
        services.AddScoped<EditorRevisionService>();
        services.AddScoped<GenerationService>();
        services.AddScoped<WorkflowStateService>();
        services.AddScoped<ReferenceResolver>();
        services.AddSingleton<BackupStartLock>();
        services.AddScoped<BackupService>();
        services.AddJobHandler<BackupJobHandler>(BackupService.JobType);

        return services;
    }
}
