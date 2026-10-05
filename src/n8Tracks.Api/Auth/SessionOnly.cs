using Microsoft.Net.Http.Headers;
using n8Tracks.Api.Problems;

namespace n8Tracks.Api.Auth;

/// <summary>
/// Marks an endpoint only a browser session may call: the session itself, and the account's
/// password. A token caller is refused it whatever its scopes (the scope markers of the bearer
/// story build on this).
/// </summary>
internal sealed class SessionOnlyEndpoint
{
    public static SessionOnlyEndpoint Instance { get; } = new();
}

/// <summary>Marks endpoints <see cref="SessionOnlyEndpoint"/>.</summary>
internal static class SessionOnlyExtensions
{
    public static TBuilder SessionOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(SessionOnlyEndpoint.Instance);
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

        if (context.GetEndpoint()?.Metadata.GetMetadata<SessionOnlyEndpoint>() is null || !CarriesBearer(context.Request))
        {
            return next(context);
        }

        return ApiProblem.For(
            context,
            StatusCodes.Status403Forbidden,
            RequiredCode,
            "This can only be done from a signed-in browser session.").ExecuteAsync(context);
    }

    private static bool CarriesBearer(HttpRequest request) =>
        request.Headers[HeaderNames.Authorization].Any(
            static value => value is not null && value.TrimStart().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase));
}
