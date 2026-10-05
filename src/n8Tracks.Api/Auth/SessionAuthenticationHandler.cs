using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Auth;

namespace n8Tracks.Api.Auth;

/// <summary>
/// Authenticates a request by the session cookie: the session it names must exist and have been
/// used within 30 days. A use that moves the expiry issues the cookie again with the new expiry.
/// A request without a valid session is answered 401 <c>not_authenticated</c> where one is needed;
/// an expired session is not told apart from a missing one.
/// </summary>
/// <remarks>
/// The base class would log under this class's name, which is held to the configured level like the
/// app's own lines, so every request would add "not authenticated" and "challenged" lines. Sign-in
/// and sign-out write their own lines; the handler writes none.
/// </remarks>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    UrlEncoder encoder,
    SessionService sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, NullLoggerFactory.Instance, encoder)
{
    public const string SchemeName = "Session";

    public const string NotAuthenticatedCode = "not_authenticated";

    /// <summary>The claim holding the stored hash of the session identifier, never the identifier.</summary>
    public const string SessionIdHashClaim = "n8tracks:session_id_hash";

    /// <summary>The claim holding the session's expiry, UTC ISO 8601.</summary>
    public const string ExpiresClaim = "n8tracks:session_expires";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The shell and its files need no session, so the database is not asked about one.
        if (Context.GetEndpoint() is null)
        {
            return AuthenticateResult.NoResult();
        }

        foreach (var token in SessionCookie.Values(Request))
        {
            var session = await sessions.AuthenticateAsync(token, Context.RequestAborted);
            if (session is null)
            {
                continue;
            }

            if (session.Extended)
            {
                SessionCookie.Write(Context, token, session.ExpiresUtc);
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, session.AdministratorId.ToString()),
                    new Claim(ClaimTypes.Name, session.Username),
                    new Claim(SessionIdHashClaim, session.IdHash),
                    new Claim(ExpiresClaim, session.ExpiresUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ],
                SchemeName);

            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }

        return AuthenticateResult.NoResult();
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiProblem.For(Context, StatusCodes.Status401Unauthorized, NotAuthenticatedCode, "Sign in to continue.").ExecuteAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblem.For(Context, StatusCodes.Status403Forbidden, "forbidden", "This is not allowed.").ExecuteAsync(Context);
}

/// <summary>Reads the signed-in session from the principal the handler built.</summary>
internal static class SessionPrincipal
{
    public static bool IsSession(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true, AuthenticationType: SessionAuthenticationHandler.SchemeName };

    public static string IdHash(ClaimsPrincipal user) => Required(user, SessionAuthenticationHandler.SessionIdHashClaim);

    public static Guid AdministratorId(ClaimsPrincipal user) => Guid.Parse(Required(user, ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture);

    public static string Username(ClaimsPrincipal user) => Required(user, ClaimTypes.Name);

    public static DateTime ExpiresUtc(ClaimsPrincipal user) =>
        DateTime.Parse(Required(user, SessionAuthenticationHandler.ExpiresClaim), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Required(ClaimsPrincipal user, string type) =>
        user.FindFirst(type)?.Value ?? throw new InvalidOperationException($"The principal has no {type} claim.");
}
