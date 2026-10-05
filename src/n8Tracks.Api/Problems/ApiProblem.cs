using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using n8Tracks.Api.Auth;

namespace n8Tracks.Api.Problems;

/// <summary>
/// The one shape of an error from <c>/api/v1</c>: Problem Details (<c>application/problem+json</c>)
/// with a stable machine-readable <c>code</c> and the request ID. Endpoints return these instead of
/// building their own.
/// </summary>
internal static class ApiProblem
{
    /// <summary>The prefix every versioned API route is under.</summary>
    public const string VersionPrefix = "/api/v1";

    public const string ValidationFailedCode = "validation_failed";
    public const string InvalidRequestCode = "invalid_request";
    public const string NotFoundCode = "not_found";

    /// <summary>A problem with the given status, code, and title, plus any extra members.</summary>
    public static ProblemHttpResult For(
        HttpContext context,
        int status,
        string code,
        string title,
        IEnumerable<KeyValuePair<string, object?>>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        problem.Extensions["requestId"] = context.TraceIdentifier;
        foreach (var (name, value) in extensions ?? [])
        {
            problem.Extensions[name] = value;
        }

        return TypedResults.Problem(problem);
    }

    /// <summary>422 <c>validation_failed</c>, with <c>errors</c>: messages keyed by field name.</summary>
    public static ProblemHttpResult ValidationFailed(HttpContext context, IReadOnlyDictionary<string, string[]> errors) =>
        For(
            context,
            StatusCodes.Status422UnprocessableEntity,
            ValidationFailedCode,
            "The request has invalid fields.",
            [new("errors", errors)]);

    /// <summary>
    /// Maps a 404 Problem Details for every <c>/api/v1</c> route nothing else matched, whatever the
    /// method, so an unknown API path never falls through to the frontend or an empty body.
    /// </summary>
    public static IEndpointRouteBuilder MapApiNotFound(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapFallback(
            VersionPrefix + "/{**path}",
            static (HttpContext context) => For(context, StatusCodes.Status404NotFound, NotFoundCode, "There is no such API resource."))
            .AnyCaller()
            .ExcludeFromDescription();

        return endpoints;
    }
}
