using n8Tracks.Api.Problems;

namespace n8Tracks.Api.Auth;

/// <summary>Marks an endpoint that needs the anti-forgery header without a session: sign-in.</summary>
internal sealed class AntiforgeryHeaderRequired
{
    public static AntiforgeryHeaderRequired Instance { get; } = new();
}

/// <summary>
/// Cross-site request forgery defence: an unsafe request (anything but GET, HEAD, OPTIONS, TRACE)
/// authenticated by the session cookie, or sent to an endpoint marked
/// <see cref="AntiforgeryHeaderRequired"/>, must carry <c>X-N8Tracks-Request: 1</c>. A cross-site
/// form cannot set a custom header, and a cross-site script cannot without a CORS preflight this
/// app never grants. Runs after authorization, so a request without a session is 401 first.
/// Bearer requests (a later story) are exempt: they carry no ambient credential.
/// </summary>
internal sealed class AntiforgeryHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-N8Tracks-Request";
    public const string HeaderValue = "1";
    public const string RequiredCode = "antiforgery_required";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var method = context.Request.Method;
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
        if (safe || !Required(context) || context.Request.Headers[HeaderName] == HeaderValue)
        {
            return next(context);
        }

        return ApiProblem.For(
            context,
            StatusCodes.Status403Forbidden,
            RequiredCode,
            $"This request must carry the {HeaderName} header.").ExecuteAsync(context);
    }

    private static bool Required(HttpContext context) =>
        SessionPrincipal.IsSession(context.User)
        || context.GetEndpoint()?.Metadata.GetMetadata<AntiforgeryHeaderRequired>() is not null;
}
