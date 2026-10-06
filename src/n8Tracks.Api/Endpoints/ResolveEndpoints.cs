using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.References;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Resolving a reference (<c>catalog.read</c>): a stable ID or a complete shortcode, in any letter
/// case, answered with what it names. The web UI's Go to box and <c>/go/</c> links go through it.
/// </summary>
internal static class ResolveEndpoints
{
    public const string ResolvePath = ApiProblem.VersionPrefix + "/resolve/{reference}";

    /// <summary>The reference is unknown or is not an ID or shortcode at all. This endpoint's own code.</summary>
    public const string ReferenceNotFoundCode = "reference_not_found";

    public static IEndpointRouteBuilder MapResolve(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(ResolvePath, ResolveAsync)
            .WithName("ResolveReference")
            .WithSummary("What a stable ID or a shortcode (n8-12, n8-12-v1.1, n8-12-v1.1-g3; any letter case) names: its type, ID, canonical shortcode, and status.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<ResolveResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>200 with what the reference names; 404 <c>reference_not_found</c> when it names nothing or is malformed.</summary>
    private static async Task<Results<Ok<ResolveResponse>, ProblemHttpResult>> ResolveAsync(
        CatalogReference reference,
        ReferenceResolver references,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.ResolveAsync(reference, cancellationToken) is not { } resolved)
        {
            return ApiProblem.For(
                context,
                StatusCodes.Status404NotFound,
                ReferenceNotFoundCode,
                "Nothing has that ID or shortcode.");
        }

        return TypedResults.Ok(ResolveResponse.From(resolved));
    }
}

/// <summary>
/// What a reference names. <c>entityType</c> is <c>song</c>, <c>version</c>, or <c>generation</c>;
/// <c>status</c> is <c>active</c>, <c>deleted</c> (within its 30-day retention: a deleted Song with
/// its Versions and Generations, or a Version deleted on its own), or, for a Version or a Generation, <c>archived</c>; <c>song</c> is there for a
/// Version or a Generation, and <c>version</c> for a Generation only.
/// </summary>
internal sealed record ResolveResponse(
    string EntityType,
    Guid Id,
    string Shortcode,
    string Status,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ResolveSongResponse? Song,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ResolveVersionResponse? Version = null)
{
    public static ResolveResponse From(ResolvedReference resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return new(
            resolved.EntityType,
            resolved.Id,
            resolved.Shortcode,
            resolved.Status,
            resolved.Song is { } song ? new ResolveSongResponse(song.Id, song.Shortcode) : null,
            resolved.Version is { } version ? new ResolveVersionResponse(version.Id, version.Shortcode) : null);
    }
}

/// <summary>The Song a resolved Version or Generation belongs to.</summary>
internal sealed record ResolveSongResponse(Guid Id, string Shortcode);

/// <summary>The Version a resolved Generation belongs to.</summary>
internal sealed record ResolveVersionResponse(Guid Id, string Shortcode);
