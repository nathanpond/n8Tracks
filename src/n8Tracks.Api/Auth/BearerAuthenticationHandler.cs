using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Auth;

/// <summary>
/// Authenticates a request by <c>Authorization: Bearer &lt;token&gt;</c>, as the credential the token
/// belongs to, with its scopes as claims. The selector scheme sends every request carrying a Bearer
/// header here, so such a request is authenticated by the token alone: a malformed, unknown, or
/// revoked token is 401 <c>invalid_token</c>, and a session cookie sent along is never used
/// instead. On an anonymous endpoint the header is ignored.
/// </summary>
/// <remarks>The handler logs nothing, like the session handler: the token must never reach a log.</remarks>
internal sealed class BearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    UrlEncoder encoder,
    CredentialVerifier verifier)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, NullLoggerFactory.Instance, encoder)
{
    public const string SchemeName = "Bearer";

    public const string InvalidTokenCode = "invalid_token";

    /// <summary>One claim of this type for each scope the credential holds.</summary>
    public const string ScopeClaim = "n8tracks:scope";

    /// <summary>The claim holding the credential's kind.</summary>
    public const string KindClaim = "n8tracks:credential_kind";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var endpoint = Context.GetEndpoint();
        if (endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return AuthenticateResult.NoResult();
        }

        var credential = BearerHeader.TryReadToken(Request) is { } token
            ? await verifier.VerifyAsync(token, Context.RequestAborted)
            : null;
        if (credential is null)
        {
            return AuthenticateResult.Fail(InvalidTokenCode);
        }

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, credential.Id.ToString()),
            new(ClaimTypes.Name, credential.Name),
            new(KindClaim, credential.Kind),
            .. credential.Scopes.Select(static scope => new Claim(ScopeClaim, scope)),
        ];

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer error=\"invalid_token\"";

        return ApiProblem.For(Context, StatusCodes.Status401Unauthorized, InvalidTokenCode, "The token is not valid.").ExecuteAsync(Context);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblem.For(Context, StatusCodes.Status403Forbidden, "forbidden", "This is not allowed.").ExecuteAsync(Context);
}

/// <summary>Reads the <c>Authorization: Bearer</c> header.</summary>
internal static class BearerHeader
{
    private const string Scheme = "Bearer";

    /// <summary>Whether any <c>Authorization</c> value starts with <c>Bearer</c>, in any letter case.</summary>
    public static bool IsPresent(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Headers[HeaderNames.Authorization].Any(
            static value => value is not null && value.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The token of the one <c>Authorization</c> value, when it is <c>Bearer</c>, one or more spaces,
    /// and a token; otherwise null (several values included). The token's own shape is checked later.
    /// </summary>
    public static string? TryReadToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var values = request.Headers[HeaderNames.Authorization];
        if (values.Count != 1 || values[0] is not { } value)
        {
            return null;
        }

        value = value.Trim();
        if (value.Length <= Scheme.Length + 1
            || !value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            || value[Scheme.Length] != ' ')
        {
            return null;
        }

        var token = value[(Scheme.Length + 1)..].TrimStart(' ');
        return token.Length == 0 || token.Contains(' ', StringComparison.Ordinal) ? null : token;
    }
}

/// <summary>Reads the credential from the principal the Bearer handler built.</summary>
internal static class CredentialPrincipal
{
    public static bool IsCredential(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true, AuthenticationType: BearerAuthenticationHandler.SchemeName };

    public static HashSet<string> Scopes(ClaimsPrincipal user) =>
        [.. user.FindAll(BearerAuthenticationHandler.ScopeClaim).Select(static claim => claim.Value)];
}
