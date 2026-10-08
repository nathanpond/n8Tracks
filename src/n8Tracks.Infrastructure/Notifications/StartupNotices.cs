namespace n8Tracks.Infrastructure.Notifications;

/// <summary>
/// What the steps before the database is opened found, for the notifications (#231) the database
/// startup records once it is up to date: an earlier upgrade that failed (its marker is removed before
/// then), and a restore a restart interrupted that was put back. One per process; nothing is kept
/// across a restart, where the marker or the restore's record already said it.
/// </summary>
internal sealed class StartupNotices
{
    private readonly Lock gate = new();
    private DateTimeOffset? failedUpgradeStartedUtc;
    private Guid? restorePutBack;

    /// <summary>An earlier upgrade, begun at <paramref name="startedUtc"/>, failed and its safety backup was put back.</summary>
    public void UpgradeFailed(DateTimeOffset startedUtc)
    {
        lock (gate)
        {
            failedUpgradeStartedUtc = startedUtc;
        }
    }

    /// <summary>The restore <paramref name="restoreId"/> was interrupted after it began replacing data, and was put back.</summary>
    public void RestorePutBack(Guid restoreId)
    {
        lock (gate)
        {
            restorePutBack = restoreId;
        }
    }

    /// <summary>What was noted, cleared as it is taken.</summary>
    public (DateTimeOffset? FailedUpgradeStartedUtc, Guid? RestorePutBack) Take()
    {
        lock (gate)
        {
            var taken = (failedUpgradeStartedUtc, restorePutBack);
            failedUpgradeStartedUtc = null;
            restorePutBack = null;
            return taken;
        }
    }
}
