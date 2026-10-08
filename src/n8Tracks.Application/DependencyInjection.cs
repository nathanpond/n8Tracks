using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Application.Artwork;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Media;
using n8Tracks.Application.References;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Scheduling;
using n8Tracks.Application.Setup;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Application.Suno.Generate;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application;

/// <summary>Registers the application-service layer: the one place business rules live.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // The Suno audio hosts the settings hold (#221): every reader of the list takes this one.
        services.TryAddSingleton<SunoAudioHosts>(static provider => provider.GetRequiredService<N8TracksOptions>().SunoAudioHosts);
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
        services.AddScoped<SunoLibraryService>();
        services.AddScoped<TombstoneService>();
        services.AddScoped<AudioFileLifecycle>();
        services.AddScoped<SunoIdMatcher>();
        services.AddScoped<SunoClipLookupService>();
        services.AddScoped<ExternalReferenceResolver>();
        services.AddScoped<SunoWorkspaceService>();
        services.AddScoped<RecordClassifier>();
        services.AddScoped<ProposalService>();
        services.AddScoped<ExportStagingService>();
        services.AddScoped<ImportReviewService>();
        services.AddScoped<IgnoreListService>();
        services.AddScoped<RemoteStateService>();
        services.AddScoped<SunoStatusService>();
        services.AddScoped<GenerationRequestService>();
        services.AddScoped<ObservedCreateService>();
        services.AddScoped<ProvisionalCompletionService>();
        services.AddJobHandler<ExportClassifyJobHandler>(ExportStagingService.ClassifyJobType);
        services.AddScoped<ImportCommitService>();
        services.AddScoped<ImportTargetWriter>();
        services.AddScoped<ChangeResolutionService>();
        services.AddScoped<ChangeResolutionWriter>();
        services.AddJobHandler<ImportCommitJob>(ImportCommitService.JobType);
        services.AddScoped<GenreService>();
        services.AddScoped<TagService>();
        services.AddScoped<ArtistService>();
        services.AddScoped<AlbumService>();
        services.AddScoped<AlbumTrackService>();
        services.AddScoped<PlaylistService>();
        services.AddScoped<RelationshipService>();
        services.AddScoped<SongCreditService>();
        services.AddScoped<SongService>();
        services.AddScoped<SongWorkspaceService>();
        services.AddScoped<VersionService>();
        services.AddScoped<VersionDeletionService>();
        services.AddScoped<SongDeletionService>();
        services.AddScoped<EditorRevisionService>();
        services.AddScoped<GenerationService>();
        services.AddScoped<GenerationEvaluationService>();
        services.AddScoped<GenerationSelectionService>();
        services.AddScoped<GenerationMoveService>();
        services.AddScoped<GenerationDeletionService>();
        services.AddScoped<GenerationArtworkService>();
        services.AddScoped<WorkflowStateService>();
        services.AddScoped<ReferenceResolver>();
        services.AddSingleton<BackupStartLock>();
        services.AddScoped<BackupService>();
        services.AddSingleton<BackupScheduleProcess>();
        services.AddScoped<BackupScheduleService>();
        services.AddJobHandler<BackupJobHandler>(BackupService.JobType);
        services.AddDailyTask<BackupScheduleTask>();
        services.TryAddSingleton(new MediaScanOptions());
        services.AddSingleton<MediaScanStartLock>();
        services.AddScoped<MediaScanService>();
        services.AddScoped<AudioFileService>();
        services.AddScoped<AudioFileAssociationService>();
        services.AddScoped<PreferredAudioFileService>();
        services.AddScoped<PlaybackService>();
        services.AddScoped<DownloadRecordService>();
        services.AddScoped<MediaStatusService>();
        services.AddScoped<AudioContentService>();
        services.AddJobHandler<MediaScanJobHandler>(MediaScanService.JobType);
        services.AddSingleton<MediaScanStartup>();
        services.AddScoped<MediaScanScheduleService>();
        services.AddScoped<MediaAvailability>();
        services.AddSingleton<MediaProbeStreak>();
        services.AddScoped<MediaRecoveryService>();
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
