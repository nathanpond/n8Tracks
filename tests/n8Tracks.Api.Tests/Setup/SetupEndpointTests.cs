using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Health;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Application.Setup;
using n8Tracks.Infrastructure.Security;

namespace n8Tracks.Api.Tests.Setup;

public sealed class SetupEndpointTests
{
    private static readonly Uri OtherEndpoint = new("/api/v1/songs", UriKind.Relative);
    private static readonly Uri Health = new("/health", UriKind.Relative);

    [Fact]
    public async Task BeforeSetupTheStatusIsIncompleteWithBothChecksPassing()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(SetupApi.Status);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var expected = new JsonObject
        {
            ["complete"] = false,
            ["storage"] = new JsonObject { ["writable"] = true },
            ["media"] = new JsonObject { ["available"] = true },
        };
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(body)), body);
        Assert.DoesNotContain(factory.DataPath, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnavailableMediaPathIsReportedAndDoesNotBlockSetup()
    {
        using var parent = new TemporaryDirectory();
        using var factory = new N8TracksApiFactory(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvironmentOptionsLoader.MediaPath] = Path.Combine(parent.Path, "not-mounted"),
        });
        using var client = factory.CreateClient();

        var status = await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status));
        Assert.False(status.GetProperty("media").GetProperty("available").GetBoolean());
        Assert.True(status.GetProperty("storage").GetProperty("writable").GetBoolean());

        await SetupApi.CompleteAsync(client);
    }

    [Fact]
    public async Task BeforeSetupOtherApiEndpointsAre503SetupRequiredAndHealthAnswers()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();

        using (var response = await client.GetAsync(OtherEndpoint))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.ServiceUnavailable, "setup_required");
        }

        using (var response = await client.PostAsync(new Uri("/API/V1/anything/else", UriKind.Relative), null))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.ServiceUnavailable, "setup_required");
        }

        using (var response = await client.GetAsync(Health))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Complement: once setup is done the same endpoint is past the gate, and asks for a session.
        await SetupApi.CompleteAsync(client);
        using (var response = await client.GetAsync(OtherEndpoint))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.Unauthorized, "not_authenticated");
        }
    }

    [Fact]
    public async Task SetupCreatesTheAdministratorWithAnArgon2idHashAndCompletes()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();

        using var response = await SetupApi.SubmitAsync(client, "  Owner  ", SetupApi.TestPassword, SetupApi.TestPassword);
        var created = await SetupApi.JsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Owner", created.GetProperty("username").GetString());
        var id = Guid.Parse(created.GetProperty("id").GetString()!);
        Assert.Equal(7, id.Version);

        var row = TestDatabase.Scalar(factory.DataPath, "SELECT username || '|' || username_key || '|' || password_hash || '|' || slot FROM administrators;").Split('|');
        Assert.Equal("Owner", row[0]);
        Assert.Equal("OWNER", row[1]);
        Assert.NotEqual(SetupApi.TestPassword, row[2]);
        Assert.DoesNotContain(SetupApi.TestPassword, row[2], StringComparison.Ordinal);
        Assert.StartsWith("$argon2id$", row[2], StringComparison.Ordinal);
        Assert.True(new Argon2idPasswordHasher().Verify(row[2], SetupApi.TestPassword));
        Assert.Equal("1", row[3]);

        var status = await client.GetAsync(SetupApi.Status);
        Assert.Equal("""{"complete":true}""", await status.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ASecondSubmissionIs409AndChangesNothing()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var before = TestDatabase.Rows(factory.DataPath, "SELECT id, username, password_hash FROM administrators;");

        using var response = await SetupApi.SubmitAsync(client, "intruder", "another long password", "another long password");

        await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "setup_already_complete");
        Assert.Equal(before, TestDatabase.Rows(factory.DataPath, "SELECT id, username, password_hash FROM administrators;"));
    }

    [Fact]
    public async Task SetupStaysCompleteAfterARestart()
    {
        using var data = new TemporaryDirectory();
        using (var first = TestDatabase.Host(data.Path))
        using (var client = first.CreateClient())
        {
            await SetupApi.CompleteAsync(client);
        }

        using var second = TestDatabase.Host(data.Path);
        using var restarted = second.CreateClient();

        var status = await restarted.GetAsync(SetupApi.Status);
        Assert.Equal("""{"complete":true}""", await status.Content.ReadAsStringAsync());

        using var again = await SetupApi.SubmitAsync(restarted, "intruder", "another long password", "another long password");
        await SetupApi.ProblemAsync(again, HttpStatusCode.Conflict, "setup_already_complete");

        using var other = await restarted.GetAsync(OtherEndpoint);
        await SetupApi.ProblemAsync(other, HttpStatusCode.Unauthorized, "not_authenticated");
    }

    [Fact]
    public async Task InvalidFieldsAre422WithErrorsByFieldAndCreateNothing()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();

        using (var response = await SetupApi.SubmitAsync(client, "owner", "short", "short"))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            var errors = problem.GetProperty("errors");
            Assert.Equal(["password"], errors.EnumerateObject().Select(field => field.Name));
            Assert.Equal("A password must be at least 12 characters.", errors.GetProperty("password")[0].GetString());
        }

        using (var response = await SetupApi.SubmitAsync(client, "owner", SetupApi.TestPassword, SetupApi.TestPassword + "!"))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal(["passwordConfirmation"], problem.GetProperty("errors").EnumerateObject().Select(field => field.Name));
        }

        using (var response = await SetupApi.SubmitAsync(client, null, null, null))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal(
                ["password", "passwordConfirmation", "username"],
                problem.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM administrators;"));
        Assert.False((await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status))).GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task AMalformedBodyIs400InvalidRequest()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();

        using var content = new StringContent("{\"username\": ", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(SetupApi.Submit, content);

        await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task WhileStorageIsNotWritableTheStatusSaysSoAndSubmissionIs409()
    {
        var checks = new FakeSetupChecks { Writable = false };
        using var factory = new N8TracksApiFactory { TestServices = checks.Register };
        using var client = factory.CreateClient();

        var status = await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status));
        Assert.False(status.GetProperty("storage").GetProperty("writable").GetBoolean());

        using (var response = await SetupApi.SubmitAsync(client, "owner", SetupApi.TestPassword, SetupApi.TestPassword))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "storage_not_writable");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM administrators;"));

        // Complement: the same submission succeeds once storage recovers (Retry is a refetch).
        checks.Writable = true;
        Assert.True((await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status))).GetProperty("storage").GetProperty("writable").GetBoolean());
        await SetupApi.CompleteAsync(client);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task ADataPathWhereNoFileCanBeCreatedIsNotWritable()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        Assert.True((await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status))).GetProperty("storage").GetProperty("writable").GetBoolean());

        var mode = File.GetUnixFileMode(factory.DataPath);
        File.SetUnixFileMode(factory.DataPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var status = await SetupApi.JsonAsync(await client.GetAsync(SetupApi.Status));
            Assert.False(status.GetProperty("storage").GetProperty("writable").GetBoolean());

            using var response = await SetupApi.SubmitAsync(client, "owner", SetupApi.TestPassword, SetupApi.TestPassword);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "storage_not_writable");
        }
        finally
        {
            File.SetUnixFileMode(factory.DataPath, mode);
        }

        // The check leaves no file behind.
        Assert.Empty(Directory.GetFiles(factory.DataPath, ".n8tracks-write-check-*"));
    }

    [Fact]
    public async Task OfTwoConcurrentSubmissionsExactlyOneCreatesTheAdministrator()
    {
        // Both submissions are held until both have passed every check (the real ones) and hashed their
        // password, so both reach the insert believing setup is incomplete.
        using var barrier = new Barrier(2);
        using var factory = new N8TracksApiFactory
        {
            TestServices = services =>
            {
                services.RemoveAll<IPasswordHasher>();
                services.AddSingleton<IPasswordHasher>(new RendezvousHasher(new Argon2idPasswordHasher(), barrier));
            },
        };
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            SetupApi.SubmitAsync(client, "first", SetupApi.TestPassword, SetupApi.TestPassword),
            SetupApi.SubmitAsync(client, "second", SetupApi.TestPassword, SetupApi.TestPassword));
        try
        {
            var bodies = string.Join("\n", await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync())));
            Assert.True(
                responses.Select(response => response.StatusCode).Order().SequenceEqual([HttpStatusCode.Created, HttpStatusCode.Conflict]),
                bodies);
            var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            await SetupApi.ProblemAsync(loser, HttpStatusCode.Conflict, "setup_already_complete");

            var winner = await SetupApi.JsonAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(winner.GetProperty("username").GetString(), TestDatabase.Scalar(factory.DataPath, "SELECT username FROM administrators;"));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task TheDatabaseRefusesASecondAdministratorWhateverWritesIt()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        var refused = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            "INSERT INTO administrators (id, slot, username, username_key, password_hash, created_utc) "
            + "VALUES ('00000000-0000-7000-8000-000000000000', 1, 'other', 'OTHER', 'x', '2026-01-01T00:00:00.000Z');"));
        Assert.Equal(2067, refused.SqliteExtendedErrorCode);

        var badSlot = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            "INSERT INTO administrators (id, slot, username, username_key, password_hash, created_utc) "
            + "VALUES ('00000000-0000-7000-8000-000000000001', 2, 'other', 'OTHER', 'x', '2026-01-01T00:00:00.000Z');"));
        Assert.Equal(19, badSlot.SqliteErrorCode);

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM administrators;"));
    }

    [Fact]
    public async Task TheSettingsTableHoldsOnlyJson()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Health);

        TestDatabase.Execute(factory.DataPath, "INSERT INTO settings (key, value) VALUES ('example', '{\"enabled\":true}');");
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(factory.DataPath, "INSERT INTO settings (key, value) VALUES ('broken', 'not json');"));
        Assert.Equal(["example"], TestDatabase.Rows(factory.DataPath, "SELECT key FROM settings;"));
    }

    /// <summary>
    /// The administrator is created by setup and nowhere else: no endpoint registers an account,
    /// resets a password, or signs in through an external provider, and no such package is loaded.
    /// </summary>
    [Fact]
    public void ThereIsNoRegistrationPasswordResetOrOAuth()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();

        // Complement: the setup routes are there to be seen.
        Assert.Contains("/api/v1/setup", routes);
        Assert.All(routes, route => Assert.DoesNotMatch("(?i)regist|sign-?up|reset|forgot|oauth|openid|external", route));

        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name ?? string.Empty).ToList();
        Assert.DoesNotContain(assemblies, name => name.Contains("OAuth", StringComparison.OrdinalIgnoreCase)
            || name.Contains("OpenIdConnect", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.Identity", StringComparison.Ordinal));
    }

    /// <summary>Setup checks a test controls; replaces the real ones.</summary>
    private sealed class FakeSetupChecks : ISetupChecks
    {
        public bool Writable { get; set; } = true;

        public bool MediaAvailable { get; set; } = true;

        public void Register(IServiceCollection services)
        {
            services.RemoveAll<ISetupChecks>();
            services.AddSingleton<ISetupChecks>(this);
        }

        public Task<bool> IsDataPathWritableAsync(CancellationToken cancellationToken) => Task.FromResult(Writable);

        public Task<bool> IsMediaAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(MediaAvailable);
    }

    /// <summary>Hashes, then waits until the other submission has hashed too.</summary>
    private sealed class RendezvousHasher(IPasswordHasher inner, Barrier barrier) : IPasswordHasher
    {
        public string Hash(string password)
        {
            var hash = inner.Hash(password);
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "The other submission never reached the hash.");
            return hash;
        }

        public bool Verify(string encodedHash, string password) => inner.Verify(encodedHash, password);
    }
}
