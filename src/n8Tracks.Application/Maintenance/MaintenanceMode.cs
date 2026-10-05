namespace n8Tracks.Application.Maintenance;

/// <summary>The steps of a restore, in order, as the maintenance page names them.</summary>
public enum MaintenanceStage
{
    /// <summary>The archive is read and checked again before anything is touched.</summary>
    Validating,

    /// <summary>A safety backup of the instance as it is now is taken and verified.</summary>
    SafetyBackup,

    /// <summary>The database and managed assets are swapped for the archive's.</summary>
    Replacing,

    /// <summary>Pending migrations are applied to the restored database.</summary>
    Migrating,

    /// <summary>Sessions are ended and the application returns to normal.</summary>
    Finishing,
}

/// <summary>How the last maintenance ended.</summary>
public enum MaintenanceOutcome
{
    /// <summary>The restore completed.</summary>
    Succeeded,

    /// <summary>The restore stopped before anything was replaced; the instance is as it was.</summary>
    Failed,

    /// <summary>The restore failed after replacement began and the safety backup was put back.</summary>
    RolledBack,
}

/// <summary>
/// The maintenance state: whether the instance is in maintenance, at which stage and percentage, and
/// how the last maintenance ended. It holds no path and no error text, so all of it can be shown to
/// anyone.
/// </summary>
/// <param name="Active">Whether the API is closed for maintenance.</param>
/// <param name="Stage">The current stage while active; the last one reached otherwise, or null.</param>
/// <param name="Percent">0 to 100 within the stage.</param>
/// <param name="Outcome">How the last maintenance ended; null while active, or before the first.</param>
public sealed record MaintenanceSnapshot(bool Active, MaintenanceStage? Stage, int Percent, MaintenanceOutcome? Outcome)
{
    /// <summary>No maintenance has ever run.</summary>
    public static readonly MaintenanceSnapshot Idle = new(false, null, 0, null);

    /// <summary>A stage at or after which the live files may already have been changed.</summary>
    public static bool MayHaveChangedFiles(MaintenanceStage stage) => stage >= MaintenanceStage.Replacing;

    /// <summary>The stage as the API writes it: <c>validating</c>, <c>safety-backup</c>, <c>replacing</c>, <c>migrating</c>, <c>finishing</c>.</summary>
    public static string StageText(MaintenanceStage stage) => stage switch
    {
        MaintenanceStage.Validating => "validating",
        MaintenanceStage.SafetyBackup => "safety-backup",
        MaintenanceStage.Replacing => "replacing",
        MaintenanceStage.Migrating => "migrating",
        MaintenanceStage.Finishing => "finishing",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown maintenance stage."),
    };

    /// <summary>The stage <paramref name="text"/> names, or null.</summary>
    public static MaintenanceStage? ParseStage(string? text) =>
        Enum.GetValues<MaintenanceStage>().Select(static stage => (MaintenanceStage?)stage).FirstOrDefault(stage => StageText(stage!.Value) == text);

    /// <summary>The outcome as the API writes it: <c>succeeded</c>, <c>failed</c>, <c>rolled-back</c>.</summary>
    public static string OutcomeText(MaintenanceOutcome outcome) => outcome switch
    {
        MaintenanceOutcome.Succeeded => "succeeded",
        MaintenanceOutcome.Failed => "failed",
        MaintenanceOutcome.RolledBack => "rolled-back",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown maintenance outcome."),
    };

    /// <summary>The outcome <paramref name="text"/> names, or null.</summary>
    public static MaintenanceOutcome? ParseOutcome(string? text) =>
        Enum.GetValues<MaintenanceOutcome>().Select(static outcome => (MaintenanceOutcome?)outcome).FirstOrDefault(outcome => OutcomeText(outcome!.Value) == text);
}

/// <summary>
/// The small state file under the data path that keeps the maintenance state across a restart, so
/// an interrupted restore is noticed at the next start and a container command can refuse to run
/// during one.
/// </summary>
public interface IMaintenanceStateStore
{
    /// <summary>The stored state, or null when there is none or it cannot be read.</summary>
    MaintenanceSnapshot? Read();

    /// <summary>Replaces the stored state, atomically.</summary>
    void Write(MaintenanceSnapshot snapshot);
}

/// <summary>
/// Lets the API requests already in flight when maintenance begins finish: waits for them up to a
/// grace period, then aborts the rest. Requests that arrive after maintenance began are refused
/// before they start, so they are never waited for.
/// </summary>
public interface IRequestDrain
{
    /// <summary>Waits until no API request is in flight or <paramref name="grace"/> has passed, then aborts any left; returns how many were aborted.</summary>
    Task<int> DrainAsync(TimeSpan grace, CancellationToken cancellationToken);
}

/// <summary>
/// The one maintenance state of this process, held in memory and mirrored in the state file. While
/// it is active the API answers 503 <c>maintenance</c>, background jobs are not claimed, and
/// scheduled work that comes due waits until it ends.
/// </summary>
public sealed class MaintenanceMode
{
    private readonly IMaintenanceStateStore store;
    private readonly Lock gate = new();
    private MaintenanceSnapshot current;

    /// <summary>
    /// Loads the stored state. A maintenance that a restart interrupted before any file could have
    /// changed ended with nothing changed, so it is recorded as failed and the instance opens again;
    /// one that may have changed files stays active until it is put right.
    /// </summary>
    public MaintenanceMode(IMaintenanceStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        this.store = store;
        var stored = store.Read() ?? MaintenanceSnapshot.Idle;
        if (stored is { Active: true, Stage: { } stage } && !MaintenanceSnapshot.MayHaveChangedFiles(stage))
        {
            stored = new MaintenanceSnapshot(false, stage, stored.Percent, MaintenanceOutcome.Failed);
            store.Write(stored);
            RecoveredAtStart = true;
        }

        current = stored;
    }

    /// <summary>True when the state file showed a restore that a restart interrupted before it changed anything.</summary>
    public bool RecoveredAtStart { get; }

    /// <summary>The state now.</summary>
    public MaintenanceSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>Whether the instance is in maintenance now.</summary>
    public bool IsActive => Current.Active;

    /// <summary>Enters maintenance at <paramref name="stage"/>; false, changing nothing, when it is already active.</summary>
    public bool TryBegin(MaintenanceStage stage)
    {
        lock (gate)
        {
            if (current.Active)
            {
                return false;
            }

            Set(new MaintenanceSnapshot(true, stage, 0, null), persist: true);
            return true;
        }
    }

    /// <summary>Records progress. The state file is written when the stage changes, not for every percent.</summary>
    public void Report(MaintenanceStage stage, int percent)
    {
        lock (gate)
        {
            if (!current.Active)
            {
                return;
            }

            var persist = current.Stage != stage;
            Set(current with { Stage = stage, Percent = Math.Clamp(percent, 0, 100) }, persist);
        }
    }

    /// <summary>Leaves maintenance with <paramref name="outcome"/>.</summary>
    public void End(MaintenanceOutcome outcome)
    {
        lock (gate)
        {
            Set(current with { Active = false, Outcome = outcome }, persist: true);
        }
    }

    private void Set(MaintenanceSnapshot snapshot, bool persist)
    {
        if (persist)
        {
            // Written before the state changes in memory, so the file is never behind what callers saw.
            store.Write(snapshot);
        }

        current = snapshot;
    }
}
