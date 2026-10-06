using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Generations, read (<c>catalog.read</c>): a Version's in ordinal order, a Song's across its Versions,
/// and one, each named by its stable ID or shortcode; and a Generation's provider record, the raw clip
/// exactly as stored, to a signed-in session only. No answer here carries the raw clip except the
/// provider-record endpoint's, and none says anything about Generation Events. Every answer is
/// <c>no-store</c>. Generations are created by the import and observed-Create stories through
/// <see cref="GenerationService.AttachAsync"/>; their refusals map to problems with <see cref="AttachRefusal"/>.
/// The user's judgement (<c>generations.evaluate</c>, #119): the rating and (#120) the state, set
/// under the Generation's revision in <c>If-Match</c>, and comments, each written under its own
/// revision (<see cref="GenerationEvaluationService"/>). Every Generation answer carries them. The
/// Song's Selected Generation is set on the Song (<see cref="SongSelectionEndpoints"/>).
/// </summary>
internal static class GenerationsEndpoints
{
    public const string GenerationsPath = ApiProblem.VersionPrefix + "/generations";
    public const string GenerationPath = GenerationsPath + "/{reference}";
    public const string ProviderRecordPath = GenerationPath + "/provider-record";
    public const string VersionGenerationsPath = VersionsEndpoints.VersionPath + "/generations";
    public const string SongGenerationsPath = SongsEndpoints.SongPath + "/generations";
    public const string CommentsPath = GenerationPath + "/comments";
    public const string CommentPath = CommentsPath + "/{commentId:guid}";

    /// <summary>The Generation has no provider record: it was attached without Suno data.</summary>
    public const string NoProviderRecordCode = "no_provider_record";

    public static IEndpointRouteBuilder MapGenerations(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(VersionGenerationsPath, ListForVersionAsync)
            .WithName("ListVersionGenerations")
            .WithSummary("A Version's Generations (by its ID or shortcode), in ordinal order, every state included. Not paged.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenerationListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(SongGenerationsPath, ListForSongAsync)
            .WithName("ListSongGenerations")
            .WithSummary("A Song's Generations (by its ID or shortcode) across its Versions: by Version number in tree order, then ordinal. Not paged.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenerationListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(GenerationPath, GetAsync)
            .WithName("GetGeneration")
            .WithSummary("One Generation, by its ID or shortcode (n8-12-v1.1-g3): its owners, what Suno reported about its clip, its states, and its revision.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenerationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(ProviderRecordPath, GetProviderRecordAsync)
            .WithName("GetGenerationProviderRecord")
            .WithSummary("The raw clip Suno last reported for a Generation, exactly as stored, as application/json. Web UI only (session).")
            .SessionOnly()
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPatch(GenerationPath, UpdateAsync)
            .WithName("UpdateGeneration")
            .WithSummary("Sets (1 to 5), changes, or clears (null) a Generation's rating, and archives or reactivates it (state: active or archived), given its revision in If-Match; a missing field leaves it as it is. Raises the Generation's revision when either changes.")
            .RequireScope(CredentialScopes.GenerationsEvaluate)
            .Produces<GenerationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPost(CommentsPath, AddCommentAsync)
            .WithName("AddGenerationComment")
            .WithSummary("Adds a comment (plain text, 1 to 2,000 characters once trimmed) to a Generation. Leaves the Generation's revision alone.")
            .RequireScope(CredentialScopes.GenerationsEvaluate)
            .Produces<GenerationCommentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPatch(CommentPath, EditCommentAsync)
            .WithName("EditGenerationComment")
            .WithSummary("Replaces a comment's text, given the comment's revision in If-Match. The same text, once trimmed, changes nothing and does not mark it edited.")
            .RequireScope(CredentialScopes.GenerationsEvaluate)
            .Produces<GenerationCommentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(CommentPath, DeleteCommentAsync)
            .WithName("DeleteGenerationComment")
            .WithSummary("Deletes a comment for good (it is not retained), given the comment's revision in If-Match.")
            .RequireScope(CredentialScopes.GenerationsEvaluate)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>
    /// The problem an attach that stored nothing answers: 404 <c>not_found</c> (no such Version or
    /// Generation Event), 422 <c>invalid_clip</c> with the reason, or 409 <c>suno_id_exists</c> naming
    /// the live Generation that holds the Suno ID (<c>generationId</c>, <c>shortcode</c>).
    /// </summary>
    public static ProblemHttpResult AttachRefusal(HttpContext context, GenerationAttachOutcome outcome) => outcome switch
    {
        GenerationAttachOutcome.VersionNotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Version."),
        GenerationAttachOutcome.EventNotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation Event."),
        GenerationAttachOutcome.InvalidClip invalid => ApiProblem.For(context, StatusCodes.Status422UnprocessableEntity, GenerationService.InvalidClipCode, invalid.Reason),
        GenerationAttachOutcome.SunoIdExists exists => ApiProblem.For(
            context,
            StatusCodes.Status409Conflict,
            GenerationService.SunoIdExistsCode,
            $"Generation {exists.Existing.Shortcode} already holds this Suno clip.",
            [new("generationId", exists.Existing.Generation.Id), new("shortcode", exists.Existing.Shortcode)]),
        _ => throw new ArgumentException("Not a refusal: the Generation was attached.", nameof(outcome)),
    };

    /// <summary>200 with the Version's Generations; 404 (<c>version_deleted</c> when it was deleted on its own) when there is no such Version.</summary>
    private static async Task<Results<Ok<GenerationListResponse>, ProblemHttpResult>> ListForVersionAsync(
        CatalogReference reference,
        GenerationService generations,
        VersionDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await generations.ListForVersionAsync(reference, cancellationToken) is { } list
            ? TypedResults.Ok(GenerationListResponse.From(list))
            : await VersionsEndpoints.MissingVersionAsync(context, reference, deletions, cancellationToken);
    }

    /// <summary>200 with the Song's Generations; 404 (<c>song_deleted</c> when it was deleted) when there is no such Song.</summary>
    private static async Task<Results<Ok<GenerationListResponse>, ProblemHttpResult>> ListForSongAsync(
        CatalogReference reference,
        GenerationService generations,
        SongDeletionService deletions,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await generations.ListForSongAsync(reference, cancellationToken) is { } list
            ? TypedResults.Ok(GenerationListResponse.From(list))
            : await SongDeletionEndpoints.MissingSongAsync(context, reference, deletions, cancellationToken);
    }

    /// <summary>200 with the Generation, its revision as the ETag; 404 <c>not_found</c> when the reference names no live Generation.</summary>
    private static async Task<Results<Ok<GenerationResponse>, ProblemHttpResult>> GetAsync(
        CatalogReference reference,
        GenerationService generations,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await generations.FindAsync(reference, cancellationToken) is not { } generation)
        {
            return NoSuchGeneration(context);
        }

        Revisions.SetETag(context, generation.Generation.Revision);
        return TypedResults.Ok(GenerationResponse.From(generation));
    }

    /// <summary>
    /// 200 with the raw clip, byte for byte as stored, as <c>application/json</c>; 404 <c>not_found</c>
    /// when there is no such Generation, <c>no_provider_record</c> when it has no Suno data.
    /// </summary>
    private static async Task<IResult> GetProviderRecordAsync(
        CatalogReference reference,
        GenerationService generations,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await generations.ProviderRecordAsync(reference, cancellationToken) switch
        {
            ProviderRecordOutcome.Found found => TypedResults.Text(found.Record.Payload, "application/json", Encoding.UTF8),
            ProviderRecordOutcome.NoRecord => ApiProblem.For(
                context,
                StatusCodes.Status404NotFound,
                NoProviderRecordCode,
                "This Generation has no Suno data."),
            ProviderRecordOutcome.GenerationNotFound => NoSuchGeneration(context),
            _ => throw new InvalidOperationException("Unknown provider record outcome."),
        };
    }

    /// <summary>
    /// 200 with the Generation as it is now, its revision as the ETag; 404 when there is no such
    /// Generation; 409 <c>revision_conflict</c> with <c>current</c> (the Generation with its comments);
    /// 422 when the rating is not a whole number from 1 to 5 or null, or the state is not
    /// <c>active</c> or <c>archived</c>.
    /// </summary>
    private static async Task<Results<Ok<GenerationResponse>, ProblemHttpResult>> UpdateAsync(
        CatalogReference reference,
        UpdateGenerationRequest? request,
        GenerationEvaluationService evaluations,
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
        var rating = GenerationRatingEdit.Unsent;
        switch (request?.Rating)
        {
            case null or { ValueKind: JsonValueKind.Undefined }:
                break;
            case { ValueKind: JsonValueKind.Null }:
                rating = GenerationRatingEdit.Of(null);
                break;
            case { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var stars):
                rating = GenerationRatingEdit.Of(stars);
                break;
            default:
                errors[GenerationEvaluationService.RatingField] = ["Send a whole number of stars from 1 to 5, or null for none."];
                break;
        }

        GenerationState? state = null;
        switch (request?.State)
        {
            case null or { ValueKind: JsonValueKind.Undefined }:
                break;
            case { ValueKind: JsonValueKind.String } text when text.GetString() is "active" or "archived":
                state = GenerationStates.StateOf(text.GetString()!);
                break;
            default:
                errors[GenerationEvaluationService.StateField] = ["Send active or archived."];
                break;
        }

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        switch (await evaluations.UpdateAsync(reference, new GenerationEdit(rating, state), revision!.Value, cancellationToken))
        {
            case GenerationUpdateOutcome.Updated updated:
                if (updated.Generation.Generation.Revision != revision)
                {
                    Log(loggers).LogInformation(
                        "Generation updated: {GenerationId} ({Rating}, {State})",
                        updated.Generation.Generation.Id,
                        updated.Generation.Generation.Rating,
                        GenerationStates.NameOf(updated.Generation.Generation.State));
                }

                Revisions.SetETag(context, updated.Generation.Generation.Revision);
                return TypedResults.Ok(GenerationResponse.From(updated.Generation));
            case GenerationUpdateOutcome.Conflict conflict:
                return Revisions.Conflict(context, GenerationResponse.From(conflict.Current));
            case GenerationUpdateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);
            case GenerationUpdateOutcome.NotFound:
                return NoSuchGeneration(context);
            default:
                throw new InvalidOperationException("Unknown Generation update outcome.");
        }
    }

    /// <summary>201 with the new comment, its revision as the ETag; 404 when there is no such Generation; 422 on empty or too long text.</summary>
    private static async Task<Results<Created<GenerationCommentResponse>, ProblemHttpResult>> AddCommentAsync(
        CatalogReference reference,
        GenerationCommentRequest? request,
        GenerationEvaluationService evaluations,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (CommentTypeProblem(context, request) is { } problem)
        {
            return problem;
        }

        var outcome = await evaluations.AddCommentAsync(reference, request?.Text is { ValueKind: JsonValueKind.String } text ? text.GetString() : null, cancellationToken);
        if (outcome is GenerationCommentOutcome.Saved saved)
        {
            Log(loggers).LogInformation("Generation comment added: {CommentId} on {GenerationId}", saved.Comment.Id, saved.Comment.GenerationId);
            Revisions.SetETag(context, saved.Comment.Revision);
            return TypedResults.Created(
                $"{context.Request.PathBase}{GenerationsPath}/{saved.Comment.GenerationId}/comments/{saved.Comment.Id}",
                GenerationCommentResponse.From(saved.Comment));
        }

        return CommentRefusal(context, outcome);
    }

    /// <summary>
    /// 200 with the comment as it is now, its revision as the ETag; 404 when there is no such
    /// Generation or comment; 409 <c>revision_conflict</c> with <c>current</c> (the comment); 422 on
    /// empty or too long text.
    /// </summary>
    private static async Task<Results<Ok<GenerationCommentResponse>, ProblemHttpResult>> EditCommentAsync(
        CatalogReference reference,
        Guid commentId,
        GenerationCommentRequest? request,
        GenerationEvaluationService evaluations,
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

        if (CommentTypeProblem(context, request) is { } typeProblem)
        {
            return typeProblem;
        }

        var outcome = await evaluations.EditCommentAsync(
            reference,
            commentId,
            request?.Text is { ValueKind: JsonValueKind.String } text ? text.GetString() : null,
            revision!.Value,
            cancellationToken);
        if (outcome is GenerationCommentOutcome.Saved saved)
        {
            if (saved.Comment.Revision != revision)
            {
                Log(loggers).LogInformation("Generation comment edited: {CommentId} on {GenerationId}", saved.Comment.Id, saved.Comment.GenerationId);
            }

            Revisions.SetETag(context, saved.Comment.Revision);
            return TypedResults.Ok(GenerationCommentResponse.From(saved.Comment));
        }

        return CommentRefusal(context, outcome);
    }

    /// <summary>204 when the comment is deleted; 404 when there is no such Generation or comment; 409 <c>revision_conflict</c> with <c>current</c>.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteCommentAsync(
        CatalogReference reference,
        Guid commentId,
        GenerationEvaluationService evaluations,
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

        var outcome = await evaluations.DeleteCommentAsync(reference, commentId, revision!.Value, cancellationToken);
        if (outcome is GenerationCommentOutcome.Deleted)
        {
            Log(loggers).LogInformation("Generation comment deleted: {CommentId}", commentId);
            return TypedResults.NoContent();
        }

        return CommentRefusal(context, outcome);
    }

    /// <summary>
    /// The problem for a comment's text sent as anything but text; null when it is text, missing, or
    /// null (the last two refused by the service as empty).
    /// </summary>
    private static ProblemHttpResult? CommentTypeProblem(HttpContext context, GenerationCommentRequest? request) =>
        request?.Text is null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String }
            ? null
            : ApiProblem.ValidationFailed(
                context,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [GenerationEvaluationService.TextField] = ["Send the comment as text."] });

    /// <summary>The problem for every comment outcome but a save or a deletion.</summary>
    private static ProblemHttpResult CommentRefusal(HttpContext context, GenerationCommentOutcome outcome) => outcome switch
    {
        GenerationCommentOutcome.GenerationNotFound => NoSuchGeneration(context),
        GenerationCommentOutcome.CommentNotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such comment on this Generation."),
        GenerationCommentOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
        GenerationCommentOutcome.Conflict conflict => Revisions.Conflict(context, GenerationCommentResponse.From(conflict.Current)),
        _ => throw new InvalidOperationException("Unknown comment outcome."),
    };

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(GenerationsEndpoints));

    /// <summary>404 <c>not_found</c>: the reference names no live Generation (an unknown one, one of another kind, or a deleted one).</summary>
    private static ProblemHttpResult NoSuchGeneration(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation.");
}

/// <summary>
/// An edit of a Generation: <c>rating</c>, 1 to 5 or null, and <c>state</c>, <c>active</c> or
/// <c>archived</c>, each read as raw JSON so a missing field and a null differ.
/// </summary>
internal sealed record UpdateGenerationRequest(JsonElement Rating, JsonElement State);

/// <summary>A comment's text, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record GenerationCommentRequest(JsonElement Text);

/// <summary>
/// A comment on a Generation: plain text, when it was written, and when its text last changed
/// (<c>editedAt</c>, null when it never has). Times are UTC.
/// </summary>
internal sealed record GenerationCommentResponse(Guid Id, string Text, DateTime CreatedAt, DateTime? EditedAt, int Revision)
{
    public static GenerationCommentResponse From(GenerationComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        return new(comment.Id, comment.Text, comment.CreatedUtc.UtcDateTime, comment.EditedUtc?.UtcDateTime, comment.Revision);
    }
}

/// <summary>A Song or a Version a Generation belongs to: <c>{ id, shortcode }</c>.</summary>
internal sealed record GenerationOwnerResponse(Guid Id, string Shortcode);

/// <summary>
/// One Generation. <c>sunoId</c> and every field Suno reported are null for a Generation with no
/// Suno data; <c>sunoUrl</c> is Suno's page for the clip. <c>providerStatus</c> is Suno's status as
/// reported (<c>submitted</c>, <c>streaming</c>, <c>complete</c>, <c>error</c>, or another value Suno
/// sent); <c>state</c> is <c>active</c> or <c>archived</c>; <c>remoteState</c> is <c>present</c>,
/// <c>trashed</c>, or <c>missing</c>. <c>isSelected</c> says whether it is its Song's Selected
/// Generation (#120). <c>styleTags</c> is Suno's own style description of the clip
/// (<c>metadata.tags</c>), not the Version's styles. <c>rating</c> (1 to 5, or null) and
/// <c>comments</c> (oldest first) are the user's own. Times are UTC. Never the raw clip.
/// </summary>
internal sealed record GenerationResponse(
    Guid Id,
    string Shortcode,
    int Ordinal,
    GenerationOwnerResponse Song,
    GenerationOwnerResponse Version,
    string? SunoId,
    string? SunoUrl,
    string? ProviderStatus,
    string State,
    string RemoteState,
    string? Title,
    double? DurationSeconds,
    string? ModelVersion,
    string? ModelName,
    string? ModelLabel,
    string? StyleTags,
    double? MinimumBpm,
    double? MaximumBpm,
    double? AverageBpm,
    string? Key,
    DateTime? SunoCreatedAt,
    string? AudioUrl,
    string? ImageUrl,
    string? WorkspaceId,
    int? BatchIndex,
    bool IsSelected,
    int? Rating,
    IReadOnlyList<GenerationCommentResponse> Comments,
    DateTime CreatedAt,
    int Revision)
{
    public static GenerationResponse From(GenerationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var generation = summary.Generation;
        var clip = generation.Clip;
        return new(
            generation.Id,
            summary.Shortcode,
            generation.Ordinal,
            new GenerationOwnerResponse(generation.SongId, Domain.Songs.Shortcodes.ForSong(summary.SongShortcodeNumber)),
            new GenerationOwnerResponse(generation.VersionId, summary.VersionShortcode),
            clip?.SunoId,
            clip?.PageUrl,
            clip?.Status,
            GenerationStates.NameOf(generation.State),
            GenerationStates.NameOf(generation.RemoteState),
            clip?.Title,
            clip?.DurationSeconds,
            clip?.ModelVersion,
            clip?.ModelName,
            clip?.ModelLabel,
            clip?.StyleTags,
            clip?.MinimumBpm,
            clip?.MaximumBpm,
            clip?.AverageBpm,
            clip?.Key,
            clip?.SunoCreatedUtc?.UtcDateTime,
            clip?.AudioUrl,
            clip?.ImageUrl,
            clip?.WorkspaceId,
            clip?.BatchIndex,
            summary.IsSelected,
            generation.Rating,
            [.. summary.Comments.Select(GenerationCommentResponse.From)],
            generation.CreatedUtc.UtcDateTime,
            generation.Revision);
    }
}

/// <summary>Generations, in the order the endpoint gives.</summary>
internal sealed record GenerationListResponse(IReadOnlyList<GenerationResponse> Items)
{
    public static GenerationListResponse From(IReadOnlyList<GenerationSummary> generations) =>
        new([.. generations.Select(GenerationResponse.From)]);
}
