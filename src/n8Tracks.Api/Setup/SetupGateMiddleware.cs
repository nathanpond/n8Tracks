using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Logging;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Setup;

namespace n8Tracks.Api.Setup;

/// <summary>
/// Until the administrator exists, every <c>/api/v1</c> request other than the setup endpoints is
/// answered 503 <c>setup_required</c>. Health, the frontend, and anything outside <c>/api/v1</c> are
/// untouched. Once setup is complete the answer is remembered, so this costs nothing after that.
/// </summary>
internal sealed class SetupGateMiddleware(RequestDelegate next)
{
    public const string SetupRequiredCode = "setup_required";

    private static readonly PathString VersionPrefix = new(ApiProblem.VersionPrefix);
    private static readonly PathString SetupPath = new(SetupEndpoints.SubmitPath);

    public async Task InvokeAsync(HttpContext context, SetupService setup)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(setup);

        // Routing matches in any letter case, so the gate does too.
        var path = context.Request.Path;
        if (!path.StartsWithSegments(VersionPrefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments(SetupPath, StringComparison.OrdinalIgnoreCase)
            || await setup.IsCompleteAsync(context.RequestAborted))
        {
            await next(context);
            return;
        }

        RequestLog.MarkExpectedRefusal(context);
        await ApiProblem.For(
            context,
            StatusCodes.Status503ServiceUnavailable,
            SetupRequiredCode,
            "This instance has not been set up yet. Complete setup first.").ExecuteAsync(context);
    }
}
