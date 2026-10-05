using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Auth;

/// <summary>
/// Managing credentials through the API, as the Credentials screen does: create (the token shown
/// once), list (never a token), rename (revision-checked), and revoke (final). A token can never
/// manage credentials, sign out, or change the password, whatever its scopes.
/// </summary>
public sealed class CredentialEndpointTests
{
    private static readonly Uri Credentials = new("/api/v1/credentials", UriKind.Relative);
    private static readonly Uri Password = new("/api/v1/account/password", UriKind.Relative);

    [Fact]
    public async Task CreateAnswersWithTheTokenOnceAndTheListNeverHoldsIt()
    {
        using var factory = TestEndpoints.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var created = await CreateAsync(client, "  test script  ", "api", ["catalog.read"]);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.Tag);
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var body = await SetupApi.JsonAsync(created);
        var id = body.GetProperty("id").GetString()!;
        var token = body.GetProperty("token").GetString()!;
        Assert.Matches("^n8t_[0-9A-Za-z]{43}$", token);
        Assert.Equal("test script", body.GetProperty("name").GetString());
        Assert.Equal("api", body.GetProperty("kind").GetString());
        Assert.Equal(["catalog.read"], body.GetProperty("scopes").EnumerateArray().Select(static scope => scope.GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastUsedUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("revokedUtc").ValueKind);
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
        Assert.Equal(7, Guid.Parse(id).Version);
        Assert.EndsWith("/api/v1/credentials/" + id, created.Headers.Location?.OriginalString, StringComparison.Ordinal);

        // The token calls what its scope allows.
        using (var used = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token))
        {
            Assert.Equal(HttpStatusCode.OK, used.StatusCode);
        }

        using var list = await client.GetAsync(Credentials);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var text = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token[4..], text, StringComparison.Ordinal);
        Assert.DoesNotContain(CredentialToken.Hash(token), text, StringComparison.Ordinal);
        var shown = Assert.Single(JsonSerializer.Deserialize<JsonElement>(text).EnumerateArray());
        Assert.False(shown.TryGetProperty("token", out _));
        Assert.False(shown.TryGetProperty("tokenHash", out _));
        Assert.Equal(id, shown.GetProperty("id").GetString());
        Assert.Equal("test script", shown.GetProperty("name").GetString());
        Assert.NotEqual(JsonValueKind.Null, shown.GetProperty("lastUsedUtc").ValueKind);
        Assert.EndsWith("Z", shown.GetProperty("createdUtc").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACredentialNeedsAValidNameKindAndScopesAndAFreeName()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var empty = await SendJsonAsync(client, HttpMethod.Post, Credentials, new { }))
        {
            var problem = await SetupApi.ProblemAsync(empty, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal(["kind", "name", "scopes"], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name).Order(StringComparer.Ordinal));
        }

        using (var unknownScope = await CreateAsync(client, "script", "api", ["catalog.write"]))
        {
            var problem = await SetupApi.ProblemAsync(unknownScope, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("scopes", out _));
        }

        using (var first = await CreateAsync(client, "Script", "api", ["catalog.read"]))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        using (var taken = await CreateAsync(client, " SCRIPT ", "extension", ["songs.write"]))
        {
            var problem = await SetupApi.ProblemAsync(taken, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal(CredentialService.NameTakenMessage, problem.GetProperty("errors").GetProperty("name")[0].GetString());
        }

        // Complement: the one credential is all that was stored.
        Assert.Single((await ListAsync(client)).EnumerateArray());
    }

    [Fact]
    public async Task RevokingRefusesTheTokenOnTheVeryNextRequestAndCannotBeUndone()
    {
        var clock = new TestClock();
        using var factory = TestEndpoints.Host(clock: clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (id, token) = await CreatedAsync(client, "test script", "api", ["catalog.read"]);

        using (var before = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using (var revoked = await RevokeAsync(client, id))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            Assert.Equal("\"2\"", revoked.Headers.ETag?.Tag);
            var body = await SetupApi.JsonAsync(revoked);
            Assert.Equal("2026-10-01T09:00:00Z", body.GetProperty("revokedUtc").GetString());
            Assert.Equal(2, body.GetProperty("revision").GetInt32());
        }

        using (var after = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token))
        {
            await SetupApi.ProblemAsync(after, HttpStatusCode.Unauthorized, BearerAuthenticationHandler.InvalidTokenCode);
        }

        // Revoking again changes nothing: the first revocation time stays, and so does the refusal.
        clock.Advance(TimeSpan.FromHours(1));
        using (var again = await RevokeAsync(client, id))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal("2026-10-01T09:00:00Z", (await SetupApi.JsonAsync(again)).GetProperty("revokedUtc").GetString());
        }

        // The revoked credential stays listed, marked with its revocation date, behind those in force.
        var (_, other) = await CreatedAsync(client, "test script", "api", ["catalog.read"]);
        var list = (await ListAsync(client)).EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("revokedUtc").ValueKind);
        Assert.Equal(id, list[1].GetProperty("id").GetString());
        Assert.Equal("2026-10-01T09:00:00Z", list[1].GetProperty("revokedUtc").GetString());

        using (var stillRefused = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stillRefused.StatusCode);
        }

        // Complement: the new credential with the freed name works.
        using var works = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, other);
        Assert.Equal(HttpStatusCode.OK, works.StatusCode);
    }

    [Fact]
    public async Task RevokingOrRenamingAnUnknownCredentialIs404()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var unknown = Guid.CreateVersion7();

        using (var revoke = await RevokeAsync(client, unknown.ToString()))
        {
            await SetupApi.ProblemAsync(revoke, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using var rename = await RenameAsync(client, unknown.ToString(), "\"1\"", "x");
        await SetupApi.ProblemAsync(rename, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task RenamingChecksTheRevisionTheNameAndThatTheCredentialIsInForce()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (id, _) = await CreatedAsync(client, "one", "api", ["catalog.read"]);
        await CreatedAsync(client, "two", "extension", ["catalog.read"]);

        using (var missing = await RenameAsync(client, id, ifMatch: null, "uno"))
        {
            await SetupApi.ProblemAsync(missing, (HttpStatusCode)428, Revisions.RequiredCode);
        }

        using (var renamed = await RenameAsync(client, id, "\"1\"", "  uno  "))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.Equal("\"2\"", renamed.Headers.ETag?.Tag);
            var body = await SetupApi.JsonAsync(renamed);
            Assert.Equal("uno", body.GetProperty("name").GetString());
            Assert.Equal(2, body.GetProperty("revision").GetInt32());
            Assert.Equal("api", body.GetProperty("kind").GetString());
        }

        using (var stale = await RenameAsync(client, id, "\"1\"", "eins"))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
            Assert.Equal("uno", problem.GetProperty("current").GetProperty("name").GetString());
            Assert.Equal(2, problem.GetProperty("current").GetProperty("revision").GetInt32());
            Assert.False(problem.GetProperty("current").TryGetProperty("token", out _));
        }

        using (var taken = await RenameAsync(client, id, "\"2\"", "TWO"))
        {
            var problem = await SetupApi.ProblemAsync(taken, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal(CredentialService.NameTakenMessage, problem.GetProperty("errors").GetProperty("name")[0].GetString());
        }

        using (var tooLong = await RenameAsync(client, id, "\"2\"", new string('a', 101)))
        {
            await SetupApi.ProblemAsync(tooLong, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        }

        using (var revoke = await RevokeAsync(client, id))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }

        using var revoked = await RenameAsync(client, id, "\"3\"", "again");
        var refused = await SetupApi.ProblemAsync(revoked, HttpStatusCode.Conflict, CredentialEndpoints.CredentialRevokedCode);
        Assert.Equal("uno", refused.GetProperty("current").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"\"")]
    [InlineData("\"0\"")]
    [InlineData("\"01\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"x\"")]
    [InlineData("W/\"1\"")]
    [InlineData("*")]
    [InlineData("\"1\", \"2\"")]
    [InlineData("\"99999999999\"")]
    public async Task AnIfMatchThatIsNotOneQuotedRevisionIs400InvalidRevision(string ifMatch)
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (id, _) = await CreatedAsync(client, "one", "api", ["catalog.read"]);

        using var response = await RenameAsync(client, id, ifMatch, "uno");

        await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, Revisions.InvalidCode);
    }

    [Fact]
    public async Task TheListFiltersByKindAndRefusesAnUnknownKind()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CreatedAsync(client, "script", "api", ["catalog.read"]);
        await CreatedAsync(client, "browser", "extension", ["catalog.read"]);
        await CreatedAsync(client, "gateway", "mcp-gateway", ["catalog.read"]);

        using (var extensions = await client.GetAsync(new Uri("/api/v1/credentials?kind=extension", UriKind.Relative)))
        {
            var only = Assert.Single((await SetupApi.JsonAsync(extensions)).EnumerateArray());
            Assert.Equal("browser", only.GetProperty("name").GetString());
        }

        Assert.Equal(["gateway", "browser", "script"], (await ListAsync(client)).EnumerateArray().Select(static item => item.GetProperty("name").GetString()));

        using var unknown = await client.GetAsync(new Uri("/api/v1/credentials?kind=API", UriKind.Relative));
        await SetupApi.ProblemAsync(unknown, HttpStatusCode.BadRequest, ApiProblem.InvalidRequestCode);
    }

    /// <summary>
    /// Every endpoint that manages credentials, ends sessions, or changes the password refuses a
    /// token holding every scope with 403 <c>session_required</c>, and a session cookie sent along
    /// does not change that.
    /// </summary>
    [Fact]
    public async Task ATokenCanNeverManageCredentialsSignOutOrChangeThePassword()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (id, token) = await CreatedAsync(client, "everything", "api", [.. CredentialScopes.All]);

        foreach (var (method, uri) in new[]
        {
            (HttpMethod.Get, Credentials),
            (HttpMethod.Post, Credentials),
            (HttpMethod.Patch, new Uri($"/api/v1/credentials/{id}", UriKind.Relative)),
            (HttpMethod.Post, new Uri($"/api/v1/credentials/{id}/revoke", UriKind.Relative)),
            (HttpMethod.Delete, SessionApi.Session),
            (HttpMethod.Delete, SessionApi.Sessions),
            (HttpMethod.Get, SessionApi.Session),
            (HttpMethod.Post, Password),
        })
        {
            // The client holds the session cookie too: the token decides, and is refused.
            using var response = await CredentialApi.SendAsync(client, method, uri, token);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        // Complement: nothing was revoked or created, and the session still works.
        var only = Assert.Single((await ListAsync(client)).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, only.GetProperty("revokedUtc").ValueKind);
    }

    [Fact]
    public async Task ManagingCredentialsFromTheBrowserNeedsTheAntiforgeryHeaderAndASession()
    {
        using var factory = new N8TracksApiFactory();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var forged = await SendJsonAsync(client, HttpMethod.Post, Credentials, new { name = "x", kind = "api", scopes = new[] { "catalog.read" } }, antiforgery: false))
        {
            await SetupApi.ProblemAsync(forged, HttpStatusCode.Forbidden, AntiforgeryHeaderMiddleware.RequiredCode);
        }

        using var anonymous = factory.CreateClient();
        using var signedOut = await anonymous.GetAsync(Credentials);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);

        Assert.Empty((await ListAsync(client)).EnumerateArray());
    }

    [Fact]
    public async Task CredentialsKeepWorkingThroughAPasswordChangeAndAReset()
    {
        using var factory = TestEndpoints.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (_, token) = await CreatedAsync(client, "script", "api", ["catalog.read"]);

        const string newPassword = "a brand new password";
        using (var change = await SendJsonAsync(client, HttpMethod.Post, Password, new { currentPassword = SetupApi.TestPassword, newPassword, newPasswordConfirmation = newPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        }

        using (var afterChange = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token))
        {
            Assert.Equal(HttpStatusCode.OK, afterChange.StatusCode);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var reset = await scope.ServiceProvider.GetRequiredService<AccountService>()
                .ResetPasswordAsync("a reset password here", "a reset password here", CancellationToken.None);
            Assert.IsType<PasswordResetOutcome.Reset>(reset);
        }

        using (var afterReset = await CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token))
        {
            Assert.Equal(HttpStatusCode.OK, afterReset.StatusCode);
        }

        // Complement: the reset did end the browser session.
        using var session = await client.GetAsync(SessionApi.Session);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, string kind, string[] scopes) =>
        SendJsonAsync(client, HttpMethod.Post, Credentials, new { name, kind, scopes });

    private static async Task<(string Id, string Token)> CreatedAsync(HttpClient client, string name, string kind, string[] scopes)
    {
        using var response = await CreateAsync(client, name, kind, scopes);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await SetupApi.JsonAsync(response);

        return (body.GetProperty("id").GetString()!, body.GetProperty("token").GetString()!);
    }

    private static async Task<JsonElement> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Credentials);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await SetupApi.JsonAsync(response);
    }

    private static Task<HttpResponseMessage> RevokeAsync(HttpClient client, string id) =>
        SessionApi.SendAsync(client, HttpMethod.Post, new Uri($"/api/v1/credentials/{id}/revoke", UriKind.Relative));

    private static async Task<HttpResponseMessage> RenameAsync(HttpClient client, string id, string? ifMatch, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/credentials/{id}", UriKind.Relative))
        {
            Content = JsonContent.Create(new { name }),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, Uri uri, object body, bool antiforgery = true)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body) };
        if (antiforgery)
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await client.SendAsync(request);
    }
}
