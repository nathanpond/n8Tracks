using System.Net;
using System.Text.Json;
using n8Tracks.Api.Cli;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Cli;

/// <summary>
/// <c>n8tracks reset-password</c>, run in-process through the app binary's entry point against the
/// database of an app that is running in the same test (an in-memory host on the same data path), as
/// <c>docker exec</c> runs it beside the server in the container.
/// </summary>
public sealed class ResetPasswordCommandTests
{
    private const string NewPassword = "a new password 42";
    private const string PasswordSentinel = "sentinel-reset-password-6a1f";

    private static readonly Uri Session = SessionApi.Session;

    [Fact]
    public async Task TypedTwiceItReplacesThePasswordAndEndsEverySessionOfTheRunningApp()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var oldHash = PasswordHash(factory.DataPath);

        var run = await RunAsync(factory.DataPath, ["reset-password"], Typed(NewPassword, NewPassword));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("New password: ", run.Error, StringComparison.Ordinal);
        Assert.Contains("Repeat the new password: ", run.Error, StringComparison.Ordinal);
        Assert.Contains($"The password of {SetupApi.TestUsername} was reset.", run.Error, StringComparison.Ordinal);
        Assert.NotEqual(oldHash, PasswordHash(factory.DataPath));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM sessions;"));

        // The running app sees it at the next request.
        using (var session = await client.GetAsync(Session))
        {
            await SetupApi.ProblemAsync(session, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        using (var oldPassword = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        }

        using var newPassword = await SessionApi.SignInAsync(client, SetupApi.TestUsername, NewPassword);
        Assert.Equal(HttpStatusCode.Created, newPassword.StatusCode);
    }

    [Fact]
    public async Task WithPasswordStdinTheFirstLineWithoutItsLineEndingIsThePassword()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var run = await RunAsync(factory.DataPath, ["reset-password", "--password-stdin"], Piped(NewPassword + "\r\nthe second line is ignored\n"));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.DoesNotContain("New password", run.Error, StringComparison.Ordinal);
        using var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, NewPassword);
        Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
    }

    [Fact]
    public async Task AResetClearsTheSignInLockout()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await SessionApi.SignInAsync(client, SetupApi.TestUsername, "a wrong password");
        }

        using (var locked = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        }

        var run = await RunAsync(factory.DataPath, ["reset-password", "--password-stdin"], Piped(NewPassword + "\n"));

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(TestDatabase.Rows(factory.DataPath, "SELECT value FROM settings WHERE key = 'signIn.throttle';"));
        using var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, NewPassword);
        Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
    }

    [Fact]
    public async Task CredentialsKeepWorkingAfterAReset()
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);

        var run = await RunAsync(factory.DataPath, ["reset-password", "--password-stdin"], Piped(NewPassword + "\n"));

        Assert.Equal(0, run.ExitCode);
        using var read = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task TheCurrentPasswordAsTheNewOneIsAcceptedAndStillEndsEverySession()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var run = await RunAsync(factory.DataPath, ["reset-password"], Typed(SetupApi.TestPassword, SetupApi.TestPassword));

        Assert.Equal(0, run.ExitCode);
        using var session = await client.GetAsync(Session);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    [Fact]
    public async Task MismatchedPasswordsAreRefusedAndTheOldPasswordAndSessionsStay()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var oldHash = PasswordHash(factory.DataPath);

        var run = await RunAsync(factory.DataPath, ["reset-password"], Typed(NewPassword, NewPassword + "x"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("The passwords do not match.", run.Error, StringComparison.Ordinal);
        Assert.Contains("The password was not changed.", run.Error, StringComparison.Ordinal);
        await AssertUnchangedAsync(factory, client, oldHash);
    }

    [Theory]
    [InlineData("too short")]
    [InlineData("eleven char")]
    public async Task APasswordBreakingTheSetupRuleIsRefusedAndTheOldOneStays(string password)
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var oldHash = PasswordHash(factory.DataPath);

        var run = await RunAsync(factory.DataPath, ["reset-password", "--password-stdin"], Piped(password + "\n"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("A password must be at least 12 characters.", run.Error, StringComparison.Ordinal);
        await AssertUnchangedAsync(factory, client, oldHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\nthe password on the second line\n")]
    public async Task EmptyInputIsRefused(string input)
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var oldHash = PasswordHash(factory.DataPath);

        var run = await RunAsync(factory.DataPath, ["reset-password", "--password-stdin"], Piped(input));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("No password was read", run.Error, StringComparison.Ordinal);
        await AssertUnchangedAsync(factory, client, oldHash);
    }

    [Fact]
    public async Task WithoutATerminalOrPasswordStdinItRefusesAndPointsToPasswordStdin()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var oldHash = PasswordHash(factory.DataPath);

        var run = await RunAsync(factory.DataPath, ["reset-password"], Piped(NewPassword + "\n" + NewPassword + "\n"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("not a terminal", run.Error, StringComparison.Ordinal);
        Assert.Contains("--password-stdin", run.Error, StringComparison.Ordinal);
        await AssertUnchangedAsync(factory, client, oldHash);
    }

    [Fact]
    public async Task EndOfInputOrCtrlCAtThePromptChangesNothing()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var oldHash = PasswordHash(factory.DataPath);

        // The test console reads lines; the end of its input is what Ctrl-C and Ctrl-D answer.
        var run = await RunAsync(factory.DataPath, ["reset-password"], Typed(NewPassword));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Cancelled. Nothing was changed.", run.Error, StringComparison.Ordinal);
        await AssertUnchangedAsync(factory, client, oldHash);
    }

    [Fact]
    public async Task BeforeSetupItReportsThatSetupWasNeverCompleted()
    {
        using var factory = new N8TracksApiFactory();
        using (var client = factory.CreateClient())
        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            // The app has started and created the database; no administrator exists.
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        var run = await RunAsync(factory.DataPath, ["reset-password", "--password-stdin"], Piped(NewPassword + "\n"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Setup has never been completed", run.Error, StringComparison.Ordinal);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM administrators;"));
    }

    [Fact]
    public async Task WithoutADatabaseFileItReportsThatSetupWasNeverCompletedAndCreatesNothing()
    {
        using var data = new TemporaryDirectory();

        var run = await RunAsync(data.Path, ["reset-password", "--password-stdin"], Piped(NewPassword + "\n"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Setup has never been completed", run.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    [Fact]
    public async Task ADatabaseOfAnotherSchemaVersionIsRefusedAndNeverMigrated()
    {
        using var data = new TemporaryDirectory();
        using (var factory = TestDatabase.Host(data.Path))
        using (var client = factory.CreateClient())
        {
            await SetupApi.CompleteAsync(client);
        }

        var last = TestDatabase.History(data.Path)[^1].Split('|')[0];
        TestDatabase.Execute(data.Path, $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{last}';");
        var history = TestDatabase.History(data.Path);
        var oldHash = PasswordHash(data.Path);

        var run = await RunAsync(data.Path, ["reset-password", "--password-stdin"], Piped(NewPassword + "\n"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("does not match this version of n8Tracks. Start the application first", run.Error, StringComparison.Ordinal);
        Assert.Equal(history, TestDatabase.History(data.Path));
        Assert.Equal(oldHash, PasswordHash(data.Path));
    }

    [Fact]
    public async Task WhileTheMigrationLockIsHeldItRefuses()
    {
        using var data = new TemporaryDirectory();
        using (var factory = TestDatabase.Host(data.Path))
        using (var client = factory.CreateClient())
        {
            await SetupApi.CompleteAsync(client);
        }

        TestDatabase.Execute(
            data.Path,
            "CREATE TABLE IF NOT EXISTS \"__EFMigrationsLock\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, \"Timestamp\" TEXT NOT NULL);"
            + "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-01-01 00:00:00');");
        var oldHash = PasswordHash(data.Path);

        var run = await RunAsync(data.Path, ["reset-password", "--password-stdin"], Piped(NewPassword + "\n"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("is being upgraded", run.Error, StringComparison.Ordinal);
        Assert.Equal(oldHash, PasswordHash(data.Path));
    }

    [Fact]
    public async Task AnInvalidDataPathIsReported()
    {
        using var data = new TemporaryDirectory();
        var missing = Path.Combine(data.Path, "missing");

        var run = await RunAsync(missing, ["reset-password", "--password-stdin"], Piped(NewPassword + "\n"));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Invalid configuration: N8TRACKS_DATA_PATH", run.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task AnUnknownArgumentIsRefusedWithoutBeingShown()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var oldHash = PasswordHash(factory.DataPath);

        // The likeliest mistake: the password given as an argument.
        var run = await RunAsync(factory.DataPath, ["reset-password", PasswordSentinel], Typed(NewPassword, NewPassword));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Usage: n8tracks reset-password [--password-stdin]", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordSentinel, run.Error, StringComparison.Ordinal);
        await AssertUnchangedAsync(factory, client, oldHash);
    }

    [Fact]
    public async Task ItWritesOneInformationLineAndTheServerLogsTheResetOnceAtTheNextSignIn()
    {
        using var factory = new LoggingApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var run = await RunAsync(factory.DataPath, ["reset-password", "--password-stdin"], Piped(PasswordSentinel + "\n"));

        Assert.Equal(0, run.ExitCode);
        var line = Assert.Single(JsonLines(run.Error));
        Assert.Equal("Information", line.GetProperty("level").GetString());
        Assert.Equal(ResetPasswordCommand.SourceContext, line.GetProperty("properties").GetProperty("sourceContext").GetString());
        Assert.Contains("reset from the container", line.GetProperty("message").GetString(), StringComparison.Ordinal);
        var recorded = TestDatabase.Scalar(factory.DataPath, "SELECT value FROM settings WHERE key = 'account.lastPasswordReset';");
        Assert.Matches("^\"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z\"$", recorded);

        for (var signIn = 0; signIn < 2; signIn++)
        {
            using var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, PasswordSentinel);
            Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
        }

        var notices = factory.Lines()
            .Where(logged => logged.GetProperty("message").GetString()!.Contains("reset from the container", StringComparison.Ordinal))
            .ToList();
        var notice = Assert.Single(notices);
        Assert.Equal("Information", notice.GetProperty("level").GetString());
        Assert.EndsWith($"at {recorded.Trim('"')}", notice.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComplementThePasswordNeverReachesStandardOutputStandardErrorOrTheLog()
    {
        using var factory = new LoggingApiFactory("Trace");
        using var client = await SessionApi.SignedInClientAsync(factory);

        var typed = await RunAsync(factory.DataPath, ["reset-password"], Typed(PasswordSentinel, PasswordSentinel));
        var mismatched = await RunAsync(factory.DataPath, ["reset-password"], Typed(PasswordSentinel, PasswordSentinel + "!"));
        var piped = await RunAsync(factory.DataPath, ["reset-password", "--password-stdin"], Piped(PasswordSentinel + "\n"));
        using (var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, PasswordSentinel))
        {
            Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
        }

        Assert.Equal([0, 1, 0], new[] { typed.ExitCode, mismatched.ExitCode, piped.ExitCode });
        foreach (var run in new[] { typed, mismatched, piped })
        {
            Assert.Equal(string.Empty, run.Output);
            Assert.DoesNotContain(PasswordSentinel, run.Error, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(PasswordSentinel, factory.CapturedText, StringComparison.Ordinal);
        Assert.Contains("reset from the container", factory.CapturedText, StringComparison.Ordinal);
    }

    private static async Task AssertUnchangedAsync(N8TracksApiFactory factory, HttpClient client, string oldHash)
    {
        Assert.Equal(oldHash, PasswordHash(factory.DataPath));
        using (var session = await client.GetAsync(Session))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }

        using var other = factory.CreateClient();
        using var signedIn = await SessionApi.SignInAsync(other, SetupApi.TestUsername, SetupApi.TestPassword);
        Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
    }

    private static string PasswordHash(string dataPath) => TestDatabase.Scalar(dataPath, "SELECT password_hash FROM administrators;");

    /// <summary>A terminal at which each of <paramref name="lines"/> is typed in turn, then nothing more.</summary>
    private static Func<TextWriter, CommandConsole> Typed(params string[] lines) =>
        error => new CommandConsole(new StringReader(string.Join('\n', lines) + "\n"), error, isTerminal: true);

    /// <summary>Standard input that is not a terminal, holding <paramref name="input"/>.</summary>
    private static Func<TextWriter, CommandConsole> Piped(string input) =>
        error => new CommandConsole(new StringReader(input), error, isTerminal: false);

    /// <summary>Runs the app binary's entry point with <paramref name="args"/>, as the container's wrapper does.</summary>
    private static async Task<CommandRun> RunAsync(string dataPath, string[] args, Func<TextWriter, CommandConsole> console)
    {
        var environment = new EnvironmentSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.DataPath] = dataPath },
            Path.GetTempPath());
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(args, environment, output, console(error), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        return new CommandRun(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>The lines of <paramref name="text"/> that are JSON objects: the log lines among the messages.</summary>
    private static List<JsonElement> JsonLines(string text) =>
    [
        .. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith('{'))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)),
    ];

    private sealed record CommandRun(int ExitCode, string Output, string Error);
}
