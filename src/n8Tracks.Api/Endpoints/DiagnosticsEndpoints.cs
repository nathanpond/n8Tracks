using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Search;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The Diagnostics area's API. Every endpoint is session-only: no credential, whatever its scopes, can
/// use one. For now it rebuilds the search index (#223); answers are <c>no-store</c>.
/// </summary>
internal static class DiagnosticsEndpoints
{
    public const string DiagnosticsPath = ApiProblem.VersionPrefix + "/diagnostics";
    public const string SearchIndexRebuildPath = DiagnosticsPath + "/search-index/rebuild";

    public static IEndpointRouteBuilder MapDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(SearchIndexRebuildPath, RebuildSearchIndexAsync)
            .WithName("RebuildSearchIndex")
            .WithSummary("Queues a rebuild of the search index from the catalog and answers its job; a rebuild already queued or running is answered instead of starting another. Search keeps answering from the current index until the rebuild swaps in.")
            .SessionOnly()
            .Produces<SearchIndexRebuildResponse>(StatusCodes.Status202Accepted)
            .Produces<SearchIndexRebuildResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>202 with the new job (and the job as <c>Location</c>); 200 with the job already queued or running.</summary>
    private static async Task<Results<Accepted<SearchIndexRebuildResponse>, Ok<SearchIndexRebuildResponse>>> RebuildSearchIndexAsync(
        SearchIndexRebuild rebuild,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var start = await rebuild.StartAsync(cancellationToken);
        var response = new SearchIndexRebuildResponse(start.JobId, start.AlreadyInProgress);
        return start.AlreadyInProgress
            ? TypedResults.Ok(response)
            : TypedResults.Accepted($"{context.Request.PathBase}{JobsEndpoints.JobsPath}/{start.JobId}", response);
    }
}

/// <summary>A rebuild's job, and whether it was already queued or running.</summary>
internal sealed record SearchIndexRebuildResponse(Guid JobId, bool AlreadyInProgress);
