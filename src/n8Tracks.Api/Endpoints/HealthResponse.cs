using n8Tracks.Application.Health;

namespace n8Tracks.Api.Endpoints;

/// <summary>The body of <c>GET /health</c>.</summary>
/// <param name="Status">The overall status.</param>
/// <param name="Version">The product version of this build.</param>
/// <param name="TimeZone">The ID of the configured time zone (the <c>TZ</c> setting).</param>
/// <param name="Components">The status of each component, keyed by its name.</param>
internal sealed record HealthResponse(HealthStatus Status, string Version, string TimeZone, HealthComponentsResponse Components)
{
    public static HealthResponse From(HealthReport report, string version, string timeZone)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new HealthResponse(
            report.Status,
            version,
            timeZone,
            new HealthComponentsResponse(report.Application, report.Database, report.Migrations, report.Media));
    }
}

internal sealed record HealthComponentsResponse(
    HealthComponent Application,
    HealthComponent Database,
    MigrationsHealthComponent Migrations,
    HealthComponent Media);
