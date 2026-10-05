using System.Net;
using System.Text;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Auth;

/// <summary>
/// A request carrying <c>Authorization: Bearer &lt;token&gt;</c> is authenticated as that credential,
/// by the token alone, and may call only what its scopes allow. Credentials are created through the
/// application service, as the management screens will.
/// </summary>
public sealed class BearerAuthenticationTests
{
    [Fact]
    public async Task ATokenHoldingTheScopeCallsTheEndpointAsItsCredential()
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var created = Assert.IsType<CredentialOutcome.Created>(
            await CredentialApi.CreateAsync(factory, new CredentialRequest("  reader  ", CredentialKinds.McpGateway, [CredentialScopes.CatalogRead])));

        using var response = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, created.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("reader", (await SetupApi.JsonAsync(response)).GetProperty("caller").GetString());
    }

    [Fact]
    public async Task ATokenWithoutTheScopeIs403InsufficientScopeNamingIt()
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        // Every scope but the one needed: no write scope implies reading.
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);

        using var response = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token);

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
        Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
    }

    [Fact]
    public async Task ABulkEndpointNeedsTheBulkScopeAsWellAndNamesEveryMissingScope()
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using (var response = await CredentialApi.SendAsync(client, HttpMethod.Post, TestEndpoints.BulkWrite, writer))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal([CredentialScopes.CatalogBulkWrite], Strings(problem.GetProperty("requiredScope")));
        }

        var bulkOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogBulkWrite);
        using (var response = await CredentialApi.SendAsync(client, HttpMethod.Post, TestEndpoints.BulkWrite, bulkOnly))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal([CredentialScopes.SongsWrite], Strings(problem.GetProperty("requiredScope")));
        }

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var response = await CredentialApi.SendAsync(client, HttpMethod.Post, TestEndpoints.BulkWrite, reader))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal([CredentialScopes.SongsWrite, CredentialScopes.CatalogBulkWrite], Strings(problem.GetProperty("requiredScope")));
        }

        // Complement: with both it succeeds.
        var both = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite, CredentialScopes.CatalogBulkWrite);
        using var allowed = await CredentialApi.SendAsync(client, HttpMethod.Post, TestEndpoints.BulkWrite, both);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    /// <summary>A write needs no read scope, and a Bearer request needs no anti-forgery header.</summary>
    [Fact]
    public async Task AWriteTokenWithoutReadGetsTheWrittenRecordWithoutTheAntiforgeryHeader()
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);

        using var response = await CredentialApi.SendAsync(client, HttpMethod.Post, TestEndpoints.Write, token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("A song", (await SetupApi.JsonAsync(response)).GetProperty("title").GetString());

        // Complement: the same call from a session still needs the header.
        using var session = factory.CreateClient();
        using (var signedIn = await SessionApi.SignInAsync(session, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
        }

        using var refused = await SessionApi.SendAsync(session, HttpMethod.Post, TestEndpoints.Write, antiforgery: false);
        await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, AntiforgeryHeaderMiddleware.RequiredCode);
        using var accepted = await SessionApi.SendAsync(session, HttpMethod.Post, TestEndpoints.Write);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task ASessionHoldsEveryScope()
    {
        using var factory = TestEndpoints.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var uri in new[] { TestEndpoints.Write, TestEndpoints.BulkWrite })
        {
            using var response = await SessionApi.SendAsync(client, HttpMethod.Post, uri);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var read = await client.GetAsync(TestEndpoints.Read);
        Assert.Equal(SetupApi.TestUsername, (await SetupApi.JsonAsync(read)).GetProperty("caller").GetString());
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer not-a-token")]
    [InlineData("Bearer n8t_tooShort")]
    [InlineData("Bearer n8t_0000000000000000000000000000000000000000000")]
    [InlineData("Bearer x8t_0000000000000000000000000000000000000000000")]
    [InlineData("Bearer n8t_000000000000000000000000000000000000000000-")]
    [InlineData("Bearer n8t_0000000000000000000000000000000000000000000 extra")]
    [InlineData("Bearertoken")]
    public async Task AMalformedOrUnknownTokenIs401InvalidToken(string authorization)
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        using var response = await CredentialApi.SendRawAsync(client, HttpMethod.Get, TestEndpoints.Read, authorization);

        await SetupApi.ProblemAsync(response, HttpStatusCode.Unauthorized, BearerAuthenticationHandler.InvalidTokenCode);
        Assert.Equal("Bearer error=\"invalid_token\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task ARevokedTokenIs401InvalidToken()
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);

        using (var before = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        TestDatabase.Execute(factory.DataPath, "UPDATE credentials SET revoked_utc = '2026-10-05T00:00:00.000Z';");

        using var after = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token);
        await SetupApi.ProblemAsync(after, HttpStatusCode.Unauthorized, BearerAuthenticationHandler.InvalidTokenCode);
    }

    [Fact]
    public async Task ATokenOnASessionOnlyEndpointIs403SessionRequired()
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        foreach (var (method, uri) in new[]
        {
            (HttpMethod.Get, SessionApi.Session),
            (HttpMethod.Delete, SessionApi.Sessions),
            (HttpMethod.Post, new Uri("/api/v1/account/password", UriKind.Relative)),
        })
        {
            using var response = await CredentialApi.SendAsync(client, method, uri, token);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }
    }

    /// <summary>An endpoint that forgot its marker fails closed: a session may call it, a token never.</summary>
    [Fact]
    public async Task AnUnmarkedEndpointRefusesEveryToken()
    {
        using var factory = TestEndpoints.Host();
        using var session = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        using (var bySession = await session.GetAsync(TestEndpoints.Unmarked))
        {
            Assert.Equal(HttpStatusCode.OK, bySession.StatusCode);
        }

        using var client = factory.CreateClient();
        using var byToken = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Unmarked, token);
        await SetupApi.ProblemAsync(byToken, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
    }

    /// <summary>An invalid Bearer header is 401 even with a valid session cookie: it never falls back to the cookie.</summary>
    [Fact]
    public async Task AValidCookieWithAnInvalidBearerHeaderIs401()
    {
        using var factory = TestEndpoints.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        using (var cookieOnly = await client.GetAsync(TestEndpoints.Read))
        {
            Assert.Equal(HttpStatusCode.OK, cookieOnly.StatusCode);
        }

        using var response = await CredentialApi.SendRawAsync(client, HttpMethod.Get, TestEndpoints.Read, "Bearer n8t_0000000000000000000000000000000000000000000");
        await SetupApi.ProblemAsync(response, HttpStatusCode.Unauthorized, BearerAuthenticationHandler.InvalidTokenCode);

        // A valid token with the cookie is the credential, not the administrator, and holds only its scopes.
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token);
        await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
    }

    [Fact]
    public async Task TheApiNotFoundAnswersAnyValidTokenAndRefusesAnInvalidOne()
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.ArtworkWrite);
        var missing = new Uri("/api/v1/no/such/thing", UriKind.Relative);

        using (var found = await CredentialApi.SendAsync(client, HttpMethod.Get, missing, token))
        {
            await SetupApi.ProblemAsync(found, HttpStatusCode.NotFound, "not_found");
        }

        using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, missing, "n8t_0000000000000000000000000000000000000000000");
        await SetupApi.ProblemAsync(refused, HttpStatusCode.Unauthorized, BearerAuthenticationHandler.InvalidTokenCode);
    }

    /// <summary>
    /// Last used is written on the first use, then at most once a minute, on any request that
    /// presents a valid token: one refused for its scope included.
    /// </summary>
    [Fact]
    public async Task LastUsedIsWrittenAtMostOnceAMinute()
    {
        var clock = new TestClock();
        using var factory = TestEndpoints.Host(clock: clock);
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        Assert.Equal("never", LastUsed(factory));

        await Use(client, TestEndpoints.Read, token, HttpStatusCode.OK);
        Assert.Equal("2026-10-01T09:00:00.000Z", LastUsed(factory));

        clock.Advance(TimeSpan.FromSeconds(59));
        await Use(client, TestEndpoints.Read, token, HttpStatusCode.OK);
        Assert.Equal("2026-10-01T09:00:00.000Z", LastUsed(factory));

        clock.Advance(TimeSpan.FromSeconds(1));
        await Use(client, TestEndpoints.Write, token, HttpStatusCode.Forbidden);
        Assert.Equal("2026-10-01T09:01:00.000Z", LastUsed(factory));
    }

    /// <summary>The database holds the token's SHA-256 hash and never the token.</summary>
    [Fact]
    public async Task TokensAreStoredOnlyAsHashes()
    {
        using var factory = TestEndpoints.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead, CredentialScopes.SongsWrite);

        Assert.Matches("^n8t_[0-9A-Za-z]{43}$", token);
        var row = TestDatabase.Scalar(factory.DataPath, "SELECT token_hash || '|' || kind || '|' || scopes || '|' || revision FROM credentials;");
        Assert.Equal($"{CredentialToken.Hash(token)}|api|catalog.read songs.write|1", row);

        // Nowhere in the database file, its journal, or its write-ahead log is the token.
        var everything = Directory.EnumerateFiles(factory.DataPath, "n8tracks.db*")
            .Select(static path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.Latin1);
                return reader.ReadToEnd();
            });
        Assert.All(everything, static text => Assert.DoesNotContain("n8t_", text, StringComparison.Ordinal));
        Assert.Contains(everything, text => text.Contains(CredentialToken.Hash(token), StringComparison.Ordinal));
    }

    private static string LastUsed(N8TracksApiFactory factory) =>
        TestDatabase.Scalar(factory.DataPath, "SELECT coalesce(last_used_utc, 'never') FROM credentials;");

    private static async Task Use(HttpClient client, Uri uri, string token, HttpStatusCode expected)
    {
        using var response = await CredentialApi.SendAsync(client, uri == TestEndpoints.Read ? HttpMethod.Get : HttpMethod.Post, uri, token);
        Assert.Equal(expected, response.StatusCode);
    }

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(static item => item.GetString())];
}
