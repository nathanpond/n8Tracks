using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Maintenance;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Backups;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Restoring the instance from a backup, in two steps: validate an archive (a listed one, or an
/// upload) and get its summary and a validation ID, then confirm with that ID and the typed word
/// <c>RESTORE</c>, which starts the restore (202) and puts the instance into maintenance. A bad
/// archive is refused while validating, synchronously, so maintenance is never entered for one.
/// Every endpoint is session-only: no credential, whatever its scopes, can restore. Every answer is
/// <c>no-store</c>.
/// </summary>
internal static class RestoresEndpoints
{
    public const string RestoresPath = ApiProblem.VersionPrefix + "/restores";
    public const string ValidatePath = RestoresPath + "/validate";
    public const string UploadsPath = RestoresPath + "/uploads";

    public const string NewerSchemaCode = "backup_newer_schema";
    public const string BlockedByJobsCode = "restore_blocked_by_jobs";
    public const string InsufficientSpaceCode = "insufficient_space";
    public const string UploadTooLargeCode = "upload_too_large";

    /// <summary>Room for the multipart framing around the file, beyond the file's own limit.</summary>
    private const long MultipartAllowance = 1024 * 1024;

    public static IEndpointRouteBuilder MapRestores(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(ValidatePath, ValidateListedAsync)
            .WithName("ValidateRestoreFromBackup")
            .WithSummary("Validates a listed backup fully and answers its summary and a validation ID; 422 backup_invalid or backup_newer_schema, 507 insufficient_space.")
            .SessionOnly()
            .Produces<RestoreValidationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status507InsufficientStorage);

        endpoints.MapPost(UploadsPath, ValidateUploadAsync)
            .WithName("ValidateRestoreFromUpload")
            .WithSummary("Streams an uploaded archive (multipart/form-data, one file) to the data path and validates it; 413 upload_too_large over 20 GB.")
            .SessionOnly()
            .Produces<RestoreValidationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status507InsufficientStorage);

        endpoints.MapPost(RestoresPath, StartAsync)
            .WithName("StartRestore")
            .WithSummary("Starts the restore of a validated archive when the confirmation is RESTORE; the instance enters maintenance. 409 backup_in_progress or restore_blocked_by_jobs.")
            .SessionOnly()
            .Produces<MaintenanceResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>200 with the summary; 404 when there is no such backup; a refusal otherwise.</summary>
    private static async Task<Results<Ok<RestoreValidationResponse>, ProblemHttpResult>> ValidateListedAsync(
        RestoreFromBackupRequest request,
        RestoreValidator validator,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (BackupsEndpoints.ParseLocation(request.Location ?? string.Empty) is not { } location || request.Name is not { } name)
        {
            return NoSuchBackup(context);
        }

        return Answer(context, await validator.ValidateListedAsync(location, name, cancellationToken));
    }

    /// <summary>
    /// Reads the request as a stream, never buffering it: the first file part is written straight to
    /// a temporary file under the data path, counted against the limit as it arrives.
    /// </summary>
    private static async Task<Results<Ok<RestoreValidationResponse>, ProblemHttpResult>> ValidateUploadAsync(
        RestoreValidator validator,
        RestoreOptions options,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = options.MaxUploadBytes + MultipartAllowance;
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
        {
            return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send the archive as multipart/form-data with one file.");
        }

        var declared = context.Request.ContentLength is { } length ? Math.Max(0, length - MultipartAllowance) : (long?)null;
        var reader = new MultipartReader(boundary, context.Request.Body);
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
        {
            if (section.GetContentDispositionHeader() is { } disposition && disposition.IsFileDisposition())
            {
                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? string.Empty;
                return Answer(context, await validator.ValidateUploadAsync(section.Body, fileName, declared, cancellationToken));
            }
        }

        return ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send the archive as multipart/form-data with one file.");
    }

    /// <summary>
    /// 202 with the maintenance state, and <c>Location</c> the maintenance status; 404 for an unknown
    /// or expired validation; 422 when the confirmation is not <c>RESTORE</c>; 409 when a backup,
    /// another restore, or any job is under way.
    /// </summary>
    private static async Task<Results<Accepted<MaintenanceResponse>, ProblemHttpResult>> StartAsync(
        RestoreStartRequest request,
        RestoreService restores,
        Application.Maintenance.MaintenanceMode maintenance,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (!Guid.TryParse(request.ValidationId, out var validationId))
        {
            return NoSuchValidation(context);
        }

        return await restores.StartAsync(validationId, request.Confirmation, cancellationToken) switch
        {
            RestoreStartOutcome.Started => TypedResults.Accepted(
                $"{context.Request.PathBase}{MaintenanceEndpoints.StatusPath}",
                MaintenanceResponse.From(maintenance.Current)),
            RestoreStartOutcome.ConfirmationMismatch => ApiProblem.ValidationFailed(
                context,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["confirmation"] = [$"Type {RestoreOptions.ConfirmationWord} to confirm."] }),
            RestoreStartOutcome.BackupInProgress => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                BackupsEndpoints.InProgressCode,
                "A backup or a restore is queued or running. Try again when it has finished."),
            RestoreStartOutcome.BlockedByJobs => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                BlockedByJobsCode,
                "Background work is queued or running. Try again when it has finished."),
            _ => NoSuchValidation(context),
        };
    }

    private static Results<Ok<RestoreValidationResponse>, ProblemHttpResult> Answer(HttpContext context, RestoreValidationOutcome outcome) => outcome switch
    {
        RestoreValidationOutcome.Valid valid => TypedResults.Ok(RestoreValidationResponse.From(valid.Validation)),
        RestoreValidationOutcome.Refused refused => Refusal(context, refused.Refusal),
        _ => NoSuchBackup(context),
    };

    /// <summary>The problem for a refusal: its reason, and the version needed or the space short where it has them.</summary>
    private static ProblemHttpResult Refusal(HttpContext context, RestoreRefusal refusal)
    {
        var reason = RestoreRefusal.ReasonText(refusal.Reason);
        return refusal switch
        {
            { Reason: RestoreRefusalReason.TooLarge } => ApiProblem.For(context, StatusCodes.Status413PayloadTooLarge, UploadTooLargeCode, refusal.Message),
            { Reason: RestoreRefusalReason.InsufficientSpace } => ApiProblem.For(
                context,
                StatusCodes.Status507InsufficientStorage,
                InsufficientSpaceCode,
                refusal.Message,
                [new("requiredBytes", refusal.RequiredBytes), new("availableBytes", refusal.AvailableBytes)]),
            { NeedsNewerVersion: true } => ApiProblem.For(
                context,
                StatusCodes.Status422UnprocessableEntity,
                NewerSchemaCode,
                refusal.Message,
                [new("reason", reason), new("neededVersion", refusal.NeededVersion)]),
            _ => ApiProblem.For(context, StatusCodes.Status422UnprocessableEntity, BackupsEndpoints.InvalidCode, refusal.Message, [new("reason", reason)]),
        };
    }

    private static ProblemHttpResult NoSuchBackup(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such backup.");

    private static ProblemHttpResult NoSuchValidation(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such validation; it may have expired. Validate the backup again.");
}

/// <summary>The listed backup to validate: its folder (<c>mount</c> or <c>data</c>) and file name.</summary>
internal sealed record RestoreFromBackupRequest(string? Location, string? Name);

/// <summary>The validation to restore, and the word the administrator typed.</summary>
internal sealed record RestoreStartRequest(string? ValidationId, string? Confirmation);

/// <summary>A valid archive's summary, and the ID that confirms it until <c>expiresAt</c>.</summary>
internal sealed record RestoreValidationResponse(Guid ValidationId, DateTime ExpiresAt, RestoreArchiveResponse Archive)
{
    public static RestoreValidationResponse From(RestoreValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var summary = validation.Summary;
        return new(
            validation.Id,
            validation.ExpiresUtc.UtcDateTime,
            new RestoreArchiveResponse(
                summary.Name,
                summary.Location is { } location ? BackupsEndpoints.LocationText(location) : null,
                summary.SizeBytes,
                summary.CreatedUtc.UtcDateTime,
                summary.ApplicationVersion,
                summary.Kind));
    }
}

/// <summary>What the confirmation shows: <c>location</c> is null for an upload.</summary>
internal sealed record RestoreArchiveResponse(string Name, string? Location, long Size, DateTime CreatedAt, string ApplicationVersion, string Kind);
