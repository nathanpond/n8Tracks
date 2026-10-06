using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.References;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Scheduling;
using n8Tracks.Application.Setup;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;

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
        services.AddScoped<ExtensionHandshakeService>();
        services.AddSingleton<JobSignal>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<IJobQueue>(static provider => provider.GetRequiredService<JobQueue>());
        services.AddScoped<JobService>();
        services.AddScoped<ModelCatalogService>();
        services.AddScoped<ISunoModelList>(static provider => provider.GetRequiredService<ModelCatalogService>());
        services.AddScoped<VersionDefaultsService>();
        services.AddScoped<GenreService>();
        services.AddScoped<TagService>();
        services.AddScoped<ArtistService>();
        services.AddScoped<AlbumService>();
        services.AddScoped<AlbumTrackService>();
        services.AddScoped<PlaylistService>();
        services.AddScoped<RelationshipService>();
        services.AddScoped<SongCreditService>();
        services.AddScoped<SongService>();
        services.AddScoped<VersionService>();
        services.AddScoped<VersionDeletionService>();
        services.AddScoped<SongDeletionService>();
        services.AddScoped<EditorRevisionService>();
        services.AddScoped<GenerationService>();
        services.AddScoped<GenerationEvaluationService>();
        services.AddScoped<GenerationSelectionService>();
        services.AddScoped<WorkflowStateService>();
        services.AddScoped<ReferenceResolver>();
        services.AddSingleton<BackupStartLock>();
        services.AddScoped<BackupService>();
        services.AddSingleton<BackupScheduleProcess>();
        services.AddScoped<BackupScheduleService>();
        services.AddJobHandler<BackupJobHandler>(BackupService.JobType);
        services.AddDailyTask<BackupScheduleTask>();
        services.AddScoped<RetentionService>();
        services.AddScoped<DeletedItemsService>();
        services.AddDailyTask<RetentionPruneTask>();
        services.AddScoped<ArtworkService>();
        services.AddScoped<ArtworkAttachmentService>();
        services.AddScoped<ILiveFileReferences, ArtworkFileReferences>();
        services.AddSingleton<ArtworkSweepSchedule>();
        services.AddDailyTask<ArtworkSweepTask>();
        services.AddSingleton<MaintenanceMode>();
        services.TryAddSingleton(new RestoreOptions());
        services.AddSingleton<RestoreValidations>();
        services.AddSingleton<RestoreReads>();
        services.AddScoped<RestoreValidator>();
        services.AddScoped<RestoreService>();
        services.AddScoped<OfflineRestoreService>();

        return services;
    }
}
