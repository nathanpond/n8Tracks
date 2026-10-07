using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Health;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Persistence;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Setup;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Infrastructure.Assets;
using n8Tracks.Infrastructure.Backups;
using n8Tracks.Infrastructure.Health;
using n8Tracks.Infrastructure.Jobs;
using n8Tracks.Infrastructure.Maintenance;
using n8Tracks.Infrastructure.Persistence;
using n8Tracks.Infrastructure.Retention;
using n8Tracks.Infrastructure.Scheduling;
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
        services.AddScoped<IGenreStore, GenreStore>();
        services.AddScoped<ITagStore, TagStore>();
        services.AddScoped<IArtistStore, ArtistStore>();
        services.AddScoped<IAlbumStore, AlbumStore>();
        services.AddScoped<IAlbumTrackStore, AlbumTrackStore>();
        services.AddScoped<IPlaylistStore, PlaylistStore>();
        services.AddScoped<IRelationshipStore, RelationshipStore>();
        services.AddScoped<ISongCreditStore, SongCreditStore>();
        services.AddScoped<ICatalogSettingsStore, CatalogSettingsStore>();
        services.AddScoped<IVersionStore, VersionStore>();
        services.AddScoped<GenerationStore>();
        services.AddScoped<IGenerationStore>(static provider => provider.GetRequiredService<GenerationStore>());
        services.AddScoped<ISongDeletionStore, SongDeletionStore>();
        services.AddScoped<IEditorRevisionStore, EditorRevisionStore>();
        services.AddScoped<IWorkflowStateStore, WorkflowStateStore>();
        services.AddScoped<ISunoModelStore, SunoModelStore>();
        services.AddScoped<ISunoLibraryStore, SunoLibraryStore>();
        services.AddScoped<IProviderTombstoneStore, ProviderTombstoneStore>();
        services.AddScoped<ISunoWorkspaceStore, SunoWorkspaceStore>();
        services.AddScoped<SunoExportStore>();
        services.AddScoped<ISunoExportStore>(static provider => provider.GetRequiredService<SunoExportStore>());
        services.AddScoped<ISunoClipLookup>(static provider => provider.GetRequiredService<SunoExportStore>());
        services.AddScoped<ISongWorkspaceStore, SunoWorkspaceStore>();
        services.AddScoped<IVersionDefaultsStore, VersionDefaultsStore>();
        services.AddSingleton<IBackupStorage, BackupFolders>();
        services.AddSingleton<IBackupWriter, BackupWriter>();
        services.AddScoped<IBackupScheduleStore, BackupScheduleStore>();
        services.TryAddSingleton(new BackupTestHooks());
        services.AddSingleton<IMaintenanceStateStore, MaintenanceStateFile>();
        services.AddScoped<IRestoreArchives, RestoreArchives>();
        services.TryAddSingleton<IDiskSpace, DataDiskSpace>();
        services.TryAddSingleton(new RestoreTestHooks());
        services.AddScoped<ILiveDataReplacement, LiveDataReplacement>();
        services.AddSingleton<ILastRestoreStore, LastRestoreFile>();
        services.AddSingleton<IDataPathLock, DataPathLockFile>();
        services.AddSingleton<UpgradeMarkerFile>();
        services.AddSingleton<IFailedUpgradeMarker>(static provider => provider.GetRequiredService<UpgradeMarkerFile>());
        services.AddScoped<UpgradeSafetyRestore>();
        services.AddSingleton<RestoreRunner>();
        services.AddSingleton<IRestoreRunner>(static provider => provider.GetRequiredService<RestoreRunner>());

        foreach (var type in RetainedTypes.BuiltIn)
        {
            services.AddSingleton(type);
        }

        services.AddSingleton<RetainedTypeRegistry>();
        services.AddScoped<IRetentionStore, RetentionStore>();
        services.AddScoped<IRetentionPruneStateStore, RetentionPruneStateStore>();
        services.AddSingleton<ManagedFiles>();
        services.AddSingleton<IManagedFiles>(static provider => provider.GetRequiredService<ManagedFiles>());
        services.AddScoped<IAssetStore, AssetStore>();
        services.AddScoped<ArtworkAttachmentStore>();
        services.AddScoped<IArtworkAttachmentStore>(static provider => provider.GetRequiredService<ArtworkAttachmentStore>());
        services.AddScoped<IArtworkAttachments>(static provider => provider.GetRequiredService<ArtworkAttachmentStore>());
        services.AddScoped<IArtworkAttachments>(static provider => provider.GetRequiredService<GenerationStore>());
        services.AddScoped<IArtworkAttachments>(static provider => provider.GetRequiredService<SunoExportStore>());
        services.AddSingleton<IManagedAssetStore, ManagedAssetStore>();
        services.AddSingleton<IArtworkImaging, SkiaArtworkImaging>();
        services.AddJobHandler<RetentionPruneJobHandler>(RetentionPruneTask.JobType);

        return services;
    }

    /// <summary>
    /// Adds the background worker that runs queued jobs and the daily-task scheduler. Only the server
    /// adds them: a command run in the container starts nothing on its own.
    /// </summary>
    public static IServiceCollection AddJobWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new JobWorkerOptions());
        services.TryAddSingleton(new DailyTaskSchedulerOptions());
        services.AddHostedService<BackupStartupCleanup>();
        services.AddHostedService<RestoreHousekeeping>();

        // Before the worker: a commit it was running when the process stopped is still marked running.
        services.AddHostedService<ImportCommitRecovery>();
        services.AddHostedService<JobWorker>();
        services.AddHostedService<DailyTaskScheduler>();

        return services;
    }

    /// <summary>How long a graceful shutdown gives the running job to stop.</summary>
    public static TimeSpan JobShutdownGrace => JobWorkerOptions.DefaultShutdownGrace;
}
