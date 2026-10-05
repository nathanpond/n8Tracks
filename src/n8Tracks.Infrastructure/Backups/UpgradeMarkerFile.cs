using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>
/// <c>upgrade-state.json</c> under the data path: the marker a failed or interrupted database
/// upgrade leaves (its format belongs to the upgrade's safety backup, #76). Only its removal is here.
/// </summary>
internal sealed class UpgradeMarkerFile(N8TracksOptions options) : IFailedUpgradeMarker
{
    public const string FileName = "upgrade-state.json";

    public bool Clear()
    {
        var path = Path.Combine(options.DataPath, FileName);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
