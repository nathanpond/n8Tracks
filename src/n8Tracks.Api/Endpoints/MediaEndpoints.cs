using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;
using n8Tracks.Domain.Media;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The local media library (#203): starting a scan of the media folder, and reading the audio files
/// it cataloged. Starting a scan is session-only (invariant 7 keeps file-system actions away from
/// tokens and MCP); reading needs <c>catalog.read</c>. Every input names an audio file by ID, never
/// by a path, and no answer carries an absolute path but the media folder's own, as configured (the
/// path inside the container, never the host's). Every answer is <c>no-store</c>. The media status
/// says whether the media folder can be read, and since when (#207), and what the Media page shows
/// (#208): the files by status and association, the last scan and the last successful one, the scan in
/// progress, the schedule, and the majority-missing warning.
/// </summary>
internal static class MediaEndpoints
{
    public const string ScansPath = ApiProblem.VersionPrefix + "/media/scans";
    public const string StatusPath = ApiProblem.VersionPrefix + "/media/status";
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

        endpoints.MapGet(StatusPath, StatusAsync)
            .WithName("GetMediaStatus")
            .WithSummary("The media library's state: whether the media folder can be read and since when, the audio files by status and association, the last scan and the last successful one, the scan queued or running, the schedule and its next scan, and whether the last successful scan marked most files Missing.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<MediaStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(AudioFilesPath, ListAsync)
            .WithName("ListAudioFiles")
            .WithSummary("Cataloged audio files in path order, up to 200 at a time, filtered by reported status, association, and whether the header was readable.")
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

    /// <summary>200 with the media status; <c>mount.since</c> is null before anything was ever recorded, and <c>lastScan</c> before any scan ended.</summary>
    private static async Task<Ok<MediaStatusResponse>> StatusAsync(
        MediaStatusService statuses,
        N8TracksOptions options,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var status = await statuses.GetAsync(cancellationToken);
        return TypedResults.Ok(MediaStatusResponse.From(status, options.MediaPath));
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

        AudioFileReportedStatus? status = null;
        if (Single(query, "status", errors) is { } statusText)
        {
            status = MediaAvailabilityTexts.ParseReported(statusText);
            if (status is null)
            {
                errors["status"] = ["Use available, missing, or unavailable."];
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

        var page = await files.ListAsync(new AudioFileListRequest(status, association, readable, offset, limit), cancellationToken);
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

/// <summary>
/// The media status (#207, #208). <c>nextScheduledScan</c> is null while scheduled scans are off, and
/// may be in the past when one is due; <c>activeScanJobId</c> is the <c>media-scan</c> job queued or
/// running, or null.
/// </summary>
internal sealed record MediaStatusResponse(
    MediaMountResponse Mount,
    MediaFileCountsResponse Counts,
    MediaScanReportResponse? LastScan,
    MediaScanReportResponse? LastSuccessfulScan,
    Guid? ActiveScanJobId,
    MediaScheduleResponse Schedule,
    DateTime? NextScheduledScan,
    bool MajorityMissingWarning)
{
    public static MediaStatusResponse From(MediaStatus status, string folderPath)
    {
        ArgumentNullException.ThrowIfNull(status);

        var counts = status.Counts;
        return new(
            new MediaMountResponse(MediaAvailabilityTexts.Text(status.Mount.State), status.Mount.SinceUtc?.UtcDateTime, folderPath),
            new MediaFileCountsResponse(counts.Total, counts.Available, counts.Missing, counts.Associated, counts.Unmatched),
            MediaScanReportResponse.From(status.LastScan),
            MediaScanReportResponse.From(status.LastSuccessfulScan),
            status.ActiveScanJobId,
            new MediaScheduleResponse(status.Schedule.Enabled, status.Schedule.IntervalMinutes),
            status.NextScheduledScan?.UtcDateTime,
            status.MajorityMissingWarning);
    }
}

/// <summary>
/// Whether the media folder can be read (<c>available</c> or <c>unavailable</c>), since when (null
/// before anything was recorded), and the folder's path as configured: inside the container.
/// </summary>
internal sealed record MediaMountResponse(string State, DateTime? Since, string Path);

/// <summary>The cataloged audio files by stored status and by association; Unmatched is every file with no association, Missing ones included.</summary>
internal sealed record MediaFileCountsResponse(int Total, int Available, int Missing, int Associated, int Unmatched);

/// <summary>The scan schedule (#204): on or off, and the minutes from the end of one scan to the next.</summary>
internal sealed record MediaScheduleResponse(bool Enabled, int IntervalMinutes);

/// <summary>
/// One finished scan. <c>trigger</c> (<c>manual</c>, <c>startup</c>, <c>scheduled</c>, <c>recovery</c>)
/// and <c>counts</c> are null when the scan ended without a summary (cut off by a restart).
/// <c>failure</c> is null when it succeeded, otherwise <c>media_folder_unavailable</c>,
/// <c>interrupted</c>, or <c>failed</c>.
/// </summary>
internal sealed record MediaScanReportResponse(
    Guid JobId,
    string? Trigger,
    string Outcome,
    DateTime StartedAt,
    DateTime FinishedAt,
    decimal DurationSeconds,
    MediaScanCountsResponse? Counts,
    string? Failure)
{
    public static MediaScanReportResponse? From(MediaScanReport? report)
    {
        if (report is null)
        {
            return null;
        }

        return new(
            report.JobId,
            report.Trigger is { } trigger ? MediaScanTriggers.Text(trigger) : null,
            report.Outcome == MediaScanOutcome.Succeeded ? "succeeded" : "failed",
            report.StartedUtc.UtcDateTime,
            report.FinishedUtc.UtcDateTime,
            Math.Max(0m, Math.Round((decimal)report.Duration.TotalSeconds, 3)),
            report.Counts is { } counts ? MediaScanCountsResponse.From(counts) : null,
            report.Failure switch
            {
                null => null,
                MediaScanFailure.FolderUnavailable => "media_folder_unavailable",
                MediaScanFailure.Interrupted => "interrupted",
                _ => "failed",
            });
    }
}

/// <summary>What a scan counted, as its job result reports it.</summary>
internal sealed record MediaScanCountsResponse(
    int Seen,
    int New,
    int Changed,
    int Unchanged,
    int Missing,
    int Restored,
    int Associated,
    int Unmatched,
    int Skipped,
    int Unreadable,
    int UnreadableDirectories,
    int AvailableBefore)
{
    public static MediaScanCountsResponse From(MediaScanCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return new(
            counts.Seen,
            counts.New,
            counts.Changed,
            counts.Unchanged,
            counts.Missing,
            counts.Restored,
            counts.Associated,
            counts.Unmatched,
            counts.Skipped,
            counts.Unreadable,
            counts.UnreadableDirectories,
            counts.AvailableBefore);
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
/// of the association. <c>status</c> is the reported status (#207): <c>unavailable</c> while the media
/// folder cannot be read, otherwise <c>storedStatus</c> (<c>available</c> or <c>missing</c>).
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
    string StoredStatus,
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
    public static AudioFileResponse From(ReportedAudioFile reported)
    {
        ArgumentNullException.ThrowIfNull(reported);

        var file = reported.File;
        return new(
            file.Id,
            file.Path,
            file.FileName,
            file.Format,
            file.SizeBytes,
            file.ModifiedUtc.UtcDateTime,
            file.FirstSeenUtc.UtcDateTime,
            file.LastSeenUtc.UtcDateTime,
            MediaAvailabilityTexts.Text(reported.Status),
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
