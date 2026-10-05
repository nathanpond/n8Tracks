using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;

namespace n8Tracks.Api.Tests.Auth;

/// <summary>
/// POST /api/v1/account/password: the current password plus a new one meeting the setup rule; it
/// ends every other session and keeps the current one. Refusals change nothing.
/// </summary>
public sealed class AccountPasswordTests
{
    private const string NewPassword = "a brand new passphrase";

    public static readonly Uri Password = new("/api/v1/account/password", UriKind.Relative);

    [Fact]
    public async Task ChangingThePasswordEndsEveryOtherSessionKeepsThisOneAndOnlyTheNewPasswordSignsIn()
    {
        using var factory = new N8TracksApiFactory();
        using var current = await SessionApi.SignedInClientAsync(factory);
        using var other = await SecondSessionAsync(factory);
        using var third = await SecondSessionAsync(factory);
        var hashBefore = PasswordHash(factory);

        using (var changed = await ChangeAsync(current, SetupApi.TestPassword, NewPassword, NewPassword))
        {
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
            Assert.Equal("no-store", changed.Headers.CacheControl?.ToString());
        }

        Assert.NotEqual(hashBefore, PasswordHash(factory));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT CAST(count(*) AS TEXT) FROM sessions;"));

        using (var still = await current.GetAsync(SessionApi.Session))
        {
            Assert.Equal(HttpStatusCode.OK, still.StatusCode);
        }

        foreach (var client in new[] { other, third })
        {
            using var ended = await client.GetAsync(SessionApi.Session);
            await SetupApi.ProblemAsync(ended, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        using var fresh = factory.CreateClient();
        using (var old = await SessionApi.SignInAsync(fresh, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            await SetupApi.ProblemAsync(old, HttpStatusCode.Unauthorized, "invalid_credentials");
        }

        using (var signedIn = await SessionApi.SignInAsync(fresh, SetupApi.TestUsername, NewPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
        }
    }

    [Theory]
    [InlineData(null, NewPassword, NewPassword, "currentPassword", "Enter your current password.")]
    [InlineData(SetupApi.TestPassword, "too short", "too short", "newPassword", "A password must be at least 12 characters.")]
    [InlineData(SetupApi.TestPassword, null, null, "newPassword", "Enter a password.")]
    [InlineData(SetupApi.TestPassword, NewPassword, "a different passphrase", "newPasswordConfirmation", "The passwords do not match.")]
    [InlineData(SetupApi.TestPassword, NewPassword, null, "newPasswordConfirmation", "Enter the password again.")]
    public async Task AnInvalidChangeIsRefusedWithTheSetupRuleAndNothingChanges(
        string? currentPassword,
        string? newPassword,
        string? confirmation,
        string field,
        string message)
    {
        using var factory = new N8TracksApiFactory();
        using var current = await SessionApi.SignedInClientAsync(factory);
        using var other = await SecondSessionAsync(factory);
        var hashBefore = PasswordHash(factory);

        using var refused = await ChangeAsync(current, currentPassword, newPassword, confirmation);
        var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");

        Assert.Equal([message], problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(error => error.GetString()));
        Assert.Equal(hashBefore, PasswordHash(factory));
        await AssertSignedInAsync(current, other);
    }

    [Fact]
    public async Task AWrongCurrentPasswordIsRefusedNothingChangesAndItCountsTowardTheSignInThrottle()
    {
        using var factory = new N8TracksApiFactory();
        using var current = await SessionApi.SignedInClientAsync(factory);
        using var other = await SecondSessionAsync(factory);
        var hashBefore = PasswordHash(factory);

        using (var refused = await ChangeAsync(current, "not the current password", NewPassword, NewPassword))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
            var errors = problem.GetProperty("errors");
            Assert.Equal("The current password is incorrect.", Assert.Single(errors.GetProperty("currentPassword").EnumerateArray()).GetString());
            Assert.False(errors.TryGetProperty("newPassword", out _));
        }

        Assert.Equal(hashBefore, PasswordHash(factory));
        await AssertSignedInAsync(current, other);

        // Four more wrong guesses reach the sign-in limit: the next attempt, even with the right
        // password, is refused unchecked, and so is the sign-in form.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var wrong = await ChangeAsync(current, "not the current password", NewPassword, NewPassword);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        }

        using (var throttled = await ChangeAsync(current, SetupApi.TestPassword, NewPassword, NewPassword))
        {
            await SetupApi.ProblemAsync(throttled, HttpStatusCode.TooManyRequests, "sign_in_throttled");
            Assert.NotNull(throttled.Headers.RetryAfter);
        }

        using var signInClient = factory.CreateClient();
        using (var signIn = await SessionApi.SignInAsync(signInClient, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            await SetupApi.ProblemAsync(signIn, HttpStatusCode.TooManyRequests, "sign_in_throttled");
        }

        Assert.Equal(hashBefore, PasswordHash(factory));
        await AssertSignedInAsync(current, other);
    }

    [Fact]
    public async Task ABearerTokenIsRefusedWith403SessionRequiredEvenAlongsideAValidCookie()
    {
        using var factory = new N8TracksApiFactory();
        using var current = await SessionApi.SignedInClientAsync(factory);
        var hashBefore = PasswordHash(factory);

        using (var request = ChangeRequest(SetupApi.TestPassword, NewPassword, NewPassword))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "n8t_anything");
            using var refused = await current.SendAsync(request);
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "session_required");
        }

        // Without the cookie too: the token is refused, not merely unauthenticated.
        using (var request = ChangeRequest(SetupApi.TestPassword, NewPassword, NewPassword))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "n8t_anything");
            using var cookieless = factory.CreateClient();
            using var refused = await cookieless.SendAsync(request);
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "session_required");
        }

        // The session endpoints are session-only too.
        foreach (var (method, uri) in new[] { (HttpMethod.Get, SessionApi.Session), (HttpMethod.Delete, SessionApi.Session), (HttpMethod.Delete, SessionApi.Sessions) })
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "n8t_anything");
            using var refused = await current.SendAsync(request);
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "session_required");
        }

        Assert.Equal(hashBefore, PasswordHash(factory));
        using var still = await current.GetAsync(SessionApi.Session);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task WithoutASessionItIs401AndWithoutTheAntiforgeryHeaderItIs403()
    {
        using var factory = new N8TracksApiFactory();
        using var current = await SessionApi.SignedInClientAsync(factory);
        var hashBefore = PasswordHash(factory);

        using var anonymousClient = factory.CreateClient();
        using (var anonymous = await ChangeAsync(anonymousClient, SetupApi.TestPassword, NewPassword, NewPassword))
        {
            await SetupApi.ProblemAsync(anonymous, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        using (var forged = await ChangeAsync(current, SetupApi.TestPassword, NewPassword, NewPassword, antiforgery: false))
        {
            await SetupApi.ProblemAsync(forged, HttpStatusCode.Forbidden, "antiforgery_required");
        }

        Assert.Equal(hashBefore, PasswordHash(factory));
    }

    /// <summary>At Debug, through a refused and an accepted change, none of the three passwords reaches the log.</summary>
    [Fact]
    public async Task NoPasswordOfAChangeReachesTheLog()
    {
        const string wrongSentinel = "sentinel-current-password-a913";
        const string newSentinel = "sentinel-new-password-5c02";
        const string confirmationSentinel = "sentinel-new-confirmation-77de";

        using var factory = new LoggingApiFactory("Debug");
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var (current, confirmation, status) in new[]
        {
            (wrongSentinel, newSentinel, HttpStatusCode.UnprocessableEntity),
            (SetupApi.TestPassword, confirmationSentinel, HttpStatusCode.UnprocessableEntity),
            (SetupApi.TestPassword, newSentinel, HttpStatusCode.NoContent),
        })
        {
            using var response = await ChangeAsync(client, current, newSentinel, confirmation);
            Assert.Equal(status, response.StatusCode);
            await factory.CompletionLine(LoggingApiFactory.RequestId(response));
        }

        var captured = factory.CapturedText;
        Assert.Contains("/api/v1/account/password", captured, StringComparison.Ordinal);
        Assert.Contains("Password changed", captured, StringComparison.Ordinal);
        foreach (var secret in new[] { wrongSentinel, newSentinel, confirmationSentinel, SetupApi.TestPassword, PasswordHash(factory) })
        {
            Assert.DoesNotContain(secret, captured, StringComparison.Ordinal);
        }
    }

    private static HttpRequestMessage ChangeRequest(string? currentPassword, string? newPassword, string? confirmation, bool antiforgery = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Password)
        {
            Content = JsonContent.Create(new { currentPassword, newPassword, newPasswordConfirmation = confirmation }),
        };
        if (antiforgery)
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        }

        return request;
    }

    private static async Task<HttpResponseMessage> ChangeAsync(
        HttpClient client,
        string? currentPassword,
        string? newPassword,
        string? confirmation,
        bool antiforgery = true)
    {
        using var request = ChangeRequest(currentPassword, newPassword, confirmation, antiforgery);
        return await client.SendAsync(request);
    }

    /// <summary>Another browser signed in to the same, already set-up instance.</summary>
    private static async Task<HttpClient> SecondSessionAsync(N8TracksApiFactory factory)
    {
        var client = factory.CreateClient();
        using var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);

        return client;
    }

    private static async Task AssertSignedInAsync(params HttpClient[] clients)
    {
        foreach (var client in clients)
        {
            using var session = await client.GetAsync(SessionApi.Session);
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }
    }

    private static string PasswordHash(N8TracksApiFactory factory) =>
        TestDatabase.Scalar(factory.DataPath, "SELECT password_hash FROM administrators;");
}
