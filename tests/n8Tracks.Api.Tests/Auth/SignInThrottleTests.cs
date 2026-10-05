using System.Globalization;
using System.Net;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Auth;

namespace n8Tracks.Api.Tests.Auth;

public sealed class SignInThrottleTests
{
    private const string WrongPassword = "not the right password";

    [Fact]
    public async Task TheSixthAttemptWithinTheWindowIsRefusedEvenWithTheRightPasswordAndSucceedsAfterIt()
    {
        var clock = new TestClock();
        using var factory = SessionEndpointTests.WithClock(clock);
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        // Five failures, a minute apart: each is checked and refused as wrong.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await SessionApi.SignInAsync(client, SetupApi.TestUsername, WrongPassword);
            await SetupApi.ProblemAsync(wrong, HttpStatusCode.Unauthorized, "invalid_credentials");
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        // The fifth failure was at 09:04, so attempts are refused until 09:19.
        using (var refused = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.TooManyRequests, "sign_in_throttled");
            Assert.Equal("2026-10-01T09:19:00", DateTime.Parse(problem.GetProperty("retryAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("s", CultureInfo.InvariantCulture));
            Assert.Equal("Too many failed sign-in attempts. Try again at 09:19.", problem.GetProperty("detail").GetString());
            Assert.Equal(TimeSpan.FromMinutes(14), refused.Headers.RetryAfter?.Delta);
            Assert.Null(SessionApi.SessionSetCookie(refused));
        }

        // An attempt during the refusal does not extend it.
        clock.Advance(TimeSpan.FromMinutes(13));
        using (var stillRefused = await SessionApi.SignInAsync(client, SetupApi.TestUsername, WrongPassword))
        {
            var problem = await SetupApi.ProblemAsync(stillRefused, HttpStatusCode.TooManyRequests, "sign_in_throttled");
            Assert.Contains("09:19", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromMinutes(1), stillRefused.Headers.RetryAfter?.Delta);
        }

        // Fifteen minutes after the fifth failure: the right password signs in.
        clock.Advance(TimeSpan.FromMinutes(1));
        using var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
    }

    [Fact]
    public async Task ASuccessfulSignInClearsTheFailureCount()
    {
        var clock = new TestClock();
        using var factory = SessionEndpointTests.WithClock(clock);
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        await FailAsync(client, 4);
        using (var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
        }

        Assert.Empty(TestDatabase.Rows(factory.DataPath, "SELECT key FROM settings WHERE key = 'signIn.throttle';"));

        // Four more failures do not reach five; a fifth does.
        await FailAsync(client, 4);
        using (var stillAllowed = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, stillAllowed.StatusCode);
        }

        await FailAsync(client, 5);
        using var refused = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        await SetupApi.ProblemAsync(refused, HttpStatusCode.TooManyRequests, "sign_in_throttled");
    }

    [Fact]
    public async Task FailuresOlderThanTheWindowAreForgotten()
    {
        var clock = new TestClock();
        using var factory = SessionEndpointTests.WithClock(clock);
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        await FailAsync(client, 4);
        clock.Advance(TimeSpan.FromMinutes(15));
        await FailAsync(client, 4);

        using var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
    }

    [Fact]
    public async Task TheRetryTimeIsGivenInTheConfiguredTimeZoneRoundedUpToTheMinute()
    {
        var clock = new TestClock();
        clock.Advance(TimeSpan.FromSeconds(30));
        using var factory = SessionEndpointTests.WithClock(clock, new Dictionary<string, string>(StringComparer.Ordinal) { ["TZ"] = "America/New_York" });
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        await FailAsync(client, 5);
        using var refused = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.TooManyRequests, "sign_in_throttled");

        // 09:15:30 UTC is 05:15:30 in New York (EDT); the message never says a time that is too early.
        Assert.Equal("Too many failed sign-in attempts. Try again at 05:16.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TheLockoutIsHeldOnlyInTheDatabaseSoARestartSeesItAndDeletingTheRowClearsIt()
    {
        using var data = new TemporaryDirectory();
        using (var first = TestDatabase.Host(data.Path))
        using (var client = first.CreateClient())
        {
            await SetupApi.CompleteAsync(client);
            await FailAsync(client, 5);
        }

        var row = TestDatabase.Scalar(data.Path, "SELECT value FROM settings WHERE key = 'signIn.throttle';");
        Assert.Contains("lockedUntilUtc", row, StringComparison.OrdinalIgnoreCase);

        using var second = TestDatabase.Host(data.Path);
        using var restarted = second.CreateClient();
        using (var refused = await SessionApi.SignInAsync(restarted, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.TooManyRequests, "sign_in_throttled");
        }

        // What the container's reset command does, while the app runs: the next attempt sees it.
        TestDatabase.Execute(data.Path, "DELETE FROM settings WHERE key = 'signIn.throttle';");
        using var signedIn = await SessionApi.SignInAsync(restarted, SetupApi.TestUsername, SetupApi.TestPassword);
        Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
    }

    [Fact]
    public async Task ConcurrentAttemptsAreCountedOneAtATime()
    {
        var clock = new TestClock();
        using var factory = SessionEndpointTests.WithClock(clock);
        using var setupClient = factory.CreateClient();
        await SetupApi.CompleteAsync(setupClient);

        var clients = Enumerable.Range(0, 8).Select(_ => factory.CreateClient()).ToList();
        try
        {
            var responses = await Task.WhenAll(clients.Select(client => SessionApi.SignInAsync(client, SetupApi.TestUsername, WrongPassword)));
            var statuses = responses.Select(response => (int)response.StatusCode).Order().ToList();
            foreach (var response in responses)
            {
                response.Dispose();
            }

            Assert.Equal([401, 401, 401, 401, 401, 429, 429, 429], statuses);
        }
        finally
        {
            clients.ForEach(client => client.Dispose());
        }
    }

    [Fact]
    public void TheStateStartsTheRefusalAtTheFifthFailureAndForgetsOldFailures()
    {
        var start = TestClock.Start;
        var state = SignInThrottleState.Empty;
        for (var minute = 0; minute < 4; minute++)
        {
            state = state.WithFailure(start.AddMinutes(minute));
            Assert.Null(state.RefusedUntil(start.AddMinutes(minute)));
        }

        // The oldest failure leaves the window at 09:15; the fifth at 09:15 is then only the fourth.
        var later = state.WithFailure(start.AddMinutes(15));
        Assert.Null(later.RefusedUntil(start.AddMinutes(15)));
        Assert.Equal(4, later.FailuresUtc.Count);

        var locked = state.WithFailure(start.AddMinutes(10));
        Assert.Equal(start.AddMinutes(25), locked.RefusedUntil(start.AddMinutes(10)));
        Assert.Equal(start.AddMinutes(25), locked.RefusedUntil(start.AddMinutes(24).AddSeconds(59)));
        Assert.Null(locked.RefusedUntil(start.AddMinutes(25)));
        Assert.Empty(locked.FailuresUtc);
        Assert.False(locked.IsClear);
        Assert.True(SignInThrottleState.Empty.IsClear);
    }

    private static async Task FailAsync(HttpClient client, int count)
    {
        for (var attempt = 0; attempt < count; attempt++)
        {
            using var wrong = await SessionApi.SignInAsync(client, SetupApi.TestUsername, WrongPassword);
            await SetupApi.ProblemAsync(wrong, HttpStatusCode.Unauthorized, "invalid_credentials");
        }
    }
}
