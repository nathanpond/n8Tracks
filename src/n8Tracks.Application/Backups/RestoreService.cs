using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Application.Backups;

/// <summary>
/// Starts and runs a restore of a validated archive. Starting checks the typed confirmation, that no
/// backup or other restore is under way, and that no background job is queued or running, then
/// enters maintenance before anything else happens, all under the backup start lock so no backup can
/// be queued in between. The run, in the background, lets requests in flight finish (aborting what
/// is left after the grace period), validates the archive again, and takes and verifies a safety
/// backup, all inside maintenance, so nothing can be written between the safety backup and the
/// replacement. Every action is the signed-in administrator's alone; the endpoints enforce that.
/// </summary>
public sealed class RestoreService(
    RestoreValidator validator,
    RestoreValidations validations,
    RestoreReads reads,
    MaintenanceMode maintenance,
    IJobStore jobs,
    BackupStartLock startLock,
    IRestoreRunner runner,
    IRequestDrain drain,
    IBackupStorage storage,
    IBackupWriter writer,
    IRestoreArchives archives,
    RestoreOptions options,
    TimeProvider time)
{
    /// <summary>
    /// Begins the restore validated as <paramref name="validationId"/> when <paramref name="confirmation"/>
    /// is exactly <c>RESTORE</c>. A refused start changes nothing and leaves the validation usable.
    /// </summary>
    public async Task<RestoreStartOutcome> StartAsync(Guid validationId, string? confirmation, CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmation, RestoreOptions.ConfirmationWord, StringComparison.Ordinal))
        {
            return validations.Find(validationId, time.GetUtcNow()) is null ? RestoreStartOutcome.NotFound : RestoreStartOutcome.ConfirmationMismatch;
        }

        validator.Sweep();
        await startLock.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (validations.Find(validationId, time.GetUtcNow()) is null)
            {
                return RestoreStartOutcome.NotFound;
            }

            if (maintenance.IsActive || await jobs.FindActiveAsync(BackupService.JobType, cancellationToken).ConfigureAwait(false) is not null)
            {
                return RestoreStartOutcome.BackupInProgress;
            }

            if (await jobs.AnyActiveAsync(cancellationToken).ConfigureAwait(false))
            {
                return RestoreStartOutcome.BlockedByJobs;
            }

            if (!maintenance.TryBegin(MaintenanceStage.Validating))
            {
                return RestoreStartOutcome.BackupInProgress;
            }

            // Taken only now: a refused start leaves the validation for another try.
            var validation = validations.Take(validationId, time.GetUtcNow());
            if (validation is null)
            {
                maintenance.End(MaintenanceOutcome.Failed);
                return RestoreStartOutcome.NotFound;
            }

            runner.Start(new RestorePlan(Guid.CreateVersion7(), validation));
            return RestoreStartOutcome.Started;
        }
        finally
        {
            startLock.Gate.Release();
        }
    }

    /// <summary>
    /// The restore's background run, inside the maintenance that <see cref="StartAsync"/> began; it
    /// always leaves maintenance before it returns. An upload is deleted at the end, whatever happened.
    /// </summary>
    public async Task<RestoreRunResult> RunAsync(RestorePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var source = plan.Validation.Source;
        using var hold = source is RestoreSource.Listed listed ? reads.Hold(listed.Location, listed.Name) : null;
        string? safetyBackup = null;
        try
        {
            maintenance.Report(MaintenanceStage.Validating, 0);
            await drain.DrainAsync(options.DrainGrace, cancellationToken).ConfigureAwait(false);

            if (await validator.RecheckAsync(plan.Validation, cancellationToken).ConfigureAwait(false) is { } refusal)
            {
                return End(MaintenanceOutcome.Failed, $"The archive failed validation when the restore began: {refusal.Message} Nothing was changed.", null, null);
            }

            maintenance.Report(MaintenanceStage.SafetyBackup, 0);
            var created = await writer.CreateAsync(
                storage.ResolveDestination(),
                plan.Id,
                BackupKind.Safety,
                (phase, percent) => maintenance.Report(MaintenanceStage.SafetyBackup, BackupJobHandler.Overall(phase, percent)),
                cancellationToken).ConfigureAwait(false);
            safetyBackup = created.Name;
            maintenance.Report(MaintenanceStage.SafetyBackup, 100);

            // Replacing the database and assets with the archive's comes with the restore story (#74).
            return End(
                MaintenanceOutcome.Failed,
                "The archive is valid and a safety backup was taken, but replacing the instance's data is not available in this build. Nothing was changed.",
                safetyBackup,
                null);
        }
        catch (BackupVerificationException exception)
        {
            return End(MaintenanceOutcome.Failed, $"The safety backup could not be taken: {exception.Message} Nothing was changed.", null, exception);
        }
        catch (Exception exception)
        {
            return End(MaintenanceOutcome.Failed, "The restore stopped before anything was replaced. Nothing was changed.", safetyBackup, exception);
        }
        finally
        {
            if (source is RestoreSource.Uploaded upload)
            {
                archives.DeleteUpload(upload.UploadId);
            }
        }
    }

    private RestoreRunResult End(MaintenanceOutcome outcome, string detail, string? safetyBackup, Exception? error)
    {
        maintenance.End(outcome);
        return new RestoreRunResult(outcome, detail, safetyBackup) { Error = error };
    }
}
