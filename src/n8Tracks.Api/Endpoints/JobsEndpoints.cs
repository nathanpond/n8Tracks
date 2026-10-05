using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Jobs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The status of background jobs: one job, or the most recent ones. Both need <c>catalog.read</c>.
/// Neither ever shows a job's payload; a job's <c>result</c> is shown to a signed-in session only,
/// and left out altogether for a token. Every answer is <c>no-store</c>: a job's status changes.
/// </summary>
internal static class JobsEndpoints
{
    public const string JobsPath = ApiProblem.VersionPrefix + "/jobs";
    public const string JobPath = JobsPath + "/{id:guid}";

    public static IEndpointRouteBuilder MapJobs(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(JobsPath, ListAsync)
            .WithName("ListJobs")
            .WithSummary("The 50 most recently enqueued jobs, newest first.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<JobWithResultResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(JobPath, GetAsync)
            .WithName("GetJob")
            .WithSummary("One job: its type, status, progress, message, times, and error or result.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<JobWithResultResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>200 with the list, which may be empty.</summary>
    private static async Task<Results<Ok<JobWithResultResponse[]>, Ok<JobResponse[]>>> ListAsync(
        JobService jobs,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var list = await jobs.ListRecentAsync(cancellationToken);
        return CredentialPrincipal.IsCredential(context.User)
            ? TypedResults.Ok(list.Select(JobResponse.From).ToArray())
            : TypedResults.Ok(list.Select(JobWithResultResponse.From).ToArray());
    }

    /// <summary>200 with the job; 404 <c>not_found</c> when there is none, a pruned job included.</summary>
    private static async Task<Results<Ok<JobWithResultResponse>, Ok<JobResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        JobService jobs,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await jobs.FindAsync(id, cancellationToken) is not { } job)
        {
            return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such job.");
        }

        return CredentialPrincipal.IsCredential(context.User)
            ? TypedResults.Ok(JobResponse.From(job))
            : TypedResults.Ok(JobWithResultResponse.From(job));
    }
}

/// <summary>A job as a token sees it: no payload and no result. Times are UTC.</summary>
internal sealed record JobResponse(
    Guid Id,
    string Type,
    JobStatus Status,
    int Progress,
    string? Message,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    string? Error)
{
    public static JobResponse From(JobSummary job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new(
            job.Id,
            job.Type,
            job.Status,
            job.Progress,
            job.Message,
            job.CreatedUtc.UtcDateTime,
            job.StartedUtc?.UtcDateTime,
            job.FinishedUtc?.UtcDateTime,
            job.Error);
    }
}

/// <summary>A job as a signed-in session sees it: with its result (null until it has succeeded). Never its payload.</summary>
internal sealed record JobWithResultResponse(
    Guid Id,
    string Type,
    JobStatus Status,
    int Progress,
    string? Message,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    string? Error,
    JsonElement? Result)
{
    public static JobWithResultResponse From(JobSummary job)
    {
        var shown = JobResponse.From(job);
        return new(
            shown.Id,
            shown.Type,
            shown.Status,
            shown.Progress,
            shown.Message,
            shown.CreatedUtc,
            shown.StartedUtc,
            shown.FinishedUtc,
            shown.Error,
            job.Result);
    }
}
