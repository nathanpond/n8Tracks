using n8Tracks.Application.Configuration;
using n8Tracks.Application.Media;
using n8Tracks.Application.Setup;
using n8Tracks.Infrastructure.Health;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Setup;

/// <summary>
/// The storage and media checks of setup. Each is bounded by the health checks' deadline, so a hung
/// mount answers "no" instead of holding the request. Callers that arrive together share one run of a
/// check, and the shared run hands its result to them one after another on the thread that finished
/// it; each caller yields before going on, so what one does next (hashing a password) never runs inside
/// that hand-over and pushes the others past the deadline. Neither says why it failed: the setup status is
/// anonymous and reports fixed answers only, never a path or an error text.
/// </summary>
internal sealed class SetupChecks : ISetupChecks
{
    /// <summary>The <c>app_metadata</c> row the storage check writes: when the data path was last found writable.</summary>
    public const string StorageCheckedUtcKey = "storage_checked_utc";

    private const string ProbeFilePrefix = ".n8tracks-write-check-";

    private readonly IDatabaseConnectionFactory connections;
    private readonly string dataPath;
    private readonly DeadlineCheck storageCheck;
    private readonly DeadlineCheck mediaCheck;
    private readonly Serilog.ILogger log;

    public SetupChecks(N8TracksOptions options, IDatabaseConnectionFactory connections, IMediaMount media, Serilog.ILogger log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(media);

        this.connections = connections;
        this.log = log.ForContext<SetupChecks>();
        dataPath = options.DataPath;
        storageCheck = new DeadlineCheck(WriteDataPath, HealthService.CheckTimeout);
        mediaCheck = new DeadlineCheck(_ => media.Probe(), HealthService.CheckTimeout);
    }

    public async Task<bool> IsDataPathWritableAsync(CancellationToken cancellationToken)
    {
        var outcome = await storageCheck.RunAsync(cancellationToken).ConfigureAwait(false);
        await Task.Yield();
        if (outcome.Result != CheckResult.Passed)
        {
            log.Warning(outcome.Exception, "Setup check {Check} failed: {Result}", "storage", outcome.Result);
        }

        return outcome.Result == CheckResult.Passed;
    }

    public async Task<bool> IsMediaAvailableAsync(CancellationToken cancellationToken)
    {
        var outcome = await mediaCheck.RunAsync(cancellationToken).ConfigureAwait(false);
        await Task.Yield();

        return outcome.Result == CheckResult.Passed;
    }

    /// <summary>Commits a write to the database, then creates, writes, and removes a file beside it.</summary>
    private bool WriteDataPath(CancellationToken deadline)
    {
        using (var connection = connections.CreateForExistingDatabase())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"PRAGMA busy_timeout = {SqliteDatabase.BusyTimeoutMilliseconds}; "
                + "INSERT INTO app_metadata (key, value) VALUES ($key, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')) "
                + "ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
            var key = command.CreateParameter();
            key.ParameterName = "$key";
            key.Value = StorageCheckedUtcKey;
            command.Parameters.Add(key);
            command.ExecuteNonQuery();
        }

        deadline.ThrowIfCancellationRequested();

        var probe = Path.Combine(dataPath, ProbeFilePrefix + Guid.NewGuid().ToString("N"));
        using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            file.WriteByte(1);
            file.Flush(flushToDisk: true);
        }

        return true;
    }
}
