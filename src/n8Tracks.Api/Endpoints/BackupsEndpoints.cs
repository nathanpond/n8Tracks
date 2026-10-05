using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Backups;

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
            .WithSummary("Deletes the archive, valid or not.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>200 with the archives, the destination, whether it shares a disk with the data, and the backup in progress.</summary>
    private static async Task<Ok<BackupListResponse>> ListAsync(BackupService backups, HttpContext context, CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return TypedResults.Ok(BackupListResponse.From(await backups.ListAsync(cancellationToken)));
    }

    /// <summary>202 with the job ID (and the job as <c>Location</c>); 409 <c>backup_in_progress</c> with the running job's ID.</summary>
    private static async Task<Results<Accepted<BackupStartResponse>, ProblemHttpResult>> StartAsync(
        BackupService backups,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var start = await backups.StartAsync(BackupKind.Manual, cancellationToken);
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

    /// <summary>204; 404 when there is no such archive.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        string location,
        string name,
        BackupService backups,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        return ParseLocation(location) is { } parsed && await backups.DeleteAsync(parsed, name, cancellationToken)
            ? TypedResults.NoContent()
            : NotFound(context);
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
/// disk with the data, the backup job queued or running (or null), and every archive, newest first.
/// </summary>
internal sealed record BackupListResponse(string Destination, bool SharesDiskWithData, Guid? ActiveJobId, BackupResponse[] Items)
{
    public static BackupListResponse From(BackupListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

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
                archive.Validity))]);
    }
}
