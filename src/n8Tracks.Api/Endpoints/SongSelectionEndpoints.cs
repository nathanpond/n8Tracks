using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// A Song's Selected Generation (#120, <c>generations.evaluate</c>): <c>PUT</c> chooses one of the
/// Song's Generations (<c>{ generation }</c>, its ID or shortcode) and <c>DELETE</c> clears it, each
/// under the Song's revision in <c>If-Match</c>, answering the Song (whose <c>selectedGeneration</c>
/// names the choice). Nothing but the Song's selection, revision, and updated time changes
/// (<see cref="GenerationSelectionService"/>).
/// </summary>
internal static class SongSelectionEndpoints
{
    public const string SelectedGenerationPath = SongsEndpoints.SongPath + "/selected-generation";

    public static IEndpointRouteBuilder MapSongSelection(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPut(SelectedGenerationPath, SelectAsync)
            .WithName("SelectGeneration")
            .WithSummary("Makes one of the Song's Generations, of any Version and in any state ({generation}: its ID or shortcode), the Song's Selected Generation, replacing any other, given the Song's revision in If-Match. Raises the Song's revision when the selection changes; changes nothing else.")
            .RequireScope(CredentialScopes.GenerationsEvaluate)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(SelectedGenerationPath, ClearAsync)
            .WithName("ClearSelectedGeneration")
            .WithSummary("Leaves the Song with no Selected Generation, given its revision in If-Match. Clearing a Song with none changes nothing.")
            .RequireScope(CredentialScopes.GenerationsEvaluate)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>
    /// 200 with the Song, its revision as the ETag; 404 when there is no such Song (<c>song_deleted</c>
    /// when it was deleted) or Generation; 409 <c>revision_conflict</c> with <c>current</c> (the Song);
    /// 422 <c>generation_not_in_song</c> for another Song's Generation, <c>validation_failed</c> when no
    /// Generation is named.
    /// </summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> SelectAsync(
        CatalogReference reference,
        SelectGenerationRequest? request,
        GenerationSelectionService selections,
        SongDeletionService deletions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        if (request?.Generation is not (null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String }))
        {
            return ApiProblem.ValidationFailed(
                context,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [GenerationSelectionService.GenerationField] = ["Name the Generation by its ID or shortcode, as text."] });
        }

        var generation = request?.Generation is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
        var outcome = await selections.SelectAsync(reference, generation, revision!.Value, cancellationToken);
        if (outcome is GenerationSelectionOutcome.NotInSong other)
        {
            return ApiProblem.For(
                context,
                StatusCodes.Status422UnprocessableEntity,
                GenerationSelectionService.NotInSongCode,
                $"Generation {other.Generation.Shortcode} belongs to another Song.",
                [new("generationId", other.Generation.Generation.Id), new("shortcode", other.Generation.Shortcode)]);
        }

        return await AnswerAsync(context, reference, revision.Value, outcome, deletions, loggers, cancellationToken);
    }

    /// <summary>200 with the Song, its revision as the ETag; 404 when there is no such Song; 409 <c>revision_conflict</c> with <c>current</c> (the Song).</summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> ClearAsync(
        CatalogReference reference,
        GenerationSelectionService selections,
        SongDeletionService deletions,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        return await AnswerAsync(context, reference, revision!.Value, await selections.ClearAsync(reference, revision.Value, cancellationToken), deletions, loggers, cancellationToken);
    }

    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> AnswerAsync(
        HttpContext context,
        CatalogReference reference,
        int revision,
        GenerationSelectionOutcome outcome,
        SongDeletionService deletions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case GenerationSelectionOutcome.Selected selected:
                if (selected.Song.Revision != revision)
                {
                    loggers.CreateLogger(typeof(SongSelectionEndpoints)).LogInformation(
                        "Selected Generation of {SongId} set to {GenerationId}",
                        selected.Song.Id,
                        selected.Song.SelectedGeneration?.Id);
                }

                Revisions.SetETag(context, selected.Song.Revision);
                return TypedResults.Ok(SongResponse.From(selected.Song, context.Request.PathBase));
            case GenerationSelectionOutcome.Conflict conflict:
                return Revisions.Conflict(context, SongResponse.From(conflict.Current, context.Request.PathBase));
            case GenerationSelectionOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);
            case GenerationSelectionOutcome.GenerationNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation.");
            case GenerationSelectionOutcome.SongNotFound:
                return await SongDeletionEndpoints.MissingSongAsync(context, reference, deletions, cancellationToken);
            default:
                throw new InvalidOperationException("Unknown selection outcome.");
        }
    }
}

/// <summary>The Generation to select: <c>generation</c>, its ID or shortcode, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record SelectGenerationRequest(JsonElement Generation);
