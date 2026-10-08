namespace n8Tracks.Application.Health;

/// <summary>
/// Reports the health of the instance. Every call runs the checks again, except that the schema
/// version is read from the database at most every 30 seconds.
/// </summary>
public interface IHealthService
{
    /// <summary>
    /// Checks every component and returns the whole report. A check that fails or takes too long is
    /// reported as that component's status; it never throws.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<HealthReport> GetReportAsync(CancellationToken cancellationToken);
}
