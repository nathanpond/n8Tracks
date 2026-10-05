using System.Text.Json;
using n8Tracks.Application.Jobs;

namespace n8Tracks.Application.Backups;

/// <summary>How "Back up now" ended.</summary>
/// <param name="JobId">The new job's ID, or the one already queued or running.</param>
/// <param name="AlreadyInProgress">True when nothing was queued because a backup was queued or running already.</param>
public sealed record BackupStart(Guid JobId, bool AlreadyInProgress);

/// <summary>
/// Backups of the whole instance: starting one (as a <c>backup</c> job), listing, opening for
/// download, and deleting. Every backup action is the signed-in administrator's alone; the
/// endpoints enforce that, not this service.
/// </summary>
public sealed class BackupService(IBackupStorage storage, IJobStore jobs, IJobQueue queue, BackupStartLock startLock)
{
    /// <summary>The job type every backup runs as, whatever its kind.</summary>
    public const string JobType = "backup";

    /// <summary>How the payload is written: camelCase, as the handler reads it.</summary>
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Queues a backup of <paramref name="kind"/>, unless one is queued or running already, in which
    /// case that job is returned and nothing is queued. The check and the enqueue happen under one
    /// lock, so two requests at once queue one job. <paramref name="retry"/> marks a scheduled
    /// backup as the one retry after a failure.
    /// </summary>
    public Task<BackupStart> StartAsync(BackupKind kind, CancellationToken cancellationToken) => StartAsync(kind, retry: false, cancellationToken);

    /// <inheritdoc cref="StartAsync(BackupKind, CancellationToken)"/>
    public async Task<BackupStart> StartAsync(BackupKind kind, bool retry, CancellationToken cancellationToken)
    {
        await startLock.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await jobs.FindActiveAsync(JobType, cancellationToken).ConfigureAwait(false) is { } active)
            {
                return new BackupStart(active, AlreadyInProgress: true);
            }

            var payload = JsonSerializer.SerializeToElement(new BackupJobPayload(BackupKinds.Text(kind), retry), PayloadJson);
            var id = await queue.EnqueueAsync(JobType, payload, cancellationToken).ConfigureAwait(false);
            return new BackupStart(id, AlreadyInProgress: false);
        }
        finally
        {
            startLock.Gate.Release();
        }
    }

    /// <summary>Where the next backup goes, the backup in progress, and every archive in both folders.</summary>
    public async Task<BackupListing> ListAsync(CancellationToken cancellationToken)
    {
        var destination = storage.ResolveDestination();
        var active = await jobs.FindActiveAsync(JobType, cancellationToken).ConfigureAwait(false);
        var archives = await storage.ListAsync(cancellationToken).ConfigureAwait(false);

        return new BackupListing(destination.Location, destination.SharesDiskWithData, active, archives);
    }

    /// <summary>The archive, or null when there is none by that location and name.</summary>
    public Task<BackupArchive?> FindAsync(BackupLocation location, string name, CancellationToken cancellationToken) =>
        storage.FindAsync(location, name, cancellationToken);

    /// <summary>Opens a found archive for streaming; null when it went in the meantime.</summary>
    public Stream? OpenRead(BackupArchive archive) => storage.OpenRead(archive);

    /// <summary>Deletes the archive; false when there is none by that location and name.</summary>
    public async Task<bool> DeleteAsync(BackupLocation location, string name, CancellationToken cancellationToken) =>
        await storage.FindAsync(location, name, cancellationToken).ConfigureAwait(false) is { } archive && storage.Delete(archive);
}

/// <summary>
/// The one lock "Back up now" takes, so the check for a backup in progress and the enqueue are one
/// step. A singleton: there is one worker, and one process, per data path.
/// </summary>
public sealed class BackupStartLock : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose() => Gate.Dispose();
}

/// <summary>The text of each kind, as the manifest and the job payload write it.</summary>
public static class BackupKinds
{
    public static string Text(BackupKind kind) => kind switch
    {
        BackupKind.Manual => "manual",
        BackupKind.Scheduled => "scheduled",
        BackupKind.Safety => "safety",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown backup kind."),
    };

    /// <summary>The kind <paramref name="text"/> names, or null for one this build does not know.</summary>
    public static BackupKind? Parse(string? text) => text switch
    {
        "manual" => BackupKind.Manual,
        "scheduled" => BackupKind.Scheduled,
        "safety" => BackupKind.Safety,
        _ => null,
    };
}

/// <summary>What a backup job is enqueued with, written camelCase.</summary>
/// <param name="Kind">The kind's text (<see cref="BackupKinds.Text"/>).</param>
/// <param name="Retry">Whether a scheduled backup is the one retry after a failure.</param>
public sealed record BackupJobPayload(string Kind, bool Retry = false);

/// <summary>
/// Runs a <c>backup</c> job: removes what earlier interrupted runs left behind, resolves the
/// destination when the job starts, then copies, archives, and verifies, reporting the three
/// phases. A scheduled backup is recorded as the latest attempt when it starts and, once it has
/// succeeded, retention deletes the scheduled backups beyond the number kept; a failed one deletes
/// nothing. It cannot be cancelled; only shutdown stops it, and the half-built archive is then
/// removed. The result names the archive.
/// </summary>
public sealed class BackupJobHandler(IBackupStorage storage, IBackupWriter writer, BackupScheduleService schedule) : IJobHandler
{
    /// <summary>The share of the progress bar each phase ends at.</summary>
    private const int CopyEnd = 70;
    private const int ArchiveEnd = 90;
    private const int VerifyEnd = 99;

    public async Task<JsonElement?> RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = context.Payload is { ValueKind: JsonValueKind.Object } value ? value : default;
        var kind = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("kind", out var text)
            && text.ValueKind == JsonValueKind.String
            && BackupKinds.Parse(text.GetString()) is { } parsed
                ? parsed
                : BackupKind.Manual;
        var retry = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("retry", out var flag)
            && flag.ValueKind == JsonValueKind.True;

        if (kind == BackupKind.Scheduled)
        {
            await schedule.RecordStartAsync(context.JobId, retry, cancellationToken).ConfigureAwait(false);
        }

        // Only one backup runs at a time, so any temporary folder now is a leftover.
        storage.RemoveLeftoverTemporaryFolders();

        var destination = storage.ResolveDestination();
        var created = await writer.CreateAsync(
            destination,
            context.JobId,
            kind,
            (phase, percent) => context.Report(Overall(phase, percent), Message(phase)),
            cancellationToken).ConfigureAwait(false);

        if (kind == BackupKind.Scheduled)
        {
            await schedule.ApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.SerializeToElement(
            new
            {
                location = created.Location == BackupLocation.Mount ? "mount" : "data",
                name = created.Name,
                size = created.SizeBytes,
            });
    }

    /// <summary>The overall percentage at <paramref name="percent"/> through <paramref name="phase"/>.</summary>
    public static int Overall(BackupPhase phase, int percent)
    {
        var within = Math.Clamp(percent, 0, 100);
        var (start, end) = phase switch
        {
            BackupPhase.CopyingDatabase => (0, CopyEnd),
            BackupPhase.Archiving => (CopyEnd, ArchiveEnd),
            _ => (ArchiveEnd, VerifyEnd),
        };

        return start + ((end - start) * within / 100);
    }

    /// <summary>The phase's name as the page shows it.</summary>
    public static string Message(BackupPhase phase) => phase switch
    {
        BackupPhase.CopyingDatabase => "Copying database",
        BackupPhase.Archiving => "Archiving",
        _ => "Verifying",
    };
}
