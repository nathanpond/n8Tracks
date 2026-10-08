using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Media;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Preferred audio files and playback (#212). <c>GET .../playback</c> (<c>catalog.read</c>) answers what
/// plays for a Generation or a Song, by the one rule (<see cref="PlaybackResolver"/>):
/// <c>{ source: "local" | "none", audioFile, reason }</c>, the Song's with the <c>generation</c> its
/// file came from. <c>PUT</c> and <c>DELETE .../preferred-audio-file</c> (<c>songs.write</c>) set
/// (<c>{ audioFile }</c>, the file's ID) or clear the owner's choice under the owner's revision in
/// <c>If-Match</c>, answering the owner as it is now (its revision raised when the choice changed),
/// like the Song's Selected Generation. Files are named by ID, never by a path; nothing in the media
/// folder changes (invariant 2), and no Version (invariant 1).
/// </summary>
internal static class PlaybackEndpoints
{
    public const string GenerationPlaybackPath = GenerationsEndpoints.GenerationPath + "/playback";
    public const string SongPlaybackPath = SongsEndpoints.SongPath + "/playback";
    public const string GenerationPreferredPath = GenerationsEndpoints.GenerationPath + "/preferred-audio-file";
    public const string SongPreferredPath = SongsEndpoints.SongPath + "/preferred-audio-file";

    public static IEndpointRouteBuilder MapPlayback(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(GenerationPlaybackPath, GenerationPlaybackAsync)
            .WithName("GetGenerationPlayback")
            .WithSummary("What plays for the Generation (by its ID or shortcode): its preferred audio file while available, otherwise its highest-ranked available file (WAV, M4A, MP3, FLAC, OGG, Opus, AAC), or nothing local; with the reason.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenerationPlaybackResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(SongPlaybackPath, SongPlaybackAsync)
            .WithName("GetSongPlayback")
            .WithSummary("What plays for the Song (by its ID or shortcode): its preferred Song-level audio file while available, otherwise its Selected Generation's file, or nothing local; with the reason and the Generation the file came from.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SongPlaybackResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPut(GenerationPreferredPath, SetForGenerationAsync)
            .WithName("SetGenerationPreferredAudioFile")
            .WithSummary("Makes one of the Generation's audio files ({audioFile}: its ID), whatever its status, the Generation's preferred file, given the Generation's revision in If-Match. Raises the Generation's revision when the choice changes; changes nothing else.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<GenerationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(GenerationPreferredPath, ClearForGenerationAsync)
            .WithName("ClearGenerationPreferredAudioFile")
            .WithSummary("Leaves the Generation with no preferred audio file (the automatic rule decides), given its revision in If-Match. Clearing one with none changes nothing.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<GenerationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPut(SongPreferredPath, SetForSongAsync)
            .WithName("SetSongPreferredAudioFile")
            .WithSummary("Makes one of the Song's Song-level audio files ({audioFile}: its ID), whatever its status, the Song's preferred file, given the Song's revision in If-Match. Raises the Song's revision when the choice changes; changes nothing else.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(SongPreferredPath, ClearForSongAsync)
            .WithName("ClearSongPreferredAudioFile")
            .WithSummary("Leaves the Song with no preferred Song-level audio file (its Selected Generation's file plays), given its revision in If-Match. Clearing one with none changes nothing.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with what plays; 404 <c>not_found</c> when the reference names no live Generation.</summary>
    private static async Task<Results<Ok<GenerationPlaybackResponse>, ProblemHttpResult>> GenerationPlaybackAsync(
        CatalogReference reference,
        GenerationService generations,
        PlaybackService playback,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await generations.FindAsync(reference, cancellationToken) is not { } generation)
        {
            return NoSuchGeneration(context);
        }

        var resolved = await playback.ForGenerationAsync(generation.Generation.SongId, generation.Generation.Id, cancellationToken);
        return TypedResults.Ok(new GenerationPlaybackResponse(
            Source(resolved.Played),
            PlaybackFileResponse.From(resolved.Played, context.Request.PathBase),
            PlaybackReasons.Text(resolved.Reason)));
    }

    /// <summary>200 with what plays; 404 (<c>song_deleted</c> when it was deleted) when the reference names no live Song.</summary>
    private static async Task<Results<Ok<SongPlaybackResponse>, ProblemHttpResult>> SongPlaybackAsync(
        CatalogReference reference,
        SongService songs,
        SongDeletionService deletions,
        PlaybackService playback,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await songs.FindAsync(reference.Text, cancellationToken) is not { } song)
        {
            return await SongDeletionEndpoints.MissingSongAsync(context, reference, deletions, cancellationToken);
        }

        var resolved = await playback.ForSongAsync(song.Id, song.SelectedGeneration?.Id, cancellationToken);
        var generation = resolved.GenerationId is { } id && song.SelectedGeneration is { } selected && selected.Id == id
            ? new AudioFileLinkResponse(selected.Id, selected.Shortcode)
            : null;
        return TypedResults.Ok(new SongPlaybackResponse(
            Source(resolved.Played),
            PlaybackFileResponse.From(resolved.Played, context.Request.PathBase),
            PlaybackReasons.Text(resolved.Reason),
            generation));
    }

    /// <summary>
    /// 200 with the Generation, its revision as the ETag; 404 <c>not_found</c> when there is no such
    /// Generation or audio file; 409 <c>revision_conflict</c> with <c>current</c> (the Generation); 422
    /// <c>audio_file_not_in_generation</c> for a file not associated with it, <c>validation_failed</c>
    /// when <c>audioFile</c> is not an ID.
    /// </summary>
    private static async Task<Results<Ok<GenerationResponse>, ProblemHttpResult>> SetForGenerationAsync(
        CatalogReference reference,
        PreferredAudioFileRequest? request,
        PreferredAudioFileService preferences,
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

        if (FileOf(request) is not { } file)
        {
            return InvalidFile(context);
        }

        return AnswerGeneration(context, revision!.Value, await preferences.SetForGenerationAsync(reference.Text, file, revision.Value, cancellationToken), loggers);
    }

    /// <summary>200 with the Generation, its revision as the ETag; 404 <c>not_found</c> when there is no such Generation; 409 <c>revision_conflict</c> with <c>current</c>.</summary>
    private static async Task<Results<Ok<GenerationResponse>, ProblemHttpResult>> ClearForGenerationAsync(
        CatalogReference reference,
        PreferredAudioFileService preferences,
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

        return AnswerGeneration(context, revision!.Value, await preferences.ClearForGenerationAsync(reference.Text, revision.Value, cancellationToken), loggers);
    }

    /// <summary>
    /// 200 with the Song, its revision as the ETag; 404 when there is no such Song (<c>song_deleted</c>
    /// when it was deleted) or audio file; 409 <c>revision_conflict</c> with <c>current</c> (the Song);
    /// 422 <c>audio_file_not_in_song</c> for a file not associated with it,
    /// <c>audio_file_not_song_level</c> for one of its Generations' files, <c>validation_failed</c>
    /// when <c>audioFile</c> is not an ID.
    /// </summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> SetForSongAsync(
        CatalogReference reference,
        PreferredAudioFileRequest? request,
        PreferredAudioFileService preferences,
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

        if (FileOf(request) is not { } file)
        {
            return InvalidFile(context);
        }

        return await AnswerSongAsync(
            context,
            reference,
            revision!.Value,
            await preferences.SetForSongAsync(reference.Text, file, revision.Value, cancellationToken),
            deletions,
            loggers,
            cancellationToken);
    }

    /// <summary>200 with the Song, its revision as the ETag; 404 when there is no such Song; 409 <c>revision_conflict</c> with <c>current</c>.</summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> ClearForSongAsync(
        CatalogReference reference,
        PreferredAudioFileService preferences,
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

        return await AnswerSongAsync(
            context,
            reference,
            revision!.Value,
            await preferences.ClearForSongAsync(reference.Text, revision.Value, cancellationToken),
            deletions,
            loggers,
            cancellationToken);
    }

    private static Results<Ok<GenerationResponse>, ProblemHttpResult> AnswerGeneration(
        HttpContext context,
        int revision,
        PreferredAudioFileOutcome<GenerationSummary> outcome,
        ILoggerFactory loggers)
    {
        switch (outcome)
        {
            case PreferredAudioFileOutcome<GenerationSummary>.Done done:
                if (done.Owner.Generation.Revision != revision)
                {
                    Log(loggers).LogInformation("Preferred audio file of Generation {GenerationId} changed", done.Owner.Generation.Id);
                }

                Revisions.SetETag(context, done.Owner.Generation.Revision);
                return TypedResults.Ok(GenerationResponse.From(done.Owner, context.Request.PathBase));
            case PreferredAudioFileOutcome<GenerationSummary>.Conflict conflict:
                return Revisions.Conflict(context, GenerationResponse.From(conflict.Current, context.Request.PathBase));
            case PreferredAudioFileOutcome<GenerationSummary>.OwnerNotFound:
                return NoSuchGeneration(context);
            default:
                return FileProblem(context, outcome);
        }
    }

    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> AnswerSongAsync(
        HttpContext context,
        CatalogReference reference,
        int revision,
        PreferredAudioFileOutcome<SongSummary> outcome,
        SongDeletionService deletions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case PreferredAudioFileOutcome<SongSummary>.Done done:
                if (done.Owner.Revision != revision)
                {
                    Log(loggers).LogInformation("Preferred audio file of Song {SongId} changed", done.Owner.Id);
                }

                Revisions.SetETag(context, done.Owner.Revision);
                return TypedResults.Ok(SongResponse.From(done.Owner, context.Request.PathBase));
            case PreferredAudioFileOutcome<SongSummary>.Conflict conflict:
                return Revisions.Conflict(context, SongResponse.From(conflict.Current, context.Request.PathBase));
            case PreferredAudioFileOutcome<SongSummary>.OwnerNotFound:
                return await SongDeletionEndpoints.MissingSongAsync(context, reference, deletions, cancellationToken);
            default:
                return FileProblem(context, outcome);
        }
    }

    /// <summary>The answer to a file that cannot be chosen: 404 when there is none, 422 with its code otherwise.</summary>
    private static ProblemHttpResult FileProblem<TOwner>(HttpContext context, PreferredAudioFileOutcome<TOwner> outcome) => outcome switch
    {
        PreferredAudioFileOutcome<TOwner>.FileNotFound =>
            ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such audio file."),
        PreferredAudioFileOutcome<TOwner>.Refused refused =>
            ApiProblem.For(context, StatusCodes.Status422UnprocessableEntity, refused.Code, refused.Detail),
        _ => throw new InvalidOperationException("Unknown preferred audio file outcome."),
    };

    /// <summary>The file ID sent as <c>audioFile</c>, or null when it is missing or not an ID.</summary>
    private static Guid? FileOf(PreferredAudioFileRequest? request) =>
        request?.AudioFile is { ValueKind: JsonValueKind.String } text
        && Guid.TryParseExact(text.GetString()?.Trim(), "D", out var id)
            ? id
            : null;

    private static ProblemHttpResult InvalidFile(HttpContext context) =>
        ApiProblem.ValidationFailed(
            context,
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [PreferredAudioFileService.AudioFileField] = ["Name the audio file by its ID."],
            });

    private static string Source(ReportedAudioFile? file) => file is null ? "none" : "local";

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(PlaybackEndpoints));

    private static ProblemHttpResult NoSuchGeneration(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation.");
}

/// <summary>The file to prefer: <c>audioFile</c>, its ID, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record PreferredAudioFileRequest(JsonElement AudioFile);

/// <summary>
/// What plays for a Generation (#212): <c>source</c> (<c>local</c>, or <c>none</c> when no local file
/// can play), the file, and <c>reason</c>, a code (<c>generation_preferred</c>, <c>format_order</c>,
/// <c>preferred_missing_fallback</c>, <c>preferred_unavailable_fallback</c>, <c>nothing_available</c>).
/// </summary>
internal sealed record GenerationPlaybackResponse(string Source, PlaybackFileResponse? AudioFile, string Reason);

/// <summary>
/// What plays for a Song (#212): as for a Generation, with <c>song_preferred</c> and
/// <c>no_selected_generation</c> among the reasons, and <c>generation</c>, the Generation the file
/// came from (the Selected Generation), or null.
/// </summary>
internal sealed record SongPlaybackResponse(string Source, PlaybackFileResponse? AudioFile, string Reason, AudioFileLinkResponse? Generation);

/// <summary>The file that plays: its ID, name, format, duration (seconds, or null), and where its content is served, under the page base.</summary>
internal sealed record PlaybackFileResponse(Guid Id, string FileName, string Format, decimal? DurationSeconds, string ContentUrl)
{
    public static PlaybackFileResponse? From(ReportedAudioFile? reported, PathString pathBase)
    {
        if (reported is null)
        {
            return null;
        }

        var file = reported.File;
        return new(
            file.Id,
            file.FileName,
            file.Format,
            file.Duration is { } duration ? Math.Round((decimal)duration.TotalMilliseconds / 1000m, 3) : null,
            $"{pathBase}{AudioContentEndpoint.ContentPath.Replace("{id:guid}", file.Id.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)}");
    }
}
