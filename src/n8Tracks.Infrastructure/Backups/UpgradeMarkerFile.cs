using System.Text.Json;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>The stages an upgrade's marker records.</summary>
internal static class UpgradeStages
{
    /// <summary>The safety backup is verified and migrations are being applied: the database may be half-migrated.</summary>
    public const string Migrating = "migrating";

    /// <summary>A migration failed, or an upgrade was interrupted, and the safety backup is being put back (or could not be).</summary>
    public const string Restoring = "restoring";

    /// <summary>The safety backup was put back: the database is the one from before the upgrade.</summary>
    public const string Restored = "restored";
}

/// <summary>The safety backup an upgrade took, as the marker records it.</summary>
/// <param name="Location"><c>mount</c> or <c>data</c>.</param>
/// <param name="Name">The archive's file name.</param>
/// <param name="Path">The archive's full path, for the operator.</param>
internal sealed record UpgradeSafetyBackup(string Location, string Name, string Path)
{
    public BackupLocation BackupLocation => Location == "mount" ? BackupLocation.Mount : BackupLocation.Data;

    public static UpgradeSafetyBackup From(CreatedBackup created, string folder) =>
        new(created.Location == BackupLocation.Mount ? "mount" : "data", created.Name, System.IO.Path.Combine(folder, created.Name));
}

/// <summary>
/// What <c>upgrade-state.json</c> holds: the application version that began the upgrade, the
/// migrations it went from and to, its safety backup, and how far it got.
/// </summary>
/// <param name="ApplicationVersion">The version that began the upgrade; a later start of the same version refuses to start.</param>
/// <param name="FromMigration">The newest migration the database had before the upgrade.</param>
/// <param name="TargetMigration">The newest migration of the version that began the upgrade.</param>
/// <param name="SafetyBackup">The verified safety backup taken before any migration.</param>
/// <param name="Stage">One of <see cref="UpgradeStages"/>.</param>
/// <param name="StartedAt">When the upgrade began.</param>
/// <param name="FailedMigration">The migration (or step) that failed, or null when the upgrade was interrupted.</param>
/// <param name="RestoreId">The ID the putting back of the safety backup runs under, so a repeat finds its own work.</param>
internal sealed record UpgradeMarker(
    string ApplicationVersion,
    string FromMigration,
    string TargetMigration,
    UpgradeSafetyBackup SafetyBackup,
    string Stage,
    DateTimeOffset StartedAt,
    string? FailedMigration = null,
    Guid? RestoreId = null)
{
    /// <summary>Whether the database may be the one the upgrade left, not the one from before it.</summary>
    public bool MayBeHalfMigrated => Stage is UpgradeStages.Migrating or UpgradeStages.Restoring;
}

/// <summary>
/// <c>upgrade-state.json</c> under the data path: written (atomically) once an upgrade's safety
/// backup is verified and before any migration, and removed when the upgrade succeeds. A failed or
/// interrupted upgrade leaves it behind (see <see cref="Persistence.DatabaseStartup"/>).
/// </summary>
internal sealed class UpgradeMarkerFile(N8TracksOptions options) : IFailedUpgradeMarker
{
    public const string FileName = "upgrade-state.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath => Path.Combine(options.DataPath, FileName);

    /// <summary>The marker, or null when there is none.</summary>
    /// <exception cref="InvalidDataException">The file is there but is not a marker this build can read; nothing is guessed.</exception>
    public UpgradeMarker? Read()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UpgradeMarker>(bytes, Json) is
            {
                ApplicationVersion: not null,
                FromMigration: not null,
                TargetMigration: not null,
                SafetyBackup: { Location: "mount" or "data", Name: { Length: > 0 }, Path: { Length: > 0 } },
                Stage: UpgradeStages.Migrating or UpgradeStages.Restoring or UpgradeStages.Restored,
            } marker
                ? marker
                : throw new InvalidDataException($"{FilePath} is not an upgrade marker this version can read.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{FilePath} is not an upgrade marker this version can read.", exception);
        }
    }

    /// <summary>Replaces the marker atomically, flushed to disk before it is moved into place.</summary>
    public void Write(UpgradeMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        var temporary = FilePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, marker, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }

    public bool Clear()
    {
        if (!File.Exists(FilePath))
        {
            return false;
        }

        File.Delete(FilePath);
        return true;
    }
}
