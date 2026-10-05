using System.Text.Json;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Infrastructure.Maintenance;

/// <summary>
/// <c>maintenance.json</c> under the data path: the maintenance state, kept outside the database
/// (which a restore replaces). It is written to a temporary file and moved over the old one, so a
/// reader never sees half of it. It holds no path and no error text.
/// </summary>
internal sealed class MaintenanceStateFile(N8TracksOptions options) : IMaintenanceStateStore
{
    public const string FileName = "maintenance.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath { get; } = Path.Combine(options.DataPath, FileName);

    public MaintenanceSnapshot? Read() => Read(FilePath);

    /// <summary>Reads the file at <paramref name="path"/>; null when it is missing or unreadable.</summary>
    public static MaintenanceSnapshot? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var stored = JsonSerializer.Deserialize<StoredState>(File.ReadAllBytes(path), Json);
            if (stored is null)
            {
                return null;
            }

            var stage = MaintenanceSnapshot.ParseStage(stored.Stage);

            // A file saying "active" at a stage this build does not know is still active: never open the API on a guess.
            return new MaintenanceSnapshot(
                stored.Active,
                stage ?? (stored.Active ? MaintenanceStage.Replacing : null),
                Math.Clamp(stored.Percent, 0, 100),
                MaintenanceSnapshot.ParseOutcome(stored.Outcome));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Write(MaintenanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var stored = new StoredState(
            snapshot.Active,
            snapshot.Stage is { } stage ? MaintenanceSnapshot.StageText(stage) : null,
            snapshot.Percent,
            snapshot.Outcome is { } outcome ? MaintenanceSnapshot.OutcomeText(outcome) : null);

        Directory.CreateDirectory(options.DataPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(stored, Json));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed record StoredState(bool Active, string? Stage, int Percent, string? Outcome);
}
