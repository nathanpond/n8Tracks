using System.Net;
using System.Net.Http.Json;
using n8Tracks.Api.Tests.Setup;

namespace n8Tracks.Api.Tests.Auth;

/// <summary>
/// Calls the session endpoints the way the web UI does. Later stories use
/// <see cref="SignedInClientAsync"/> for a client past setup and signed in.
/// </summary>
internal static class SessionApi
{
    public const string CookieName = "n8tracks_session";
    public const string AntiforgeryHeader = "X-N8Tracks-Request";

    public static readonly Uri Session = new("/api/v1/session", UriKind.Relative);
    public static readonly Uri Sessions = new("/api/v1/sessions", UriKind.Relative);

    /// <summary>Completes setup, signs in with the test administrator, and returns the client holding the cookie.</summary>
    public static async Task<HttpClient> SignedInClientAsync(N8TracksApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        using var response = await SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return client;
    }

    /// <summary>Signs in, with the anti-forgery header unless <paramref name="antiforgery"/> is false.</summary>
    public static Task<HttpResponseMessage> SignInAsync(HttpClient client, string? username, string? password, bool antiforgery = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Session) { Content = JsonContent.Create(new { username, password }) };
        if (antiforgery)
        {
            request.Headers.Add(AntiforgeryHeader, "1");
        }

        return SendAndDisposeAsync(client, request);
    }

    /// <summary>A request to <paramref name="uri"/> with the anti-forgery header, as the web UI sends an unsafe one.</summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, bool antiforgery = true)
    {
        var request = new HttpRequestMessage(method, uri);
        if (antiforgery)
        {
            request.Headers.Add(AntiforgeryHeader, "1");
        }

        return SendAndDisposeAsync(client, request);
    }

    /// <summary>The <c>Set-Cookie</c> header for the session cookie, or null.</summary>
    public static string? SessionSetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.SingleOrDefault(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal))
            : null;

    /// <summary>The value of the session cookie a response set, or null.</summary>
    public static string? SessionToken(HttpResponseMessage response) =>
        SessionSetCookie(response) is { } header ? header[(CookieName.Length + 1)..header.IndexOf(';', StringComparison.Ordinal)] : null;

    private static async Task<HttpResponseMessage> SendAndDisposeAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            return await client.SendAsync(request);
        }
    }
}

/// <summary>A clock the test moves by hand. It starts at <see cref="Start"/>.</summary>
internal sealed class TestClock : TimeProvider
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private DateTimeOffset now = Start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
