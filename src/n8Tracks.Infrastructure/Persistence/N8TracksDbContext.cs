using Microsoft.EntityFrameworkCore;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

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

    /// <summary>The trigger that refuses any change to a Generation's Version, Song, or ordinal.</summary>
    public const string GenerationFixedTrigger = "tr_generations_identity_never_changes";

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

    public DbSet<VersionRecord> Versions => Set<VersionRecord>();

    public DbSet<UsedVersionNumberRecord> UsedVersionNumbers => Set<UsedVersionNumberRecord>();

    public DbSet<EditorRevisionRecord> EditorRevisions => Set<EditorRevisionRecord>();

    public DbSet<GenerationRecord> Generations => Set<GenerationRecord>();

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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Conventions.Add(static _ => new SnakeCaseNamingConvention());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

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
            });
            song.HasKey(record => record.Id);
            song.Property(record => record.ShortcodeNumber).ValueGeneratedNever();
            song.HasIndex(record => record.ShortcodeNumber).IsUnique();
            song.HasIndex(record => new { record.TitleSortKey, record.ShortcodeNumber });
            song.HasIndex(record => new { record.UpdatedUtc, record.ShortcodeNumber });
            song.HasOne<WorkflowStateRecord>()
                .WithMany()
                .HasForeignKey(record => record.WorkflowStateId)
                .OnDelete(DeleteBehavior.Restrict);
            song.HasOne<VersionRecord>()
                .WithMany()
                .HasForeignKey(record => record.CurrentVersionId)
                .OnDelete(DeleteBehavior.Restrict);
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
                table.HasTrigger(GenerationFixedTrigger);
            });
            generation.HasKey(record => record.Id);
            generation.HasIndex(record => new { record.VersionId, record.Ordinal }).IsUnique();
            generation.HasIndex(record => record.SongId);

            // Nothing removes a Version or a Song with Generations by accident: deleting them is M3's.
            generation.HasOne<VersionRecord>()
                .WithMany()
                .HasForeignKey(record => record.VersionId)
                .OnDelete(DeleteBehavior.Restrict);
            generation.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
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
            });
            tag.HasKey(record => record.Id);
            tag.HasIndex(record => record.NameKey).IsUnique();
        });

        modelBuilder.Entity<SongTagRecord>(songTag =>
        {
            songTag.ToTable("song_tags");

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
    }
}
