using n8Tracks.Application.Auth;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Application.Logging;

/// <summary>
/// The log's level, retention, and size cap (#234). Until the settings are first saved, the level is
/// <c>N8TRACKS_LOG_LEVEL</c>'s (Trace and Critical included, shown as set by the environment) and the
/// limits are the defaults; a saved setting overrides the variable. A saved Debug level lasts
/// <see cref="LoggingSettings.DebugDuration"/> from the save and then switches itself back to
/// Information. The monitor calls <see cref="RefreshAsync"/> every 30 seconds, so a level changed
/// anywhere takes effect within a minute; a change saved here takes effect at once.
/// </summary>
public sealed class LoggingSettingsService(
    ILoggingSettingsStore store,
    ILogFiles files,
    ILogLevelControl level,
    N8TracksOptions options,
    MaintenanceMode maintenance,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The settings and what is in effect.</summary>
    public async Task<LoggingView> GetAsync(CancellationToken cancellationToken) =>
        View(await store.FindAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Replaces the settings when <paramref name="revision"/> is the current one (0 before the first
    /// save). When the new limits would delete log files the current ones keep, nothing is written
    /// unless <paramref name="confirmDelete"/> is set; then the files are deleted on save. Saving Debug
    /// (again) starts its 24 hours from now.
    /// </summary>
    public async Task<LoggingSettingsUpdate> UpdateAsync(LoggingSettings settings, int revision, bool confirmDelete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var outcome = await transaction.RunAsync<LoggingSettingsUpdate>(
            async token =>
            {
                var current = View(await store.FindAsync(token).ConfigureAwait(false));
                if (current.Stored.Revision != revision)
                {
                    return new LoggingSettingsUpdate.Stale(current);
                }

                var deletion = files.Preview(current.Stored.Settings.Limits, settings.Limits);
                if (deletion.Files > 0 && !confirmDelete)
                {
                    return new LoggingSettingsUpdate.ConfirmationRequired(deletion);
                }

                var debugUntil = settings.Level == N8TracksLogLevel.Debug ? time.GetUtcNow() + LoggingSettings.DebugDuration : (DateTimeOffset?)null;
                var stored = new StoredLoggingSettings(settings with { DebugUntil = debugUntil }, current.Stored.Revision + 1);
                await store.WriteAsync(stored, token).ConfigureAwait(false);
                return new LoggingSettingsUpdate.Updated(new LoggingView(stored, LogLevelSource.Setting, null), LogFileDeletion.None);
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome is not LoggingSettingsUpdate.Updated updated)
        {
            return outcome;
        }

        Apply(updated.Current.Stored);
        var deleted = files.Sweep();
        return new LoggingSettingsUpdate.Updated(updated.Current with { FolderProblem = files.Problem }, deleted);
    }

    /// <summary>
    /// One look: a saved Debug level whose 24 hours are over is saved back as Information, and the
    /// stored level and limits are put in effect. Nothing happens during maintenance, while the
    /// database may be being replaced.
    /// </summary>
    public async Task<LoggingRefresh> RefreshAsync(CancellationToken cancellationToken)
    {
        if (maintenance.IsActive)
        {
            return LoggingRefresh.None;
        }

        var stored = await store.FindAsync(cancellationToken).ConfigureAwait(false);
        var result = LoggingRefresh.None;
        if (IsExpiredDebug(stored))
        {
            stored = await transaction.RunAsync(
                async token =>
                {
                    var current = await store.FindAsync(token).ConfigureAwait(false);
                    if (!IsExpiredDebug(current))
                    {
                        return current;
                    }

                    var reverted = new StoredLoggingSettings(
                        current!.Settings with { Level = N8TracksLogLevel.Information, DebugUntil = null },
                        current.Revision + 1);
                    await store.WriteAsync(reverted, token).ConfigureAwait(false);
                    result = LoggingRefresh.DebugEnded;
                    return reverted;
                },
                cancellationToken).ConfigureAwait(false);
        }

        Apply(stored ?? Unsaved());
        return result;
    }

    private bool IsExpiredDebug(StoredLoggingSettings? stored) =>
        stored is { Settings: { Level: N8TracksLogLevel.Debug, DebugUntil: { } until } } && until <= time.GetUtcNow();

    private void Apply(StoredLoggingSettings stored)
    {
        level.Set(stored.Settings.Level);
        files.Configure(stored.Settings.Limits);
    }

    private LoggingView View(StoredLoggingSettings? stored) =>
        stored is null
            ? new LoggingView(Unsaved(), LogLevelSource.Environment, files.Problem)
            : new LoggingView(stored, LogLevelSource.Setting, files.Problem);

    /// <summary>What an instance that never saved the settings has: the environment's level and the default limits, at revision 0.</summary>
    private StoredLoggingSettings Unsaved() =>
        new(new LoggingSettings(options.LogLevel, LogFileLimits.Default.RetentionDays, LogFileLimits.Default.MaxMegabytes, DebugUntil: null), 0);
}

/// <summary>What one look did.</summary>
public enum LoggingRefresh
{
    /// <summary>Nothing changed but what the stored settings say is (still) in effect.</summary>
    None,

    /// <summary>A saved Debug level reached its end and was saved back as Information.</summary>
    DebugEnded,
}
