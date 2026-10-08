using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Persistence;

namespace n8Tracks.Api.Tests.Health;

public class HealthEndpointTests
{
    private const string FaultText = "sentinel-fault-text-4f1c";

    private static readonly Uri Health = new("/health", UriKind.Relative);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task WhenEverythingWorksTheReportIsHealthyWithEveryComponent()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await UntilTheJobWorkerHasReported(client);

        using var response = await client.GetAsync(Health);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var lastApplied = TestDatabase.History(factory.DataPath)[^1].Split('|')[0];
        var expected = new JsonObject
        {
            ["status"] = "healthy",
            ["version"] = ProductVersion.Current,
            ["timeZone"] = "UTC",
            ["components"] = new JsonObject
            {
                ["application"] = new JsonObject { ["status"] = "healthy", ["detail"] = "running" },
                ["database"] = new JsonObject { ["status"] = "healthy", ["detail"] = "reachable" },
                ["migrations"] = new JsonObject
                {
                    ["status"] = "healthy",
                    ["detail"] = "up to date",
                    ["lastApplied"] = lastApplied,

                    // This start created the database, so it applied every migration, with no safety backup to take.
                    ["lastOutcome"] = "succeeded",
                    ["lastSafetyBackupAt"] = null,
                },
                ["media"] = new JsonObject { ["status"] = "healthy", ["detail"] = "available" },
                ["jobs"] = new JsonObject { ["status"] = "healthy", ["detail"] = "idle" },
                ["maintenance"] = new JsonObject { ["status"] = "healthy", ["detail"] = "off" },
            },
        };

        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(body)), body);
        Assert.Matches(@"^\d{14}_\w+$", lastApplied);
        AssertSaysNothingAboutWhereThingsAre(body, factory);
    }

    [Fact]
    public async Task TheConfiguredTimeZoneIsReported()
    {
        using var factory = Host((EnvironmentOptionsLoader.TimeZone, "Europe/Oslo"));
        using var client = factory.CreateClient();

        var report = await Report(client, HttpStatusCode.OK);

        Assert.Equal("Europe/Oslo", report.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task AMissingMediaPathIsDegradedAndTheAppKeepsAnswering()
    {
        using var parent = new TemporaryDirectory();
        using var factory = Host((EnvironmentOptionsLoader.MediaPath, Path.Combine(parent.Path, "not-mounted")));
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync(Health);
            var body = await response.Content.ReadAsStringAsync();
            var report = JsonSerializer.Deserialize<JsonElement>(body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("degraded", report.GetProperty("status").GetString());
            AssertComponent(report, "media", "degraded", "unavailable");
            AssertComponent(report, "application", "healthy", "running");
            AssertComponent(report, "database", "healthy", "reachable");
            AssertComponent(report, "migrations", "healthy", "up to date");
            AssertSaysNothingAboutWhereThingsAre(body, factory);
        }

        // The check looked, and left nothing behind.
        Assert.False(Directory.Exists(factory.MediaPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent.Path));
    }

    [Fact]
    public async Task AMediaMountThatAppearsOrGoesAwayIsSeenOnTheNextRequest()
    {
        using var parent = new TemporaryDirectory();
        var mount = Path.Combine(parent.Path, "mount");
        using var factory = Host((EnvironmentOptionsLoader.MediaPath, mount));
        using var client = factory.CreateClient();

        AssertComponent(await Report(client, HttpStatusCode.OK), "media", "degraded", "unavailable");

        Directory.CreateDirectory(mount);
        var mounted = await Report(client, HttpStatusCode.OK);
        Assert.Equal("healthy", mounted.GetProperty("status").GetString());
        AssertComponent(mounted, "media", "healthy", "available");

        Directory.Delete(mount);
        AssertComponent(await Report(client, HttpStatusCode.OK), "media", "degraded", "unavailable");
    }

    [Fact]
    public async Task AFileAtTheMediaPathIsDegraded()
    {
        using var parent = new TemporaryDirectory();
        var file = Path.Combine(parent.Path, "media");
        await File.WriteAllTextAsync(file, "not a directory");
        using var factory = Host((EnvironmentOptionsLoader.MediaPath, file));
        using var client = factory.CreateClient();

        var report = await Report(client, HttpStatusCode.OK);

        Assert.Equal("degraded", report.GetProperty("status").GetString());
        AssertComponent(report, "media", "degraded", "unavailable");
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task AReadOnlyMediaPathIsHealthy()
    {
        using var media = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(media.Path, "song.mp3"), "audio");
        var original = File.GetUnixFileMode(media.Path);
        File.SetUnixFileMode(media.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            using var factory = Host((EnvironmentOptionsLoader.MediaPath, media.Path));
            using var client = factory.CreateClient();

            var report = await Report(client, HttpStatusCode.OK);

            Assert.Equal("healthy", report.GetProperty("status").GetString());
            AssertComponent(report, "media", "healthy", "available");
            Assert.Equal(["song.mp3"], Directory.EnumerateFileSystemEntries(media.Path).Select(Path.GetFileName));
        }
        finally
        {
            File.SetUnixFileMode(media.Path, original);
        }
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task AMediaPathThatCannotBeReadIsDegraded()
    {
        using var media = new TemporaryDirectory();
        var original = File.GetUnixFileMode(media.Path);
        File.SetUnixFileMode(media.Path, UnixFileMode.None);

        try
        {
            using var factory = Host((EnvironmentOptionsLoader.MediaPath, media.Path));
            using var client = factory.CreateClient();

            var report = await Report(client, HttpStatusCode.OK);

            // Permissions do not stop a privileged user (root in a container), who can read it after all.
            var expected = Environment.IsPrivilegedProcess ? ("healthy", "available") : ("degraded", "unavailable");
            AssertComponent(report, "media", expected.Item1, expected.Item2);
        }
        finally
        {
            File.SetUnixFileMode(media.Path, original);
        }
    }

    [Fact]
    public async Task ADatabaseMadeUnreachableAfterStartupIsUnhealthyWith503AndRecovers()
    {
        using var database = new FaultInjectingConnectionFactory();
        using var factory = new N8TracksApiFactory { TestServices = database.Register };
        using var client = factory.CreateClient();

        AssertComponent(await Report(client, HttpStatusCode.OK), "database", "healthy", "reachable");

        database.Fault = new InvalidOperationException($"{FaultText}: unable to open database file '{TestDatabase.FilePath(factory.DataPath)}'");

        using var response = await client.GetAsync(Health);
        var body = await response.Content.ReadAsStringAsync();
        var report = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("unhealthy", report.GetProperty("status").GetString());
        AssertComponent(report, "database", "unhealthy", "unreachable");
        AssertComponent(report, "application", "healthy", "running");
        AssertComponent(report, "media", "healthy", "available");

        // The schema cannot be read, so it is unknown (#237); the fields captured at startup stay.
        AssertComponent(report, "migrations", "unhealthy", "unknown");
        Assert.Equal(
            TestDatabase.History(factory.DataPath)[^1].Split('|')[0],
            report.GetProperty("components").GetProperty("migrations").GetProperty("lastApplied").GetString());

        // Complement: the fault names the data path and carries text of its own; neither reaches the body.
        Assert.Contains(factory.DataPath, database.Fault.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FaultText, body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("unable to open", body, StringComparison.Ordinal);
        AssertSaysNothingAboutWhereThingsAre(body, factory);

        database.Fault = null;
        var recovered = await Report(client, HttpStatusCode.OK);
        Assert.Equal("healthy", recovered.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WhenBothTheDatabaseAndTheMediaFailTheReportIsUnhealthy()
    {
        using var database = new FaultInjectingConnectionFactory { Fault = new InvalidOperationException(FaultText) };
        using var parent = new TemporaryDirectory();
        using var factory = new N8TracksApiFactory(
            new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.MediaPath] = Path.Combine(parent.Path, "gone") })
        {
            TestServices = database.Register,
        };
        using var client = factory.CreateClient();

        var report = await Report(client, HttpStatusCode.ServiceUnavailable);

        Assert.Equal("unhealthy", report.GetProperty("status").GetString());
        AssertComponent(report, "database", "unhealthy", "unreachable");
        AssertComponent(report, "media", "degraded", "unavailable");
    }

    [Fact]
    public async Task ADatabaseFileDeletedAfterStartupIsUnhealthyAndIsNotCreatedAgain()
    {
        using var factory = new N8TracksApiFactory { TestServices = ClaimCountingJobStore.Register };
        using var client = factory.CreateClient();
        var worker = factory.Services.GetRequiredService<ClaimCountingJobStore.Counter>();

        AssertComponent(await Report(client, HttpStatusCode.OK), "database", "healthy", "reachable");

        // Windows will not delete a file a pooled connection still holds.
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(factory.DataPath, "n8tracks.db*"))
        {
            File.Delete(file);
        }

        // The job worker goes on polling the database through the context (#300: its connection
        // created the missing file, empty, and the check then found a database). Two finished polls
        // after the delete mean at least one opened a connection after it.
        await worker.UntilAsync(worker.Claims, 2);
        Assert.Empty(Directory.EnumerateFiles(factory.DataPath, "n8tracks.db*"));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync(Health);
            var body = await response.Content.ReadAsStringAsync();
            var report = JsonSerializer.Deserialize<JsonElement>(body);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("unhealthy", report.GetProperty("status").GetString());
            AssertComponent(report, "database", "unhealthy", "unreachable");
            AssertComponent(report, "migrations", "unhealthy", "unknown");
            AssertSaysNothingAboutWhereThingsAre(body, factory);
            Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(Directory.EnumerateFiles(factory.DataPath, "n8tracks.db*"));
    }

    [Fact]
    public async Task ADatabaseCheckThatHangsIsUnhealthyAfterTwoSecondsAndOnlyOneCheckIsLeftRunning()
    {
        using var database = new FaultInjectingConnectionFactory();
        using var factory = new N8TracksApiFactory { TestServices = database.Register };
        using var client = factory.CreateClient();

        await Report(client, HttpStatusCode.OK);
        var callsBefore = database.Calls;
        database.Hang();

        var (first, firstTook) = await TimedReport(client, HttpStatusCode.ServiceUnavailable);
        AssertComponent(first, "database", "unhealthy", "unreachable");
        AssertComponent(first, "media", "healthy", "available");
        Assert.InRange(firstTook, Deadline - TimeSpan.FromMilliseconds(200), Deadline + TimeSpan.FromSeconds(3));

        // While the abandoned check is still out, the answer is immediate and no second check starts.
        var (second, secondTook) = await TimedReport(client, HttpStatusCode.ServiceUnavailable);
        AssertComponent(second, "database", "unhealthy", "unreachable");
        Assert.InRange(secondTook, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(callsBefore + 1, database.Calls);

        database.Release();
        await UntilHealthy(client);
    }

    [Fact]
    public async Task AMediaCheckThatHangsIsDegradedAfterTwoSecondsAndOnlyOneCheckIsLeftRunning()
    {
        using var media = new SwitchableMediaProbe();
        using var factory = new N8TracksApiFactory { TestServices = media.Register };
        using var client = factory.CreateClient();

        await Report(client, HttpStatusCode.OK);
        Assert.Equal(1, media.Calls);
        media.Hang();

        var (first, firstTook) = await TimedReport(client, HttpStatusCode.OK);
        Assert.Equal("degraded", first.GetProperty("status").GetString());
        AssertComponent(first, "media", "degraded", "unavailable");
        AssertComponent(first, "database", "healthy", "reachable");
        Assert.InRange(firstTook, Deadline - TimeSpan.FromMilliseconds(200), Deadline + TimeSpan.FromSeconds(3));

        var (second, secondTook) = await TimedReport(client, HttpStatusCode.OK);
        AssertComponent(second, "media", "degraded", "unavailable");
        Assert.InRange(secondTook, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(2, media.Calls);

        media.Release();
        await UntilHealthy(client);
    }

    [Fact]
    public async Task EveryRequestRunsTheChecksAgain()
    {
        using var database = new FaultInjectingConnectionFactory();
        using var media = new SwitchableMediaProbe();
        using var factory = new N8TracksApiFactory
        {
            TestServices = services =>
            {
                database.Register(services);
                media.Register(services);
            },
        };
        using var client = factory.CreateClient();

        // The schema and the queue are read through the same factory (the schema at most every 30
        // seconds), so the database's count only has to grow with every request.
        var databaseCalls = 0;
        for (var request = 1; request <= 3; request++)
        {
            await Report(client, HttpStatusCode.OK);

            Assert.True(database.Calls > databaseCalls, $"Request {request} did not check the database.");
            databaseCalls = database.Calls;
            Assert.Equal(request, media.Calls);
        }
    }

    [Fact]
    public async Task AStatusChangeIsLoggedOnceAsAWarningAndTheRecoveryAsInformation()
    {
        using var database = new FaultInjectingConnectionFactory();
        using var media = new SwitchableMediaProbe();
        using var factory = new LoggingApiFactory
        {
            TestServices = services =>
            {
                database.Register(services);
                media.Register(services);
            },
        };
        using var client = factory.CreateClient();
        await UntilTheJobWorkerHasReported(client);

        await Report(client, HttpStatusCode.OK);
        Assert.Empty(HealthLines(factory));

        media.Readable = false;
        database.Fault = new InvalidOperationException(FaultText);
        for (var poll = 0; poll < 3; poll++)
        {
            await Report(client, HttpStatusCode.ServiceUnavailable);
        }

        // The schema and the queue cannot be read without the database, so they go with it.
        var failures = HealthLines(factory);
        Assert.Equal(["database", "migrations", "media", "jobs"], failures.Select(line => LoggingApiFactory.Property(line, "component")));
        Assert.All(failures, line => Assert.Equal("Warning", line.GetProperty("level").GetString()));
        Assert.Equal(["Unhealthy", "Unhealthy", "Degraded", "Degraded"], failures.Select(line => LoggingApiFactory.Property(line, "healthStatus")));

        // The operator gets the cause in the log, which is the only place it goes.
        Assert.Contains(FaultText, failures[0].GetProperty("exception").GetProperty("message").GetString(), StringComparison.Ordinal);

        media.Readable = true;
        database.Fault = null;
        for (var poll = 0; poll < 3; poll++)
        {
            await Report(client, HttpStatusCode.OK);
        }

        var recoveries = HealthLines(factory).Skip(4).ToList();
        Assert.Equal(["database", "migrations", "media", "jobs"], recoveries.Select(line => LoggingApiFactory.Property(line, "component")));
        Assert.All(recoveries, line => Assert.Equal("Information", line.GetProperty("level").GetString()));
    }

    [Fact]
    public async Task HeadIsAnsweredLikeGet()
    {
        using var database = new FaultInjectingConnectionFactory();
        using var factory = new N8TracksApiFactory { TestServices = database.Register };
        using var client = factory.CreateClient();

        using var healthy = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Health));
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal("no-store", healthy.Headers.CacheControl?.ToString());

        database.Fault = new InvalidOperationException(FaultText);
        using var unhealthy = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Health));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);

        using var post = await client.PostAsync(Health, content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public void TheEndpointIsAnonymousAndAnswersOnlyGetAndHead()
    {
        using var factory = new N8TracksApiFactory();

        var endpoint = Assert.Single(
            factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/health");

        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Equal(["GET", "HEAD"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
    }

    [Fact]
    public async Task TheOpenApiDocumentDeclaresBothAnswersAndTheStatusNames()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var text = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        var document = JsonSerializer.Deserialize<JsonElement>(text);
        var path = document.GetProperty("paths").GetProperty("/health");

        foreach (var method in new[] { "get", "head" })
        {
            var responses = path.GetProperty(method).GetProperty("responses");
            Assert.Equal(["200", "503"], responses.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        }

        foreach (var status in new[] { "\"healthy\"", "\"degraded\"", "\"unhealthy\"" })
        {
            Assert.Contains(status, text, StringComparison.Ordinal);
        }
    }

    private static N8TracksApiFactory Host(params (string Name, string Value)[] variables) =>
        new(variables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.Ordinal));

    private static async Task<JsonElement> Report(HttpClient client, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(Health);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(expected == response.StatusCode, $"Expected {expected}, got {response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static async Task<(JsonElement Report, TimeSpan Took)> TimedReport(HttpClient client, HttpStatusCode expected)
    {
        var watch = Stopwatch.StartNew();
        var report = await Report(client, expected);

        return (report, watch.Elapsed);
    }

    /// <summary>Until the job worker has beaten once, so the <c>jobs</c> component is past <c>starting</c>.</summary>
    internal static async Task UntilTheJobWorkerHasReported(HttpClient client)
    {
        var giveUp = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync(Health);
            var report = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            if (report.GetProperty("components").GetProperty("jobs").GetProperty("detail").GetString() != "starting")
            {
                return;
            }

            Assert.True(giveUp.Elapsed < TimeSpan.FromSeconds(10), "The job worker did not report.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private static async Task UntilHealthy(HttpClient client)
    {
        var giveUp = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync(Health);
            var report = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            if (report.GetProperty("status").GetString() == "healthy")
            {
                return;
            }

            Assert.True(giveUp.Elapsed < TimeSpan.FromSeconds(10), "The component did not recover once its check was released.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private static void AssertComponent(JsonElement report, string name, string status, string detail)
    {
        var component = report.GetProperty("components").GetProperty(name);

        Assert.Equal(status, component.GetProperty("status").GetString());
        Assert.Equal(detail, component.GetProperty("detail").GetString());
        Assert.Equal(
            name == "migrations" ? ["detail", "lastApplied", "lastOutcome", "lastSafetyBackupAt", "status"] : ["detail", "status"],
            component.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>No filesystem path of the instance, and nothing that looks like a path or a connection string.</summary>
    private static void AssertSaysNothingAboutWhereThingsAre(string body, N8TracksApiFactory factory)
    {
        Assert.DoesNotContain(factory.DataPath, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.MediaPath, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFileName(factory.DataPath), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("n8tracks.db", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('/', body);
        Assert.DoesNotContain('\\', body);
    }

    private static List<JsonElement> HealthLines(LoggingApiFactory factory) =>
        [.. factory.Lines().Where(line => LoggingApiFactory.Property(line, "component") is not null)];
}
