using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Media;
using n8Tracks.Application.References;
using n8Tracks.Domain.Media;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Download records (#222): the extension reports each file it downloaded from Suno, with its
/// <c>suno.sync</c> token, and the Generation panel reads a Generation's records. A record says a file
/// was fetched to the user's computer, not that it is in the media folder, and creates nothing else.
/// </summary>
internal static class DownloadRecordsEndpoints
{
    public const string DownloadsPath = ApiProblem.VersionPrefix + "/suno/downloads";
    public const string GenerationDownloadsPath = GenerationsEndpoints.GenerationPath + "/downloads";

    public static IEndpointRouteBuilder MapDownloadRecords(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(DownloadsPath, RecordAsync)
            .WithName("RecordSunoDownload")
            .WithSummary("Records a file the extension downloaded from Suno (#222): { id (the report's own UUID), sunoId (a UUID), format (wav, mp3, m4a, or m4a-stream), fileName (the base name saved, at most 255 characters), completedAt, sizeBytes (optional, not negative), spentUnlock }. Kept whether or not the clip is a Generation, and creates nothing else. 201 for a new record; 200 with the stored record when one with that ID exists (nothing changes, whatever the body says). A completion time later than now is taken as now.")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<DownloadRecordResponse>(StatusCodes.Status201Created)
            .Produces<DownloadRecordResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(GenerationDownloadsPath, ListForGenerationAsync)
            .WithName("ListGenerationDownloads")
            .WithSummary("A Generation's download records (#222), by its Suno ID, newest first, at most 100: { items: [{ id, sunoId, format, fileName, completedAt, receivedAt, sizeBytes, spentUnlock, mediaFolder: { match: attached | elsewhere | not-found, audioFile: { id, status } | null } }] }. match compares the record's file name with scanned audio files' names, without regard to case and without the browser's \" (n)\" numbering: attached when such a file is associated with this Generation, elsewhere when one exists but is associated otherwise or not at all.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenerationDownloadListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>201 with the new record; 200 with the stored one for a repeated ID; 422 <c>validation_failed</c> keyed by field.</summary>
    private static async Task<Results<Created<DownloadRecordResponse>, Ok<DownloadRecordResponse>, ProblemHttpResult>> RecordAsync(
        JsonElement? body,
        DownloadRecordService downloads,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (report, errors) = Read(body);
        if (report is null)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var outcome = await downloads.RecordAsync(report, cancellationToken);
        var log = loggers.CreateLogger(typeof(DownloadRecordsEndpoints));
        switch (outcome)
        {
            case DownloadRecordOutcome.Recorded recorded:
                log.LogInformation("Download recorded: {RecordId}, format {Format}", recorded.Record.Id, recorded.Record.Format);
                return TypedResults.Created($"{context.Request.PathBase}{DownloadsPath}/{recorded.Record.Id}", DownloadRecordResponse.From(recorded.Record));
            case DownloadRecordOutcome.AlreadyRecorded already:
                log.LogInformation("Download already recorded: {RecordId}", already.Record.Id);
                return TypedResults.Ok(DownloadRecordResponse.From(already.Record));
            case DownloadRecordOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(invalid.Errors, StringComparer.Ordinal));
            default:
                throw new InvalidOperationException("Unknown download record outcome.");
        }
    }

    /// <summary>200 with the Generation's records; 404 when the reference names no Generation.</summary>
    private static async Task<Results<Ok<GenerationDownloadListResponse>, ProblemHttpResult>> ListForGenerationAsync(
        CatalogReference reference,
        GenerationService generations,
        DownloadRecordService downloads,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await generations.FindAsync(reference, cancellationToken) is not { } found)
        {
            return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Generation.");
        }

        var items = await downloads.ListForGenerationAsync(found.Generation.Id, found.Generation.SunoId, cancellationToken);
        return TypedResults.Ok(new GenerationDownloadListResponse([.. items.Select(GenerationDownloadResponse.From)]));
    }

    /// <summary>The report in the body, or each field's problem: every field is read as raw JSON so a wrong type is a field error.</summary>
    private static (DownloadReport? Report, Dictionary<string, string[]> Errors) Read(JsonElement? body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (body is not { ValueKind: JsonValueKind.Object } root)
        {
            errors["body"] = ["Send the download as a JSON object."];
            return (null, errors);
        }

        string? Text(string name, string problem)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            errors[name] = [problem];
            return null;
        }

        var idText = Text(DownloadRecordService.IdField, "Send the report's own ID, a UUID.");
        var id = Guid.Empty;
        if (idText is not null && !Guid.TryParseExact(idText, "D", out id))
        {
            errors[DownloadRecordService.IdField] = ["Send the report's own ID, a UUID."];
        }

        var sunoId = Text(DownloadRecordService.SunoIdField, "Send the clip's Suno ID, a UUID.");
        var format = Text(DownloadRecordService.FormatField, $"Send one of {string.Join(", ", DownloadFormats.All)}.");
        var fileName = Text(DownloadRecordService.FileNameField, "Send the saved file's name.");
        var completedText = Text(DownloadRecordService.CompletedAtField, "Send when the download finished, as an ISO 8601 time with its offset.");
        var completed = default(DateTimeOffset);
        if (completedText is not null
            && !DateTimeOffset.TryParse(completedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out completed))
        {
            errors[DownloadRecordService.CompletedAtField] = ["Send when the download finished, as an ISO 8601 time with its offset."];
        }

        long? size = null;
        if (root.TryGetProperty(DownloadRecordService.SizeBytesField, out var sizeValue) && sizeValue.ValueKind != JsonValueKind.Null)
        {
            if (sizeValue.ValueKind == JsonValueKind.Number && sizeValue.TryGetInt64(out var bytes))
            {
                size = bytes;
            }
            else
            {
                errors[DownloadRecordService.SizeBytesField] = ["Send the size in bytes, a whole number, or leave it out."];
            }
        }

        var spent = false;
        if (root.TryGetProperty(DownloadRecordService.SpentUnlockField, out var spentValue)
            && spentValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            spent = spentValue.GetBoolean();
        }
        else
        {
            errors[DownloadRecordService.SpentUnlockField] = ["Send whether the run spent a Suno download unlock on the clip, true or false."];
        }

        return errors.Count > 0
            ? (null, errors)
            : (new DownloadReport(id, sunoId!, format!, fileName!, completed, size, spent), errors);
    }
}

/// <summary>A download record as answered.</summary>
internal sealed record DownloadRecordResponse(
    Guid Id,
    string SunoId,
    string Format,
    string FileName,
    DateTimeOffset CompletedAt,
    DateTimeOffset ReceivedAt,
    long? SizeBytes,
    bool SpentUnlock)
{
    public static DownloadRecordResponse From(DownloadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new(record.Id, record.SunoId, record.Format, record.FileName, record.CompletedUtc, record.ReceivedUtc, record.SizeBytes, record.SpentUnlock);
    }
}

/// <summary>A Generation's download records.</summary>
internal sealed record GenerationDownloadListResponse(IReadOnlyList<GenerationDownloadResponse> Items);

/// <summary>A download record with whether the media folder has a scanned file of its name.</summary>
internal sealed record GenerationDownloadResponse(
    Guid Id,
    string SunoId,
    string Format,
    string FileName,
    DateTimeOffset CompletedAt,
    DateTimeOffset ReceivedAt,
    long? SizeBytes,
    bool SpentUnlock,
    DownloadMediaFolderResponse MediaFolder)
{
    public static GenerationDownloadResponse From(GenerationDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);

        var record = download.Record;
        return new(
            record.Id,
            record.SunoId,
            record.Format,
            record.FileName,
            record.CompletedUtc,
            record.ReceivedUtc,
            record.SizeBytes,
            record.SpentUnlock,
            new DownloadMediaFolderResponse(
                download.Match switch
                {
                    DownloadMediaMatch.Attached => "attached",
                    DownloadMediaMatch.Elsewhere => "elsewhere",
                    _ => "not-found",
                },
                download.File is { } file ? new DownloadMediaFileResponse(file.Id, MediaAvailabilityTexts.Text(file.Status)) : null));
    }
}

/// <summary>Whether a scanned file of a record's name is attached to the Generation, elsewhere, or not found.</summary>
internal sealed record DownloadMediaFolderResponse(string Match, DownloadMediaFileResponse? AudioFile);

/// <summary>The scanned file found, with the status it reports (available, missing, or unavailable).</summary>
internal sealed record DownloadMediaFileResponse(Guid Id, string Status);
