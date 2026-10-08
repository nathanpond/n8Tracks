using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The user's decision about where an audio file belongs (#210, <c>songs.write</c>): <c>PUT</c>
/// associates the file with a Song and optionally one of its Generations (<c>{ song, generation? }</c>,
/// each by ID or shortcode), replacing any association it has; <c>DELETE</c> removes its association;
/// <c>POST .../rematch</c> lets the Suno ID matcher look at it again. Each takes the file's revision in
/// <c>If-Match</c> and names the file by ID, never by a path. Only the file's record changes: nothing
/// in the media folder (invariant 2), and no Song, Version, or Generation (invariant 1)
/// (<see cref="AudioFileAssociationService"/>).
/// </summary>
internal static class AudioFileAssociationEndpoints
{
    public const string AssociationPath = MediaEndpoints.AudioFilePath + "/association";
    public const string RematchPath = MediaEndpoints.AudioFilePath + "/rematch";

    public static IEndpointRouteBuilder MapAudioFileAssociations(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPut(AssociationPath, AssociateAsync)
            .WithName("AssociateAudioFile")
            .WithSummary("Associates the audio file with a Song ({song}: its ID or shortcode) and, when given, one of that Song's Generations ({generation}), given the file's revision in If-Match; replaces any association it has, a scan's included. Naming the association it already has changes nothing.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<AudioFileResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(AssociationPath, RemoveAsync)
            .WithName("RemoveAudioFileAssociation")
            .WithSummary("Removes the audio file's association, given its revision in If-Match: it becomes unmatched, unassociated by you, and a scan never matches it by Suno ID again until asked to. A file with no association changes nothing.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPost(RematchPath, RematchAsync)
            .WithName("RematchAudioFile")
            .WithSummary("Match by Suno ID again: clears the unassociated audio file's block on automatic matching and its reason, given its revision in If-Match, and associates it at once when its name carries the Suno ID of exactly one live Generation.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<AudioFileResponse>(StatusCodes.Status200OK)
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
    /// 200 with the file, its revision as the ETag; 404 <c>not_found</c> when there is no such file,
    /// Song, or Generation (a deleted one included); 409 <c>revision_conflict</c> with <c>current</c>
    /// (the file); 422 <c>generation_not_in_song</c> for another Song's Generation, and
    /// <c>validation_failed</c> when no Song is named or a field is not text.
    /// </summary>
    private static async Task<Results<Ok<AudioFileResponse>, ProblemHttpResult>> AssociateAsync(
        Guid id,
        AssociateAudioFileRequest? request,
        AudioFileAssociationService associations,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var song = Text(request?.Song, AudioFileAssociationService.SongField, errors);
        var generation = Text(request?.Generation, AudioFileAssociationService.GenerationField, errors);
        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        return Answer(context, await associations.AssociateAsync(id, song, generation, revision!.Value, cancellationToken));
    }

    /// <summary>204 with the file's revision as the ETag; 404 <c>not_found</c> when there is no such file; 409 <c>revision_conflict</c> with <c>current</c> (the file).</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        Guid id,
        AudioFileAssociationService associations,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        return Answer(context, await associations.RemoveAsync(id, revision!.Value, cancellationToken)).Result switch
        {
            Ok<AudioFileResponse> => TypedResults.NoContent(),
            ProblemHttpResult refused => refused,
            _ => throw new InvalidOperationException("Unknown answer."),
        };
    }

    /// <summary>
    /// 200 with the file, its revision as the ETag; 404 <c>not_found</c> when there is no such file;
    /// 409 <c>revision_conflict</c> with <c>current</c> (the file); 422 <c>audio_file_associated</c>
    /// when the file is associated (there is nothing to match).
    /// </summary>
    private static async Task<Results<Ok<AudioFileResponse>, ProblemHttpResult>> RematchAsync(
        Guid id,
        AudioFileAssociationService associations,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        return Answer(context, await associations.RematchAsync(id, revision!.Value, cancellationToken));
    }

    private static Results<Ok<AudioFileResponse>, ProblemHttpResult> Answer(HttpContext context, AudioFileAssociationOutcome outcome)
    {
        switch (outcome)
        {
            case AudioFileAssociationOutcome.Done done:
                var answer = AudioFileResponse.From(done.File);
                Revisions.SetETag(context, answer.Revision);
                return TypedResults.Ok(answer);
            case AudioFileAssociationOutcome.Conflict conflict:
                return Revisions.Conflict(context, AudioFileResponse.From(conflict.Current));
            case AudioFileAssociationOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);
            case AudioFileAssociationOutcome.FileNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such audio file.");
            case AudioFileAssociationOutcome.SongNotFound:
                return SongsEndpoints.NoSuchSong(context);
            case AudioFileAssociationOutcome.GenerationNotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation.");
            case AudioFileAssociationOutcome.NotInSong other:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    AudioFileAssociationService.NotInSongCode,
                    $"Generation {other.Shortcode} belongs to another Song.",
                    [new("generationId", other.GenerationId), new("shortcode", other.Shortcode)]);
            case AudioFileAssociationOutcome.Associated associated:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    AudioFileAssociationService.AssociatedCode,
                    "This audio file is associated; remove its association before matching it by Suno ID.",
                    [new("current", AudioFileResponse.From(associated.File))]);
            default:
                throw new InvalidOperationException("Unknown association outcome.");
        }
    }

    /// <summary>A field sent as text, absent, or null; anything else is an error for that field.</summary>
    private static string? Text(JsonElement? value, string field, Dictionary<string, string[]> errors)
    {
        switch (value?.ValueKind)
        {
            case null or JsonValueKind.Undefined or JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return value.Value.GetString();
            default:
                errors[field] = ["Name it by its ID or shortcode, as text."];
                return null;
        }
    }
}

/// <summary>The association to make: <c>song</c> and <c>generation</c> (optional), each its ID or shortcode, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record AssociateAudioFileRequest(JsonElement? Song, JsonElement? Generation);
