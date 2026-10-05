using n8Tracks.Application.Jobs;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Setup;

namespace n8Tracks.Application.Backups;

/// <summary>
/// Starts and runs a restore of a validated archive. Starting checks the typed confirmation, that no
/// backup or other restore is under way, and that no background job is queued or running, then
/// enters maintenance before anything else happens, all under the backup start lock so no backup can
/// be queued in between. The run, in the background, lets requests in flight finish (aborting what
/// is left after the grace period), validates the archive again, takes and verifies a safety
/// backup, and replaces the live data with the archive's, all inside maintenance, so nothing can be
/// written between the safety backup and the replacement. Every action is the signed-in
/// administrator's alone; the endpoints enforce that.
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
    ILiveDataReplacement replacement,
    ILastRestoreStore lastRestore,
    SetupCompletion setup,
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
    /// The restore's background run, inside the maintenance that <see cref="StartAsync"/> began. It
    /// lets requests in flight finish, validates the archive again, takes and verifies a safety
    /// backup, then replaces the live database and managed assets with the archive's, applies the
    /// migrations it lacks, ends every session, and leaves maintenance. Anything that fails before
    /// the replacement began leaves the instance as it was (<see cref="MaintenanceOutcome.Failed"/>);
    /// anything that fails after, shutdown included, puts the previous data back
    /// (<see cref="MaintenanceOutcome.RolledBack"/>). If putting it back fails too, maintenance stays
    /// on (<see cref="MaintenanceOutcome.RollbackFailed"/>) and the result says where the safety backup
    /// and the previous files are. An upload is deleted at the end, whatever happened.
    /// </summary>
    public async Task<RestoreRunResult> RunAsync(RestorePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var source = plan.Validation.Source;
        using var hold = source is RestoreSource.Listed listed ? reads.Hold(listed.Location, listed.Name) : null;
        SafetyBackupRecord? safety = null;
        var replacing = false;
        try
        {
            maintenance.Report(MaintenanceStage.Validating, 0);
            await drain.DrainAsync(options.DrainGrace, cancellationToken).ConfigureAwait(false);

            if (await validator.RecheckAsync(plan.Validation, cancellationToken).ConfigureAwait(false) is { } refusal)
            {
                return End(MaintenanceOutcome.Failed, $"The archive failed validation when the restore began: {refusal.Message} Nothing was changed.", null, null);
            }

            maintenance.Report(MaintenanceStage.SafetyBackup, 0);
            var destination = storage.ResolveDestination();
            var created = await writer.CreateAsync(
                destination,
                plan.Id,
                BackupKind.Safety,
                (phase, percent) => maintenance.Report(MaintenanceStage.SafetyBackup, BackupJobHandler.Overall(phase, percent)),
                cancellationToken).ConfigureAwait(false);
            safety = new SafetyBackupRecord(created.Location, created.Name, Path.Combine(destination.Path, created.Name));
            maintenance.Report(MaintenanceStage.SafetyBackup, 100);

            // The journal is written before the stage says "replacing", so a restart that finds that
            // stage always finds the journal too, and can put the previous data back.
            replacement.Prepare(new RestoreJournalEntry(plan.Id, plan.Validation.Summary.Name, safety, time.GetUtcNow()));
            replacing = true;
            maintenance.Report(MaintenanceStage.Replacing, 0);
            var staged = await archives.StageAsync(
                source,
                plan.Id,
                percent => maintenance.Report(MaintenanceStage.Replacing, percent * 9 / 10),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            replacement.Swap(staged);
            maintenance.Report(MaintenanceStage.Replacing, 100);

            maintenance.Report(MaintenanceStage.Migrating, 0);
            await replacement.MigrateAsync(percent => maintenance.Report(MaintenanceStage.Migrating, percent), cancellationToken).ConfigureAwait(false);
            maintenance.Report(MaintenanceStage.Migrating, 100);

            maintenance.Report(MaintenanceStage.Finishing, 0);
            await replacement.FinishAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            setup.Forget();

            const string detail = "The instance now holds the backup's data, and every session was ended.";
            var result = End(MaintenanceOutcome.Succeeded, detail, safety, null);
            Record(MaintenanceOutcome.Succeeded, plan.Validation.Summary.Name, null, detail, safety);
            Tidy();
            await ApplySafetyRetentionAsync(source).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (replacing)
        {
            return PutBack(plan, safety!, exception);
        }
        catch (BackupVerificationException exception)
        {
            return End(MaintenanceOutcome.Failed, $"The safety backup could not be taken: {exception.Message} Nothing was changed.", null, exception);
        }
        catch (Exception exception)
        {
            // Nothing was moved: at most this restore's journal was written, and it goes.
            TidyUnmoved(plan.Id);
            return End(MaintenanceOutcome.Failed, "The restore stopped before anything was replaced. Nothing was changed.", safety, exception);
        }
        finally
        {
            if (source is RestoreSource.Uploaded upload)
            {
                archives.DeleteUpload(upload.UploadId);
            }
        }
    }

    /// <summary>
    /// At start, before the database is opened: a restore that a restart interrupted after it began
    /// replacing data is put back from its journal, and maintenance ends as rolled back. When there is
    /// no journal, or putting back fails, maintenance stays on. With maintenance off, whatever an
    /// ended restore left under the data path is removed.
    /// </summary>
    public RestoreRecovery RecoverAtStart()
    {
        var state = maintenance.Current;
        RestoreJournal? journal;
        try
        {
            journal = replacement.FindJournal();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Never guessed at: an unreadable journal is left for the operator, and an open instance stays open.
            return new RestoreRecovery(RestoreRecoveryOutcome.UnreadableJournal, null, exception);
        }

        if (!state.Active)
        {
            // Maintenance ended as succeeded or rolled back, or the swap never began: what is left
            // is the previous data of a finished restore, or nothing. Anything else is a swap the
            // state file lost track of, and it is put back like an interrupted one.
            if (journal is not { SwapBegan: true } || state.Outcome is MaintenanceOutcome.Succeeded or MaintenanceOutcome.RolledBack)
            {
                Tidy();
                return RestoreRecovery.NothingToDo;
            }
        }
        else if (journal is null)
        {
            return new RestoreRecovery(RestoreRecoveryOutcome.NoJournal, null, null);
        }

        try
        {
            replacement.PutBack();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Stays in maintenance, or enters it when the state file had lost track of the swap.
            maintenance.Stall();
            return new RestoreRecovery(RestoreRecoveryOutcome.PutBackFailed, journal.Entry, exception);
        }

        var stage = state.Active ? state.Stage ?? MaintenanceStage.Replacing : MaintenanceStage.Replacing;
        var detail = state.Outcome == MaintenanceOutcome.RollbackFailed
            ? $"The restore failed while {StageWords(stage)}, and putting the data from before it back failed at first; it was put back when n8Tracks started again."
            : $"A restart interrupted the restore while {StageWords(stage)}, and the data from before it was put back when n8Tracks started again.";
        maintenance.End(MaintenanceOutcome.RolledBack);
        Record(MaintenanceOutcome.RolledBack, journal.Entry.ArchiveName, stage, detail, journal.Entry.SafetyBackup);
        Tidy();
        return new RestoreRecovery(RestoreRecoveryOutcome.PutBack, journal.Entry, null);
    }

    /// <summary>The stage in words, for "while ...".</summary>
    public static string StageWords(MaintenanceStage stage) => stage switch
    {
        MaintenanceStage.Validating => "checking the backup",
        MaintenanceStage.SafetyBackup => "taking the safety backup",
        MaintenanceStage.Replacing => "replacing the data",
        MaintenanceStage.Migrating => "updating the restored database",
        MaintenanceStage.Finishing => "finishing",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown maintenance stage."),
    };

    private RestoreRunResult PutBack(RestorePlan plan, SafetyBackupRecord safety, Exception error)
    {
        var stage = maintenance.Current.Stage ?? MaintenanceStage.Replacing;
        try
        {
            replacement.PutBack();
        }
        catch (Exception putBackError)
        {
            maintenance.Stall();
            return new RestoreRunResult(
                MaintenanceOutcome.RollbackFailed,
                $"The restore failed while {StageWords(stage)}, and putting the previous data back failed too. The instance stays in maintenance.",
                safety.Name)
            {
                Error = new AggregateException(error, putBackError),
                Safety = safety,
                PreviousDataFolder = replacement.PreviousDataFolder,
            };
        }

        // The database is the one from before; what this process remembers of setup still holds,
        // but a fresh look costs nothing.
        setup.Forget();
        var detail = $"The restore failed while {StageWords(stage)}, and the data from before it was put back. The server log has the details.";
        var result = End(MaintenanceOutcome.RolledBack, detail, safety, error);
        Record(MaintenanceOutcome.RolledBack, plan.Validation.Summary.Name, stage, detail, safety);
        Tidy();
        return result;
    }

    private void Record(MaintenanceOutcome outcome, string archiveName, MaintenanceStage? failedStage, string detail, SafetyBackupRecord safety)
    {
        try
        {
            lastRestore.Write(new LastRestore(outcome, time.GetUtcNow(), archiveName, failedStage, detail, safety));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only the Backups page's note is lost; the restore's outcome stands and is logged.
        }
    }

    /// <summary>Removes this restore's journal when the swap never began; anything else is left alone.</summary>
    private void TidyUnmoved(Guid restoreId)
    {
        try
        {
            if (replacement.FindJournal() is { SwapBegan: false } journal && journal.Entry.RestoreId == restoreId)
            {
                replacement.Discard();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Left for the next start, which removes it once maintenance is off.
        }
    }

    /// <summary>Removes the journal and the previous files once the restore has ended; a failure only leaves them for the next start.</summary>
    private void Tidy()
    {
        try
        {
            replacement.Discard();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Removed at the next start instead.
        }
    }

    /// <summary>Deletes the safety backups beyond the newest three, never the archive just restored from.</summary>
    private async Task ApplySafetyRetentionAsync(RestoreSource source)
    {
        try
        {
            var all = await storage.ListAsync(CancellationToken.None).ConfigureAwait(false);
            var restoredFrom = source is RestoreSource.Listed listed
                ? all.FirstOrDefault(archive => archive.Location == listed.Location && archive.Name == listed.Name)
                : null;
            storage.DeleteForRetention(BackupRetention.SelectSafety(all, restoredFrom));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Tried again after the next restore.
        }
    }

    private RestoreRunResult End(MaintenanceOutcome outcome, string detail, SafetyBackupRecord? safety, Exception? error)
    {
        maintenance.End(outcome);
        return new RestoreRunResult(outcome, detail, safety?.Name) { Error = error, Safety = safety };
    }
}

/// <summary>What the start found of an interrupted restore, and did about it.</summary>
public enum RestoreRecoveryOutcome
{
    /// <summary>No restore was interrupted.</summary>
    NothingToDo,

    /// <summary>The previous data was put back and the instance is open.</summary>
    PutBack,

    /// <summary>The instance is in maintenance with no journal to put back from; it stays closed.</summary>
    NoJournal,

    /// <summary>Putting the previous data back failed; the instance stays closed.</summary>
    PutBackFailed,

    /// <summary>A journal is there but cannot be read; nothing is touched.</summary>
    UnreadableJournal,
}

/// <summary>What <see cref="RestoreService.RecoverAtStart"/> did, for the start-up log.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Journal">The interrupted restore's journal, when there was one.</param>
/// <param name="Error">Why putting back failed, or null.</param>
public sealed record RestoreRecovery(RestoreRecoveryOutcome Outcome, RestoreJournalEntry? Journal, Exception? Error)
{
    public static readonly RestoreRecovery NothingToDo = new(RestoreRecoveryOutcome.NothingToDo, null, null);
}
