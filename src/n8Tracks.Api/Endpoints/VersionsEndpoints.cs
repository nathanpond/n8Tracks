using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Versions: for now, the numbers a new Version may take when it branches from one
/// (<c>catalog.read</c>). Every answer is <c>no-store</c>.
/// </summary>
internal static class VersionsEndpoints
{
    public const string VersionsPath = ApiProblem.VersionPrefix + "/versions";
    public const string NextNumbersPath = VersionsPath + "/{id:guid}/next-numbers";

    /// <summary>Both options would be longer than a Version number may be.</summary>
    public const string TooDeepCode = "version_number_too_deep";

    public const string SiblingKind = "sibling";
    public const string ChildKind = "child";

    public static IEndpointRouteBuilder MapVersions(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(NextNumbersPath, NextNumbersAsync)
            .WithName("GetNextVersionNumbers")
            .WithSummary("The numbers a new Version created from this one may take (next sibling and child), the proposal first.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<NextNumbersResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    /// <summary>
    /// 200 with the options; 404 <c>not_found</c> when there is no such Version; 409
    /// <c>version_number_too_deep</c> when both options would be longer than 64 characters.
    /// </summary>
    private static async Task<Results<Ok<NextNumbersResponse>, ProblemHttpResult>> NextNumbersAsync(
        Guid id,
        VersionService versions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await versions.NextNumbersAsync(id, cancellationToken) switch
        {
            NextNumbersOutcome.Found found => TypedResults.Ok(NextNumbersResponse.From(found.Options)),
            NextNumbersOutcome.NotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version."),
            NextNumbersOutcome.TooDeep => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                TooDeepCode,
                "No Version can branch from this one: every new number would be longer than 64 characters."),
            _ => throw new InvalidOperationException("Unknown next-numbers outcome."),
        };
    }
}

/// <summary>The numbers a new Version may take, the proposal first.</summary>
internal sealed record NextNumbersResponse(NextNumberResponse[] Options)
{
    public static NextNumbersResponse From(IReadOnlyList<VersionNumberOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new([.. options.Select(static option => new NextNumberResponse(
            option.Number.ToString(),
            option.Kind == VersionNumberKind.Sibling ? VersionsEndpoints.SiblingKind : VersionsEndpoints.ChildKind,
            option.Proposed))]);
    }
}

/// <summary>One number: <c>kind</c> is <c>sibling</c> or <c>child</c>.</summary>
internal sealed record NextNumberResponse(string Number, string Kind, bool Proposed);
