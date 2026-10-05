using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>
/// Runs a begun restore in the background, in a scope of its own, outside the job queue. One at a
/// time: the restore's start allows no second while maintenance is active. Shutdown cancels the run
/// and waits for it; a run cut short before anything was replaced is recorded as failed at the next
/// start (see <see cref="MaintenanceMode"/>). The outcome and its detail go to the log; the
/// maintenance page sees only the outcome.
/// </summary>
internal sealed partial class RestoreRunner(IServiceScopeFactory scopes, ILogger<RestoreRunner> logger) : IRestoreRunner, IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private Task running = Task.CompletedTask;
    private int disposed;

    /// <summary>The run in progress, or a completed task; tests wait on it.</summary>
    public Task Running
    {
        get
        {
            lock (gate)
            {
                return running;
            }
        }
    }

    public void Start(RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        lock (gate)
        {
            LogStarted(logger, plan.Id);
            running = Task.Run(() => RunAsync(plan), CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Registered as itself and as the port, so the container disposes it twice.
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Running.ConfigureAwait(false);
        }
        finally
        {
            stopping.Dispose();
        }
    }

    private async Task RunAsync(RestorePlan plan)
    {
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var result = await scope.ServiceProvider.GetRequiredService<RestoreService>().RunAsync(plan, stopping.Token).ConfigureAwait(false);
                if (result.Outcome == MaintenanceOutcome.Succeeded)
                {
                    LogSucceeded(logger, plan.Id, result.SafetyBackup);
                }
                else
                {
                    LogFailed(logger, result.Error, plan.Id, result.Outcome, result.Detail, result.SafetyBackup);
                }
            }
        }
        catch (Exception exception)
        {
            // RunAsync leaves maintenance itself; this is a fault in getting it to run at all.
            LogCrashed(logger, exception, plan.Id);
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<MaintenanceMode>().End(MaintenanceOutcome.Failed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restore {RestoreId} started; the instance is in maintenance")]
    private static partial void LogStarted(ILogger logger, Guid restoreId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restore {RestoreId} succeeded; safety backup {SafetyBackup}")]
    private static partial void LogSucceeded(ILogger logger, Guid restoreId, string? safetyBackup);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restore {RestoreId} ended {RestoreOutcome}: {Detail} Safety backup: {SafetyBackup}")]
    private static partial void LogFailed(ILogger logger, Exception? exception, Guid restoreId, MaintenanceOutcome restoreOutcome, string detail, string? safetyBackup);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restore {RestoreId} could not run; maintenance has ended and nothing was changed")]
    private static partial void LogCrashed(ILogger logger, Exception exception, Guid restoreId);
}

/// <summary>
/// Restore's housekeeping: at start, notes a restore a restart interrupted and removes the uploads
/// and work folders left behind (validations live in memory, so none survives a restart); then,
/// once a minute, drops validations no one confirmed within their lifetime, deleting their uploads.
/// </summary>
internal sealed partial class RestoreHousekeeping(IServiceScopeFactory scopes, MaintenanceMode maintenance, ILogger<RestoreHousekeeping> logger)
    : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (maintenance.RecoveredAtStart)
        {
            LogRecovered(logger);
        }
        else if (maintenance.IsActive)
        {
            LogStillInMaintenance(logger);
        }

        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IRestoreArchives>().DeleteLeftovers();
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var scope = scopes.CreateScope();
                var dropped = scope.ServiceProvider.GetRequiredService<RestoreValidator>().Sweep();
                if (dropped > 0)
                {
                    LogSwept(logger, dropped);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LogSweepFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A restore was interrupted by a restart before it changed anything; it is recorded as failed and the instance is open")]
    private static partial void LogRecovered(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The instance is in maintenance: a restore was interrupted after it began replacing data. The API stays closed until the instance is restored")]
    private static partial void LogStillInMaintenance(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped {ValidationCount} restore validations that were not confirmed in time")]
    private static partial void LogSwept(ILogger logger, int validationCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropping expired restore validations failed; it is tried again in a minute")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
