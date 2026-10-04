namespace n8Tracks.Api.Endpoints;

internal static class HealthEndpoint
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
            .WithName("GetHealth");

        return endpoints;
    }
}
