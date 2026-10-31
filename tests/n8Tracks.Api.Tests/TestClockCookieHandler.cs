using Microsoft.Net.Http.Headers;

namespace n8Tracks.Api.Tests;

/// <summary>
/// The cookie jar of a test client, in place of the test host's own (<c>CookieContainerHandler</c>).
/// That one keeps a <see cref="System.Net.CookieContainer"/>, which judges expiry by the machine's
/// real clock, while the server sets the session cookie's expiry from the host's
/// <see cref="TimeProvider"/>. A test that runs the host on <c>TestClock</c> (October 2026) then lost
/// its cookie once the real date passed the cookie's expiry, and every signed-in request after that
/// was 401. This jar judges expiry by the host's own clock instead (<paramref name="now"/>), so a
/// cookie lives exactly as long as the clock the server used says, whatever the date the suite runs.
/// It keeps cookies by name and path for the one in-memory host, sends a <c>Secure</c> one only over
/// HTTPS, and drops a cookie the server expires (Max-Age of zero or less, or an Expires not after now).
/// </summary>
internal sealed class TestClockCookieHandler(Func<DateTimeOffset> now) : DelegatingHandler
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Name, string Path), Stored> cookies = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = request.RequestUri!;
        var header = CookieHeader(uri);
        if (header.Length > 0)
        {
            request.Headers.Add(HeaderNames.Cookie, header);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues(HeaderNames.SetCookie, out var values)
            && SetCookieHeaderValue.TryParseList([.. values], out var parsed))
        {
            Store(uri, parsed);
        }

        return response;
    }

    private string CookieHeader(Uri uri)
    {
        var current = now();
        lock (gate)
        {
            foreach (var expired in cookies.Where(cookie => cookie.Value.Expires is { } at && at <= current).Select(cookie => cookie.Key).ToList())
            {
                cookies.Remove(expired);
            }

            return string.Join(
                "; ",
                cookies
                    .Where(cookie => PathMatches(uri.AbsolutePath, cookie.Key.Path) && (!cookie.Value.Secure || uri.Scheme == Uri.UriSchemeHttps))
                    .OrderByDescending(cookie => cookie.Key.Path.Length)
                    .Select(cookie => $"{cookie.Key.Name}={cookie.Value.Value}"));
        }
    }

    private void Store(Uri uri, IList<SetCookieHeaderValue> parsed)
    {
        var current = now();
        lock (gate)
        {
            foreach (var cookie in parsed)
            {
                var key = (cookie.Name.ToString(), cookie.Path.HasValue ? cookie.Path.ToString() : DefaultPath(uri.AbsolutePath));
                DateTimeOffset? expires = cookie.MaxAge is { } maxAge ? current + maxAge : cookie.Expires;
                if (expires is { } at && at <= current)
                {
                    cookies.Remove(key);
                }
                else
                {
                    cookies[key] = new Stored(cookie.Value.ToString(), expires, cookie.Secure);
                }
            }
        }
    }

    /// <summary>RFC 6265 5.1.4.</summary>
    private static bool PathMatches(string requestPath, string cookiePath) =>
        requestPath.Equals(cookiePath, StringComparison.Ordinal)
        || (requestPath.StartsWith(cookiePath, StringComparison.Ordinal)
            && (cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/'));

    /// <summary>RFC 6265 5.1.4: the request path up to its last slash.</summary>
    private static string DefaultPath(string requestPath)
    {
        var last = requestPath.LastIndexOf('/');
        return last <= 0 ? "/" : requestPath[..last];
    }

    private sealed record Stored(string Value, DateTimeOffset? Expires, bool Secure);
}
