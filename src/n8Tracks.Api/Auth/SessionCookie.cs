using Microsoft.Net.Http.Headers;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Auth;

/// <summary>
/// The session cookie, <c>n8tracks_session</c>: the session identifier and nothing else. It is
/// HTTP-only, SameSite=Lax, scoped to the base path, persistent with the session's expiry, and
/// Secure whenever the request arrived over HTTPS (directly, or as the proxy's
/// <c>X-Forwarded-Proto</c> says, which forwarded-headers handling has applied by now).
/// </summary>
internal static class SessionCookie
{
    public const string Name = "n8tracks_session";

    /// <summary>At most this many cookies of the name are tried, whatever the request carries.</summary>
    private const int MaximumCandidates = 4;

    /// <summary>
    /// Every value of the cookie the request carries, in order. A browser can send more than one: a
    /// second instance on the same host under another path has a cookie of the same name.
    /// </summary>
    public static IReadOnlyList<string> Values(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!CookieHeaderValue.TryParseList(request.Headers.Cookie, out var cookies))
        {
            return [];
        }

        return [.. cookies
            .Where(static cookie => cookie.Name.Equals(Name, StringComparison.Ordinal))
            .Select(static cookie => cookie.Value.ToString())
            .Take(MaximumCandidates)];
    }

    /// <summary>Sets the cookie to <paramref name="token"/>, expiring at <paramref name="expiresUtc"/>.</summary>
    public static void Write(HttpContext context, string token, DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Append(Name, token, Options(context, expiresUtc));
    }

    /// <summary>Tells the browser to drop the cookie.</summary>
    public static void Clear(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Delete(Name, Options(context, DateTimeOffset.UnixEpoch));
    }

    private static CookieOptions Options(HttpContext context, DateTimeOffset expiresUtc)
    {
        var pathBase = context.RequestServices.GetRequiredService<N8TracksOptions>().PathBase;

        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = pathBase.Length == 0 ? "/" : pathBase,
            Expires = expiresUtc,
            IsEssential = true,
        };
    }
}
