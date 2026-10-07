using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The local media library (#203): starting a scan of the media folder, and reading the audio files
/// it cataloged. Starting a scan is session-only (invariant 7 keeps file-system actions away from
/// tokens and MCP); reading needs <c>catalog.read</c>. Every input names an audio file by ID, never
/// by a path, and no answer carries an absolute path. Every answer is <c>no-store</c>.
/// </summary>
internal static class MediaEndpoints
{
    public const string ScansPath = ApiProblem.VersionPrefix + "/media/scans";
    public const string AudioFilesPath = ApiProblem.VersionPrefix + "/audio-files";
    public const string AudioFilePath = AudioFilesPath + "/{id:guid}";

    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(ScansPath, StartScanAsync)
            .WithName("StartMediaScan")
            .WithSummary("Queues a scan of the media folder and answers its job; a scan already queued or running is answered instead of starting another.")
            .SessionOnly()
            .Produces<MediaScanStartResponse>(StatusCodes.Status202Accepted)
            .Produces<MediaScanStartResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(AudioFilesPath, ListAsync)
            .WithName("ListAudioFiles")
            .WithSummary("Cataloged audio files in path order, up to 200 at a time, filtered by status, association, and whether the header was readable.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<AudioFileListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(AudioFilePath, GetAsync)
            .WithName("GetAudioFile")
            .WithSummary("One cataloged audio file.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<AudioFileResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>202 with the new job (and the job as <c>Location</c>); 200 with the job already queued or running.</summary>
    private static async Task<Results<Accepted<MediaScanStartResponse>, Ok<MediaScanStartResponse>>> StartScanAsync(
        MediaScanService scans,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var start = await scans.StartAsync(MediaScanTrigger.Manual, cancellationToken);
        var response = new MediaScanStartResponse(start.JobId, start.AlreadyInProgress);
        return start.AlreadyInProgress
            ? TypedResults.Ok(response)
            : TypedResults.Accepted($"{context.Request.PathBase}{JobsEndpoints.JobsPath}/{start.JobId}", response);
    }

    /// <summary>200 with a page; 422 <c>validation_failed</c> for a malformed or unknown query value, or a limit over 200.</summary>
    private static async Task<Results<Ok<AudioFileListResponse>, ProblemHttpResult>> ListAsync(
        AudioFileService files,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var query = context.Request.Query;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        AudioFileStatus? status = null;
        if (Single(query, "status", errors) is { } statusText)
        {
            status = AudioFormats.ParseStatus(statusText);
            if (status is null)
            {
                errors["status"] = ["Use available or missing."];
            }
        }

        var association = AudioFileAssociation.Any;
        if (Single(query, "association", errors) is { } associationText)
        {
            switch (associationText)
            {
                case "any":
                    break;
                case "associated":
                    association = AudioFileAssociation.Associated;
                    break;
                case "none":
                    association = AudioFileAssociation.None;
                    break;
                default:
                    errors["association"] = ["Use any, associated, or none."];
                    break;
            }
        }

        bool? readable = null;
        if (Single(query, "metadataReadable", errors) is { } readableText)
        {
            readable = readableText switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            };
            if (readable is null)
            {
                errors["metadataReadable"] = ["Use true or false."];
            }
        }

        var offset = Number(query, "offset", 0, 0, int.MaxValue, errors);
        var limit = Number(query, "limit", AudioFileService.MaximumLimit, 1, AudioFileService.MaximumLimit, errors);

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var page = await files.ListAsync(new AudioFileQuery(status, association, readable, offset, limit), cancellationToken);
        return TypedResults.Ok(new AudioFileListResponse(page.Items.Select(AudioFileResponse.From).ToArray(), page.Total, offset, limit));
    }

    /// <summary>200 with the file; 404 <c>not_found</c> when there is none.</summary>
    private static async Task<Results<Ok<AudioFileResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        AudioFileService files,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return await files.FindAsync(id, cancellationToken) is { } file
            ? TypedResults.Ok(AudioFileResponse.From(file))
            : ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such audio file.");
    }

    /// <summary>The one value of <paramref name="name"/>, or null when it is absent; a repeated parameter is an error.</summary>
    private static string? Single(IQueryCollection query, string name, Dictionary<string, string[]> errors)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return null;
        }

        if (values.Count > 1)
        {
            errors[name] = ["Give this at most once."];
            return null;
        }

        return values[0] ?? string.Empty;
    }

    private static int Number(IQueryCollection query, string name, int absent, int minimum, int maximum, Dictionary<string, string[]> errors)
    {
        if (Single(query, name, errors) is not { } text)
        {
            return absent;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
        {
            errors[name] = [maximum == int.MaxValue
                ? string.Create(CultureInfo.InvariantCulture, $"Use a whole number of at least {minimum}.")
                : string.Create(CultureInfo.InvariantCulture, $"Use a whole number from {minimum} to {maximum}.")];
            return absent;
        }

        return value;
    }
}

/// <summary>The scan's job, and whether it was already queued or running.</summary>
internal sealed record MediaScanStartResponse(Guid JobId, bool AlreadyInProgress);

/// <summary>One page of audio files.</summary>
internal sealed record AudioFileListResponse(AudioFileResponse[] Items, int Total, int Offset, int Limit);

/// <summary>
/// An audio file as the API answers it. The path is relative to the media folder, never absolute.
/// Its association (#206): <c>song</c> and <c>generation</c> as <c>{id, shortcode}</c> or null,
/// <c>associationOrigin</c> (<c>suno-id</c> or <c>user</c>) while associated, and
/// <c>unmatchedReason</c> (a code: <c>generation_deleted</c>, <c>multiple_suno_ids</c>,
/// <c>unassociated_by_user</c>, <c>song_deleted</c>) or null. <c>revision</c> rises with every change
/// of the association.
/// </summary>
internal sealed record AudioFileResponse(
    Guid Id,
    string Path,
    string FileName,
    string Format,
    long SizeBytes,
    DateTime ModifiedAt,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    string Status,
    bool MetadataReadable,
    decimal? DurationSeconds,
    string? Title,
    string? Artist,
    AudioFileLinkResponse? Song,
    AudioFileLinkResponse? Generation,
    string? AssociationOrigin,
    string? UnmatchedReason,
    int Revision)
{
    public static AudioFileResponse From(AudioFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new(
            file.Id,
            file.Path,
            file.FileName,
            file.Format,
            file.SizeBytes,
            file.ModifiedUtc.UtcDateTime,
            file.FirstSeenUtc.UtcDateTime,
            file.LastSeenUtc.UtcDateTime,
            AudioFormats.StatusText(file.Status),
            file.MetadataReadable,
            file.Duration is { } duration ? Math.Round((decimal)duration.TotalMilliseconds / 1000m, 3) : null,
            file.Title,
            file.Artist,
            file.Link is { } link ? new AudioFileLinkResponse(link.Song.Id, link.Song.Shortcode) : null,
            file.Link?.Generation is { } generation ? new AudioFileLinkResponse(generation.Id, generation.Shortcode) : null,
            file.Link is { } origin ? AudioFileAssociations.Text(origin.Origin) : null,
            file.UnmatchedReason is { } reason ? AudioFileAssociations.Text(reason) : null,
            file.Revision);
    }
}

/// <summary>The Song or Generation an audio file is associated with.</summary>
internal sealed record AudioFileLinkResponse(Guid Id, string Shortcode);
