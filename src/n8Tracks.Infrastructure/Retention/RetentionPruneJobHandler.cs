using System.Text.Json;
using Microsoft.Extensions.Logging;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Infrastructure.Retention;

/// <summary>
/// Runs the <see cref="RetentionPruneTask.JobType"/> job the daily task queues: records the start (so
/// the task does not queue another for the same planned time), prunes, and records the finish. A prune
/// that fails, or leaves files it could not delete, is logged at Warning; nothing retained is logged.
/// A second step expires staged Suno exports (#131, <see cref="ExportStagingService.ExpireAsync"/>); it
/// runs even when the prune failed, and its own failure is logged and fails the job.
/// </summary>
internal sealed partial class RetentionPruneJobHandler(
    RetentionService retention,
    ExportStagingService exports,
    IRetentionPruneStateStore state,
    TimeProvider time,
    ILogger<RetentionPruneJobHandler> logger) : IJobHandler
{
    public async Task<JsonElement?> RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var started = time.GetUtcNow();
        var armed = (await state.FindAsync(cancellationToken).ConfigureAwait(false))?.ArmedUtc ?? started;
        await state.WriteAsync(new RetentionPruneState(armed, started, null), cancellationToken).ConfigureAwait(false);

        RetentionPruneSummary summary;
        try
        {
            summary = await retention.PruneAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, exception);
            await ExpireExportsAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        var expiry = await ExpireExportsAsync(cancellationToken).ConfigureAwait(false);

        await state.WriteAsync(new RetentionPruneState(armed, started, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        if (summary.FilesFailed > 0)
        {
            LogPartial(logger, summary.GroupsPruned, summary.FilesFailed);
        }
        else if (summary.GroupsPruned > 0 || summary.FilesDeleted > 0)
        {
            LogPruned(logger, summary.GroupsPruned, summary.FilesDeleted);
        }

        context.Report(100);
        return JsonSerializer.SerializeToElement(new
        {
            groupsPruned = summary.GroupsPruned,
            filesDeleted = summary.FilesDeleted,
            filesKept = summary.FilesKept,
            filesFailed = summary.FilesFailed,
            exportsExpired = expiry.Expired,
            exportsDiscarded = expiry.Discarded,
            exportsFailed = expiry.Failed,
            committedExportsCleared = expiry.CommittedCleared,
        });
    }

    /// <summary>The second step: expires staged Suno exports and logs what it did (counts only).</summary>
    private async Task<ExportExpirySummary> ExpireExportsAsync(CancellationToken cancellationToken)
    {
        ExportExpirySummary expiry;
        try
        {
            expiry = await exports.ExpireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogExpiryFailed(logger, exception);
            throw;
        }

        if (expiry.Expired + expiry.Discarded + expiry.Failed + expiry.CommittedCleared > 0)
        {
            LogExpired(logger, expiry.Expired, expiry.Discarded, expiry.Failed, expiry.CommittedCleared);
        }

        return expiry;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The retention prune removed {GroupCount} deleted groups and {FileCount} files")]
    private static partial void LogPruned(ILogger logger, int groupCount, int fileCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The retention prune removed {GroupCount} deleted groups but could not delete {FileCount} files; the next run tries again")]
    private static partial void LogPartial(ILogger logger, int groupCount, int fileCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The retention prune failed; deleted records stay until the next run")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Staged Suno exports: {ExpiredCount} expired, {DiscardedCount} never completed and discarded, {FailedCount} stuck classifying and failed, {ClearedCount} committed and cleared")]
    private static partial void LogExpired(ILogger logger, int expiredCount, int discardedCount, int failedCount, int clearedCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expiring staged Suno exports failed; the next run tries again")]
    private static partial void LogExpiryFailed(ILogger logger, Exception exception);
}
