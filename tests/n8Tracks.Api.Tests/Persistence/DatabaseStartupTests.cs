using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Application.Persistence;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Persistence;

/// <summary>Starts real hosts against a temporary data path and looks at the database they leave.</summary>
public sealed class DatabaseStartupTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public void TheFirstStartCreatesTheDatabaseFileWithTheSeededRowAndEveryMigrationApplied()
    {
        Assert.False(File.Exists(TestDatabase.FilePath(directory.Path)));
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Start();

        Assert.True(File.Exists(TestDatabase.FilePath(directory.Path)));

        var history = TestDatabase.History(directory.Path);
        Assert.Collection(
            history,
            migration => Assert.Matches("^[0-9]{14}_InitialCreate\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddAdministratorsAndSettings\\|10\\.0\\.", migration));

        // ISO 8601 UTC with milliseconds and Z, taken when the migration ran.
        var initialized = TestDatabase.SchemaInitializedUtc(directory.Path);
        Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$", initialized);
        var parsed = DateTimeOffset.Parse(initialized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.InRange(parsed, before, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(
            ["schema_initialized_utc"],
            TestDatabase.Rows(directory.Path, "SELECT key FROM app_metadata;"));
    }

    [Fact]
    public void TheSchemaIsSnakeCaseAndTheMigrationHistoryKeepsItsDefaultNames()
    {
        Start();

        Assert.Equal(
            ["__EFMigrationsHistory", "administrators", "app_metadata", "settings"],
            TestDatabase.Rows(
                directory.Path,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsLock' ORDER BY name;"));
        Assert.Equal(
            ["key|TEXT|1|1", "value|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('app_metadata') ORDER BY cid;"));
        Assert.Equal(
            ["id|TEXT|1|1", "slot|INTEGER|1|0", "username|TEXT|1|0", "username_key|TEXT|1|0", "password_hash|TEXT|1|0", "created_utc|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('administrators') ORDER BY cid;"));
        Assert.Equal(
            ["ix_administrators_slot|1", "ix_administrators_username_key|1"],
            TestDatabase.Rows(directory.Path, "SELECT name, CAST(\"unique\" AS TEXT) FROM pragma_index_list('administrators') WHERE origin = 'c' ORDER BY name;"));
        Assert.Equal(
            ["key|TEXT|1|1", "value|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('settings') ORDER BY cid;"));
        Assert.Equal(
            ["MigrationId", "ProductVersion"],
            TestDatabase.Rows(directory.Path, "SELECT name FROM pragma_table_info('__EFMigrationsHistory') ORDER BY cid;"));
    }

    [Fact]
    public void ASecondStartChangesNothing()
    {
        Start();
        var history = TestDatabase.History(directory.Path);
        var initialized = TestDatabase.SchemaInitializedUtc(directory.Path);
        var schemaVersion = TestDatabase.Scalar(directory.Path, "SELECT CAST(schema_version AS TEXT) FROM pragma_schema_version;");
        var schema = TestDatabase.Rows(directory.Path, "SELECT name || ':' || coalesce(sql, '') FROM sqlite_master ORDER BY name;");

        Start();

        Assert.Equal(history, TestDatabase.History(directory.Path));
        Assert.Equal(initialized, TestDatabase.SchemaInitializedUtc(directory.Path));
        Assert.Equal(schemaVersion, TestDatabase.Scalar(directory.Path, "SELECT CAST(schema_version AS TEXT) FROM pragma_schema_version;"));
        Assert.Equal(schema, TestDatabase.Rows(directory.Path, "SELECT name || ':' || coalesce(sql, '') FROM sqlite_master ORDER BY name;"));
        Assert.Equal("1", TestDatabase.Scalar(directory.Path, "SELECT CAST(count(*) AS TEXT) FROM app_metadata;"));
    }

    [Fact]
    public async Task AValueWrittenThroughTheContextIsThereForANewHostOnTheSameDataPath()
    {
        using (var first = TestDatabase.Host(directory.Path))
        {
            using var scope = first.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>();
            context.AppMetadata.Add(new AppMetadataEntry { Key = "restart_probe", Value = "still here" });
            await context.SaveChangesAsync();
        }

        using var second = TestDatabase.Host(directory.Path);
        using var secondScope = second.Services.CreateScope();
        var reloaded = await secondScope.ServiceProvider.GetRequiredService<N8TracksDbContext>()
            .AppMetadata.AsNoTracking().SingleAsync(entry => entry.Key == "restart_probe");

        Assert.Equal("still here", reloaded.Value);
    }

    [Fact]
    public async Task EveryConnectionHasForeignKeysOnAFiveSecondBusyTimeoutAndSynchronousNormalOverAWalDatabase()
    {
        using var host = TestDatabase.Host(directory.Path);

        // Two scopes: the settings are applied to each connection, not only to the one startup used.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var scope = host.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>().Database;

            Assert.Equal(1, await database.SqlQueryRaw<int>("SELECT foreign_keys AS Value FROM pragma_foreign_keys").SingleAsync());
            Assert.Equal(5000, await database.SqlQueryRaw<int>("SELECT timeout AS Value FROM pragma_busy_timeout").SingleAsync());
            Assert.Equal(1, await database.SqlQueryRaw<int>("SELECT synchronous AS Value FROM pragma_synchronous").SingleAsync());
            Assert.Equal("wal", await database.SqlQueryRaw<string>("SELECT journal_mode AS Value FROM pragma_journal_mode").SingleAsync());
        }

        // WAL is a property of the file: a connection the app did not configure sees it too.
        Assert.Equal("wal", TestDatabase.Scalar(directory.Path, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void TheMigrationStateIsUpToDateWithTheLastAppliedMigration()
    {
        using var host = TestDatabase.Host(directory.Path);

        var state = host.Services.GetRequiredService<IMigrationStateProvider>().Current;

        Assert.Equal(MigrationStatus.UpToDate, state.Status);
        Assert.Equal(TestDatabase.History(directory.Path)[^1].Split('|')[0], state.LastAppliedMigrationId);
        Assert.EndsWith("_AddAdministratorsAndSettings", state.LastAppliedMigrationId, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMigrationStateIsNotReadableBeforeStartupCompletes()
    {
        var holder = new MigrationStateHolder();

        Assert.Throws<InvalidOperationException>(() => holder.Current);

        holder.Set(new MigrationState(MigrationStatus.UpToDate, "20260101000000_Example"));
        Assert.Equal("20260101000000_Example", holder.Current.LastAppliedMigrationId);
    }

    [Fact]
    public async Task AFirstStartLogsOneLinePerAppliedMigrationAndOneSummaryLine()
    {
        using var host = new LoggingApiFactory();
        using var client = host.CreateClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        var summary = await host.WaitForLine(line => line.GetProperty("message").GetString()!.StartsWith("Database is up to date", StringComparison.Ordinal));
        Assert.Equal("Information", summary.GetProperty("level").GetString());
        var history = TestDatabase.History(host.DataPath).Select(row => row.Split('|')[0]).ToList();
        Assert.Equal(history.Count, summary.GetProperty("properties").GetProperty("appliedCount").GetInt32());
        Assert.Equal(history[^1], LoggingApiFactory.Property(summary, "lastAppliedMigrationId"));

        // One line per migration, in the order they were applied.
        var applied = host.Lines()
            .Where(line => line.GetProperty("message").GetString()!.StartsWith("Applied database migration", StringComparison.Ordinal))
            .ToList();
        Assert.All(applied, line => Assert.Equal("Information", line.GetProperty("level").GetString()));
        Assert.Equal(history, applied.Select(line => LoggingApiFactory.Property(line, "migrationId")));
        Assert.EndsWith("_InitialCreate", history[0], StringComparison.Ordinal);

        // Nothing from EF Core itself at Information or below: no SQL in the log.
        Assert.DoesNotContain("CREATE TABLE", host.CapturedText, StringComparison.Ordinal);
    }

    /// <summary>Runs a host through startup on the shared data path and stops it.</summary>
    private void Start()
    {
        using var host = TestDatabase.Host(directory.Path);
        _ = host.Services;
    }
}
