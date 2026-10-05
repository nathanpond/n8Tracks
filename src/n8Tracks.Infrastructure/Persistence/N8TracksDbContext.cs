using Microsoft.EntityFrameworkCore;
using n8Tracks.Domain.Songs;

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

    public DbSet<AppMetadataEntry> AppMetadata => Set<AppMetadataEntry>();

    public DbSet<AdministratorRecord> Administrators => Set<AdministratorRecord>();

    public DbSet<SettingRecord> Settings => Set<SettingRecord>();

    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    public DbSet<CredentialRecord> Credentials => Set<CredentialRecord>();

    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    public DbSet<WorkflowStateRecord> WorkflowStates => Set<WorkflowStateRecord>();

    public DbSet<ShortcodeSequenceRecord> ShortcodeSequence => Set<ShortcodeSequenceRecord>();

    public DbSet<SongRecord> Songs => Set<SongRecord>();

    public DbSet<VersionRecord> Versions => Set<VersionRecord>();

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
            });
            version.HasKey(record => record.Id);
            version.HasIndex(record => new { record.SongId, record.Number }).IsUnique();
            version.HasIndex(record => new { record.SongId, record.NumberSortKey });
            version.HasOne<SongRecord>()
                .WithMany()
                .HasForeignKey(record => record.SongId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
