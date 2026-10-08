using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Deleting a Generation from n8Tracks (#124), web UI only (session): what it would take with it,
/// then the delete itself under the Generation's revision in <c>If-Match</c>, into the 30-day
/// retention store (<see cref="GenerationDeletionService"/>). Nothing in Suno is changed.
/// </summary>
internal static class GenerationDeletionEndpoints
{
    public const string DeletionImpactPath = GenerationsEndpoints.GenerationPath + "/deletion-impact";

    public static IEndpointRouteBuilder MapGenerationDeletion(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(DeletionImpactPath, DeletionImpactAsync)
            .WithName("GetGenerationDeletionImpact")
            .WithSummary("What deleting a Generation would do: whether it is its Song's Selected Generation and, if so, the Song's other Generations it may select instead; its comment and artwork counts; how many Versions use it as a source; and its revision. Web UI only (session).")
            .SessionOnly()
            .Produces<GenerationDeletionImpactResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapDelete(GenerationsEndpoints.GenerationPath, DeleteAsync)
            .WithName("DeleteGeneration")
            .WithSummary("Deletes a Generation with its rating, comments, and Suno artwork into retention, given its revision in If-Match; nothing in Suno is changed. When it is its Song's Selected Generation, send replacementGeneration (another of its Generations; the workflow state stays) or workflowState (a state's ID; the selection is cleared). Answers its Song as it is now. Web UI only (session).")
            .SessionOnly()
            .Produces<GenerationDeletedResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with what deleting the Generation would do now; 404 <c>not_found</c> when the reference names no live Generation.</summary>
    private static async Task<Results<Ok<GenerationDeletionImpactResponse>, ProblemHttpResult>> DeletionImpactAsync(
        CatalogReference reference,
        GenerationDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await deletions.ImpactAsync(reference, cancellationToken) is not GenerationDeletionImpactOutcome.Found found)
        {
            return NoSuchGeneration(context);
        }

        var impact = found.Impact;
        Revisions.SetETag(context, impact.Generation.Generation.Revision);
        return TypedResults.Ok(new GenerationDeletionImpactResponse(
            impact.Generation.Generation.Id,
            impact.Generation.Shortcode,
            impact.IsSelected,
            [.. impact.Replacements.Select(replacement => GenerationResponse.From(replacement, context.Request.PathBase))],
            impact.CommentCount,
            impact.ArtworkCount,
            impact.SourceVersionCount,
            LocalAudioFilesResponse.From(impact.LocalAudioFiles),
            impact.Generation.Generation.Revision));
    }

    /// <summary>
    /// 200 with the Song as it is now once the Generation is in retention; 404 when there is no such
    /// Generation; 409 <c>revision_conflict</c> with <c>current</c> (the Generation); 422
    /// <c>selection_choice_required</c> when it is its Song's Selected Generation and no choice was
    /// sent, <c>invalid_replacement</c> (with <c>errors</c>) for a choice that is wrong or not wanted,
    /// and <c>validation_failed</c> for a field of the wrong type. Nothing is changed unless the answer is 200.
    /// </summary>
    private static async Task<Results<Ok<GenerationDeletedResponse>, ProblemHttpResult>> DeleteAsync(
        CatalogReference reference,
        GenerationDeletionService deletions,
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

        var (choice, bodyProblem) = await ReadChoiceAsync(context, cancellationToken);
        if (bodyProblem is not null)
        {
            return bodyProblem;
        }

        switch (await deletions.DeleteAsync(reference, choice, revision!.Value, cancellationToken))
        {
            case GenerationDeleteOutcome.Deleted deleted:
                loggers.CreateLogger(typeof(GenerationDeletionEndpoints)).LogInformation(
                    "Generation deleted: {Shortcode} into retention group {RetentionGroupId} (Song {SongId})",
                    deleted.Group.Shortcode,
                    deleted.Group.Id,
                    deleted.Song.Id);
                return TypedResults.Ok(new GenerationDeletedResponse(SongResponse.From(deleted.Song, context.Request.PathBase)));
            case GenerationDeleteOutcome.NotFound:
                return NoSuchGeneration(context);
            case GenerationDeleteOutcome.Conflict conflict:
                return Revisions.Conflict(context, GenerationResponse.From(conflict.Current, context.Request.PathBase));
            case GenerationDeleteOutcome.SelectionChoiceRequired required:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    GenerationSelectionService.SelectionChoiceRequiredCode,
                    $"{required.Generation.Shortcode} is its Song's Selected Generation. Choose another Generation for that Song, or a workflow state for it, first.",
                    [new("generationId", required.Generation.Generation.Id), new("shortcode", required.Generation.Shortcode)]);
            case GenerationDeleteOutcome.InvalidReplacement invalid:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    GenerationDeletionService.InvalidReplacementCode,
                    "Choose either another Generation of this Song or a workflow state, and only when the Generation is the Song's Selected Generation.",
                    [new("errors", invalid.Errors)]);
            default:
                throw new InvalidOperationException("Unknown Generation deletion outcome.");
        }
    }

    private static ProblemHttpResult NoSuchGeneration(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation.");

    /// <summary>
    /// The body's <see cref="SelectionChoice"/>: none when there is no body (a Generation that is not
    /// selected needs none). A body that is not a JSON object is 400 <c>invalid_request</c>; a field
    /// that is not text is 422 <c>validation_failed</c>. Read by hand, as the Song's delete reads its
    /// body, because a DELETE with a bound body parameter does not match a request without one.
    /// </summary>
    private static async Task<(SelectionChoice Choice, ProblemHttpResult? Problem)> ReadChoiceAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength == 0 || (context.Request.ContentLength is null && !context.Request.Headers.TransferEncoding.Any()))
        {
            return (SelectionChoice.None, null);
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return (SelectionChoice.None, ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "The body is not JSON."));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (SelectionChoice.None, ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send a JSON object, or no body."));
            }

            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var replacement = Text(root, GenerationSelectionService.ReplacementGenerationField, "Send the Generation's ID or shortcode as text.", errors);
            var state = Text(root, GenerationSelectionService.WorkflowStateField, "Send the workflow state's ID as text.", errors);
            return errors.Count > 0
                ? (SelectionChoice.None, ApiProblem.ValidationFailed(context, errors))
                : (new SelectionChoice(replacement, state), null);
        }
    }

    /// <summary>A field of <paramref name="body"/> sent as text (or null, or missing: null); any other kind is an error under <paramref name="field"/>.</summary>
    private static string? Text(JsonElement body, string field, string message, Dictionary<string, string[]> errors)
    {
        switch (body.TryGetProperty(field, out var value) ? value : default(JsonElement?))
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
/// What deleting a Generation would do: its <c>id</c> and <c>shortcode</c>; whether it
/// <c>isSelected</c>; the <c>replacements</c> its Song may select instead (when selected); its
/// <c>commentCount</c> and <c>artworkCount</c>, deleted with it; how many Versions use it as a source
/// (<c>sourceVersionCount</c>); its <c>localAudioFiles</c>, which stay on disk and become unmatched
/// (#213); and its <c>revision</c>, to delete it under.
/// </summary>
internal sealed record GenerationDeletionImpactResponse(
    Guid Id,
    string Shortcode,
    bool IsSelected,
    IReadOnlyList<GenerationResponse> Replacements,
    int CommentCount,
    int ArtworkCount,
    int SourceVersionCount,
    LocalAudioFilesResponse LocalAudioFiles,
    int Revision);

/// <summary>What deleting a Generation answers: its <c>song</c> as it is now (its selection and state).</summary>
internal sealed record GenerationDeletedResponse(SongResponse Song);
