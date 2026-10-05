using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Auth;

namespace n8Tracks.Api.Tests.Auth;

public sealed class SessionEndpointTests
{
    private static readonly Uri Songs = new("/api/v1/songs", UriKind.Relative);

    [Fact]
    public async Task WithoutASessionTheApiIs401ExceptSetupStatusAndSignInWhileHealthAndTheShellAnswer()
    {
        using var factory = new N8TracksApiFactory();
        await File.WriteAllTextAsync(Path.Combine(factory.WebRootPath, "index.html"), "<!doctype html><html><head></head><body><div id=\"root\"></div></body></html>");
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        foreach (var (method, uri) in new[] { (HttpMethod.Get, SessionApi.Session), (HttpMethod.Delete, SessionApi.Session), (HttpMethod.Delete, SessionApi.Sessions), (HttpMethod.Get, Songs), (HttpMethod.Post, Songs) })
        {
            using var response = await SessionApi.SendAsync(client, method, uri);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        // Complement: what stays open.
        using (var status = await client.GetAsync(SetupApi.Status))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        using (var shell = await client.GetAsync(new Uri("/library/deep/link", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
            Assert.Contains("<div id=\"root\">", await shell.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Sign-in answers for itself: a wrong password is its own 401, not "sign in to continue".
        using var wrong = await SessionApi.SignInAsync(client, SetupApi.TestUsername, "not the password");
        await SetupApi.ProblemAsync(wrong, HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task SigningInSetsAnHttpOnlyLaxCookieHoldingOnlyTheIdentifierAndTheSessionAnswers()
    {
        var clock = new TestClock();
        using var factory = WithClock(clock);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("n8tracks-test/1.0");
        await SetupApi.CompleteAsync(client);

        using var response = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        var body = await SetupApi.JsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(SetupApi.TestUsername, body.GetProperty("username").GetString());
        Assert.Equal(TestClock.Start.AddDays(30), DateTimeOffset.Parse(body.GetProperty("expiresAt").GetString()!, CultureInfo.InvariantCulture));

        var cookie = SessionApi.SessionSetCookie(response)!.ToLowerInvariant();
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=lax", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/;", cookie + ";", StringComparison.Ordinal);
        Assert.Contains("expires=sat, 31 oct 2026 09:00:00 gmt", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("secure", cookie, StringComparison.Ordinal);

        // The cookie carries a 256-bit identifier; the database holds only its SHA-256 hash.
        var token = SessionApi.SessionToken(response)!;
        Assert.Equal(43, token.Length);
        Assert.Equal(32, Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=").Length);
        var row = TestDatabase.Scalar(factory.DataPath, "SELECT id_hash || '|' || created_utc || '|' || last_used_utc || '|' || user_agent FROM sessions;").Split('|');
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token))), row[0]);
        Assert.Equal("2026-10-01T09:00:00.000Z", row[1]);
        Assert.Equal(row[1], row[2]);
        Assert.Equal("n8tracks-test/1.0", row[3]);
        foreach (var file in Directory.GetFiles(factory.DataPath, "n8tracks.db*"))
        {
            Assert.DoesNotContain(token, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file)), StringComparison.Ordinal);
        }

        using var session = await client.GetAsync(SessionApi.Session);
        var current = await SetupApi.JsonAsync(session);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(SetupApi.TestUsername, current.GetProperty("username").GetString());
        Assert.Equal("no-store", session.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("OWNER")]
    [InlineData("  Owner\t")]
    public async Task UsernameMatchingIgnoresCaseAndSurroundingWhitespace(string typed)
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        using var response = await SessionApi.SignInAsync(client, typed, SetupApi.TestPassword);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(SetupApi.TestUsername, (await SetupApi.JsonAsync(response)).GetProperty("username").GetString());
    }

    [Fact]
    public async Task AWrongPasswordAndAnUnknownUsernameGiveTheSameGenericErrorAndNoCookie()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        using var wrongPassword = await SessionApi.SignInAsync(client, SetupApi.TestUsername, "not the password at all");
        using var unknownUser = await SessionApi.SignInAsync(client, "nobody", SetupApi.TestPassword);
        using var passwordCase = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword.ToUpperInvariant());

        var problems = new List<string>();
        foreach (var response in new[] { wrongPassword, unknownUser, passwordCase })
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Unauthorized, "invalid_credentials");
            problems.Add(problem.GetProperty("title").GetString()!);
            Assert.Null(SessionApi.SessionSetCookie(response));
        }

        Assert.Single(problems.Distinct(StringComparer.Ordinal));
        Assert.Equal("The username or password is incorrect.", problems[0]);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT CAST(count(*) AS TEXT) FROM sessions;"));
    }

    [Fact]
    public async Task AnUnknownUsernameIsCheckedAgainstADummyHashSoItTakesAsLong()
    {
        var hasher = new CountingHasher();
        using var factory = new N8TracksApiFactory { TestServices = hasher.Register };
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        hasher.Reset();

        using (var unknown = await SessionApi.SignInAsync(client, "nobody", SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        }

        Assert.Equal(1, hasher.Verifications);

        // Complement: a known username is verified once too.
        using (var known = await SessionApi.SignInAsync(client, SetupApi.TestUsername, "not the password"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, known.StatusCode);
        }

        Assert.Equal(2, hasher.Verifications);
    }

    [Fact]
    public async Task MissingFieldsAre422AndAreNotCountedAsFailures()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        for (var attempt = 0; attempt < SignInThrottleState.MaximumFailures + 1; attempt++)
        {
            using var response = await SessionApi.SignInAsync(client, "  ", null);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal(["password", "username"], problem.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        }

        using var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
    }

    [Fact]
    public async Task ASessionLasts30DaysFromItsLastUseAndUsingItExtendsIt()
    {
        var clock = new TestClock();
        using var factory = WithClock(clock);
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        // Day 29 after signing in: valid, and this use moves the expiry and issues the cookie again.
        clock.Advance(TimeSpan.FromDays(29));
        using (var day29 = await client.GetAsync(SessionApi.Session))
        {
            Assert.Equal(HttpStatusCode.OK, day29.StatusCode);
            Assert.Equal(TestClock.Start.AddDays(59), DateTimeOffset.Parse((await SetupApi.JsonAsync(day29)).GetProperty("expiresAt").GetString()!, CultureInfo.InvariantCulture));
            Assert.Contains("expires=sun, 29 nov 2026 09:00:00 gmt", SessionApi.SessionSetCookie(day29)!.ToLowerInvariant(), StringComparison.Ordinal);
        }

        // Within a minute of the last recorded use nothing is written, and the cookie is not issued again.
        clock.Advance(TimeSpan.FromSeconds(59));
        using (var soon = await client.GetAsync(SessionApi.Session))
        {
            Assert.Equal(HttpStatusCode.OK, soon.StatusCode);
            Assert.Null(SessionApi.SessionSetCookie(soon));
        }

        Assert.Equal("2026-10-30T09:00:00.000Z", TestDatabase.Scalar(factory.DataPath, "SELECT last_used_utc FROM sessions;"));

        // 29 days after the last use (58 after signing in): still valid.
        clock.Advance(TimeSpan.FromDays(29) - TimeSpan.FromSeconds(59));
        using (var day58 = await client.GetAsync(SessionApi.Session))
        {
            Assert.Equal(HttpStatusCode.OK, day58.StatusCode);
        }

        // 30 days after the last use: expired, and told apart from nothing.
        clock.Advance(TimeSpan.FromDays(30));
        using var expired = await client.GetAsync(SessionApi.Session);
        await SetupApi.ProblemAsync(expired, HttpStatusCode.Unauthorized, "not_authenticated");
    }

    [Fact]
    public async Task ASessionSurvivesARestart()
    {
        using var data = new TemporaryDirectory();
        string token;
        using (var first = TestDatabase.Host(data.Path))
        using (var client = first.CreateClient())
        {
            await SetupApi.CompleteAsync(client);
            using var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
            token = SessionApi.SessionToken(signIn)!;
        }

        using var second = TestDatabase.Host(data.Path);
        using var restarted = CookielessClient(second);

        using var response = await SendWithCookie(restarted, HttpMethod.Get, SessionApi.Session, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SetupApi.TestUsername, (await SetupApi.JsonAsync(response)).GetProperty("username").GetString());
    }

    [Fact]
    public async Task SignOutEndsOnlyTheCurrentSessionAndDropsTheCookie()
    {
        using var factory = new N8TracksApiFactory();
        using var first = await SessionApi.SignedInClientAsync(factory);
        using var second = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(second, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        using (var signOut = await SessionApi.SendAsync(first, HttpMethod.Delete, SessionApi.Session))
        {
            Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
            var cleared = SessionApi.SessionSetCookie(signOut)!.ToLowerInvariant();
            Assert.Contains("expires=thu, 01 jan 1970", cleared, StringComparison.Ordinal);
        }

        using (var ended = await first.GetAsync(SessionApi.Session))
        {
            await SetupApi.ProblemAsync(ended, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        using var other = await second.GetAsync(SessionApi.Session);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT CAST(count(*) AS TEXT) FROM sessions;"));
    }

    [Fact]
    public async Task SignOutEverywhereEndsEverySessionOnTheirNextRequest()
    {
        using var factory = new N8TracksApiFactory();
        using var first = await SessionApi.SignedInClientAsync(factory);
        using var second = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(second, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        using (var everywhere = await SessionApi.SendAsync(first, HttpMethod.Delete, SessionApi.Sessions))
        {
            Assert.Equal(HttpStatusCode.NoContent, everywhere.StatusCode);
            Assert.NotNull(SessionApi.SessionSetCookie(everywhere));
        }

        foreach (var client in new[] { first, second })
        {
            using var ended = await client.GetAsync(SessionApi.Session);
            await SetupApi.ProblemAsync(ended, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT CAST(count(*) AS TEXT) FROM sessions;"));
    }

    [Fact]
    public async Task SigningInWhileSignedInCreatesANewSessionAndEndsTheOldOne()
    {
        using var factory = new N8TracksApiFactory();
        using var client = CookielessClient(factory);
        await SetupApi.CompleteAsync(client);

        using var firstSignIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        var old = SessionApi.SessionToken(firstSignIn)!;

        using var request = new HttpRequestMessage(HttpMethod.Post, SessionApi.Session)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { username = SetupApi.TestUsername, password = SetupApi.TestPassword }),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        request.Headers.Add("Cookie", $"{SessionApi.CookieName}={old}");
        using var secondSignIn = await client.SendAsync(request);
        var current = SessionApi.SessionToken(secondSignIn)!;

        Assert.Equal(HttpStatusCode.Created, secondSignIn.StatusCode);
        Assert.NotEqual(old, current);
        using (var oldSession = await SendWithCookie(client, HttpMethod.Get, SessionApi.Session, old))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        }

        using var newSession = await SendWithCookie(client, HttpMethod.Get, SessionApi.Session, current);
        Assert.Equal(HttpStatusCode.OK, newSession.StatusCode);
    }

    [Fact]
    public async Task AnUnsafeRequestWithASessionButWithoutTheAntiforgeryHeaderIs403WhileAGetIsAllowed()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var refused = await SessionApi.SendAsync(client, HttpMethod.Delete, SessionApi.Session, antiforgery: false))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "antiforgery_required");
        }

        using (var everywhere = await SessionApi.SendAsync(client, HttpMethod.Delete, SessionApi.Sessions, antiforgery: false))
        {
            await SetupApi.ProblemAsync(everywhere, HttpStatusCode.Forbidden, "antiforgery_required");
        }

        using (var wrongValue = new HttpRequestMessage(HttpMethod.Delete, SessionApi.Session))
        {
            wrongValue.Headers.Add(SessionApi.AntiforgeryHeader, "0");
            using var response = await client.SendAsync(wrongValue);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, "antiforgery_required");
        }

        // Nothing was ended, and a GET needs no header.
        using (var get = await client.GetAsync(SessionApi.Session))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT CAST(count(*) AS TEXT) FROM sessions;"));

        // Without a session the answer is 401 first.
        using var anonymous = factory.CreateClient();
        using var unauthenticated = await SessionApi.SendAsync(anonymous, HttpMethod.Delete, SessionApi.Session, antiforgery: false);
        await SetupApi.ProblemAsync(unauthenticated, HttpStatusCode.Unauthorized, "not_authenticated");
    }

    [Fact]
    public async Task SignInRequiresTheAntiforgeryHeaderToo()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        using var response = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword, antiforgery: false);

        await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, "antiforgery_required");
        Assert.Null(SessionApi.SessionSetCookie(response));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT CAST(count(*) AS TEXT) FROM sessions;"));
    }

    [Fact]
    public async Task OverPlainHttpTheCookieIsNotSecureAndBehindAnHttpsProxyItIs()
    {
        using var factory = new N8TracksApiFactory();
        using var client = CookielessClient(factory);
        await SetupApi.CompleteAsync(client);

        using (var plain = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.DoesNotContain("secure", SessionApi.SessionSetCookie(plain)!.ToLowerInvariant(), StringComparison.Ordinal);
        }

        using var proxied = new HttpRequestMessage(HttpMethod.Post, SessionApi.Session)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { username = SetupApi.TestUsername, password = SetupApi.TestPassword }),
        };
        proxied.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        proxied.Headers.Add("X-Forwarded-Proto", "https");
        proxied.Headers.Add("X-Forwarded-Host", "tracks.example.com");
        using var secure = await client.SendAsync(proxied);

        Assert.Equal(HttpStatusCode.Created, secure.StatusCode);
        Assert.Contains("secure", SessionApi.SessionSetCookie(secure)!.ToLowerInvariant(), StringComparison.Ordinal);

        // The session works the same through the proxy.
        using var through = new HttpRequestMessage(HttpMethod.Get, SessionApi.Session);
        through.Headers.Add("X-Forwarded-Proto", "https");
        through.Headers.Add("X-Forwarded-Host", "tracks.example.com");
        through.Headers.Add("Cookie", $"{SessionApi.CookieName}={SessionApi.SessionToken(secure)}");
        using var session = await client.SendAsync(through);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task UnderASubPathTheCookiePathIsTheBasePath()
    {
        using var factory = new N8TracksApiFactory(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["N8TRACKS_BASE_URL"] = "https://nas.example/n8tracks",
        });
        using var client = CookielessClient(factory);
        using (var setup = await client.PostAsync(
            new Uri("/n8tracks/api/v1/setup", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new { username = SetupApi.TestUsername, password = SetupApi.TestPassword, passwordConfirmation = SetupApi.TestPassword })))
        {
            Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/n8tracks/api/v1/session", UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { username = SetupApi.TestUsername, password = SetupApi.TestPassword }),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("path=/n8tracks;", SessionApi.SessionSetCookie(response)!.ToLowerInvariant() + ";", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredSessionsArePurgedAndOthersAreKept()
    {
        var clock = new TestClock();
        using var factory = WithClock(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        clock.Advance(TimeSpan.FromDays(10));
        using (var second = factory.CreateClient())
        using (var signIn = await SessionApi.SignInAsync(second, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        clock.Advance(TimeSpan.FromDays(25));
        int purged;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            purged = await scope.ServiceProvider.GetRequiredService<SessionService>().PurgeExpiredAsync(CancellationToken.None);
        }

        Assert.Equal(1, purged);
        Assert.Equal("2026-10-11T09:00:00.000Z", TestDatabase.Scalar(factory.DataPath, "SELECT created_utc FROM sessions;"));
    }

    [Fact]
    public async Task TheAppPurgesExpiredSessionsWhenItStarts()
    {
        using var data = new TemporaryDirectory();
        using (var first = TestDatabase.Host(data.Path))
        using (var client = first.CreateClient())
        {
            await SetupApi.CompleteAsync(client);
            using var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        TestDatabase.Execute(data.Path, "UPDATE sessions SET last_used_utc = '2020-01-01T00:00:00.000Z';");
        using var second = TestDatabase.Host(data.Path);
        using var restarted = second.CreateClient();
        using (var health = await restarted.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (TestDatabase.Scalar(data.Path, "SELECT CAST(count(*) AS TEXT) FROM sessions;") != "0" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal("0", TestDatabase.Scalar(data.Path, "SELECT CAST(count(*) AS TEXT) FROM sessions;"));
    }

    internal static N8TracksApiFactory WithClock(TestClock clock, IReadOnlyDictionary<string, string>? variables = null) =>
        new(variables ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            TestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            },
        };

    /// <summary>A client that keeps no cookies, so each request carries exactly the cookie the test gives it.</summary>
    internal static HttpClient CookielessClient(N8TracksApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    internal static async Task<HttpResponseMessage> SendWithCookie(HttpClient client, HttpMethod method, Uri uri, string token)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("Cookie", $"{SessionApi.CookieName}={token}");
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");

        return await client.SendAsync(request);
    }

    /// <summary>The real hasher, counting verifications.</summary>
    private sealed class CountingHasher
    {
        private int verifications;

        public int Verifications => Volatile.Read(ref verifications);

        public void Reset() => Volatile.Write(ref verifications, 0);

        public void Register(IServiceCollection services)
        {
            var real = services.Single(service => service.ServiceType == typeof(n8Tracks.Application.Setup.IPasswordHasher));
            services.Remove(real);
            services.AddSingleton<n8Tracks.Application.Setup.IPasswordHasher>(provider =>
                new Counting((n8Tracks.Application.Setup.IPasswordHasher)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), this));
        }

        private sealed class Counting(n8Tracks.Application.Setup.IPasswordHasher inner, CountingHasher owner) : n8Tracks.Application.Setup.IPasswordHasher
        {
            public string Hash(string password) => inner.Hash(password);

            public bool Verify(string encodedHash, string password)
            {
                Interlocked.Increment(ref owner.verifications);
                return inner.Verify(encodedHash, password);
            }
        }
    }
}
