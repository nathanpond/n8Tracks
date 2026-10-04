using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Logging;

namespace n8Tracks.Api.Tests.Persistence;

/// <summary>
/// Runs the entry point in-process, on a real port, against a database it must refuse. Each case
/// checks the exit code, the one Error line, and that nothing ever answered on the port.
/// </summary>
public sealed class DatabaseStartupFailureTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public async Task AFileThatIsNotASqliteDatabaseFailsAtTheOpenStep()
    {
        await File.WriteAllTextAsync(TestDatabase.FilePath(directory.Path), string.Concat(Enumerable.Repeat("this is not a database\n", 200)));

        var line = await RunToFailure();

        Assert.Equal("open", Step(line));
        Assert.Equal(JsonValueKind.Object, line.GetProperty("exception").ValueKind);
        Assert.Contains("Database startup failed at step open", line.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADatabaseWithAMigrationThisBuildDoesNotKnowIsRefusedAsNewerThanTheApplication()
    {
        CreateUpToDateDatabase();
        var latest = Assert.Single(TestDatabase.History(directory.Path)).Split('|')[0];
        TestDatabase.Execute(
            directory.Path,
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29990101000000_FromALaterVersion', '99.0.0');");

        var line = await RunToFailure();

        Assert.Equal("schema-version", Step(line));
        var message = line.GetProperty("message").GetString();
        Assert.Contains("newer than the application", message, StringComparison.Ordinal);
        Assert.Contains("29990101000000_FromALaterVersion", message, StringComparison.Ordinal);
        Assert.Contains(latest, message, StringComparison.Ordinal);

        // Refused, not repaired: the unknown row is still there.
        Assert.Equal(2, TestDatabase.History(directory.Path).Count);
    }

    [Fact]
    public async Task ADatabaseWithTablesButNoMigrationHistoryIsRefused()
    {
        TestDatabase.Execute(directory.Path, "CREATE TABLE somebody_elses (id INTEGER PRIMARY KEY);");

        var line = await RunToFailure();

        Assert.Equal("schema-version", Step(line));
        Assert.Contains("not a database this application can upgrade", line.GetProperty("message").GetString(), StringComparison.Ordinal);

        // Nothing was added to a file the app does not own.
        Assert.Equal(
            ["somebody_elses"],
            TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'table';"));
    }

    [Fact]
    public async Task AMigrationThatThrowsFailsWithTheMigrationIdAsTheStep()
    {
        // A view is not a table, so the file passes the history check; creating app_metadata then collides with it.
        TestDatabase.Execute(directory.Path, "CREATE VIEW app_metadata AS SELECT 1 AS key, 2 AS value;");

        var line = await RunToFailure();

        Assert.Matches("^[0-9]{14}_InitialCreate$", Step(line));
        Assert.Equal(JsonValueKind.Object, line.GetProperty("exception").ValueKind);

        // The failed migration left no history row and no table behind.
        Assert.Empty(TestDatabase.Rows(directory.Path, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\";"));
        Assert.Empty(TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'app_metadata';"));

        // And the next start is not stuck behind a migration lock: it fails the same way, promptly.
        Assert.Matches("^[0-9]{14}_InitialCreate$", Step(await RunToFailure()));
    }

    [Fact]
    public async Task AMigrationLockLeftBehindIsAFailureInsteadOfAnEndlessWait()
    {
        TestDatabase.Execute(
            directory.Path,
            "CREATE TABLE \"__EFMigrationsLock\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, \"Timestamp\" TEXT NOT NULL);"
            + "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-01-01 00:00:00');");

        var line = await RunToFailure();

        Assert.Equal("migration-lock", Step(line));
        Assert.Contains("__EFMigrationsLock", line.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpToDateDatabaseStartsListeningAndAppliesNothing()
    {
        // Complement: the same harness does see a listener and an exit code of 0 when the database is good.
        CreateUpToDateDatabase();
        var history = TestDatabase.History(directory.Path);

        var (exitCode, lines, answered) = await Run(stopWhenAnswered: true);

        Assert.Equal(0, exitCode);
        Assert.True(answered);
        Assert.DoesNotContain(lines, line => line.GetProperty("level").GetString() is "Error" or "Critical");
        Assert.DoesNotContain(lines, line => line.GetProperty("message").GetString()!.StartsWith("Applied database migration", StringComparison.Ordinal));
        var summary = Assert.Single(lines, line => line.GetProperty("message").GetString()!.StartsWith("Database is up to date", StringComparison.Ordinal));
        Assert.Equal(0, summary.GetProperty("properties").GetProperty("appliedCount").GetInt32());
        Assert.Equal(history, TestDatabase.History(directory.Path));
    }

    private static string? Step(JsonElement line) => line.GetProperty("properties").GetProperty("step").GetString();

    private void CreateUpToDateDatabase()
    {
        using var host = TestDatabase.Host(directory.Path);
        _ = host.Services;
    }

    /// <summary>Runs to exit, asserting code 1, exactly one line (an Error), and no answer on the port at any time.</summary>
    private async Task<JsonElement> RunToFailure()
    {
        var (exitCode, lines, answered) = await Run(stopWhenAnswered: false);

        Assert.False(answered, "The app answered an HTTP request although database startup failed.");
        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        LogLineAssert.HasTheLogShape(line);
        Assert.Equal("Error", line.GetProperty("level").GetString());

        return line;
    }

    /// <summary>
    /// Runs the entry point while asking <c>/health</c> over and over, and once more after it exits.
    /// <c>Answered</c> is whether any request got an HTTP response.
    /// </summary>
    private async Task<(int ExitCode, List<JsonElement> Lines, bool Answered)> Run(bool stopWhenAnswered)
    {
        var port = FreePort();
        var health = new Uri($"http://127.0.0.1:{port}/health");
        using var output = new StringWriter();
        using var stop = new CancellationTokenSource();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["N8TRACKS_DATA_PATH"] = directory.Path,
            ["N8TRACKS_PORT"] = port.ToString(CultureInfo.InvariantCulture),
        };

        var run = Program.RunAsync([], new EnvironmentSnapshot(variables, directory.Path), output, stop.Token);
        var answered = false;
        var deadline = DateTimeOffset.UtcNow + StartTimeout;

        try
        {
            while (!run.IsCompleted && !(stopWhenAnswered && answered))
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The app neither exited nor answered in time.");
                answered |= await Answers(client, health);
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }
        finally
        {
            await stop.CancelAsync();
        }

        var exitCode = await run.WaitAsync(StartTimeout);
        answered |= await Answers(client, health);

        var lines = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();

        return (exitCode, lines, answered);
    }

    private static async Task<bool> Answers(HttpClient client, Uri uri)
    {
        try
        {
            using var response = await client.GetAsync(uri);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var listener = TcpListener.Create(0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
