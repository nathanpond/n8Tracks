namespace n8Tracks.Gateway.Health;

internal static class HealthEndpoint
{
    /// <summary>
    /// Maps <c>GET /health</c>. Every request probes the upstream afresh; nothing is cached, here or by
    /// the caller. The answer is always 200: a degraded gateway is still running.
    /// </summary>
    public static void MapGatewayHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", static async (
            UpstreamHealthClient upstream,
            UpstreamStateLog stateLog,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var version = ProductVersion.Current;
            var probe = await upstream.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var state = GatewayHealth.StateOf(probe, version);

            stateLog.Report(state, probe, version);

            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Json(GatewayHealth.Respond(state, version));
        });
    }
}
