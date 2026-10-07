using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The diff of a Changed or Conflict record of a sync review (#141): session-only, read-only. Its values
/// are the clip's and the Generation's own (titles, style text, lyrics of a conflict) and are answered
/// only here, never logged (invariant 6).
/// </summary>
internal static class SunoExportDiffEndpoint
{
    public const string DiffPath = SunoExportsEndpoints.RecordsPath + "/{sunoId}/diff";

    /// <summary>422: the record is not Changed or Conflict, so it has no diff.</summary>
    public const string NotDiffedCode = "record_not_changed";

    public static IEndpointRouteBuilder MapSunoExportDiff(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(DiffPath, DiffAsync)
            .WithName("GetSunoExportRecordDiff")
            .WithSummary("How a Changed or Conflict record differs (#141): fields: [{ field, current, incoming }] for each provider field that differs between the Generation (current) and Suno (incoming), an image address without its query string; inputs: [{ field, current, incoming }] for each creation input in which a Conflict's clip differs from its Version. 404 for an unknown export or record; 422 record_not_changed for any other class.")
            .SessionOnly()
            .Produces<SunoExportDiffResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    /// <summary>200 with the diff; 404 or 422 otherwise.</summary>
    private static async Task<Results<Ok<SunoExportDiffResponse>, ProblemHttpResult>> DiffAsync(
        Guid id,
        string sunoId,
        ChangeResolutionService changes,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await changes.DiffAsync(id, sunoId, cancellationToken) switch
        {
            RecordDiffOutcome.Found found => TypedResults.Ok(new SunoExportDiffResponse(
                found.Diff.SunoId,
                SunoExportRules.NameOf(found.Diff.Class),
                found.Diff.GenerationId,
                found.Diff.Fields,
                found.Diff.Inputs)),
            RecordDiffOutcome.NotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Suno export."),
            RecordDiffOutcome.RecordNotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "The export has no record with this Suno ID."),
            RecordDiffOutcome.NotDiffed => ApiProblem.For(context, StatusCodes.Status422UnprocessableEntity, NotDiffedCode, "Only a Changed or Conflict record has a diff."),
            _ => throw new InvalidOperationException("Unknown diff outcome."),
        };
    }
}

/// <summary>A record's diff as answered.</summary>
internal sealed record SunoExportDiffResponse(string SunoId, string Class, Guid GenerationId, IReadOnlyList<FieldDiff> Fields, IReadOnlyList<InputDiff> Inputs);
