using System.Globalization;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Health;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Persistence;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Health;

/// <summary>
/// Checks the components one after another on every call. One per host: it remembers each component's
/// last status so that a failure is logged when it starts and when it ends, not on every poll.
/// </summary>
internal sealed class HealthService : IHealthService
{
    /// <summary>How long the database check and the media check may each take.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(2);

    private const string DatabaseComponent = "database";
    private const string MigrationsComponent = "migrations";
    private const string MediaComponent = "media";

    private static readonly HealthComponent ApplicationRunning = new(HealthStatus.Healthy, HealthDetails.ApplicationRunning);
    private static readonly HealthComponent DatabaseReachable = new(HealthStatus.Healthy, HealthDetails.DatabaseReachable);
    private static readonly HealthComponent DatabaseUnreachable = new(HealthStatus.Unhealthy, HealthDetails.DatabaseUnreachable);
    private static readonly HealthComponent MediaAvailable = new(HealthStatus.Healthy, HealthDetails.MediaAvailable);
    private static readonly HealthComponent MediaUnavailable = new(HealthStatus.Degraded, HealthDetails.MediaUnavailable);
    private static readonly HealthComponent MaintenanceOff = new(HealthStatus.Healthy, HealthDetails.MaintenanceOff);
    private static readonly HealthComponent MaintenanceRestoring = new(HealthStatus.Degraded, HealthDetails.MaintenanceRestoring);
    private static readonly HealthComponent DatabaseInMaintenance = new(HealthStatus.Degraded, HealthDetails.DatabaseInMaintenance);

    private readonly IDatabaseConnectionFactory connections;
    private readonly IMediaMountProbe mediaProbe;
    private readonly IMigrationStateProvider migrationState;
    private readonly IBackupStorage backups;
    private readonly MaintenanceMode maintenance;
    private readonly Serilog.ILogger log;
    private readonly string mediaPath;
    private readonly DeadlineCheck databaseCheck;
    private readonly DeadlineCheck mediaCheck;

    private readonly Lock gate = new();
    private readonly Dictionary<string, HealthStatus> previous = new(StringComparer.Ordinal);

    public HealthService(
        N8TracksOptions options,
        IDatabaseConnectionFactory connections,
        IMediaMountProbe mediaProbe,
        IMigrationStateProvider migrationState,
        IBackupStorage backups,
        MaintenanceMode maintenance,
        Serilog.ILogger log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        this.connections = connections;
        this.mediaProbe = mediaProbe;
        this.migrationState = migrationState;
        this.backups = backups;
        this.maintenance = maintenance;
        this.log = log.ForContext<HealthService>();
        mediaPath = options.MediaPath;
        databaseCheck = new DeadlineCheck(QueryDatabase, CheckTimeout);
        mediaCheck = new DeadlineCheck(ReadMedia, CheckTimeout);
    }

    public async Task<HealthReport> GetReportAsync(CancellationToken cancellationToken)
    {
        // During maintenance a restore may be replacing the database file, so it is not opened: the
        // instance is degraded, not unhealthy, and the container is not restarted under the restore.
        var inMaintenance = maintenance.IsActive;
        HealthComponent database;
        if (inMaintenance)
        {
            database = DatabaseInMaintenance;
        }
        else
        {
            var databaseOutcome = await databaseCheck.RunAsync(cancellationToken).ConfigureAwait(false);
            database = databaseOutcome.Result == CheckResult.Passed ? DatabaseReachable : DatabaseUnreachable;
            Observe(DatabaseComponent, database.Status, databaseOutcome);
        }

        var migrations = Migrations(await LastSafetyBackupAtAsync(cancellationToken).ConfigureAwait(false));

        var mediaOutcome = await mediaCheck.RunAsync(cancellationToken).ConfigureAwait(false);
        var media = mediaOutcome.Result == CheckResult.Passed ? MediaAvailable : MediaUnavailable;
        Observe(MediaComponent, media.Status, mediaOutcome);

        return new HealthReport(ApplicationRunning, database, migrations, media, inMaintenance ? MaintenanceRestoring : MaintenanceOff);
    }

    private bool QueryDatabase(CancellationToken deadline)
    {
        using var connection = connections.CreateForExistingDatabase();
        deadline.ThrowIfCancellationRequested();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        command.CommandTimeout = (int)CheckTimeout.TotalSeconds;
        deadline.ThrowIfCancellationRequested();

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private bool ReadMedia(CancellationToken deadline)
    {
        deadline.ThrowIfCancellationRequested();

        return mediaProbe.IsReadable(mediaPath);
    }

    /// <summary>
    /// When the newest valid safety backup was made, from the folder listing (so an upgrade's, a
    /// restore's, and one whose making is not recorded anywhere else all count); null when there is
    /// none or the folders cannot be read.
    /// </summary>
    private async Task<DateTimeOffset?> LastSafetyBackupAtAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await backups.ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(static archive => archive.Validity == BackupValidity.Valid && BackupKinds.Parse(archive.Kind) == BackupKind.Safety)
                .Select(static archive => (DateTimeOffset?)archive.CreatedUtc)
                .Max();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The state captured at startup: it stays as it was even if the database later goes away.</summary>
    private MigrationsHealthComponent Migrations(DateTimeOffset? lastSafetyBackupAt)
    {
        MigrationsHealthComponent component;
        var outcome = new CheckOutcome(CheckResult.Passed);
        try
        {
            var state = migrationState.Current;
            component = new MigrationsHealthComponent(
                HealthStatus.Healthy,
                HealthDetails.MigrationsUpToDate,
                state.LastAppliedMigrationId,
                state.LastOutcome,
                lastSafetyBackupAt);
        }
        catch (InvalidOperationException exception)
        {
            // Database startup has not completed. The app does not listen before it has, so this is a fault.
            component = new MigrationsHealthComponent(HealthStatus.Unhealthy, HealthDetails.MigrationsUnknown, null, MigrationOutcome.None, lastSafetyBackupAt);
            outcome = new CheckOutcome(CheckResult.Failed, exception);
        }

        Observe(MigrationsComponent, component.Status, outcome);

        return component;
    }

    /// <summary>Logs a component's status only when it differs from the last one seen. A component starts out healthy.</summary>
    private void Observe(string component, HealthStatus status, CheckOutcome outcome)
    {
        lock (gate)
        {
            var before = previous.GetValueOrDefault(component, HealthStatus.Healthy);
            if (before == status)
            {
                return;
            }

            previous[component] = status;
        }

        if (status == HealthStatus.Healthy)
        {
            log.Information("Health component {Component} recovered and is {HealthStatus}", component, status);
            return;
        }

        var reason = outcome.Result == CheckResult.TimedOut
            ? string.Create(CultureInfo.InvariantCulture, $"the check did not finish within {CheckTimeout.TotalSeconds:0} seconds")
            : "the check failed";

        log.Warning(outcome.Exception, "Health component {Component} is {HealthStatus}: {Reason}", component, status, reason);
    }
}
