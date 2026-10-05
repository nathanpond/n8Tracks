using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.HttpOverrides;

namespace n8Tracks.Api.Auth;

/// <summary>
/// Puts the app behind the sign-in. Every endpoint needs a session or a credential's token (the
/// fallback policy) unless it is marked <c>AllowAnonymous</c>: health, the setup status and
/// submission, and sign-in. A request carrying <c>Authorization: Bearer</c> is authenticated by the
/// token alone, any other by the session cookie (the selector scheme picks one; they are never
/// combined). What a token may then call is decided by the endpoint's scope marker
/// (<see cref="ScopeMiddleware"/>). Requests that reach no endpoint (the frontend's shell and
/// files, and 404s) are not checked, and neither session nor token is even looked up for them:
/// the shell is served to anyone, and it asks the API who is signed in.
/// </summary>
internal static class AuthenticationSetup
{
    /// <summary>The scheme that forwards to the Bearer handler or the session handler, by the request's headers.</summary>
    public const string SelectorScheme = "SessionOrBearer";

    public static IServiceCollection AddSessionAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, null, static selector => selector.ForwardDefaultSelector = static context =>
                BearerHeader.IsPresent(context.Request) ? BearerAuthenticationHandler.SchemeName : SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null)
            .AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>(BearerAuthenticationHandler.SchemeName, null);

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(SelectorScheme).RequireAuthenticatedUser().Build());
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, EndpointOnlyAuthorizationResultHandler>();

        // The app runs as one container behind the operator's own proxy, whose address is not known
        // in advance, so the scheme and host it forwards are taken from any address. The README says
        // to expose the port only to that proxy or to a trusted network.
        services.Configure<ForwardedHeadersOptions>(static forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
        });

        services.AddHostedService<SessionPurgeService>();
        services.AddSingleton<PasswordResetNotice>();

        return services;
    }

    /// <summary>
    /// The session-only check, authentication, authorization, the scope check, and the anti-forgery check. Call it on the app itself (so the
    /// host does not add its own authentication in front of the path base), inside the path base,
    /// and after the setup gate (before setup there is no one to sign in).
    /// </summary>
    public static IApplicationBuilder UseSessionAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app
            .UseMiddleware<SessionOnlyMiddleware>()
            .UseAuthentication()
            .UseAuthorization()
            .UseMiddleware<ScopeMiddleware>()
            .UseMiddleware<AntiforgeryHeaderMiddleware>();
    }
}

/// <summary>
/// The framework applies the fallback policy to a request that reached no endpoint too. Such a
/// request is the frontend's (its shell or a file) or a 404, and is let through unchecked, and so
/// is routing's own 405 for a known path with another method (it runs nothing). Any other endpoint
/// gets the framework's own answer (the handler's 401 or 403).
/// </summary>
internal sealed class EndpointOnlyAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    /// <summary>The name routing gives the endpoint it answers 405 with; it carries no metadata to tell it by.</summary>
    internal const string MethodNotAllowedEndpointName = "405 HTTP Method Not Supported";

    private readonly AuthorizationMiddlewareResultHandler framework = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);

        return context.GetEndpoint() is null or { DisplayName: MethodNotAllowedEndpointName }
            ? next(context)
            : framework.HandleAsync(next, context, policy, authorizeResult);
    }
}
