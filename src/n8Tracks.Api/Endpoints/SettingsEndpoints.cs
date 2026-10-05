using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Backups;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Instance settings the administrator changes. Session-only: no credential, whatever its scopes,
/// reads or changes them. A setting has a revision; a change sends it in <c>If-Match</c> and is
/// answered with the new one as the <c>ETag</c>. Every answer is <c>no-store</c>.
/// </summary>
internal static class SettingsEndpoints
{
    public const string BackupSchedulePath = ApiProblem.VersionPrefix + "/settings/backup-schedule";

    public static IEndpointRouteBuilder MapSettings(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(BackupSchedulePath, GetBackupScheduleAsync)
            .WithName("GetBackupSchedule")
            .WithSummary("The backup schedule: on or off, daily or weekly, the time of day in the configured time zone, and how many scheduled backups are kept.")
            .SessionOnly()
            .Produces<BackupScheduleResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPut(BackupSchedulePath, PutBackupScheduleAsync)
            .WithName("SetBackupSchedule")
            .WithSummary("Replaces the backup schedule, given its revision in If-Match. Keep is 1 to 365; time is HH:mm.")
            .SessionOnly()
            .Produces<BackupScheduleResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with the schedule; its revision is the <c>ETag</c>.</summary>
    private static async Task<Ok<BackupScheduleResponse>> GetBackupScheduleAsync(
        BackupScheduleService schedules,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var stored = await schedules.GetAsync(cancellationToken);
        Revisions.SetETag(context, stored.Revision);
        return TypedResults.Ok(BackupScheduleResponse.From(stored));
    }

    /// <summary>200 with the new schedule; 422 on a missing or wrong field; 409 <c>revision_conflict</c> with the current one.</summary>
    private static async Task<Results<Ok<BackupScheduleResponse>, ProblemHttpResult>> PutBackupScheduleAsync(
        BackupScheduleRequest? request,
        BackupScheduleService schedules,
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

        var (schedule, errors) = BackupSchedule.Parse(request is null ? null : new BackupScheduleInput(request.Enabled, request.Frequency, request.Time, request.Keep));
        if (schedule is null)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        switch (await schedules.UpdateAsync(schedule, revision!.Value, cancellationToken))
        {
            case BackupScheduleUpdate.Updated updated:
                loggers.CreateLogger(typeof(SettingsEndpoints)).LogInformation(
                    "Backup schedule changed: {BackupScheduleEnabled} {BackupFrequency} {BackupTime} keep {BackupKeep}",
                    schedule.Enabled,
                    BackupSchedule.FrequencyText(schedule.Frequency),
                    BackupSchedule.TimeText(schedule.Time),
                    schedule.Keep);
                Revisions.SetETag(context, updated.Current.Revision);
                return TypedResults.Ok(BackupScheduleResponse.From(updated.Current));
            case BackupScheduleUpdate.Stale stale:
                return Revisions.Conflict(context, BackupScheduleResponse.From(stale.Current));
            default:
                throw new InvalidOperationException("Unknown backup schedule update.");
        }
    }
}

/// <summary>A backup schedule as a client sends it: every field required.</summary>
internal sealed record BackupScheduleRequest(bool? Enabled, string? Frequency, string? Time, int? Keep);

/// <summary>The backup schedule. <c>frequency</c> is <c>daily</c> or <c>weekly</c> (Sundays); <c>time</c> is <c>HH:mm</c> in the configured time zone.</summary>
internal sealed record BackupScheduleResponse(bool Enabled, string Frequency, string Time, int Keep, int Revision)
{
    public static BackupScheduleResponse From(StoredBackupSchedule stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return new(
            stored.Schedule.Enabled,
            BackupSchedule.FrequencyText(stored.Schedule.Frequency),
            BackupSchedule.TimeText(stored.Schedule.Time),
            stored.Schedule.Keep,
            stored.Revision);
    }
}
