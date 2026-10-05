using Microsoft.EntityFrameworkCore;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The EF Core context over <c>n8tracks.db</c>. Tables and columns are snake_case, by convention;
/// each schema change is one migration under <c>Persistence/Migrations</c>.
/// </summary>
public sealed class N8TracksDbContext(DbContextOptions<N8TracksDbContext> options) : DbContext(options)
{
    public DbSet<AppMetadataEntry> AppMetadata => Set<AppMetadataEntry>();

    public DbSet<AdministratorRecord> Administrators => Set<AdministratorRecord>();

    public DbSet<SettingRecord> Settings => Set<SettingRecord>();

    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    public DbSet<CredentialRecord> Credentials => Set<CredentialRecord>();

    public DbSet<JobRecord> Jobs => Set<JobRecord>();

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
    }
}
