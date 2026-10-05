using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using n8Tracks.Application.Backups;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>One file the manifest lists: its path inside the archive, its size, and its SHA-256 in lower-case hex.</summary>
internal sealed record BackupManifestFile(string Path, long Size, string Sha256);

/// <summary>
/// <c>manifest.json</c>, format version 1: what made the archive, when, from which schema, and a
/// checksum of every other file in it.
/// </summary>
internal sealed record BackupManifest(
    int FormatVersion,
    string ApplicationVersion,
    string LastMigration,
    string CreatedAt,
    string Kind,
    IReadOnlyList<BackupManifestFile> Files)
{
    /// <summary>The manifest format this build writes and reads.</summary>
    public const int CurrentFormatVersion = 1;

    public const string EntryName = "manifest.json";
    public const string DatabaseEntry = "n8tracks.db";
    public const string SettingsEntry = "settings.json";
    public const string AssetsFolder = "assets/";

    /// <summary>The largest manifest listing will read: anything bigger is not one this app wrote.</summary>
    private const long MaximumLength = 4 * 1024 * 1024;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Time(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the manifest of an open archive. Null when there is none, it does not parse, it lacks a
    /// field, or it names an entry the archive does not have.
    /// </summary>
    public static (BackupManifest Manifest, DateTimeOffset CreatedUtc)? Read(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        if (archive.GetEntry(EntryName) is not { } entry || entry.Length > MaximumLength)
        {
            return null;
        }

        BackupManifest? manifest;
        try
        {
            using var stream = entry.Open();
            manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }

        if (manifest is not { FormatVersion: >= 1, ApplicationVersion: { Length: > 0 }, LastMigration: not null, Kind: { Length: > 0 }, Files: not null }
            || !DateTimeOffset.TryParseExact(
                manifest.CreatedAt,
                TimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var created))
        {
            return null;
        }

        foreach (var file in manifest.Files)
        {
            if (file is not { Path: { Length: > 0 }, Sha256: not null } || archive.GetEntry(file.Path) is null)
            {
                return null;
            }
        }

        return (manifest, created);
    }

    /// <summary>A newer format, or a kind this build does not know, is the work of a newer version.</summary>
    public BackupValidity Validity =>
        FormatVersion > CurrentFormatVersion || BackupKinds.Parse(Kind) is null ? BackupValidity.Newer : BackupValidity.Valid;
}
