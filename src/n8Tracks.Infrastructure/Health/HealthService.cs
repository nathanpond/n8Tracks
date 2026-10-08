using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Health;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Media;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Health;

/// <summary>
/// Checks the components one after another on every call. One per host: it remembers each component's
/// last status so that a failure is logged when it starts and when it ends, not on every poll. The
/// media component is the live answer of the one media probe (#207), and what it saw is recorded as
/// the mount state at once (the state the audio files report from), which queues a recovery scan when
/// the folder has just come back; that is skipped while the database cannot be used. The schema
/// version (<see cref="MigrationsHealthCheck"/>) and the job worker (<see cref="JobsHealthCheck"/>)
/// are checks of their own, under the same deadline, told whether the database answered.
/// </summary>
internal sealed class HealthService : IHealthService
{
    /// <summary>How long each check that reads the database or the media mount may take.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(2);

    private const string DatabaseComponent = "database";
    private const string MigrationsComponent = "migrations";
    private const string MediaComponent = "media";
    private const string JobsComponent = "jobs";

    private static readonly HealthComponent ApplicationRunning = new(HealthStatus.Healthy, HealthDetails.ApplicationRunning);
    private static readonly HealthComponent DatabaseReachable = new(HealthStatus.Healthy, HealthDetails.DatabaseReachable);
    private static readonly HealthComponent DatabaseUnreachable = new(HealthStatus.Unhealthy, HealthDetails.DatabaseUnreachable);
    private static readonly HealthComponent MediaAvailable = new(HealthStatus.Healthy, HealthDetails.MediaAvailable);
    private static readonly HealthComponent MediaUnavailable = new(HealthStatus.Degraded, HealthDetails.MediaUnavailable);
    private static readonly HealthComponent MaintenanceOff = new(HealthStatus.Healthy, HealthDetails.MaintenanceOff);
    private static readonly HealthComponent MaintenanceRestoring = new(HealthStatus.Degraded, HealthDetails.MaintenanceRestoring);
    private static readonly HealthComponent DatabaseInMaintenance = new(HealthStatus.Degraded, HealthDetails.DatabaseInMaintenance);

    private readonly IDatabaseConnectionFactory connections;
    private readonly IMediaFolderProbe media;
    private readonly IServiceScopeFactory scopes;
    private readonly MigrationsHealthCheck migrations;
    private readonly JobsHealthCheck jobs;
    private readonly IBackupStorage backups;
    private readonly MaintenanceMode maintenance;
    private readonly Serilog.ILogger log;
    private readonly DeadlineCheck databaseCheck;

    private readonly Lock gate = new();
    private readonly Dictionary<string, HealthStatus> previous = new(StringComparer.Ordinal);

    public HealthService(
        IDatabaseConnectionFactory connections,
        IMediaFolderProbe media,
        IServiceScopeFactory scopes,
        MigrationsHealthCheck migrations,
        JobsHealthCheck jobs,
        IBackupStorage backups,
        MaintenanceMode maintenance,
        Serilog.ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);

        this.connections = connections;
        this.media = media;
        this.scopes = scopes;
        this.migrations = migrations;
        this.jobs = jobs;
        this.backups = backups;
        this.maintenance = maintenance;
        this.log = log.ForContext<HealthService>();
        databaseCheck = new DeadlineCheck(QueryDatabase, CheckTimeout);
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

        var access = inMaintenance ? DatabaseAccess.Maintenance
            : database == DatabaseReachable ? DatabaseAccess.Reachable
            : DatabaseAccess.Unreachable;

        var lastSafetyBackupAt = await LastSafetyBackupAtAsync(cancellationToken).ConfigureAwait(false);
        var (migrations, migrationsOutcome) = await this.migrations.CheckAsync(access, lastSafetyBackupAt, cancellationToken).ConfigureAwait(false);
        Observe(MigrationsComponent, migrations.Status, migrationsOutcome);

        var probe = await this.media.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var media = probe.Readable ? MediaAvailable : MediaUnavailable;
        Observe(MediaComponent, media.Status, OutcomeOf(probe));
        if (database == DatabaseReachable)
        {
            await RecordMediaAsync(probe.Readable, cancellationToken).ConfigureAwait(false);
        }

        var (jobs, jobsOutcome) = await this.jobs.CheckAsync(access, cancellationToken).ConfigureAwait(false);
        Observe(JobsComponent, jobs.Status, jobsOutcome);

        return new HealthReport(ApplicationRunning, database, migrations, media, jobs, inMaintenance ? MaintenanceRestoring : MaintenanceOff);
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

    private static CheckOutcome OutcomeOf(MediaProbeOutcome probe) => probe.Result switch
    {
        MediaProbeResult.Readable => new CheckOutcome(CheckResult.Passed),
        MediaProbeResult.TimedOut => new CheckOutcome(CheckResult.TimedOut, probe.Exception),
        _ => new CheckOutcome(CheckResult.Failed, probe.Exception),
    };

    /// <summary>Records what the media probe saw as the mount state. A failure to record does not change the report.</summary>
    private async Task RecordMediaAsync(bool readable, CancellationToken cancellationToken)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                _ = await scope.ServiceProvider.GetRequiredService<MediaRecoveryService>().ObserveAsync(readable, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log.Warning(exception, "The media folder's state could not be recorded");
        }
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
