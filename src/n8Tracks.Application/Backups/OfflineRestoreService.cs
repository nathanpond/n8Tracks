using n8Tracks.Application.Configuration;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Application.Backups;

/// <summary>
/// The restore the container command runs with the server stopped (<c>n8tracks restore &lt;archive&gt;</c>),
/// for an instance that cannot start or is stuck in maintenance. It validates the archive with the
/// web restore's <see cref="RestoreValidator"/>, and replaces the live data with the web restore's
/// <see cref="ILiveDataReplacement"/>: the live database and managed assets are moved aside under
/// the data path first, and put back if anything fails. It takes no safety backup (the database it
/// replaces may be the reason for the restore) and applies no migration: the application does that,
/// with its own safety backup, at its next start. On success it ends maintenance as succeeded,
/// removes a failed upgrade's marker, and keeps the previous files in a dated folder under the data
/// path. The caller holds the data path's lock, so no server runs meanwhile.
/// </summary>
public sealed class OfflineRestoreService(
    RestoreValidator validator,
    IRestoreArchives archives,
    ILiveDataReplacement replacement,
    MaintenanceMode maintenance,
    IFailedUpgradeMarker upgradeMarker,
    N8TracksOptions options,
    TimeProvider time)
{
    /// <summary>Restores the archive at <paramref name="archivePath"/>; every refusal changes nothing.</summary>
    public async Task<OfflineRestoreOutcome> RestoreAsync(string archivePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archivePath);

        var fullPath = Path.GetFullPath(archivePath);
        if (IsInside(fullPath, options.MediaPath))
        {
            // Invariant 2: nothing under the media mount is used for backups, read or written.
            return new OfflineRestoreOutcome.Refused(
                $"{fullPath} is inside the media mount, which n8Tracks never reads backups from; copy it to the backup mount first. Nothing was changed.");
        }

        RestoreJournal? earlier;
        try
        {
            earlier = replacement.FindJournal();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new OfflineRestoreOutcome.Refused(
                $"The record an earlier restore left in {replacement.PreviousDataFolder} cannot be read. Move the files in that folder back into the data path by hand, "
                + "replacing the database and the assets folder there, delete the folder, and run this command again. Nothing was changed.");
        }

        RestoreArchiveSummary summary;
        switch (await validator.ValidateFileAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            case RestoreValidationOutcome.Valid valid:
                summary = valid.Validation.Summary;
                break;

            case RestoreValidationOutcome.Refused refused:
                return new OfflineRestoreOutcome.Refused($"{refused.Refusal.Message} Nothing was changed.");

            default:
                return new OfflineRestoreOutcome.Refused($"There is no file at {fullPath}. Nothing was changed.");
        }

        var wasInMaintenance = maintenance.IsActive;
        if (earlier is not null && SettleEarlier(earlier) is { } stuck)
        {
            return stuck;
        }

        // Still active here only when it was stuck with no record to put back from: the data's state
        // is unknown, so a failure below leaves the instance closed as it found it.
        var stillStuck = maintenance.IsActive;
        if (stillStuck)
        {
            maintenance.Report(MaintenanceStage.Replacing, 0);
        }
        else
        {
            // At "replacing", so a start that finds this state after a crash puts the previous data back.
            maintenance.TryBegin(MaintenanceStage.Replacing);
        }

        var restoreId = Guid.CreateVersion7();
        try
        {
            replacement.Prepare(new RestoreJournalEntry(restoreId, summary.Name, null, time.GetUtcNow()));
            var staged = await archives.StageAsync(new RestoreSource.File(fullPath), restoreId, static _ => { }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            replacement.Swap(staged);
            maintenance.Report(MaintenanceStage.Finishing, 0);
            await replacement.FinishAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return PutBack(exception, stillStuck);
        }

        // In this order: a crash after the state says "succeeded" leaves at most the marker (the next
        // run of this command finishes the job) and the previous files (the next start removes them).
        maintenance.End(MaintenanceOutcome.Succeeded);
        var clearedMarker = upgradeMarker.Clear();
        string? kept;
        try
        {
            kept = replacement.KeepPrevious(time.GetUtcNow());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left where the swap put them; the server removes them at its next start.
            kept = replacement.PreviousDataFolder;
        }

        return new OfflineRestoreOutcome.Restored(summary, kept, wasInMaintenance, clearedMarker);
    }

    /// <summary>
    /// What an earlier restore left, dealt with the way the server's start does: the leftovers of
    /// one that ended are removed, and the previous data of one that stopped part way is put back.
    /// Null when that worked; otherwise the outcome to report, with the instance kept in maintenance.
    /// </summary>
    private OfflineRestoreOutcome? SettleEarlier(RestoreJournal earlier)
    {
        var state = maintenance.Current;
        if (!state.Active && (!earlier.SwapBegan || state.Outcome is MaintenanceOutcome.Succeeded or MaintenanceOutcome.RolledBack))
        {
            replacement.Discard();
            return null;
        }

        try
        {
            replacement.PutBack();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            maintenance.Stall();
            return new OfflineRestoreOutcome.RollbackFailed(
                $"The data an earlier restore moved aside could not be put back ({exception.Message}). Move the files in {replacement.PreviousDataFolder} back into the data path by hand, "
                + "replacing the database and the assets folder there, delete that folder, and run this command again.",
                replacement.PreviousDataFolder);
        }

        maintenance.End(MaintenanceOutcome.RolledBack);
        return null;
    }

    private OfflineRestoreOutcome PutBack(Exception error, bool stillStuck)
    {
        var folder = replacement.PreviousDataFolder;
        try
        {
            replacement.PutBack();
        }
        catch (Exception putBackError) when (putBackError is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            maintenance.Stall();
            return new OfflineRestoreOutcome.RollbackFailed(
                $"The restore failed ({error.Message}), and putting the previous data back failed too ({putBackError.Message}). The instance stays in maintenance: "
                + $"move the files in {folder} back into the data path by hand, replacing the database and the assets folder there, or run this command again.",
                folder);
        }

        if (stillStuck)
        {
            maintenance.Stall();
        }
        else
        {
            maintenance.End(MaintenanceOutcome.RolledBack);
        }

        return new OfflineRestoreOutcome.RolledBack(
            $"The restore failed ({error.Message}). The database and assets from before it, moved aside to {folder}, were put back.",
            folder);
    }

    private static bool IsInside(string path, string folder)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(path, root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}

/// <summary>How the container command's restore ended.</summary>
public abstract record OfflineRestoreOutcome
{
    private OfflineRestoreOutcome()
    {
    }

    /// <summary>The instance holds the archive's data.</summary>
    /// <param name="Archive">What was restored.</param>
    /// <param name="PreviousDataFolder">Where the database and assets from before are kept, or null when there were none.</param>
    /// <param name="ClearedMaintenance">Whether the instance was in maintenance before, and is not now.</param>
    /// <param name="ClearedUpgradeMarker">Whether a failed upgrade's marker was removed.</param>
    public sealed record Restored(RestoreArchiveSummary Archive, string? PreviousDataFolder, bool ClearedMaintenance, bool ClearedUpgradeMarker) : OfflineRestoreOutcome;

    /// <summary>Refused before anything was changed.</summary>
    public sealed record Refused(string Reason) : OfflineRestoreOutcome;

    /// <summary>Failed after the replacement began; the previous data was put back.</summary>
    public sealed record RolledBack(string Reason, string PreviousDataFolder) : OfflineRestoreOutcome;

    /// <summary>Failed, and the previous data could not be put back; the instance stays in maintenance.</summary>
    public sealed record RollbackFailed(string Reason, string PreviousDataFolder) : OfflineRestoreOutcome;
}
