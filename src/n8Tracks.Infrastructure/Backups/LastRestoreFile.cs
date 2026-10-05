using System.Text.Json;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>
/// <c>last-restore.json</c> under the data path: how the last restore that began replacing data
/// ended, kept outside the database (which a restore replaces). It is written to a temporary file
/// and moved over the old one, so a reader never sees half of it. Only signed-in pages show it.
/// </summary>
internal sealed class LastRestoreFile(N8TracksOptions options) : ILastRestoreStore
{
    public const string FileName = "last-restore.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath { get; } = Path.Combine(options.DataPath, FileName);

    public LastRestore? Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(FilePath), Json);
            if (stored is not { ArchiveName: not null, Detail: not null, SafetyBackupName: not null, SafetyBackupPath: not null }
                || MaintenanceSnapshot.ParseOutcome(stored.Outcome) is not { } outcome)
            {
                return null;
            }

            return new LastRestore(
                outcome,
                stored.FinishedUtc,
                stored.ArchiveName,
                MaintenanceSnapshot.ParseStage(stored.FailedStage),
                stored.Detail,
                new SafetyBackupRecord(
                    stored.SafetyBackupLocation == "mount" ? BackupLocation.Mount : BackupLocation.Data,
                    stored.SafetyBackupName,
                    stored.SafetyBackupPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Write(LastRestore lastRestore)
    {
        ArgumentNullException.ThrowIfNull(lastRestore);

        var stored = new Stored(
            MaintenanceSnapshot.OutcomeText(lastRestore.Outcome),
            lastRestore.FinishedUtc,
            lastRestore.ArchiveName,
            lastRestore.FailedStage is { } stage ? MaintenanceSnapshot.StageText(stage) : null,
            lastRestore.Detail,
            lastRestore.SafetyBackup.Location == BackupLocation.Mount ? "mount" : "data",
            lastRestore.SafetyBackup.Name,
            lastRestore.SafetyBackup.Path);

        Directory.CreateDirectory(options.DataPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(stored, Json));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed record Stored(
        string? Outcome,
        DateTimeOffset FinishedUtc,
        string? ArchiveName,
        string? FailedStage,
        string? Detail,
        string? SafetyBackupLocation,
        string? SafetyBackupName,
        string? SafetyBackupPath);
}
