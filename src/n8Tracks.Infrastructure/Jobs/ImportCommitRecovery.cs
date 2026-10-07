using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Maintenance;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Infrastructure.Jobs;

/// <summary>
/// Once, when the server starts and before the job worker takes anything (#140): a Suno import commit
/// that was running when the process stopped is not resumed; its export goes back to <c>ready</c> with
/// its records classified again, so the user confirms again (<see cref="ImportCommitService.RecoverInterruptedAsync"/>).
/// Never stops startup, and does nothing while the instance is in maintenance.
/// </summary>
internal sealed partial class ImportCommitRecovery(IServiceScopeFactory scopes, MaintenanceMode maintenance, ILogger<ImportCommitRecovery> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (maintenance.IsActive)
        {
            return;
        }

        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var recovered = await scope.ServiceProvider.GetRequiredService<ImportCommitService>().RecoverInterruptedAsync(cancellationToken).ConfigureAwait(false);
                if (recovered > 0)
                {
                    LogRecovered(logger, recovered);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The export stays committing until the next start tries again.
            LogRecoveryFailed(logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Suno imports interrupted by a restart returned to review: {ExportCount}")]
    private static partial void LogRecovered(ILogger logger, int exportCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not return interrupted Suno imports to review; trying again at the next start")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);
}
