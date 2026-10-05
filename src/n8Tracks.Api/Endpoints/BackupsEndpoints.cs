using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Backups of the whole instance: list, back up now, download, delete. Every one is session-only:
/// no credential, whatever its scopes, can start, download, or delete a backup. An archive is
/// addressed by its folder (<c>mount</c> or <c>data</c>) and its file name; anything else in either
/// is 404. Every answer is <c>no-store</c>.
/// </summary>
internal static class BackupsEndpoints
{
    public const string BackupsPath = ApiProblem.VersionPrefix + "/backups";
    public const string BackupPath = BackupsPath + "/{location}/{name}";

    public const string InProgressCode = "backup_in_progress";
    public const string InvalidCode = "backup_invalid";
    public const string InUseCode = "backup_in_use";

    public const string MountLocation = "mount";
    public const string DataLocation = "data";

    public static IEndpointRouteBuilder MapBackups(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(BackupsPath, ListAsync)
            .WithName("ListBackups")
            .WithSummary("Every backup archive in the backup mount and the data path's fallback folder, newest first, with where the next one goes.")
            .SessionOnly()
            .Produces<BackupListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(BackupsPath, StartAsync)
            .WithName("StartBackup")
            .WithSummary("Queues a backup job and answers its ID; 409 backup_in_progress when one is queued or running.")
            .SessionOnly()
            .Produces<BackupStartResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet(BackupPath, DownloadAsync)
            .WithName("DownloadBackup")
            .WithSummary("Streams the archive as a download; 409 backup_invalid for a file that is not a valid backup.")
            .SessionOnly()
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapDelete(BackupPath, DeleteAsync)
            .WithName("DeleteBackup")
            .WithSummary("Deletes the archive, valid or not; 409 backup_in_use while a restore is reading it.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    /// <summary>
    /// 200 with the archives, the destination, whether it shares a disk with the data, the backup in
    /// progress, the last successful backup, and the schedule's state.
    /// </summary>
    private static async Task<Ok<BackupListResponse>> ListAsync(
        BackupService backups,
        BackupScheduleService schedules,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var listing = await backups.ListAsync(cancellationToken);
        var schedule = await schedules.GetStatusAsync(cancellationToken);
        return TypedResults.Ok(BackupListResponse.From(listing, schedule));
    }

    /// <summary>202 with the job ID (and the job as <c>Location</c>); 409 <c>backup_in_progress</c> with the running job's ID.</summary>
    private static async Task<Results<Accepted<BackupStartResponse>, ProblemHttpResult>> StartAsync(
        BackupService backups,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var start = await backups.StartAsync(BackupKind.Manual, cancellationToken);
        if (start.Deferred)
        {
            // Only between the maintenance check and the lock: the maintenance gate answers the rest.
            return Maintenance.MaintenanceMiddleware.Refusal(context);
        }

        if (start.AlreadyInProgress)
        {
            return ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                InProgressCode,
                "A backup is already queued or running.",
                [new("jobId", start.JobId)]);
        }

        return TypedResults.Accepted($"{context.Request.PathBase}{JobsEndpoints.JobsPath}/{start.JobId}", new BackupStartResponse(start.JobId));
    }

    /// <summary>200 streaming the file; 404 when there is no such archive; 409 <c>backup_invalid</c> for one that is not a valid backup.</summary>
    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> DownloadAsync(
        string location,
        string name,
        BackupService backups,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (ParseLocation(location) is not { } parsed
            || await backups.FindAsync(parsed, name, cancellationToken) is not { } archive)
        {
            return NotFound(context);
        }

        if (archive.Validity == BackupValidity.Invalid)
        {
            return ApiProblem.For(context, StatusCodes.Status409Conflict, InvalidCode, "This file is not a valid backup; it can only be deleted.");
        }

        if (backups.OpenRead(archive) is not { } stream)
        {
            return NotFound(context);
        }

        return TypedResults.File(stream, "application/zip", archive.Name);
    }

    /// <summary>204; 404 when there is no such archive; 409 <c>backup_in_use</c> while a restore is reading it.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        string location,
        string name,
        BackupService backups,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (ParseLocation(location) is not { } parsed)
        {
            return NotFound(context);
        }

        return await backups.DeleteAsync(parsed, name, cancellationToken) switch
        {
            BackupDeleteOutcome.Deleted => TypedResults.NoContent(),
            BackupDeleteOutcome.InUse => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                InUseCode,
                "A restore is reading this backup; it can be deleted once the restore has finished."),
            _ => NotFound(context),
        };
    }

    private static ProblemHttpResult NotFound(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such backup.");

    public static BackupLocation? ParseLocation(string location) => location switch
    {
        MountLocation => BackupLocation.Mount,
        DataLocation => BackupLocation.Data,
        _ => null,
    };

    public static string LocationText(BackupLocation location) => location == BackupLocation.Mount ? MountLocation : DataLocation;
}

/// <summary>The answer to "Back up now": the job to follow at <c>/api/v1/jobs/{jobId}</c>.</summary>
internal sealed record BackupStartResponse(Guid JobId);

/// <summary>One archive. <c>status</c> is <c>valid</c>, <c>newer</c> (made by a newer version), or <c>invalid</c>.</summary>
internal sealed record BackupResponse(
    string Location,
    string Name,
    long Size,
    DateTime CreatedAt,
    string? ApplicationVersion,
    string? Kind,
    BackupValidity Status);

/// <summary>
/// The Backups page: where the next backup goes (<c>mount</c> or <c>data</c>), whether that shares a
/// disk with the data, the backup job queued or running (or null), every archive, newest first, when
/// the newest valid archive of any kind was made (or null), and the schedule's state.
/// </summary>
internal sealed record BackupListResponse(
    string Destination,
    bool SharesDiskWithData,
    Guid? ActiveJobId,
    BackupResponse[] Items,
    DateTime? LastSuccessAt,
    BackupScheduleStatusResponse Schedule,
    LastRestoreResponse? LastRestore)
{
    public static BackupListResponse From(BackupListing listing, BackupScheduleStatus schedule)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(schedule);

        return new(
            BackupsEndpoints.LocationText(listing.Destination),
            listing.SharesDiskWithData,
            listing.ActiveJobId,
            [.. listing.Archives.Select(static archive => new BackupResponse(
                BackupsEndpoints.LocationText(archive.Location),
                archive.Name,
                archive.SizeBytes,
                archive.CreatedUtc.UtcDateTime,
                archive.ApplicationVersion,
                archive.Kind,
                archive.Validity))],
            listing.LastSuccessUtc?.UtcDateTime,
            BackupScheduleStatusResponse.From(schedule),
            listing.LastRestore is { } lastRestore ? LastRestoreResponse.From(lastRestore) : null);
    }
}

/// <summary>
/// How the last restore that began replacing data ended, shown until the next one: <c>outcome</c> is
/// <c>succeeded</c> or <c>rolled-back</c>; <c>failedStage</c> (null when it succeeded) is the stage it
/// failed at, as the maintenance status names it; <c>safetyBackup</c> is the backup taken before it,
/// with its full path for restoring by hand.
/// </summary>
internal sealed record LastRestoreResponse(
    string Outcome,
    DateTime FinishedAt,
    string Archive,
    string? FailedStage,
    string Detail,
    SafetyBackupResponse SafetyBackup)
{
    public static LastRestoreResponse From(LastRestore lastRestore)
    {
        ArgumentNullException.ThrowIfNull(lastRestore);

        return new(
            MaintenanceSnapshot.OutcomeText(lastRestore.Outcome),
            lastRestore.FinishedUtc.UtcDateTime,
            lastRestore.ArchiveName,
            lastRestore.FailedStage is { } stage ? MaintenanceSnapshot.StageText(stage) : null,
            lastRestore.Detail,
            new SafetyBackupResponse(
                BackupsEndpoints.LocationText(lastRestore.SafetyBackup.Location),
                lastRestore.SafetyBackup.Name,
                lastRestore.SafetyBackup.Path));
    }
}

/// <summary>A safety backup: its folder (<c>mount</c> or <c>data</c>), name, and full path.</summary>
internal sealed record SafetyBackupResponse(string Location, string Name, string Path);

/// <summary>
/// The schedule as the Backups page shows it: the settings, the next planned time (null when off),
/// and the latest scheduled attempt (null before the first), which stays until the next replaces it.
/// </summary>
internal sealed record BackupScheduleStatusResponse(
    bool Enabled,
    string Frequency,
    string Time,
    int Keep,
    DateTime? NextAt,
    BackupAttemptResponse? LastAttempt)
{
    public static BackupScheduleStatusResponse From(BackupScheduleStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var schedule = status.Stored.Schedule;
        return new(
            schedule.Enabled,
            BackupSchedule.FrequencyText(schedule.Frequency),
            BackupSchedule.TimeText(schedule.Time),
            schedule.Keep,
            status.NextUtc?.UtcDateTime,
            status.LastAttempt is { } attempt
                ? new BackupAttemptResponse(
                    attempt.Outcome,
                    attempt.StartedUtc.UtcDateTime,
                    attempt.FinishedUtc?.UtcDateTime,
                    attempt.Error,
                    status.RetryUtc?.UtcDateTime)
                : null);
    }
}

/// <summary>
/// One scheduled attempt: <c>outcome</c> is <c>running</c>, <c>succeeded</c>, or <c>failed</c> with
/// <c>error</c>; <c>retryAt</c> is when a first failure's one retry runs, or null.
/// </summary>
internal sealed record BackupAttemptResponse(BackupAttemptOutcome Outcome, DateTime StartedAt, DateTime? FinishedAt, string? Error, DateTime? RetryAt);
