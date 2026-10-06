using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Creating a new Song from a Generation (#123), web UI only (session): the Generation moves (never a
/// copy) into a new Song's Version 1, which holds a copy of its Version's creation inputs and
/// lineage, under the Generation's revision in <c>If-Match</c>. Its old shortcode becomes an alias
/// that still resolves to it (<see cref="GenerationMoveService"/>).
/// </summary>
internal static class GenerationMoveEndpoints
{
    public const string MoveToNewSongPath = GenerationsEndpoints.GenerationPath + "/move-to-new-song";

    public static IEndpointRouteBuilder MapGenerationMoves(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(MoveToNewSongPath, MoveToNewSongAsync)
            .WithName("MoveGenerationToNewSong")
            .WithSummary("Moves a Generation (never a copy) into a new Song ({title}) whose Version 1 holds a copy of its Version's creation inputs and sources, frozen, recording Derived From and leaving the old shortcode as an alias. When the Generation is its Song's Selected Generation, send replacementGeneration (another of its Generations) or workflowState (a state's ID). Given the Generation's revision in If-Match. Web UI only (session).")
            .SessionOnly()
            .Produces<GenerationMoveResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>
    /// 201 with the new Song, its Version, the Generation where it is now, and the alias; 404 when
    /// there is no such Generation; 409 <c>revision_conflict</c> with <c>current</c> (the Generation);
    /// 422 <c>validation_failed</c> for the title or a choice that is wrong or not wanted, and
    /// <c>selection_choice_required</c> when the Generation is its Song's Selected Generation and no
    /// choice was sent.
    /// </summary>
    private static async Task<Results<Created<GenerationMoveResponse>, ProblemHttpResult>> MoveToNewSongAsync(
        CatalogReference reference,
        MoveToNewSongRequest? request,
        GenerationMoveService moves,
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

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var title = Text(request?.Title, GenerationMoveService.TitleField, "Send the new Song's title as text.", errors);
        var replacement = Text(request?.ReplacementGeneration, GenerationSelectionService.ReplacementGenerationField, "Send the Generation's ID or shortcode as text.", errors);
        var state = Text(request?.WorkflowState, GenerationSelectionService.WorkflowStateField, "Send the workflow state's ID as text.", errors);
        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var outcome = await moves.MoveToNewSongAsync(
            reference,
            new GenerationMoveRequest(title, new SelectionChoice(replacement, state)),
            revision!.Value,
            cancellationToken);
        switch (outcome)
        {
            case GenerationMoveOutcome.Moved moved:
                loggers.CreateLogger(typeof(GenerationMoveEndpoints)).LogInformation(
                    "Generation moved to a new Song: {GenerationId} from {Alias} to {Shortcode} (Song {SongId})",
                    moved.Generation.Generation.Id,
                    moved.Alias,
                    moved.Generation.Shortcode,
                    moved.Song.Id);
                Revisions.SetETag(context, moved.Generation.Generation.Revision);
                return TypedResults.Created(
                    $"{context.Request.PathBase}{SongsEndpoints.SongsPath}/{moved.Song.Id}",
                    new GenerationMoveResponse(
                        SongResponse.From(moved.Song, context.Request.PathBase),
                        VersionResponse.From(moved.Version),
                        GenerationResponse.From(moved.Generation, context.Request.PathBase),
                        moved.Alias));
            case GenerationMoveOutcome.NotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation.");
            case GenerationMoveOutcome.Conflict conflict:
                return Revisions.Conflict(context, GenerationResponse.From(conflict.Current, context.Request.PathBase));
            case GenerationMoveOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);
            case GenerationMoveOutcome.SelectionChoiceRequired required:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    GenerationSelectionService.SelectionChoiceRequiredCode,
                    $"{required.Generation.Shortcode} is its Song's Selected Generation. Choose another Generation for that Song, or a workflow state for it, first.",
                    [new("generationId", required.Generation.Generation.Id), new("shortcode", required.Generation.Shortcode)]);
            default:
                throw new InvalidOperationException("Unknown Generation move outcome.");
        }
    }

    /// <summary>A field sent as text (or null, or missing: null); any other kind is an error under <paramref name="field"/>.</summary>
    private static string? Text(JsonElement? value, string field, string message, Dictionary<string, string[]> errors)
    {
        switch (value)
        {
            case null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null }:
                return null;
            case { ValueKind: JsonValueKind.String } text:
                return text.GetString();
            default:
                errors[field] = [message];
                return null;
        }
    }
}

/// <summary>
/// A request to create a new Song from a Generation: <c>title</c>, and at most one of
/// <c>replacementGeneration</c> and <c>workflowState</c>, each read as raw JSON so a wrong type is a
/// field error.
/// </summary>
internal sealed record MoveToNewSongRequest(JsonElement Title, JsonElement ReplacementGeneration, JsonElement WorkflowState);

/// <summary>
/// What creating a new Song from a Generation made: the new <c>song</c> (whose Selected Generation is
/// the moved one), its <c>version</c> 1, the <c>generation</c> where it is now, and the <c>alias</c>
/// its old shortcode became.
/// </summary>
internal sealed record GenerationMoveResponse(SongResponse Song, VersionResponse Version, GenerationResponse Generation, string Alias);
