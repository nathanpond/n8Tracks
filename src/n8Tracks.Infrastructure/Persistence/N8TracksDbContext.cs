using Microsoft.EntityFrameworkCore;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Media;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;
using n8Tracks.Infrastructure.Retention;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The EF Core context over <c>n8tracks.db</c>. Tables and columns are snake_case, by convention;
/// each schema change is one migration under <c>Persistence/Migrations</c>.
/// </summary>
public sealed class N8TracksDbContext(DbContextOptions<N8TracksDbContext> options) : DbContext(options)
{
    /// <summary>The trigger that moves a Song's last-updated time when one of its Versions is added.</summary>
    public const string VersionInsertedTrigger = "tr_versions_touch_song_after_insert";

    /// <summary>The trigger that moves a Song's last-updated time when one of its Versions changes.</summary>
    public const string VersionUpdatedTrigger = "tr_versions_touch_song_after_update";

    /// <summary>The trigger that records a new Version's number as used, failing when it was ever used before.</summary>
    public const string VersionNumberRecordedTrigger = "tr_versions_record_number_after_insert";

    /// <summary>The trigger that refuses any change to a Version's number or Song.</summary>
    public const string VersionNumberFixedTrigger = "tr_versions_number_never_changes";

    /// <summary>The trigger that refuses changing a frozen Version's lyrics, styles, kind, model, or options, or unfreezing it.</summary>
    public const string VersionFrozenTrigger = "tr_versions_frozen_inputs_never_change";

    /// <summary>
    /// The trigger that refuses any change to a Generation's Version, Song, or ordinal but a move
    /// (#123): to the newest ordinal of another, frozen Version of the Song it names, once its old
    /// shortcode is recorded as its alias, and never to a shortcode that is another's alias.
    /// </summary>
    public const string GenerationMoveTrigger = "tr_generations_move_only_leaving_an_alias";

    /// <summary>The trigger that refuses a new Generation (or a restored one) at a shortcode that is another Generation's alias.</summary>
    public const string GenerationAliasReservedTrigger = "tr_generations_aliases_stay_reserved";

    /// <summary>
    /// The trigger that refuses changing a Generation's Suno ID once it has one (#324): a frozen Version's
    /// source that points at the Generation is compared by it.
    /// </summary>
    public const string GenerationSunoIdFixedTrigger = "tr_generations_suno_id_never_changes";

    /// <summary>The trigger that refuses changing an external reference's Suno ID or kind, which frozen sources are compared by.</summary>
    public const string ExternalReferenceFixedTrigger = "tr_external_suno_references_identity_never_changes";

    /// <summary>
    /// The tables holding a Version's lineage (#122), each with insert, update, and delete triggers
    /// that refuse any change while the Version is frozen (<see cref="LineageFrozenTriggers"/>).
    /// </summary>
    public static IReadOnlyList<string> LineageTables { get; } = ["version_sources", "version_inspiration_playlists", "version_voices", "version_file_inputs"];

    /// <summary>The names of the three freeze triggers of a lineage table: insert, update, delete.</summary>
    public static IReadOnlyList<string> LineageFrozenTriggers(string table) =>
        [$"tr_{table}_frozen_insert", $"tr_{table}_frozen_update", $"tr_{table}_frozen_delete"];

    /// <summary>
    /// The tables holding text the search index holds of a Song, or which Songs it belongs to (#223),
    /// each with triggers that record the Songs a write touched (<see cref="SearchTriggers"/>), which
    /// the unit of work re-indexes before it commits.
    /// </summary>
    public static IReadOnlyList<string> SearchTables { get; } =
        ["songs", "versions", "generations", "generation_comments", "song_tags", "tags", "album_songs", "albums", "playlist_songs", "playlists"];

    /// <summary>
    /// The names of a <see cref="SearchTables"/> table's search triggers: insert, update, and delete;
    /// only update and delete on <c>tags</c>, <c>albums</c>, and <c>playlists</c>, whose new rows are on
    /// no Song yet.
    /// </summary>
    public static IReadOnlyList<string> SearchTriggers(string table) =>
        table is "tags" or "albums" or "playlists"
            ? [$"tr_{table}_search_update", $"tr_{table}_search_delete"]
            : [$"tr_{table}_search_insert", $"tr_{table}_search_update", $"tr_{table}_search_delete"];

    public DbSet<AppMetadataEntry> AppMetadata => Set<AppMetadataEntry>();

    public DbSet<AdministratorRecord> Administrators => Set<AdministratorRecord>();

    public DbSet<SettingRecord> Settings => Set<SettingRecord>();

    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    public DbSet<CredentialRecord> Credentials => Set<CredentialRecord>();

    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    public DbSet<WorkflowStateRecord> WorkflowStates => Set<WorkflowStateRecord>();

    public DbSet<SunoModelRecord> SunoModels => Set<SunoModelRecord>();

    public DbSet<ShortcodeSequenceRecord> ShortcodeSequence => Set<ShortcodeSequenceRecord>();

    public DbSet<SongRecord> Songs => Set<SongRecord>();

    public DbSet<SongLinkRecord> SongLinks => Set<SongLinkRecord>();

    public DbSet<VersionRecord> Versions => Set<VersionRecord>();

    public DbSet<UsedVersionNumberRecord> UsedVersionNumbers => Set<UsedVersionNumberRecord>();

    public DbSet<EditorRevisionRecord> EditorRevisions => Set<EditorRevisionRecord>();

    public DbSet<GenerationRecord> Generations => Set<GenerationRecord>();

    public DbSet<ProviderRecordRecord> ProviderRecords => Set<ProviderRecordRecord>();

    public DbSet<GenerationEventRecord> GenerationEvents => Set<GenerationEventRecord>();

    public DbSet<GenerationCommentRecord> GenerationComments => Set<GenerationCommentRecord>();

    public DbSet<GenerationEventLinkRecord> GenerationEventLinks => Set<GenerationEventLinkRecord>();

    public DbSet<ExternalSunoReferenceRecord> ExternalSunoReferences => Set<ExternalSunoReferenceRecord>();

    public DbSet<ShortcodeAliasRecord> ShortcodeAliases => Set<ShortcodeAliasRecord>();

    public DbSet<SunoPlaylistRecord> SunoPlaylists => Set<SunoPlaylistRecord>();

    public DbSet<SunoPersonaRecord> SunoPersonas => Set<SunoPersonaRecord>();

    public DbSet<SunoWorkspaceRecord> SunoWorkspaces => Set<SunoWorkspaceRecord>();

    public DbSet<ProviderTombstoneRecord> ProviderTombstones => Set<ProviderTombstoneRecord>();

    public DbSet<SunoGenerationRequestRecord> SunoGenerationRequests => Set<SunoGenerationRequestRecord>();

    public DbSet<SunoExportRecord> SunoExports => Set<SunoExportRecord>();

    public DbSet<AttentionDismissalRecord> AttentionDismissals => Set<AttentionDismissalRecord>();

    public DbSet<SunoExportPartRecord> SunoExportParts => Set<SunoExportPartRecord>();

    public DbSet<StagedClipRecord> StagedClips => Set<StagedClipRecord>();

    public DbSet<StagedClipPlaylistRecord> StagedClipPlaylists => Set<StagedClipPlaylistRecord>();

    public DbSet<SunoIgnoredItemRecord> SunoIgnoredItems => Set<SunoIgnoredItemRecord>();

    public DbSet<VersionSourceRecord> VersionSources => Set<VersionSourceRecord>();

    public DbSet<VersionInspirationPlaylistRecord> VersionInspirationPlaylists => Set<VersionInspirationPlaylistRecord>();

    public DbSet<VersionVoiceRecord> VersionVoices => Set<VersionVoiceRecord>();

    public DbSet<VersionFileInputRecord> VersionFileInputs => Set<VersionFileInputRecord>();

    public DbSet<GenreRecord> Genres => Set<GenreRecord>();

    public DbSet<SongGenreRecord> SongGenres => Set<SongGenreRecord>();

    public DbSet<TagRecord> Tags => Set<TagRecord>();

    public DbSet<SongTagRecord> SongTags => Set<SongTagRecord>();

    public DbSet<ArtistRecord> Artists => Set<ArtistRecord>();

    public DbSet<ArtistAliasRecord> ArtistAliases => Set<ArtistAliasRecord>();

    public DbSet<ArtistLinkRecord> ArtistLinks => Set<ArtistLinkRecord>();

    public DbSet<SongCreditRecord> SongCredits => Set<SongCreditRecord>();

    public DbSet<AlbumRecord> Albums => Set<AlbumRecord>();

    public DbSet<AlbumLinkRecord> AlbumLinks => Set<AlbumLinkRecord>();

    public DbSet<AlbumSongRecord> AlbumSongs => Set<AlbumSongRecord>();

    public DbSet<PlaylistRecord> Playlists => Set<PlaylistRecord>();

    public DbSet<PlaylistSongRecord> PlaylistSongs => Set<PlaylistSongRecord>();

    public DbSet<RelationshipTypeRecord> RelationshipTypes => Set<RelationshipTypeRecord>();

    public DbSet<SongRelationshipRecord> SongRelationships => Set<SongRelationshipRecord>();

    public DbSet<RetentionGroupRecord> RetentionGroups => Set<RetentionGroupRecord>();

    public DbSet<RetentionRecordRecord> RetentionRecords => Set<RetentionRecordRecord>();

    public DbSet<RetentionReleasedAudioFileRecord> RetentionReleasedAudioFiles => Set<RetentionReleasedAudioFileRecord>();

    public DbSet<PendingFileDeletionRecord> PendingFileDeletions => Set<PendingFileDeletionRecord>();

    public DbSet<AssetRecord> Assets => Set<AssetRecord>();

    public DbSet<ArtworkAttachmentRecord> ArtworkAttachments => Set<ArtworkAttachmentRecord>();

    public DbSet<AudioFileRecord> AudioFiles => Set<AudioFileRecord>();

    public DbSet<DownloadRecordRecord> DownloadRecords => Set<DownloadRecordRecord>();

    public DbSet<GenerationPreferredAudioFileRecord> GenerationPreferredAudioFiles => Set<GenerationPreferredAudioFileRecord>();

    public DbSet<SongPreferredAudioFileRecord> SongPreferredAudioFiles => Set<SongPreferredAudioFileRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Conventions.Add(static _ => new SnakeCaseNamingConvention());
    }

    /// <summary>
    /// <paramref name="text"/> in lower case, every letter that has one (not only ASCII): the SQL
    /// function <see cref="ConnectionSettingsInterceptor.LowerFunction"/>, for queries only.
    /// </summary>
    public static string? Lower(string? text) => throw new NotSupportedException("Only for queries: SQLite computes it.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDbFunction(typeof(N8TracksDbContext).GetMethod(nameof(Lower), [typeof(string)])!)
            .HasName(ConnectionSettingsInterceptor.LowerFunction);

        modelBuilder.Entity<AppMetadataEntry>().HasKey(entry => entry.Key);

        modelBuilder.Entity<AdministratorRecord>(administrator =>
        {
            administrator.ToTable("administrators", static table =>
                table.HasCheckConstraint("ck_administrators_slot", $"slot = {AdministratorRecord.OnlySlot}"));
            administrator.HasKey(record => record.Id);
            administrator.HasIndex(record => record.Slot).IsUnique();
            administrator.HasIndex(record => record.UsernameKey).IsUnique();
        });

        modelBuilder.Entity<SettingRecord>(setting =>
        {
            setting.ToTable("settings", static table => table.HasCheckConstraint("ck_settings_value_json", "json_valid(value)"));
            setting.HasKey(record => record.Key);
        });

        modelBuilder.Entity<SessionRecord>(session =>
        {
            session.ToTable("sessions");
            session.HasKey(record => record.IdHash);
            session.HasOne<AdministratorRecord>()
                .WithMany()
                .HasForeignKey(record => record.AdministratorId)
                .OnDelete(DeleteBehavior.Cascade);
            session.HasIndex(record => record.LastUsedUtc);
        });

        modelBuilder.Entity<CredentialRecord>(credential =>
        {
            credential.ToTable("credentials", static table =>
            {
                table.HasCheckConstraint("ck_credentials_scopes", "length(scopes) > 0");
                table.HasCheckConstraint("ck_credentials_revision", "revision >= 1");
            });
            credential.HasKey(record => record.Id);
            credential.HasIndex(record => record.TokenHash).IsUnique();
            credential.HasIndex(record => record.NameKey).IsUnique().HasFilter("revoked_utc IS NULL");
        });

        modelBuilder.Entity<JobRecord>(job =>
        {
            job.ToTable("jobs", static table =>
            {
                table.HasCheckConstraint(
                    "ck_jobs_status",
                    $"status IN ('{JobRecord.Queued}', '{JobRecord.Running}', '{JobRecord.Succeeded}', '{JobRecord.Failed}')");
                table.HasCheckConstraint("ck_jobs_progress", "progress BETWEEN 0 AND 100");
                table.HasCheckConstraint("ck_jobs_payload_json", "payload IS NULL OR json_valid(payload)");
                table.HasCheckConstraint("ck_jobs_result_json", "result IS NULL OR json_valid(result)");
            });
            job.HasKey(record => record.Id);
            job.Property(record => record.Sequence).ValueGeneratedNever();
            job.HasIndex(record => record.Sequence).IsUnique();
            job.HasIndex(record => new { record.Status, record.Sequence });
            job.HasIndex(record => record.FinishedUtc);
        });

        OnCatalogCreating(modelBuilder);
        OnRetentionCreating(modelBuilder);
        OnAssetsCreating(modelBuilder);
        OnMediaCreating(modelBuilder);
    }

    /// <summary>An audio file's association origin (#206): one of the origins, or none.</summary>
    internal const string AudioFileAssociationOriginCheck = "association_origin IS NULL OR association_origin IN ('suno-id', 'user')";

    /// <summary>An unmatched reason (#206): one of the codes, or none.</summary>
    internal const string AudioFileUnmatchedReasonCheck =
        "unmatched_reason IS NULL OR unmatched_reason IN ('generation_deleted', 'multiple_suno_ids', 'unassociated_by_user', 'song_deleted')";

    /// <summary>
    /// An audio file's association (#206): an origin exactly when it has a Song; a Generation only with
    /// a Song (the composite foreign key then makes it that Song's); a <c>suno-id</c> association always
    /// names a Generation; and a reason only while it has no Song.
    /// </summary>
    internal const string AudioFileAssociationCheck =
        "(song_id IS NULL) = (association_origin IS NULL) "
        + "AND (generation_id IS NULL OR song_id IS NOT NULL) "
        + "AND (association_origin IS NOT 'suno-id' OR generation_id IS NOT NULL) "
        + "AND (unmatched_reason IS NULL OR song_id IS NULL)";

    /// <summary>
    /// The audio file catalog (#203): one row per distinct path under the media mount, unique by its
    /// relative path (compared byte for byte, so letter case and Unicode form make distinct rows).
    /// </summary>
    private static void OnMediaCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AudioFileRecord>(file =>
        {
            file.ToTable("audio_files", static table =>
            {
                table.HasCheckConstraint("ck_audio_files_path", "length(path) > 0 AND substr(path, 1, 1) <> '/'");
                table.HasCheckConstraint("ck_audio_files_file_name", "length(file_name) > 0");
                table.HasCheckConstraint(
                    "ck_audio_files_format",
                    $"format IN ({string.Join(", ", AudioFormats.All.Select(static format => $"'{format}'"))})");
                table.HasCheckConstraint("ck_audio_files_size_bytes", "size_bytes >= 0");
                table.HasCheckConstraint("ck_audio_files_status", $"status IN ('{AudioFileRecord.Available}', '{AudioFileRecord.Missing}')");
                table.HasCheckConstraint("ck_audio_files_duration_ms", "duration_ms IS NULL OR duration_ms > 0");
                table.HasCheckConstraint("ck_audio_files_metadata_readable", "metadata_readable = (duration_ms IS NOT NULL)");
                table.HasCheckConstraint("ck_audio_files_title", $"title IS NULL OR length(title) BETWEEN 1 AND {AudioFormats.MaximumTagLength}");
                table.HasCheckConstraint("ck_audio_files_artist", $"artist IS NULL OR length(artist) BETWEEN 1 AND {AudioFormats.MaximumTagLength}");
                table.HasCheckConstraint("ck_audio_files_association_origin", AudioFileAssociationOriginCheck);
                table.HasCheckConstraint("ck_audio_files_unmatched_reason", AudioFileUnmatchedReasonCheck);
                table.HasCheckConstraint("ck_audio_files_association", AudioFileAssociationCheck);
                table.HasCheckConstraint("ck_audio_files_revision", "revision >= 1");
            });
            file.HasKey(record => record.Id);
            file.HasIndex(record => record.Path).IsUnique();
            file.HasIndex(record => record.Status);
            file.Property(record => record.Revision).HasDefaultValue(1);

            // Its association (#206). The Song by a plain foreign key; the Generation by a composite one,
            // (generation_id, song_id) to generations (id, song_id), written by hand in the migration and
            // not modelled here: EF Core would make (id, song_id) an alternate key of the Generation,
            // which it then refuses to change, and a move changes a Generation's Song. The composite key
            // cascades on update, so a moved Generation's files follow it to its new Song. Neither key
            // cascades on delete: a deletion releases the files first (AudioFileLifecycle).
            file.HasIndex(record => record.SongId);
            file.HasIndex(record => new { record.GenerationId, record.SongId });

            // The parent keys of the preferred-file tables' composite foreign keys (#212). The ID alone
            // is already unique; SQLite wants a unique index over exactly the columns referred to.
            file.HasIndex(record => new { record.Id, record.GenerationId }).IsUnique();
            file.HasIndex(record => new { record.Id, record.SongId }).IsUnique();
            file.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Preferred audio files (#212): at most one per Generation and one per Song, each naming a file
        // that is that owner's. Each owner by a plain RESTRICT key (a deletion clears the choice first,
        // AudioFileLifecycle); the file by a composite key to audio_files (id, generation_id) or
        // (id, song_id), written by hand in the migration and not modelled here, as audio_files' own
        // composite key is not: it refuses a choice of another owner's file, and refuses changing a
        // chosen file's association before the choice is cleared. A file is chosen by one owner at most.
        modelBuilder.Entity<GenerationPreferredAudioFileRecord>(preferred =>
        {
            preferred.ToTable("generation_preferred_audio_files");
            preferred.HasKey(record => record.GenerationId);
            preferred.HasIndex(record => record.AudioFileId).IsUnique();
            preferred.HasOne<GenerationRecord>()
                .WithMany()
                .HasForeignKey(record => record.GenerationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SongPreferredAudioFileRecord>(preferred =>
        {
            preferred.ToTable("song_preferred_audio_files");
            preferred.HasKey(record => record.SongId);
            preferred.HasIndex(record => record.AudioFileId).IsUnique();
            preferred.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Download records (#222): files the extension fetched to the user's computer, by Suno ID with
        // no foreign key (a record is kept for a clip that is not, or no longer, a Generation). Only
        // ever inserted; the times compare as text because both are written by UtcText.
        modelBuilder.Entity<DownloadRecordRecord>(download =>
        {
            download.ToTable("download_records", static table =>
            {
                table.HasCheckConstraint(
                    "ck_download_records_format",
                    $"format IN ({string.Join(", ", DownloadFormats.All.Select(static format => $"'{format}'"))})");
                table.HasCheckConstraint("ck_download_records_suno_id", "length(suno_id) = 36 AND suno_id = lower(suno_id)");
                table.HasCheckConstraint(
                    "ck_download_records_file_name",
                    $"length(file_name) BETWEEN 1 AND {DownloadFormats.MaximumFileNameLength} AND instr(file_name, '/') = 0 AND instr(file_name, '\\') = 0");
                table.HasCheckConstraint("ck_download_records_size_bytes", "size_bytes IS NULL OR size_bytes >= 0");
                table.HasCheckConstraint("ck_download_records_completed_utc", "completed_utc <= received_utc");
            });
            download.HasKey(record => record.Id);
            download.HasIndex(record => record.SunoId);
        });
    }

    /// <summary>
    /// Managed artwork: one row per distinct image, and the attachments that make an asset an
    /// owner's artwork, one per owner. An attachment refers to its asset by a RESTRICT foreign key, so
    /// an attached asset's row is never removed; its owner is named by type and ID (no foreign key, so
    /// one table serves every owner type).
    /// </summary>
    private static void OnAssetsCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AssetRecord>(asset =>
        {
            asset.ToTable("assets", static table =>
            {
                table.HasCheckConstraint("ck_assets_media_type", "media_type IN ('image/jpeg', 'image/png', 'image/webp')");
                table.HasCheckConstraint("ck_assets_bytes", "bytes > 0");
                table.HasCheckConstraint("ck_assets_dimensions", "width > 0 AND height > 0");
                table.HasCheckConstraint("ck_assets_thumbnail_sizes_json", "json_valid(thumbnail_sizes) AND json_type(thumbnail_sizes) = 'array'");
            });
            asset.HasKey(record => record.Id);
            asset.HasIndex(record => record.ContentHash).IsUnique();
            asset.HasIndex(record => record.UploadedUtc);
        });

        modelBuilder.Entity<ArtworkAttachmentRecord>(attachment =>
        {
            attachment.ToTable("artwork_attachments", static table =>
            {
                table.HasCheckConstraint("ck_artwork_attachments_owner_type", "owner_type IN ('song', 'album', 'playlist', 'artist')");
                table.HasCheckConstraint(
                    "ck_artwork_attachments_crop",
                    "(crop_x IS NULL AND crop_y IS NULL AND crop_size IS NULL) OR (crop_x IS NOT NULL AND crop_y IS NOT NULL AND crop_size IS NOT NULL AND crop_x >= 0 AND crop_y >= 0 AND crop_size > 0)");
            });
            attachment.HasKey(record => record.Id);
            attachment.HasIndex(record => new { record.OwnerType, record.OwnerId }).IsUnique();
            attachment.HasIndex(record => record.AssetId);
            attachment.HasOne<AssetRecord>()
                .WithMany()
                .HasForeignKey(record => record.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// The retention store: deleted rows leave their live tables for these, so no live query needs a
    /// "not deleted" filter. Nothing here has a foreign key to a live table: a group outlives the
    /// parents of what it holds, and a restore checks them itself.
    /// </summary>
    private static void OnRetentionCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RetentionGroupRecord>(group =>
        {
            group.ToTable("retention_groups", static table => table.HasCheckConstraint("ck_retention_groups_files_json", "json_valid(files) AND json_type(files) = 'array'"));
            group.HasKey(record => record.Id);
            group.HasIndex(record => record.PruneAfterUtc);
            group.HasIndex(record => record.Shortcode);
        });

        modelBuilder.Entity<RetentionRecordRecord>(record =>
        {
            record.ToTable("retention_records", static table =>
            {
                table.HasCheckConstraint("ck_retention_records_document_json", "json_valid(document) AND json_type(document) = 'object'");
                table.HasCheckConstraint("ck_retention_records_shape_version", "shape_version >= 1");
            });
            record.HasKey(row => new { row.GroupId, row.Position });
            record.HasIndex(row => new { row.RecordType, row.OriginalId });
            record.HasOne<RetentionGroupRecord>()
                .WithMany()
                .HasForeignKey(row => row.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // The audio files a deletion released (#388), with the reason each was given; they go with the group.
        modelBuilder.Entity<RetentionReleasedAudioFileRecord>(released =>
        {
            released.ToTable("retention_released_audio_files", static table =>
                table.HasCheckConstraint("ck_retention_released_audio_files_reason", "reason IN ('song_deleted', 'generation_deleted')"));
            released.HasKey(row => new { row.GroupId, row.AudioFileId });
            released.HasOne<RetentionGroupRecord>()
                .WithMany()
                .HasForeignKey(row => row.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PendingFileDeletionRecord>(pending =>
        {
            pending.ToTable("pending_file_deletions");
            pending.HasKey(record => record.Path);
        });
    }

    private static void OnCatalogCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowStateRecord>(state =>
        {
            state.ToTable("workflow_states", static table =>
            {
                table.HasCheckConstraint("ck_workflow_states_position", "position >= 1");
                table.HasCheckConstraint("ck_workflow_states_name", "length(name) > 0");
            });
            state.HasKey(record => record.Id);
            state.HasIndex(record => record.NameKey).IsUnique();
            state.HasIndex(record => record.Position).IsUnique();
            state.HasData(DefaultWorkflowStates.All.Select(static seeded => new WorkflowStateRecord
            {
                Id = seeded.Id,
                Name = seeded.Name,
                NameKey = seeded.Name.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant(),
                Colour = seeded.Colour,
                Position = seeded.Order,
                Hidden = seeded.Hidden,
            }));
        });

        modelBuilder.Entity<SunoModelRecord>(model =>
        {
            model.ToTable("suno_models", static table =>
            {
                table.HasCheckConstraint("ck_suno_models_position", "position >= 1");
                table.HasCheckConstraint("ck_suno_models_name", "length(name) > 0");
            });
            model.HasKey(record => record.Id);
            model.HasIndex(record => record.NameKey).IsUnique();
            model.HasIndex(record => record.Position).IsUnique();
            model.HasData(DefaultSunoModels.All.Select(static seeded => new SunoModelRecord
            {
                Id = seeded.Id,
                Name = seeded.Name,
                NameKey = SunoModelRules.NameKey(seeded.Name),
                Note = seeded.Note,
                Position = seeded.Order,
                Retired = seeded.Retired,
                Discovered = seeded.Discovered,
                ReportedAs = seeded.ReportedAs,
            }));
        });

        modelBuilder.Entity<ShortcodeSequenceRecord>(sequence =>
        {
            sequence.ToTable("shortcode_sequence", static table =>
            {
                table.HasCheckConstraint("ck_shortcode_sequence_slot", $"slot = {ShortcodeSequenceRecord.OnlySlot}");
                table.HasCheckConstraint("ck_shortcode_sequence_last_value", "last_value >= 0");
            });
            sequence.HasKey(record => record.Slot);
            sequence.Property(record => record.Slot).ValueGeneratedNever();
            sequence.HasData(new ShortcodeSequenceRecord { Slot = ShortcodeSequenceRecord.OnlySlot, LastValue = 0 });
        });

        modelBuilder.Entity<SongRecord>(song =>
        {
            song.ToTable("songs", static table =>
            {
                table.HasCheckConstraint("ck_songs_shortcode_number", "shortcode_number >= 1");
                table.HasCheckConstraint("ck_songs_title", "length(title) > 0");
                table.HasCheckConstraint("ck_songs_revision", "revision >= 1");
                foreach (var trigger in SearchTriggers("songs"))
                {
                    table.HasTrigger(trigger);
                }
            });
            song.HasKey(record => record.Id);
            song.Property(record => record.ShortcodeNumber).ValueGeneratedNever();
            song.HasIndex(record => record.ShortcodeNumber).IsUnique();
            song.HasIndex(record => new { record.TitleSortKey, record.ShortcodeNumber });

            // The Songs list's title sort (#226), with its shortcode tie-break.
            song.HasIndex(record => new { record.TitleOrderKey, record.ShortcodeNumber });

            // The duplicate title indicator looks titles up by key. Not unique: titles may be shared.
            song.HasIndex(record => record.TitleKey);
            song.HasIndex(record => new { record.UpdatedUtc, record.ShortcodeNumber });

            // The Songs list's creation date filter (#225) reads a range of creation times.
            song.HasIndex(record => new { record.CreatedUtc, record.ShortcodeNumber });

            // The duplicate ISRC warning looks codes up. Not unique: a shared ISRC is allowed. The
            // release columns carry no CHECK constraints, which SQLite could add only by rebuilding
            // the table; the rules are the application's (SongReleaseRules).
            song.HasIndex(record => record.Isrc);
            song.HasOne<WorkflowStateRecord>()
                .WithMany()
                .HasForeignKey(record => record.WorkflowStateId)
                .OnDelete(DeleteBehavior.Restrict);
            song.HasOne<VersionRecord>()
                .WithMany()
                .HasForeignKey(record => record.CurrentVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            // The Selected Generation (#120): a Generation names its Song and the Song may name one of
            // its Generations back, so the reference is nullable and never cascades; deleting or moving
            // a Generation clears it first (retention clears it within a group and puts it back on restore).
            song.HasOne<GenerationRecord>()
                .WithMany()
                .HasForeignKey(record => record.SelectedGenerationId)
                .OnDelete(DeleteBehavior.Restrict);

            // The Suno workspace the Song lives in (#129), by Suno ID. Workspace records are never
            // deleted, so the key only guards against a dangling name.
            song.HasOne<SunoWorkspaceRecord>()
                .WithMany()
                .HasForeignKey(record => record.SunoWorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SongLinkRecord>(link =>
        {
            link.ToTable("song_links", static table => table.HasCheckConstraint("ck_song_links_url", "url LIKE 'http://%' OR url LIKE 'https://%'"));
            link.HasKey(record => new { record.SongId, record.Position });
            link.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VersionRecord>(version =>
        {
            version.ToTable("versions", static table =>
            {
                table.HasCheckConstraint("ck_versions_visibility", $"visibility IN ('{VersionRecord.Active}', '{VersionRecord.Archived}')");
                table.HasCheckConstraint("ck_versions_revision", "revision >= 1");
                table.HasTrigger(VersionInsertedTrigger);
                table.HasTrigger(VersionUpdatedTrigger);
                table.HasTrigger(VersionNumberRecordedTrigger);
                table.HasTrigger(VersionNumberFixedTrigger);
                table.HasTrigger(VersionFrozenTrigger);
                foreach (var trigger in SearchTriggers("versions"))
                {
                    table.HasTrigger(trigger);
                }
            });
            version.HasKey(record => record.Id);
            version.HasIndex(record => new { record.SongId, record.Number }).IsUnique();
            version.HasIndex(record => new { record.SongId, record.NumberSortKey });
            version.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UsedVersionNumberRecord>(used =>
        {
            used.ToTable("used_version_numbers", static table =>
                table.HasCheckConstraint("ck_used_version_numbers_number", $"length(number) BETWEEN 1 AND {VersionNumber.MaximumLength}"));

            // The key is the unique index on Song plus number: a number is used at most once per Song.
            used.HasKey(record => new { record.SongId, record.Number });
            used.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditorRevisionRecord>(revision =>
        {
            revision.ToTable("editor_revisions");
            revision.HasKey(record => record.Id);
            revision.Property(record => record.Sequence).ValueGeneratedNever();
            revision.HasIndex(record => record.Sequence).IsUnique();

            // Newest first, per Version: the History list, the duplicate check, and pruning.
            revision.HasIndex(record => new { record.VersionId, record.CreatedUtc, record.Sequence });

            // A Version's history goes with it (Version deletion is M3's).
            revision.HasOne<VersionRecord>()
                .WithMany()
                .HasForeignKey(record => record.VersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GenerationRecord>(generation =>
        {
            generation.ToTable("generations", static table =>
            {
                table.HasCheckConstraint("ck_generations_ordinal", "ordinal >= 1");
                table.HasCheckConstraint("ck_generations_state", $"state IN ('{GenerationRecord.Active}', '{GenerationRecord.Archived}')");
                table.HasCheckConstraint(
                    "ck_generations_remote_state",
                    $"remote_state IN ('{GenerationRecord.Present}', '{GenerationRecord.Trashed}', '{GenerationRecord.Missing}')");
                table.HasCheckConstraint("ck_generations_revision", "revision >= 1");
                table.HasCheckConstraint("ck_generations_suno_id", "suno_id IS NULL OR length(suno_id) > 0");
                table.HasCheckConstraint("ck_generations_rating", "rating IS NULL OR rating BETWEEN 1 AND 5");
                table.HasCheckConstraint(
                    "ck_generations_archived_by",
                    $"archived_by IS NULL OR archived_by IN ('{GenerationRecord.ArchivedByUser}', '{GenerationRecord.ArchivedBySync}')");
                table.HasTrigger(GenerationMoveTrigger);
                table.HasTrigger(GenerationAliasReservedTrigger);
                table.HasTrigger(GenerationSunoIdFixedTrigger);
                foreach (var trigger in SearchTriggers("generations"))
                {
                    table.HasTrigger(trigger);
                }
            });
            generation.HasKey(record => record.Id);
            generation.HasIndex(record => new { record.VersionId, record.Ordinal }).IsUnique();
            generation.HasIndex(record => record.SongId);

            // The Songs list's model filter and the model picker's values (#225): the models reported,
            // and the Songs reporting one.
            generation.HasIndex(record => new { record.ModelVersion, record.SongId });

            // The Songs list's rating and last Generation date sorts (#226): each Song's highest rating
            // and latest Generation date, read from the index alone.
            generation.HasIndex(record => new { record.SongId, record.Rating });
            generation.HasIndex(record => new { record.SongId, record.SunoCreatedUtc, record.CreatedUtc });

            // The parent key of an audio file's (generation_id, song_id) foreign key (#206): an index, not
            // an alternate key, so it is added without rebuilding the table and a move may still change
            // the Song.
            generation.HasIndex(record => new { record.Id, record.SongId }).IsUnique();
            generation.Property(record => record.State).HasDefaultValue(GenerationRecord.Active);
            generation.Property(record => record.RemoteState).HasDefaultValue(GenerationRecord.Present);
            generation.Property(record => record.Revision).HasDefaultValue(1);

            // A Suno clip appears in the catalog at most once: unique among the live Generations that
            // have a Suno ID (deleted ones are in retention, not in this table).
            generation.HasIndex(record => record.SunoId).IsUnique().HasFilter("suno_id IS NOT NULL");

            // Nothing removes a Version or a Song with Generations by accident: deleting them is M3's.
            generation.HasOne<VersionRecord>()
                .WithMany()
                .HasForeignKey(record => record.VersionId)
                .OnDelete(DeleteBehavior.Restrict);
            generation.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Restrict);

            // Its cover image (#121): the asset is kept while the Generation names it, and the artwork
            // sweep cannot remove a named asset.
            generation.HasIndex(record => record.ArtworkAssetId);
            generation.HasOne<AssetRecord>()
                .WithMany()
                .HasForeignKey(record => record.ArtworkAssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // A moved Generation's old shortcodes (#123): kept for good, in lower case. No foreign key to
        // the Generation, so an alias outlives its deletion and stays reserved after its purge.
        modelBuilder.Entity<ShortcodeAliasRecord>(alias =>
        {
            alias.ToTable("shortcode_aliases", static table =>
            {
                table.HasCheckConstraint("ck_shortcode_aliases_alias", "length(alias) > 0 AND alias = lower(alias)");
            });
            alias.HasKey(record => record.Alias);
            alias.HasIndex(record => record.GenerationId);
        });

        modelBuilder.Entity<SunoPlaylistRecord>(playlist =>
        {
            playlist.ToTable("suno_playlists", static table =>
            {
                table.HasCheckConstraint("ck_suno_playlists_suno_id", "length(suno_id) BETWEEN 1 AND 100");
            });
            playlist.HasKey(record => record.SunoId);
        });

        modelBuilder.Entity<SunoWorkspaceRecord>(workspace =>
        {
            workspace.ToTable("suno_workspaces", static table =>
            {
                table.HasCheckConstraint("ck_suno_workspaces_suno_id", "length(suno_id) BETWEEN 1 AND 100");
                table.HasCheckConstraint("ck_suno_workspaces_state", "state IN ('available', 'unavailable')");
            });
            workspace.HasKey(record => record.SunoId);
        });

        // Provider tombstones (#130): keyed by Suno ID alone, with no foreign key, so they outlive the
        // retention prune of the Generations they stand for.
        modelBuilder.Entity<ProviderTombstoneRecord>(tombstone =>
        {
            tombstone.ToTable("provider_tombstones", static table =>
            {
                table.HasCheckConstraint("ck_provider_tombstones_suno_id", "length(suno_id) > 0");
                table.HasCheckConstraint("ck_provider_tombstones_kind", $"kind IN ('{ProviderTombstoneRecord.ClipKind}')");
            });
            tombstone.HasKey(record => record.SunoId);
        });

        // Generate on Suno requests (#144): not catalog tables. The Version and the claiming credential
        // are named without foreign keys; the newest request of a Version is found by the index.
        modelBuilder.Entity<SunoGenerationRequestRecord>(request =>
        {
            request.ToTable("suno_generation_requests", static table =>
            {
                table.HasCheckConstraint(
                    "ck_suno_generation_requests_state",
                    $"state IN ({string.Join(", ", GenerationRequestRules.StateNames.Select(static name => $"'{name}'"))})");
                table.HasCheckConstraint("ck_suno_generation_requests_step", $"step IS NULL OR length(step) BETWEEN 1 AND {GenerationRequestRules.MaximumStepLength}");
                table.HasCheckConstraint("ck_suno_generation_requests_message", $"message IS NULL OR length(message) BETWEEN 1 AND {GenerationRequestRules.MaximumMessageLength}");
            });
            request.HasKey(record => record.Id);
            request.HasIndex(record => new { record.VersionId, record.CreatedUtc });
        });

        // Dismissed problems (#229): not a catalog table. A subject is an export's or a request's ID,
        // named without a foreign key: the dismissal outlives nothing it names, and is only ever read by it.
        modelBuilder.Entity<AttentionDismissalRecord>(dismissal =>
        {
            dismissal.ToTable("attention_dismissals", static table =>
            {
                table.HasCheckConstraint("ck_attention_dismissals_kind", "kind IN ('failedSync', 'failedGenerate')");
            });
            dismissal.HasKey(record => new { record.Kind, record.Subject });
        });

        // Suno export staging (#131): staging tables, not catalog tables. The rows of an export go with it
        // (cascade); a staged record names a Generation without a foreign key, and its staged image keeps
        // its asset live (RESTRICT, and the store reports it as attached to the artwork sweep).
        modelBuilder.Entity<SunoExportRecord>(export =>
        {
            export.ToTable("suno_exports", static table =>
            {
                table.HasCheckConstraint(
                    "ck_suno_exports_state",
                    "state IN ('receiving', 'classifying', 'ready', 'committing', 'committed', 'discarded', 'failed', 'expired')");
                table.HasCheckConstraint("ck_suno_exports_scope", "scope IN ('library', 'workspaces', 'playlists', 'clips')");
            });
            export.HasKey(record => record.Id);
            export.HasIndex(record => record.State);
            export.Property(record => record.Revision).HasDefaultValue(1);
        });

        modelBuilder.Entity<SunoExportPartRecord>(part =>
        {
            part.ToTable("suno_export_parts", static table =>
            {
                table.HasCheckConstraint("ck_suno_export_parts_part_number", "part_number >= 1");
            });
            part.HasKey(record => new { record.ExportId, record.PartNumber });
            part.HasOne<SunoExportRecord>()
                .WithMany()
                .HasForeignKey(record => record.ExportId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StagedClipRecord>(clip =>
        {
            clip.ToTable("suno_export_records", static table =>
            {
                table.HasCheckConstraint("ck_suno_export_records_suno_id", "length(suno_id) > 0");
                table.HasCheckConstraint(
                    "ck_suno_export_records_class",
                    "class IS NULL OR class IN ('new', 'linked', 'changed', 'conflict', 'ignored', 'deleted')");
            });
            clip.HasKey(record => new { record.ExportId, record.SunoId });
            clip.HasIndex(record => new { record.ExportId, record.Class });
            clip.HasIndex(record => new { record.ExportId, record.SunoCreatedUtc });
            clip.HasIndex(record => record.ArtworkAssetId);
            clip.Property(record => record.Flags).HasDefaultValue("[]");
            clip.Property(record => record.ChangedFields).HasDefaultValue("[]");
            clip.HasOne<SunoExportRecord>()
                .WithMany()
                .HasForeignKey(record => record.ExportId)
                .OnDelete(DeleteBehavior.Cascade);
            clip.HasOne<AssetRecord>()
                .WithMany()
                .HasForeignKey(record => record.ArtworkAssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StagedClipPlaylistRecord>(membership =>
        {
            membership.ToTable("suno_export_record_playlists");
            membership.HasKey(record => new { record.ExportId, record.SunoId, record.PlaylistId });
            membership.HasIndex(record => new { record.ExportId, record.PlaylistId });
            membership.HasOne<StagedClipRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.ExportId, record.SunoId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        // The ignore list (#143 fills it): keyed by Suno ID, with no foreign key.
        modelBuilder.Entity<SunoIgnoredItemRecord>(item =>
        {
            item.ToTable("suno_ignored_items", static table =>
            {
                table.HasCheckConstraint("ck_suno_ignored_items_suno_id", "length(suno_id) > 0");
            });
            item.HasKey(record => record.SunoId);
        });

        modelBuilder.Entity<SunoPersonaRecord>(persona =>
        {
            persona.ToTable("suno_personas", static table =>
            {
                table.HasCheckConstraint("ck_suno_personas_suno_id", "length(suno_id) BETWEEN 1 AND 100");
            });
            persona.HasKey(record => record.SunoId);
        });

        modelBuilder.Entity<ExternalSunoReferenceRecord>(reference =>
        {
            reference.ToTable("external_suno_references", static table =>
            {
                table.HasCheckConstraint(
                    "ck_external_suno_references_kind",
                    $"kind IN ('{ExternalSunoReferenceRecord.ClipKind}', '{ExternalSunoReferenceRecord.PlaylistKind}', '{ExternalSunoReferenceRecord.PersonaKind}')");
                table.HasCheckConstraint("ck_external_suno_references_suno_id", "length(suno_id) BETWEEN 1 AND 100");
                table.HasTrigger(ExternalReferenceFixedTrigger);
            });
            reference.HasKey(record => record.Id);
            reference.HasIndex(record => new { record.SunoId, record.Kind }).IsUnique();
        });

        // A Version's lineage (#122): each table cascades from the Version, so the Version's deletion
        // retains it with the Version, and each has freeze triggers (see LineageTables).
        modelBuilder.Entity<VersionSourceRecord>(source =>
        {
            source.ToTable("version_sources", static table =>
            {
                table.HasCheckConstraint(
                    "ck_version_sources_group",
                    $"source_group IN ('{VersionSourceRecord.AudioGroup}', '{VersionSourceRecord.InspirationGroup}')");
                table.HasCheckConstraint("ck_version_sources_position", "position >= 0");
                table.HasCheckConstraint(
                    "ck_version_sources_one_target",
                    "(generation_id IS NOT NULL) + (song_id IS NOT NULL) + (external_reference_id IS NOT NULL) = 1");
                table.HasCheckConstraint("ck_version_sources_continue_at", "continue_at_hundredths IS NULL OR continue_at_hundredths >= 0");
                table.HasCheckConstraint("ck_version_sources_secondary_ids", "secondary_ids IS NULL OR (json_valid(secondary_ids) AND json_type(secondary_ids) = 'object')");
                foreach (var trigger in LineageFrozenTriggers("version_sources"))
                {
                    table.HasTrigger(trigger);
                }
            });
            source.HasKey(record => record.Id);
            source.HasIndex(record => new { record.VersionId, record.SourceGroup, record.Position }).IsUnique();
            source.HasIndex(record => record.GenerationId);
            source.HasIndex(record => record.SongId);
            source.HasIndex(record => record.ExternalReferenceId);
            source.HasIndex(record => record.TypeId);
            source.HasOne<VersionRecord>()
                .WithMany()
                .HasForeignKey(record => record.VersionId)
                .OnDelete(DeleteBehavior.Cascade);
            source.HasOne<RelationshipTypeRecord>()
                .WithMany()
                .HasForeignKey(record => record.TypeId)
                .OnDelete(DeleteBehavior.Restrict);
            source.HasOne<ExternalSunoReferenceRecord>()
                .WithMany()
                .HasForeignKey(record => record.ExternalReferenceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VersionInspirationPlaylistRecord>(playlist =>
        {
            playlist.ToTable("version_inspiration_playlists", static table =>
            {
                table.HasCheckConstraint("ck_version_inspiration_playlists_id", "length(suno_playlist_id) BETWEEN 1 AND 100");
                table.HasCheckConstraint("ck_version_inspiration_playlists_name", "length(name) <= 200");
                table.HasCheckConstraint(
                    "ck_version_inspiration_playlists_clip_ids",
                    "json_valid(clip_ids) AND json_type(clip_ids) = 'array' AND json_array_length(clip_ids) <= 500");
                foreach (var trigger in LineageFrozenTriggers("version_inspiration_playlists"))
                {
                    table.HasTrigger(trigger);
                }
            });
            playlist.HasKey(record => record.VersionId);
            playlist.HasOne<VersionRecord>()
                .WithOne()
                .HasForeignKey<VersionInspirationPlaylistRecord>(record => record.VersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VersionVoiceRecord>(voice =>
        {
            voice.ToTable("version_voices", static table =>
            {
                table.HasCheckConstraint("ck_version_voices_persona_id", "length(persona_id) BETWEEN 1 AND 100");
                table.HasCheckConstraint("ck_version_voices_name", "length(name) <= 200");
                foreach (var trigger in LineageFrozenTriggers("version_voices"))
                {
                    table.HasTrigger(trigger);
                }
            });
            voice.HasKey(record => record.VersionId);
            voice.HasOne<VersionRecord>()
                .WithOne()
                .HasForeignKey<VersionVoiceRecord>(record => record.VersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VersionFileInputRecord>(file =>
        {
            file.ToTable("version_file_inputs", static table =>
            {
                table.HasCheckConstraint(
                    "ck_version_file_inputs_kind",
                    $"kind IN ('{VersionFileInputRecord.AudioKind}', '{VersionFileInputRecord.ImageKind}', '{VersionFileInputRecord.VideoKind}')");
                table.HasCheckConstraint("ck_version_file_inputs_description", "length(description) BETWEEN 1 AND 500");
                foreach (var trigger in LineageFrozenTriggers("version_file_inputs"))
                {
                    table.HasTrigger(trigger);
                }
            });
            file.HasKey(record => new { record.VersionId, record.Kind });
            file.HasOne<VersionRecord>()
                .WithMany()
                .HasForeignKey(record => record.VersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProviderRecordRecord>(record =>
        {
            record.ToTable("provider_records", static table =>
            {
                table.HasCheckConstraint("ck_provider_records_kind", $"kind IN ('{ProviderRecordRecord.ClipKind}')");
                table.HasCheckConstraint("ck_provider_records_payload_json", "json_valid(payload) AND json_type(payload) = 'object'");
            });
            record.HasKey(provider => provider.GenerationId);

            // The raw clip goes with its Generation (into retention with it, as a registered type).
            record.HasOne<GenerationRecord>()
                .WithOne()
                .HasForeignKey<ProviderRecordRecord>(provider => provider.GenerationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GenerationCommentRecord>(comment =>
        {
            comment.ToTable("generation_comments", static table =>
            {
                table.HasCheckConstraint("ck_generation_comments_text", "length(text) BETWEEN 1 AND 2000");
                table.HasCheckConstraint("ck_generation_comments_revision", "revision >= 1");
                foreach (var trigger in SearchTriggers("generation_comments"))
                {
                    table.HasTrigger(trigger);
                }
            });
            comment.HasKey(record => record.Id);

            // A Generation's comments, oldest first.
            comment.HasIndex(record => new { record.GenerationId, record.CreatedUtc, record.Id });
            comment.Property(record => record.Revision).HasDefaultValue(1);

            // The comments go with their Generation (into retention with it, as a registered type).
            comment.HasOne<GenerationRecord>()
                .WithMany()
                .HasForeignKey(record => record.GenerationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GenerationEventRecord>(generationEvent =>
        {
            generationEvent.ToTable("generation_events", static table =>
            {
                table.HasCheckConstraint("ck_generation_events_source", "source IN ('observed', 'inferred', 'user')");
                table.HasCheckConstraint("ck_generation_events_confidence", "confidence IN ('high', 'medium')");
                table.HasCheckConstraint("ck_generation_events_batch_size", "batch_size >= 1");
            });
            generationEvent.HasKey(record => record.Id);
        });

        modelBuilder.Entity<GenerationEventLinkRecord>(link =>
        {
            link.ToTable("generation_event_links");

            // A Generation has at most one event; an event has any number of Generations.
            link.HasKey(record => record.GenerationId);
            link.HasIndex(record => record.EventId);
            link.HasOne<GenerationRecord>()
                .WithOne()
                .HasForeignKey<GenerationEventLinkRecord>(record => record.GenerationId)
                .OnDelete(DeleteBehavior.Cascade);
            link.HasOne<GenerationEventRecord>()
                .WithMany()
                .HasForeignKey(record => record.EventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<GenreRecord>(genre =>
        {
            genre.ToTable("genres", static table => table.HasCheckConstraint("ck_genres_name", "length(name) > 0"));
            genre.HasKey(record => record.Id);
            genre.HasIndex(record => record.NameKey).IsUnique();
        });

        modelBuilder.Entity<SongGenreRecord>(songGenre =>
        {
            songGenre.ToTable("song_genres");

            // A Song has a Genre at most once.
            songGenre.HasKey(record => new { record.SongId, record.GenreId });
            songGenre.HasIndex(record => record.GenreId);

            // A Song's Genres go with it; a Genre on any Song is never removed by accident: removing
            // or merging Genres (GenreService) moves each affected Song's rows and revision itself.
            songGenre.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Cascade);
            songGenre.HasOne<GenreRecord>()
                .WithMany()
                .HasForeignKey(record => record.GenreId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TagRecord>(tag =>
        {
            tag.ToTable("tags", static table =>
            {
                table.HasCheckConstraint("ck_tags_name", "length(name) > 0");
                table.HasCheckConstraint(
                    "ck_tags_colour",
                    "colour IN (" + string.Join(", ", TagRules.Palette.Select(static colour => "'" + colour + "'")) + ")");
                foreach (var trigger in SearchTriggers("tags"))
                {
                    table.HasTrigger(trigger);
                }
            });
            tag.HasKey(record => record.Id);
            tag.HasIndex(record => record.NameKey).IsUnique();
        });

        modelBuilder.Entity<SongTagRecord>(songTag =>
        {
            songTag.ToTable("song_tags", static table =>
            {
                foreach (var trigger in SearchTriggers("song_tags"))
                {
                    table.HasTrigger(trigger);
                }
            });

            // A Song has a Tag at most once.
            songTag.HasKey(record => new { record.SongId, record.TagId });
            songTag.HasIndex(record => record.TagId);

            // A Song's Tags go with it; a Tag on any Song is never removed by accident (removing or
            // merging Tags is Settings → Tags', which moves each affected Song's revision on).
            songTag.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Cascade);
            songTag.HasOne<TagRecord>()
                .WithMany()
                .HasForeignKey(record => record.TagId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ArtistRecord>(artist =>
        {
            artist.ToTable("artists", static table => table.HasCheckConstraint("ck_artists_name", "length(name) > 0"));
            artist.HasKey(record => record.Id);

            // The list's order: by name ignoring case, then the earlier created. Not unique: a shared
            // name is allowed once the user confirms it.
            artist.HasIndex(record => new { record.NameKey, record.CreatedUtc });
        });

        modelBuilder.Entity<ArtistAliasRecord>(alias =>
        {
            alias.ToTable("artist_aliases", static table => table.HasCheckConstraint("ck_artist_aliases_name", "length(name) > 0"));
            alias.HasKey(record => new { record.ArtistId, record.Position });

            // An Artist has an alias at most once, ignoring case.
            alias.HasIndex(record => new { record.ArtistId, record.NameKey }).IsUnique();
            alias.HasIndex(record => record.NameKey);
            alias.HasOne<ArtistRecord>()
                .WithMany()
                .HasForeignKey(record => record.ArtistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArtistLinkRecord>(link =>
        {
            link.ToTable("artist_links", static table => table.HasCheckConstraint("ck_artist_links_url", "url LIKE 'http://%' OR url LIKE 'https://%'"));
            link.HasKey(record => new { record.ArtistId, record.Position });
            link.HasOne<ArtistRecord>()
                .WithMany()
                .HasForeignKey(record => record.ArtistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SongCreditRecord>(credit =>
        {
            credit.ToTable("song_artist_credits", static table => table.HasCheckConstraint(
                "ck_song_artist_credits_role",
                "(role = 'primary' AND position = 0) OR (role = 'featured' AND position >= 0)"));

            // A Song credits an Artist at most once, so never as both primary and featured.
            credit.HasKey(record => new { record.SongId, record.ArtistId });

            // One primary Artist (always at position 0), and one featured Artist per place.
            credit.HasIndex(record => new { record.SongId, record.Role, record.Position }).IsUnique();
            credit.HasIndex(record => record.ArtistId);

            // A Song's credits go with it; an Artist credited on any Song is never removed by
            // accident (the Artist deletion story decides what happens to its credits).
            credit.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Cascade);
            credit.HasOne<ArtistRecord>()
                .WithMany()
                .HasForeignKey(record => record.ArtistId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AlbumRecord>(album =>
        {
            album.ToTable("albums", static table =>
            {
                table.HasCheckConstraint("ck_albums_title", "length(title) > 0");
                table.HasCheckConstraint("ck_albums_upc", "upc IS NULL OR ((length(upc) = 12 OR length(upc) = 13) AND upc NOT GLOB '*[^0-9]*')");
                foreach (var trigger in SearchTriggers("albums"))
                {
                    table.HasTrigger(trigger);
                }
            });
            album.HasKey(record => record.Id);

            // The list's default order: by title ignoring case, then the earlier created.
            album.HasIndex(record => new { record.TitleKey, record.CreatedUtc });

            // The duplicate UPC/EAN warning looks codes up by their 13-digit form. Not unique: a
            // shared code is allowed.
            album.HasIndex(record => record.UpcKey);

            // An Album Artist is never removed by accident (the Artist deletion story decides what
            // happens to the Albums it is Album Artist of).
            album.HasIndex(record => record.AlbumArtistId);
            album.HasOne<ArtistRecord>()
                .WithMany()
                .HasForeignKey(record => record.AlbumArtistId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AlbumLinkRecord>(link =>
        {
            link.ToTable("album_links", static table => table.HasCheckConstraint("ck_album_links_url", "url LIKE 'http://%' OR url LIKE 'https://%'"));
            link.HasKey(record => new { record.AlbumId, record.Position });
            link.HasOne<AlbumRecord>()
                .WithMany()
                .HasForeignKey(record => record.AlbumId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlbumSongRecord>(track =>
        {
            track.ToTable("album_songs", static table =>
            {
                table.HasCheckConstraint("ck_album_songs_disc", "disc BETWEEN 1 AND 999");
                table.HasCheckConstraint("ck_album_songs_track", "track BETWEEN 1 AND 999");
                foreach (var trigger in SearchTriggers("album_songs"))
                {
                    table.HasTrigger(trigger);
                }
            });

            // A Song is on an Album at most once, and two tracks on one disc never share a number.
            // The order is disc, then track number: there is no separate position.
            track.HasKey(record => new { record.AlbumId, record.SongId });
            track.HasIndex(record => new { record.AlbumId, record.Disc, record.Track }).IsUnique();

            // A Song's Albums are listed on its Details panel.
            track.HasIndex(record => record.SongId);

            // An Album's tracks go with it, and so do a Song's: a track is only membership, never
            // part of the Song (the deletion stories decide about retention).
            track.HasOne<AlbumRecord>()
                .WithMany()
                .HasForeignKey(record => record.AlbumId)
                .OnDelete(DeleteBehavior.Cascade);
            track.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlaylistRecord>(playlist =>
        {
            playlist.ToTable("playlists", static table =>
            {
                table.HasCheckConstraint("ck_playlists_title", "length(title) > 0");
                foreach (var trigger in SearchTriggers("playlists"))
                {
                    table.HasTrigger(trigger);
                }
            });
            playlist.HasKey(record => record.Id);

            // The list's order: by title ignoring case, then the earlier created. Not unique.
            playlist.HasIndex(record => new { record.TitleKey, record.CreatedUtc });
        });

        modelBuilder.Entity<PlaylistSongRecord>(entry =>
        {
            entry.ToTable("playlist_songs", static table =>
            {
                table.HasCheckConstraint("ck_playlist_songs_position", "position >= 0");
                foreach (var trigger in SearchTriggers("playlist_songs"))
                {
                    table.HasTrigger(trigger);
                }
            });

            // A Song is on a Playlist at most once, and each place holds one Song.
            entry.HasKey(record => new { record.PlaylistId, record.SongId });
            entry.HasIndex(record => new { record.PlaylistId, record.Position }).IsUnique();

            // A Song's Playlists are listed on its Details panel.
            entry.HasIndex(record => record.SongId);

            // A Playlist's entries go with it, and so do a Song's: an entry is only membership,
            // never part of the Song (the Song deletion story decides about retention).
            entry.HasOne<PlaylistRecord>()
                .WithMany()
                .HasForeignKey(record => record.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);
            entry.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        OnRelationshipsCreating(modelBuilder);
    }

    private static void OnRelationshipsCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RelationshipTypeRecord>(type =>
        {
            type.ToTable("song_relationship_types", static table =>
            {
                table.HasCheckConstraint("ck_song_relationship_types_names", "length(name) > 0 AND length(reverse_name) > 0");
                // A system type's action is fixed by its seed; a user's own may stand for an audio action (#126).
                table.HasCheckConstraint("ck_song_relationship_types_suno_action", "suno_action IS NULL OR is_system = 1 OR suno_action IN ('cover', 'extend', 'mashup', 'sample', 'reuse_prompt')");
            });
            type.HasKey(record => record.Id);

            // No two types share a forward name, or a reverse name, ignoring case; that no name is
            // another type's in the other direction is the service's to check.
            type.HasIndex(record => record.NameKey).IsUnique();
            type.HasIndex(record => record.ReverseNameKey).IsUnique();
            type.HasData(SystemRelationshipTypes.All.Select(static seeded => new RelationshipTypeRecord
            {
                Id = seeded.Id,
                Name = seeded.Name,
                NameKey = RelationshipRules.NameKey(seeded.Name),
                ReverseName = seeded.ReverseName,
                ReverseNameKey = RelationshipRules.NameKey(seeded.ReverseName),
                IsSystem = true,
                SunoAction = seeded.SunoAction,
                Revision = 1,
            }));
        });

        modelBuilder.Entity<SongRelationshipRecord>(relationship =>
        {
            relationship.ToTable("song_relationships", static table => table.HasCheckConstraint("ck_song_relationships_two_songs", "from_song_id <> to_song_id"));
            relationship.HasKey(record => record.Id);

            // A Song's relationships are read from either side. That a pair is related at most once
            // per type, either way round, is a unique index on the ordered pair, which the
            // migration creates in SQL (EF Core cannot express it).
            relationship.HasIndex(record => new { record.FromSongId, record.TypeId });
            relationship.HasIndex(record => new { record.ToSongId, record.TypeId });
            relationship.HasIndex(record => record.TypeId);

            // A Song's relationships go with it: a relationship is only organisation, never part of
            // either Song (the Song deletion story decides about retention). A type in use is never
            // removed by accident: deleting one removes its relationships first, moving their Songs.
            relationship.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.FromSongId)
                .OnDelete(DeleteBehavior.Cascade);
            relationship.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.ToSongId)
                .OnDelete(DeleteBehavior.Cascade);
            relationship.HasOne<RelationshipTypeRecord>()
                .WithMany()
                .HasForeignKey(record => record.TypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
