namespace n8Tracks.Infrastructure.Health;

/// <summary>How one run of a <see cref="DeadlineCheck"/> ended.</summary>
internal enum CheckResult
{
    Passed,
    Failed,
    TimedOut,
}

/// <summary>The result of a check, with the exception when it failed by throwing.</summary>
internal readonly record struct CheckOutcome(CheckResult Result, Exception? Exception = null);

/// <summary>
/// Runs a blocking check on a worker and stops waiting for it at a deadline. The check itself may
/// not be interruptible (a file system call on a dead mount), so a worker that overruns is abandoned
/// rather than stopped. At most one worker is alive at a time: while an abandoned one is still
/// running, the check answers <see cref="CheckResult.TimedOut"/> at once instead of starting another,
/// and callers that arrive while a worker is in flight wait for that worker.
/// </summary>
internal sealed class DeadlineCheck(Func<CancellationToken, bool> check, TimeSpan timeout)
{
    private readonly Lock gate = new();
    private Task<bool>? worker;
    private bool abandoned;

    public async Task<CheckOutcome> RunAsync(CancellationToken cancellationToken)
    {
        Task<bool> current;
        lock (gate)
        {
            if (worker is { IsCompleted: false })
            {
                if (abandoned)
                {
                    return new CheckOutcome(CheckResult.TimedOut);
                }
            }
            else
            {
                abandoned = false;
                worker = Start();
            }

            current = worker;
        }

        try
        {
            var passed = await current.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

            return new CheckOutcome(passed ? CheckResult.Passed : CheckResult.Failed);
        }
        catch (TimeoutException)
        {
            lock (gate)
            {
                if (ReferenceEquals(worker, current) && !current.IsCompleted)
                {
                    abandoned = true;
                }
            }

            return new CheckOutcome(CheckResult.TimedOut);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The worker noticed its own deadline before the wait did.
            return new CheckOutcome(CheckResult.TimedOut);
        }
        catch (Exception exception)
        {
            return new CheckOutcome(CheckResult.Failed, exception);
        }
    }

    private Task<bool> Start()
    {
        // The worker's token is its deadline only, never the request's: other callers may be waiting on it.
        var deadline = new CancellationTokenSource(timeout);
        var started = Task.Run(() => check(deadline.Token), CancellationToken.None);

        // Disposed when the worker is done with it, and the exception of an abandoned worker is observed.
        _ = started.ContinueWith(
            static (finished, state) =>
            {
                _ = finished.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            deadline,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return started;
    }
}
