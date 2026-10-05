using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Backups;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Persistence;

/// <summary>
/// Test-only migrations for a database upgrade: a host given one of these scenarios (through
/// <see cref="Use{TScenario}"/>) knows every real migration plus the scenario's own, ordered after
/// them, so a database the real migrations made has them pending. Nothing here ships in the app.
/// </summary>
internal static class UpgradeMigrations
{
    /// <summary>A migration that changes rows and adds a table: what an upgrade does to the data.</summary>
    public const string ChangesDataId = "29990101000000_TestChangesData";

    /// <summary>A migration whose SQL fails part way, after it has changed a row.</summary>
    public const string FailsId = "29990101000001_TestFails";

    /// <summary>Replaces EF Core's migrations assembly in a host's database context with <typeparamref name="TScenario"/>.</summary>
    public static void Use<TScenario>(IServiceCollection services)
        where TScenario : TestMigrationsAssembly
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ConfigureDbContext<N8TracksDbContext>(
            static (_, options) => options.ReplaceService<IMigrationsAssembly, TScenario>());
    }
}

/// <summary>The real migrations and model snapshot of the app, found the way EF Core finds them, plus the scenario's.</summary>
internal abstract class TestMigrationsAssembly : IMigrationsAssembly
{
    private static readonly Assembly AppAssembly = typeof(N8TracksDbContext).Assembly;

    private static readonly Lazy<IReadOnlyDictionary<string, TypeInfo>> RealMigrations = new(static () =>
        AppAssembly.DefinedTypes.Where(static type => !type.IsAbstract && !type.IsGenericTypeDefinition)
            .Where(static type => type.IsSubclassOf(typeof(Migration))
                && type.GetCustomAttribute<DbContextAttribute>()?.ContextType == typeof(N8TracksDbContext)
                && type.GetCustomAttribute<MigrationAttribute>() is not null)
            .ToDictionary(static type => type.GetCustomAttribute<MigrationAttribute>()!.Id, static type => type, StringComparer.Ordinal));

    private readonly Lazy<IReadOnlyDictionary<string, TypeInfo>> migrations;

    protected TestMigrationsAssembly()
    {
        migrations = new(() => RealMigrations.Value
            .Concat(Extra.Select(static pair => new KeyValuePair<string, TypeInfo>(pair.Id, pair.Type.GetTypeInfo())))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, TypeInfo> Migrations => migrations.Value;

    public ModelSnapshot? ModelSnapshot { get; } = (ModelSnapshot?)Activator.CreateInstance(
        AppAssembly.DefinedTypes.Where(static type => !type.IsAbstract && !type.IsGenericTypeDefinition).Single(static type => type.IsSubclassOf(typeof(ModelSnapshot))));

    public Assembly Assembly => AppAssembly;

    /// <summary>The scenario's own migrations, by ID.</summary>
    protected abstract IEnumerable<(string Id, Type Type)> Extra { get; }

    public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        ArgumentNullException.ThrowIfNull(migrationClass);

        var migration = (Migration)Activator.CreateInstance(migrationClass.AsType())!;
        migration.ActiveProvider = activeProvider;
        return migration;
    }

    public string? FindMigrationId(string nameOrId) =>
        Migrations.Keys.FirstOrDefault(id => string.Equals(id, nameOrId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id[(id.IndexOf('_', StringComparison.Ordinal) + 1)..], nameOrId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An upgrade that succeeds and changes the data.</summary>
internal sealed class UpgradeThatSucceeds : TestMigrationsAssembly
{
    protected override IEnumerable<(string Id, Type Type)> Extra => [(UpgradeMigrations.ChangesDataId, typeof(ChangesDataMigration))];
}

/// <summary>An upgrade whose first migration succeeds and changes the data, and whose second fails.</summary>
internal sealed class UpgradeThatFails : TestMigrationsAssembly
{
    protected override IEnumerable<(string Id, Type Type)> Extra =>
        [(UpgradeMigrations.ChangesDataId, typeof(ChangesDataMigration)), (UpgradeMigrations.FailsId, typeof(FailingMigration))];
}

/// <summary>
/// As <see cref="UpgradeThatFails"/>, and the failing migration also overwrites every backup archive
/// in <see cref="BackupFolder"/> with bytes that are not a ZIP: the safety backup was taken, and is
/// unreadable by the time it is needed. Only one test uses it, which sets the folder first.
/// </summary>
internal sealed class UpgradeThatFailsAndBreaksTheSafetyBackup : TestMigrationsAssembly
{
    public static string? BackupFolder { get; set; }

    protected override IEnumerable<(string Id, Type Type)> Extra =>
        [(UpgradeMigrations.ChangesDataId, typeof(ChangesDataMigration)), (UpgradeMigrations.FailsId, typeof(BreakingFailingMigration))];
}

/// <summary>
/// As <see cref="UpgradeThatFails"/>, and the failing migration also replaces the database in every
/// backup archive in <see cref="BackupFolder"/> with one that opens but fails
/// <c>PRAGMA integrity_check</c> (an index's entry is gone from the schema, so its page is never
/// used), with the manifest's checksums recomputed: the archive passes every check but the restored
/// database's own. Only one test uses it, which sets the folder first.
/// </summary>
internal sealed class UpgradeThatFailsAndCorruptsTheSafetyBackup : TestMigrationsAssembly
{
    public static string? BackupFolder { get; set; }

    protected override IEnumerable<(string Id, Type Type)> Extra =>
        [(UpgradeMigrations.ChangesDataId, typeof(ChangesDataMigration)), (UpgradeMigrations.FailsId, typeof(CorruptingFailingMigration))];
}

[Migration(UpgradeMigrations.ChangesDataId)]
internal sealed class ChangesDataMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "CREATE TABLE upgrade_probe (id INTEGER PRIMARY KEY); INSERT INTO upgrade_probe (id) VALUES (1); "
            + "INSERT INTO app_metadata (key, value) VALUES ('upgrade_probe', 'changed by the upgrade');");
}

[Migration(UpgradeMigrations.FailsId)]
internal class FailingMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("INSERT INTO app_metadata (key, value) VALUES ('half_done', 'yes');");
        migrationBuilder.Sql("SELECT * FROM a_table_no_migration_ever_created;");
    }
}

[Migration(UpgradeMigrations.FailsId)]
internal sealed class BreakingFailingMigration : FailingMigration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var folder = UpgradeThatFailsAndBreaksTheSafetyBackup.BackupFolder ?? throw new InvalidOperationException("Set the backup folder first.");
        foreach (var archive in Directory.EnumerateFiles(folder, "n8tracks-backup-*.zip"))
        {
            File.WriteAllText(archive, "not a zip any more");
        }

        base.Up(migrationBuilder);
    }
}

[Migration(UpgradeMigrations.FailsId)]
internal sealed class CorruptingFailingMigration : FailingMigration
{
    /// <summary>Drops one index from the schema but not its page: the file opens, and the integrity check reports the page.</summary>
    public const string Corruption =
        "PRAGMA writable_schema = ON; "
        + "DELETE FROM sqlite_master WHERE name = (SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY name LIMIT 1);";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var folder = UpgradeThatFailsAndCorruptsTheSafetyBackup.BackupFolder ?? throw new InvalidOperationException("Set the backup folder first.");
        foreach (var archive in Directory.EnumerateFiles(folder, "n8tracks-backup-*.zip"))
        {
            File.WriteAllBytes(
                archive,
                RestoreApi.Rebuild(
                    File.ReadAllBytes(archive),
                    static entries => entries["n8tracks.db"] = RestoreApi.ChangeDatabase(entries["n8tracks.db"], Corruption)));
        }

        base.Up(migrationBuilder);
    }
}
