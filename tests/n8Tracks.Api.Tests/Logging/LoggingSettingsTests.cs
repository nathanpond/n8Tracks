using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Logging;

namespace n8Tracks.Api.Tests.Logging;

/// <summary>
/// The log settings (#234) through <c>GET</c> and <c>PUT /api/v1/settings/logging</c>: the defaults
/// and the environment's level before the first save, every bound on both sides, the revision, the
/// level taking effect for the files and standard output, Debug ending after 24 hours, the
/// confirmation before limits delete files, and an unwritable log folder.
/// </summary>
public sealed class LoggingSettingsTests
{
    [Fact]
    public async Task BeforeTheFirstSaveTheLevelIsTheEnvironmentsAndTheLimitsAreTheDefaults()
    {
        using var factory = LoggingApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(LoggingApi.Settings);
        var settings = await SetupApi.JsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"0\"", response.Headers.ETag?.Tag);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(0, settings.GetProperty("revision").GetInt32());
        Assert.Equal("information", settings.GetProperty("level").GetString());
        Assert.Equal("environment", settings.GetProperty("levelSource").GetString());
        Assert.Equal(14, settings.GetProperty("retentionDays").GetInt32());
        Assert.Equal(200, settings.GetProperty("maxMegabytes").GetInt32());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("debugUntil").ValueKind);
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("folderProblem").ValueKind);
    }

    [Theory]
    [InlineData("Trace", "trace")]
    [InlineData("Critical", "critical")]
    [InlineData("Warning", "warning")]
    public async Task TheEnvironmentsLevelIsHonouredAndShownAsSetByTheEnvironment(string variable, string shown)
    {
        using var factory = LoggingApi.Host(new TestClock(), variable);
        using var client = await SessionApi.SignedInClientAsync(factory);

        var settings = await LoggingApi.GetAsync(client);
        Assert.Equal(shown, settings.GetProperty("level").GetString());
        Assert.Equal("environment", settings.GetProperty("levelSource").GetString());

        await LoggingApi.LookAsync(factory);
        Assert.Equal(Enum.Parse<N8TracksLogLevel>(variable), LoggingApi.Level(factory));
    }

    [Theory]
    [InlineData("retentionDays", 0, false)]
    [InlineData("retentionDays", 1, true)]
    [InlineData("retentionDays", 90, true)]
    [InlineData("retentionDays", 91, false)]
    [InlineData("maxMegabytes", 9, false)]
    [InlineData("maxMegabytes", 10, true)]
    [InlineData("maxMegabytes", 5120, true)]
    [InlineData("maxMegabytes", 5121, false)]
    public async Task EachBoundIsAcceptedAndOneBeyondItRefused(string field, int value, bool accepted)
    {
        using var factory = LoggingApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);

        var body = new Dictionary<string, object> { ["level"] = "information", ["retentionDays"] = 14, ["maxMegabytes"] = 200, [field] = value };
        using var response = await LoggingApi.PutAsync(client, "\"0\"", body);

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = await SetupApi.JsonAsync(response);
            Assert.Equal(value, saved.GetProperty(field).GetInt32());
            Assert.Equal(1, saved.GetProperty("revision").GetInt32());
            Assert.Equal("setting", saved.GetProperty("levelSource").GetString());
        }
        else
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
            Assert.Equal(0, (await LoggingApi.GetAsync(client)).GetProperty("revision").GetInt32());
        }
    }

    [Theory]
    [InlineData("{\"level\":\"trace\",\"retentionDays\":14,\"maxMegabytes\":200}", "level")]
    [InlineData("{\"level\":\"critical\",\"retentionDays\":14,\"maxMegabytes\":200}", "level")]
    [InlineData("{\"level\":\"Debug\",\"retentionDays\":14,\"maxMegabytes\":200}", "level")]
    [InlineData("{\"level\":3,\"retentionDays\":14,\"maxMegabytes\":200}", "level")]
    [InlineData("{\"retentionDays\":14,\"maxMegabytes\":200}", "level")]
    [InlineData("{\"level\":\"debug\",\"retentionDays\":1.5,\"maxMegabytes\":200}", "retentionDays")]
    [InlineData("{\"level\":\"debug\",\"retentionDays\":\"14\",\"maxMegabytes\":200}", "retentionDays")]
    [InlineData("{\"level\":\"debug\",\"retentionDays\":14}", "maxMegabytes")]
    [InlineData("{\"level\":\"debug\",\"retentionDays\":14,\"maxMegabytes\":200,\"confirmDelete\":\"yes\"}", "confirmDelete")]
    public async Task AWrongOrMissingFieldIsRefusedByName(string body, string field)
    {
        using var factory = LoggingApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await LoggingApi.PutAsync(client, "\"0\"", body);

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
    }

    [Fact]
    public async Task AWriteNeedsTheCurrentRevision()
    {
        using var factory = LoggingApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var body = new { level = "warning", retentionDays = 14, maxMegabytes = 200 };

        using (var missing = await LoggingApi.PutAsync(client, null, body))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        await LoggingApi.SetAsync(client, "error");

        using var stale = await LoggingApi.PutAsync(client, "\"0\"", body);
        var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        Assert.Equal("error", problem.GetProperty("current").GetProperty("level").GetString());
        Assert.Equal(1, problem.GetProperty("current").GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task ASavedLevelTakesEffectAtOnceForTheFilesAndStandardOutputAlike()
    {
        using var factory = LoggingApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);

        // At Information, the health check's request line (Debug) is not written.
        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        await LoggingApi.SetAsync(client, "debug");
        Assert.Equal(N8TracksLogLevel.Debug, LoggingApi.Level(factory));

        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        var line = await LoggingApi.WaitForFileLineAsync(factory, IsHealthLine);
        Assert.Equal("Debug", line.GetProperty("level").GetString());
        Assert.Single(LoggingApi.FileLines(factory), IsHealthLine);

        // The framework's categories stay at Warning, Debug included.
        Assert.DoesNotContain(
            LoggingApi.FileLines(factory),
            static line => line.GetProperty("level").GetString() is "Debug" or "Information"
                && (line.GetProperty("properties").TryGetProperty("sourceContext", out var context)
                    && context.GetString() is { } name
                    && (name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal))));

        static bool IsHealthLine(JsonElement line) =>
            line.GetProperty("properties").TryGetProperty("path", out var path) && path.GetString() == "/health";
    }

    [Fact]
    public async Task ALevelSavedOutsideThisProcessTakesEffectAtTheNextLook()
    {
        using var factory = LoggingApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await LoggingApi.LookAsync(factory);
        Assert.Equal(N8TracksLogLevel.Information, LoggingApi.Level(factory));

        // Written to the row directly, as a restored database would bring it.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ILoggingSettingsStore>().WriteAsync(
                new StoredLoggingSettings(new LoggingSettings(N8TracksLogLevel.Error, 14, 200, null), 1),
                CancellationToken.None);
        }

        Assert.Equal(N8TracksLogLevel.Information, LoggingApi.Level(factory));
        await LoggingApi.LookAsync(factory);
        Assert.Equal(N8TracksLogLevel.Error, LoggingApi.Level(factory));
    }

    [Fact]
    public async Task DebugSwitchesItselfBackToInformationAfter24HoursAndSaysWhen()
    {
        var clock = new TestClock();
        using var factory = LoggingApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        var saved = await LoggingApi.SetAsync(client, "debug");
        Assert.Equal(TestClock.Start.AddHours(24), saved.GetProperty("debugUntil").GetDateTimeOffset());

        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        await LoggingApi.LookAsync(factory);
        Assert.Equal(N8TracksLogLevel.Debug, LoggingApi.Level(factory));
        Assert.Equal(1, (await LoggingApi.ViewAsync(factory)).Stored.Revision);

        clock.Advance(TimeSpan.FromSeconds(1));
        await LoggingApi.LookAsync(factory);

        var view = await LoggingApi.ViewAsync(factory);
        Assert.Equal(N8TracksLogLevel.Information, LoggingApi.Level(factory));
        Assert.Equal(N8TracksLogLevel.Information, view.Stored.Settings.Level);
        Assert.Null(view.Stored.Settings.DebugUntil);
        Assert.Equal(2, view.Stored.Revision);
        await LoggingApi.WaitForFileLineAsync(factory, static line => line.GetProperty("message").GetString()!.StartsWith("Debug logging ended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SavingDebugAgainStartsThe24HoursAgain()
    {
        var clock = new TestClock();
        using var factory = LoggingApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        await LoggingApi.SetAsync(client, "debug");
        clock.Advance(TimeSpan.FromHours(20));
        var again = await LoggingApi.SetAsync(client, "debug");
        Assert.Equal(TestClock.Start.AddHours(44), again.GetProperty("debugUntil").GetDateTimeOffset());

        clock.Advance(TimeSpan.FromHours(5));
        await LoggingApi.LookAsync(factory);
        Assert.Equal(N8TracksLogLevel.Debug, LoggingApi.Level(factory));

        // Any other level has no end.
        var warning = await LoggingApi.SetAsync(client, "warning");
        Assert.Equal(JsonValueKind.Null, warning.GetProperty("debugUntil").ValueKind);
    }

    [Fact]
    public async Task DebugFromTheEnvironmentDoesNotEnd()
    {
        var clock = new TestClock();
        using var factory = LoggingApi.Host(clock, "Debug");
        using var client = await SessionApi.SignedInClientAsync(factory);

        clock.Advance(TimeSpan.FromDays(3));
        await LoggingApi.LookAsync(factory);

        var view = await LoggingApi.ViewAsync(factory);
        Assert.Equal(N8TracksLogLevel.Debug, LoggingApi.Level(factory));
        Assert.Equal(LogLevelSource.Environment, view.Source);
        Assert.Equal(0, view.Stored.Revision);
    }

    [Fact]
    public async Task LimitsThatWouldDeleteLogFilesAreRefusedUntilTheDeletionIsConfirmed()
    {
        using var factory = LoggingApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var folder = LoggingApi.Folder(factory);
        await File.WriteAllTextAsync(Path.Combine(folder, "n8tracks-20260920.jsonl"), new string('x', 1000));
        await File.WriteAllTextAsync(Path.Combine(folder, "n8tracks-20260925.jsonl"), new string('x', 10));
        await LoggingApi.LookAsync(factory);

        // Kept by 14 days, the 20th (11 days old) is gone with 7.
        using (var refused = await LoggingApi.PutAsync(client, "\"0\"", new { level = "information", retentionDays = 7, maxMegabytes = 200 }))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "confirmation_required");
            Assert.Equal(1, problem.GetProperty("files").GetInt32());
            Assert.Equal(1000, problem.GetProperty("bytes").GetInt64());
        }

        Assert.True(File.Exists(Path.Combine(folder, "n8tracks-20260920.jsonl")));
        Assert.Equal(0, (await LoggingApi.GetAsync(client)).GetProperty("revision").GetInt32());

        using var confirmed = await LoggingApi.PutAsync(client, "\"0\"", new { level = "information", retentionDays = 7, maxMegabytes = 200, confirmDelete = true });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.False(File.Exists(Path.Combine(folder, "n8tracks-20260920.jsonl")));
        Assert.True(File.Exists(Path.Combine(folder, "n8tracks-20260925.jsonl")));
    }

    [Fact]
    public async Task LimitsThatDeleteNothingNeedNoConfirmation()
    {
        using var factory = LoggingApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await File.WriteAllTextAsync(Path.Combine(LoggingApi.Folder(factory), "n8tracks-20260925.jsonl"), "x");

        var saved = await LoggingApi.SetAsync(client, "warning", retentionDays: 7, maxMegabytes: 10);

        Assert.Equal(7, saved.GetProperty("retentionDays").GetInt32());
        Assert.True(File.Exists(Path.Combine(LoggingApi.Folder(factory), "n8tracks-20260925.jsonl")));
    }

    [Fact]
    public async Task AnUnwritableLogFolderLeavesTheHostServingAndLoggingToStandardOutputAndIsShown()
    {
        using var factory = new LoggingApiFactory();

        // A file where the log folder goes, before the host starts.
        await File.WriteAllTextAsync(Path.Combine(factory.DataPath, "logs"), "in the way");
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var health = await client.GetAsync(new Uri("/api/v1/setup/status", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await factory.CompletionLine(LoggingApiFactory.RequestId(health));

        var settings = await LoggingApi.GetAsync(client);
        Assert.Contains("standard output", settings.GetProperty("folderProblem").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(factory.DataPath, settings.GetProperty("folderProblem").GetString(), StringComparison.Ordinal);

        await LoggingApi.LookAsync(factory);
        var warning = await factory.WaitForLine(static line => line.GetProperty("message").GetString()!.StartsWith("Log files are not being written", StringComparison.Ordinal));
        Assert.Equal("Warning", warning.GetProperty("level").GetString());
    }
}
