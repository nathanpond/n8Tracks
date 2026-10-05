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
            migration => Assert.Matches("^[0-9]{14}_AddAdministratorsAndSettings\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSessions\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddCredentials\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddCredentialNameKey\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddJobs\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddSongs\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddUsedVersionNumbers\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddEditorRevisions\\|10\\.0\\.", migration),
            migration => Assert.Matches("^[0-9]{14}_AddGenerations\\|10\\.0\\.", migration));

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
            ["__EFMigrationsHistory", "administrators", "app_metadata", "credentials", "editor_revisions", "generations", "jobs", "sessions", "settings", "shortcode_sequence", "songs", "used_version_numbers", "versions", "workflow_states"],
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
            ["id|TEXT|1|1", "name|TEXT|1|0", "kind|TEXT|1|0", "scopes|TEXT|1|0", "token_hash|TEXT|1|0", "created_utc|TEXT|1|0", "last_used_utc|TEXT|0|0", "revoked_utc|TEXT|0|0", "revision|INTEGER|1|0", "name_key|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('credentials') ORDER BY cid;"));
        Assert.Equal(
            ["ix_credentials_name_key|1|1", "ix_credentials_token_hash|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, CAST(\"unique\" AS TEXT), CAST(partial AS TEXT) FROM pragma_index_list('credentials') WHERE origin = 'c' ORDER BY name;"));
        Assert.Equal(
            ["id|TEXT|1|1", "sequence|INTEGER|1|0", "type|TEXT|1|0", "status|TEXT|1|0", "progress|INTEGER|1|0", "message|TEXT|0|0", "payload|TEXT|0|0", "result|TEXT|0|0", "error|TEXT|0|0", "created_utc|TEXT|1|0", "started_utc|TEXT|0|0", "finished_utc|TEXT|0|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('jobs') ORDER BY cid;"));
        Assert.Equal(
            ["ix_jobs_finished_utc|0", "ix_jobs_sequence|1", "ix_jobs_status_sequence|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, CAST(\"unique\" AS TEXT) FROM pragma_index_list('jobs') WHERE origin = 'c' ORDER BY name;"));
        Assert.Equal(
            ["song_id|TEXT|1|1", "number|TEXT|1|2"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('used_version_numbers') ORDER BY cid;"));
        Assert.Equal(
            ["tr_versions_frozen_inputs_never_change", "tr_versions_number_never_changes", "tr_versions_record_number_after_insert", "tr_versions_touch_song_after_insert", "tr_versions_touch_song_after_update"],
            TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'versions' ORDER BY name;"));
        Assert.Equal(
            ["tr_generations_identity_never_changes"],
            TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'generations' ORDER BY name;"));
        Assert.Equal(
            ["id|TEXT|1|1", "version_id|TEXT|1|0", "song_id|TEXT|1|0", "ordinal|INTEGER|1|0", "created_utc|TEXT|1|0"],
            TestDatabase.Rows(directory.Path, "SELECT name, type, CAST(\"notnull\" AS TEXT), CAST(pk AS TEXT) FROM pragma_table_info('generations') ORDER BY cid;"));
        Assert.Equal(
            ["ix_generations_song_id|0", "ix_generations_version_id_ordinal|1"],
            TestDatabase.Rows(directory.Path, "SELECT name, CAST(\"unique\" AS TEXT) FROM pragma_index_list('generations') WHERE origin = 'c' ORDER BY name;"));
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
        Assert.EndsWith("_AddGenerations", state.LastAppliedMigrationId, StringComparison.Ordinal);
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
