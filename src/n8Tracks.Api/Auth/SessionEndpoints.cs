using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Auth;

/// <summary>
/// Signing in and out. Sign-in is the one endpoint open without a session; the others need one.
/// Every answer is <c>no-store</c>.
/// </summary>
internal static class SessionEndpoints
{
    public const string SessionPath = ApiProblem.VersionPrefix + "/session";
    public const string SessionsPath = ApiProblem.VersionPrefix + "/sessions";

    public const string InvalidCredentialsCode = "invalid_credentials";
    public const string ThrottledCode = "sign_in_throttled";

    public static IEndpointRouteBuilder MapSessions(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(SessionPath, SignInAsync)
            .WithName("SignIn")
            .WithSummary("Signs in with the administrator's username and password, and sets the session cookie.")
            .AllowAnonymous()
            .WithMetadata(AntiforgeryHeaderRequired.Instance)
            .Produces<SessionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        endpoints.MapGet(SessionPath, GetSession)
            .WithName("GetSession")
            .WithSummary("The current session: who is signed in and when the session ends unless it is used.")
            .Produces<SessionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        endpoints.MapDelete(SessionPath, SignOutAsync)
            .WithName("SignOut")
            .WithSummary("Ends the current session.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapDelete(SessionsPath, SignOutEverywhereAsync)
            .WithName("SignOutEverywhere")
            .WithSummary("Ends every session of the administrator, in every browser.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<Results<Created<SessionResponse>, ProblemHttpResult>> SignInAsync(
        SignInRequest? request,
        SessionService sessions,
        N8TracksOptions options,
        TimeProvider time,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        NoStore(context);

        var current = SessionPrincipal.IsSession(context.User) ? SessionPrincipal.IdHash(context.User) : null;
        var attempt = new SignInAttempt(request?.Username, request?.Password, current, context.Request.Headers.UserAgent.ToString());
        var logger = loggers.CreateLogger(typeof(SessionEndpoints));

        switch (await sessions.SignInAsync(attempt, cancellationToken))
        {
            case SignInOutcome.SignedIn signedIn:
                SessionCookie.Write(context, signedIn.Token, signedIn.ExpiresUtc);
                logger.LogInformation("Signed in");
                return TypedResults.Created((string?)null, new SessionResponse(signedIn.Username, signedIn.ExpiresUtc.UtcDateTime));

            case SignInOutcome.InvalidCredentials:
                logger.LogInformation("Sign-in refused: wrong username or password");
                return ApiProblem.For(context, StatusCodes.Status401Unauthorized, InvalidCredentialsCode, "The username or password is incorrect.");

            case SignInOutcome.Throttled throttled:
                logger.LogWarning("Sign-in refused: too many failed attempts");
                return Throttled(context, throttled.RetryAtUtc, options.TimeZone, time.GetUtcNow());

            case SignInOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown sign-in outcome.");
        }
    }

    private static Ok<SessionResponse> GetSession(HttpContext context)
    {
        NoStore(context);

        return TypedResults.Ok(new SessionResponse(SessionPrincipal.Username(context.User), SessionPrincipal.ExpiresUtc(context.User)));
    }

    private static async Task<NoContent> SignOutAsync(SessionService sessions, HttpContext context, CancellationToken cancellationToken)
    {
        NoStore(context);

        await sessions.SignOutAsync(SessionPrincipal.IdHash(context.User), cancellationToken);
        SessionCookie.Clear(context);

        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SignOutEverywhereAsync(
        SessionService sessions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        NoStore(context);

        var ended = await sessions.SignOutEverywhereAsync(SessionPrincipal.AdministratorId(context.User), cancellationToken);
        SessionCookie.Clear(context);
        loggers.CreateLogger(typeof(SessionEndpoints)).LogInformation("Signed out everywhere: {SessionCount} sessions ended", ended);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// 429 <c>sign_in_throttled</c>, with <c>Retry-After</c> in whole seconds, <c>retryAt</c> (UTC),
    /// and a message giving the time to retry as a clock time in the configured time zone, rounded
    /// up to the minute so that it is never too early.
    /// </summary>
    private static ProblemHttpResult Throttled(HttpContext context, DateTimeOffset retryAtUtc, TimeZoneInfo timeZone, DateTimeOffset nowUtc)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling((retryAtUtc - nowUtc).TotalSeconds));
        context.Response.Headers[HeaderNames.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);

        var local = TimeZoneInfo.ConvertTime(retryAtUtc, timeZone);
        var minute = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, local.Offset);
        if (minute < local)
        {
            minute = minute.AddMinutes(1);
        }

        var clock = minute.ToString("HH:mm", CultureInfo.InvariantCulture);
        var problem = ApiProblem.For(
            context,
            StatusCodes.Status429TooManyRequests,
            ThrottledCode,
            "Too many failed sign-in attempts.",
            [new("retryAt", retryAtUtc.UtcDateTime)]);
        problem.ProblemDetails.Detail = $"Too many failed sign-in attempts. Try again at {clock}.";

        return problem;
    }

    private static void NoStore(HttpContext context) => context.Response.Headers[HeaderNames.CacheControl] = "no-store";
}

/// <summary>The sign-in form. Either field may be missing; the username's case and surrounding whitespace do not matter.</summary>
internal sealed record SignInRequest(string? Username, string? Password);

/// <summary>Who is signed in, and when the session ends unless it is used again (UTC).</summary>
internal sealed record SessionResponse(string Username, DateTime ExpiresAt);
