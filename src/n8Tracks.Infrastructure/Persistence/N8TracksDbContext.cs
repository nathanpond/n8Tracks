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
    }
}
