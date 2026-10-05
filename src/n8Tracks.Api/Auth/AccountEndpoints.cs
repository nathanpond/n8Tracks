using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Auth;

/// <summary>The signed-in administrator's own account. Session-only: a token can never change the password.</summary>
internal static class AccountEndpoints
{
    public const string PasswordPath = ApiProblem.VersionPrefix + "/account/password";

    public static IEndpointRouteBuilder MapAccount(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(PasswordPath, ChangePasswordAsync)
            .WithName("ChangePassword")
            .WithSummary("Changes the administrator's password, given the current one, and ends every other session.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    /// <summary>
    /// 204 when the password was changed. A wrong current password is 422 <c>validation_failed</c>
    /// on <c>currentPassword</c>, never a 401: the session is fine, and a 401 would read as "sign in
    /// again". While the sign-in throttle refuses, 429 <c>sign_in_throttled</c> as sign-in answers.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest? request,
        AccountService accounts,
        N8TracksOptions options,
        TimeProvider time,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var change = new PasswordChange(request?.CurrentPassword, request?.NewPassword, request?.NewPasswordConfirmation);
        var logger = loggers.CreateLogger(typeof(AccountEndpoints));
        var outcome = await accounts.ChangePasswordAsync(
            SessionPrincipal.AdministratorId(context.User),
            SessionPrincipal.IdHash(context.User),
            change,
            cancellationToken);

        switch (outcome)
        {
            case PasswordChangeOutcome.Changed changed:
                logger.LogInformation("Password changed: {SessionCount} other sessions ended", changed.OtherSessionsEnded);
                return TypedResults.NoContent();

            case PasswordChangeOutcome.Invalid invalid:
                if (invalid.Errors.ContainsKey(AccountService.CurrentPasswordField) && request?.CurrentPassword is { Length: > 0 })
                {
                    logger.LogInformation("Password change refused: wrong current password");
                }

                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case PasswordChangeOutcome.Throttled throttled:
                logger.LogWarning("Password change refused: too many failed attempts");
                return SessionEndpoints.Throttled(context, throttled.RetryAtUtc, options.TimeZone, time.GetUtcNow());

            default:
                throw new InvalidOperationException("Unknown password change outcome.");
        }
    }
}

/// <summary>The change-password form. Any field may be missing; none is trimmed.</summary>
internal sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword, string? NewPasswordConfirmation);
