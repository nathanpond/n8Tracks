using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Maintenance;
using n8Tracks.Infrastructure.Notifications;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>
/// The first thing the server does with the data path, before the database is opened: a restore
/// that a restart interrupted after it began replacing data is put back (see
/// <see cref="RestoreService.RecoverAtStart"/>). When the instance is still in maintenance after
/// that, the database is left exactly as it is, unopened and unmigrated, and the server starts
/// closed, answering only health, the maintenance status, and the frontend.
/// </summary>
public static class RestoreStartup
{
    /// <summary>Recovers what it can, logs what it found to the application log, and returns whether the instance is still in maintenance.</summary>
    public static bool Recover(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var startupLog = scope.ServiceProvider.GetRequiredService<Serilog.ILogger>().ForContext(typeof(RestoreStartup));
        var recovery = scope.ServiceProvider.GetRequiredService<RestoreService>().RecoverAtStart();
        var journal = recovery.Journal;
        switch (recovery.Outcome)
        {
            case RestoreRecoveryOutcome.PutBack:
                if (journal is not null)
                {
                    scope.ServiceProvider.GetRequiredService<StartupNotices>().RestorePutBack(journal.RestoreId);
                }

                startupLog.Warning(
                    "A restart interrupted restore {RestoreId}; the data from before it was put back, and the instance is open. The safety backup is {SafetyBackupPath}",
                    journal?.RestoreId,
                    journal?.SafetyBackup?.Path);
                break;

            case RestoreRecoveryOutcome.PutBackFailed:
                startupLog.Error(
                    recovery.Error,
                    "The instance stays in maintenance: restore {RestoreId} was interrupted and its previous data could not be put back. The safety backup is {SafetyBackupPath}. "
                    + "Stop the container and run `{RestoreCommand}` from the image with the same volumes (docker run --rm), or move the files in {PreviousDataFolder} back into the data path by hand, replacing the database and the assets folder there.",
                    journal?.RestoreId,
                    journal?.SafetyBackup?.Path,
                    RestoreRunner.RestoreCommand(journal?.SafetyBackup?.Path),
                    scope.ServiceProvider.GetRequiredService<ILiveDataReplacement>().PreviousDataFolder);
                break;

            case RestoreRecoveryOutcome.NoJournal:
                startupLog.Error(
                    "The instance stays in maintenance: a restore was interrupted after it began replacing data, and it left no record to put the previous data back from. "
                    + "Stop the container and restore the newest safety backup with `{RestoreCommand}`.",
                    RestoreRunner.RestoreCommand(null));
                break;

            case RestoreRecoveryOutcome.UnreadableJournal:
                startupLog.Error(
                    recovery.Error,
                    "A restore's record of the data it moved aside, in {PreviousDataFolder}, cannot be read; nothing was touched. "
                    + "If the instance is in maintenance, stop the container and restore the newest safety backup with `{RestoreCommand}`.",
                    scope.ServiceProvider.GetRequiredService<ILiveDataReplacement>().PreviousDataFolder,
                    RestoreRunner.RestoreCommand(null));
                break;

            case RestoreRecoveryOutcome.NothingToDo:
            default:
                break;
        }

        return scope.ServiceProvider.GetRequiredService<MaintenanceMode>().IsActive;
    }
}
