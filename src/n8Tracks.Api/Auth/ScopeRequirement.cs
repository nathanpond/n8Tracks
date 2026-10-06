using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Auth;

/// <summary>
/// Marks an endpoint a credential may call when it holds every one of <see cref="Scopes"/>. A
/// browser session holds every scope. Scopes do not imply one another.
/// </summary>
internal sealed class RequiredScopes
{
    public RequiredScopes(IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("An endpoint must require at least one scope; mark it SessionOnly() or AllowAnonymous() instead.", nameof(scopes));
        }

        if (scopes.FirstOrDefault(static scope => !CredentialScopes.IsKnown(scope)) is { } unknown)
        {
            throw new ArgumentException($"'{unknown}' is not a scope.", nameof(scopes));
        }

        Scopes = [.. scopes.Distinct(StringComparer.Ordinal)];
    }

    public IReadOnlyList<string> Scopes { get; }
}

/// <summary>
/// Marks an endpoint only a browser session may call: the session itself, the account's password,
/// and anything else no scope covers (credentials, backups, settings, workflow-state management).
/// A token caller is refused it whatever its scopes.
/// </summary>
internal sealed class SessionOnlyEndpoint
{
    public static SessionOnlyEndpoint Instance { get; } = new();
}

/// <summary>
/// Marks the two endpoints any authenticated caller reaches whatever its scopes: the API's own 404
/// for a path nothing else matched, which does nothing but say so, and the extension handshake,
/// which tells a token's holder only about that token. A guard test names both.
/// </summary>
internal sealed class AnyCallerEndpoint
{
    public static AnyCallerEndpoint Instance { get; } = new();
}

/// <summary>
/// The markers. Every <c>/api/v1</c> endpoint carries exactly one: <c>RequireScope(...)</c>,
/// <c>SessionOnly()</c>, or <c>AllowAnonymous()</c> (a test enumerates them). An endpoint with
/// none refuses every token, so a forgotten marker fails closed.
/// </summary>
internal static class ScopeRequirementExtensions
{
    /// <summary>A credential needs every one of <paramref name="scopes"/>; a session needs none.</summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, params string[] scopes)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredScopes(scopes));

    /// <summary>Only a browser session may call it; a token is refused with 403 <c>session_required</c>.</summary>
    public static TBuilder SessionOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(SessionOnlyEndpoint.Instance);

    /// <summary>Any authenticated caller, whatever its scopes. For the API's 404 fallback and the extension handshake only.</summary>
    internal static TBuilder AnyCaller<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(AnyCallerEndpoint.Instance);
}

/// <summary>
/// Refuses a request to a <see cref="SessionOnlyEndpoint"/> that carries
/// <c>Authorization: Bearer</c> with 403 <c>session_required</c>, before authentication: a token
/// is never accepted there, and a cookie sent along with it is not used instead. Runs first, so
/// a token caller learns why without a session's 401 hiding it.
/// </summary>
internal sealed class SessionOnlyMiddleware(RequestDelegate next)
{
    public const string RequiredCode = "session_required";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint()?.Metadata.GetMetadata<SessionOnlyEndpoint>() is null || !BearerHeader.IsPresent(context.Request))
        {
            return next(context);
        }

        return SessionRequired(context).ExecuteAsync(context);
    }

    internal static IResult SessionRequired(HttpContext context) =>
        ApiProblem.For(
            context,
            StatusCodes.Status403Forbidden,
            RequiredCode,
            "This can only be done from a signed-in browser session.");
}

/// <summary>
/// Enforces the scope markers for a request authenticated by a credential, after authorization
/// (which has already refused a request with no valid session or token). A session passes
/// whatever the endpoint requires. A credential missing a scope the endpoint requires gets 403
/// <c>insufficient_scope</c> with <c>requiredScope</c>: the scope, or for an endpoint that requires
/// several, the list of those missing. A credential reaching an endpoint with no scope marker gets
/// 403 <c>session_required</c>.
/// </summary>
internal sealed class ScopeMiddleware(RequestDelegate next)
{
    public const string InsufficientScopeCode = "insufficient_scope";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();
        if (endpoint is null || !CredentialPrincipal.IsCredential(context.User))
        {
            return next(context);
        }

        var metadata = endpoint.Metadata;
        if (metadata.GetMetadata<AnyCallerEndpoint>() is not null)
        {
            return next(context);
        }

        if (metadata.GetMetadata<RequiredScopes>() is not { } required)
        {
            return SessionOnlyMiddleware.SessionRequired(context).ExecuteAsync(context);
        }

        var held = CredentialPrincipal.Scopes(context.User);
        var missing = required.Scopes.Where(scope => !held.Contains(scope)).ToList();
        if (missing.Count == 0)
        {
            return next(context);
        }

        object requiredScope = required.Scopes.Count == 1 ? missing[0] : missing;
        return ApiProblem.For(
            context,
            StatusCodes.Status403Forbidden,
            InsufficientScopeCode,
            $"This credential needs the scope {string.Join(" and ", missing)}.",
            [new("requiredScope", requiredScope)]).ExecuteAsync(context);
    }

    /// <summary>
    /// For a part of a request that needs more than the endpoint's marker (a Song edit that changes
    /// the artwork also needs <c>artwork.write</c>): 403 <c>insufficient_scope</c> when a credential
    /// lacks <paramref name="scope"/>, as the marker's own refusal is; null for a session, or a
    /// credential that holds it.
    /// </summary>
    internal static ProblemHttpResult? Lacking(HttpContext context, string scope)
    {
        ArgumentNullException.ThrowIfNull(context);

        return !CredentialPrincipal.IsCredential(context.User) || CredentialPrincipal.Scopes(context.User).Contains(scope)
            ? null
            : ApiProblem.For(
                context,
                StatusCodes.Status403Forbidden,
                InsufficientScopeCode,
                $"This credential needs the scope {scope}.",
                [new("requiredScope", scope)]);
    }
}
