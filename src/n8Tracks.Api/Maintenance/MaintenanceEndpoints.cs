using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Logging;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Api.Maintenance;

/// <summary>
/// <c>GET /api/v1/maintenance</c>: whether the instance is in maintenance, the stage, the
/// percentage, and how the last maintenance ended. Anonymous, because during maintenance no one can
/// be signed in; so it says nothing else: no path, no archive name, no error text.
/// </summary>
internal static class MaintenanceEndpoints
{
    public const string StatusPath = ApiProblem.VersionPrefix + "/maintenance";

    public static IEndpointRouteBuilder MapMaintenance(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(StatusPath, GetStatus)
            .WithName("GetMaintenance")
            .WithSummary("Whether the instance is in maintenance, at which stage and percentage, and how the last maintenance ended.")
            .AllowAnonymous()
            .Produces<MaintenanceResponse>(StatusCodes.Status200OK)

            // The maintenance page polls this every second: its completion lines are quiet, like health's.
            .WithMetadata(QuietRequestLogMetadata.Instance);

        return endpoints;
    }

    private static Ok<MaintenanceResponse> GetStatus(MaintenanceMode maintenance, HttpContext context)
    {
        SessionEndpoints.NoStore(context);
        return TypedResults.Ok(MaintenanceResponse.From(maintenance.Current));
    }
}

/// <summary>
/// The maintenance state. <c>stage</c> is <c>validating</c>, <c>safety-backup</c>, <c>replacing</c>,
/// <c>migrating</c>, or <c>finishing</c> (null before any maintenance); <c>outcome</c> is
/// <c>succeeded</c>, <c>failed</c>, or <c>rolled-back</c> (null while active or before any).
/// </summary>
internal sealed record MaintenanceResponse(bool Active, string? Stage, int Percent, string? Outcome)
{
    public static MaintenanceResponse From(MaintenanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new(
            snapshot.Active,
            snapshot.Stage is { } stage ? MaintenanceSnapshot.StageText(stage) : null,
            snapshot.Percent,
            snapshot.Outcome is { } outcome ? MaintenanceSnapshot.OutcomeText(outcome) : null);
    }
}
