using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using n8Tracks.Api.Logging;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Health;

namespace n8Tracks.Api.Endpoints;

internal static class HealthEndpoint
{
    public const string Path = "/health";

    /// <summary>
    /// Maps the health report, for operators, the shell page, and the container health check. It is
    /// open to anyone, so it says nothing about where things are: no paths, no connection strings,
    /// no exception text. 200 while the app can do its job (healthy or degraded), 503 when it cannot.
    /// </summary>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMethods(Path, [HttpMethods.Get, HttpMethods.Head], GetHealthAsync)
            .WithName("GetHealth")
            .WithSummary("Reports the health of the application, the database, the schema, and the media mount.")
            .AllowAnonymous()
            .Produces<HealthResponse>(StatusCodes.Status200OK)
            .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable)
            .WithMetadata(QuietRequestLogMetadata.Instance);

        return endpoints;
    }

    private static async Task<JsonHttpResult<HealthResponse>> GetHealthAsync(
        IHealthService health,
        N8TracksOptions options,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var report = await health.GetReportAsync(cancellationToken);

        response.Headers[HeaderNames.CacheControl] = "no-store";

        return TypedResults.Json(
            HealthResponse.From(report, ProductVersion.Current, options.TimeZone.Id),
            statusCode: report.Status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
    }
}
