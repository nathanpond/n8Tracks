namespace n8Tracks.Application.Configuration;

/// <summary>
/// Every operator-facing setting, validated once at startup. The rest of the code takes this object
/// from dependency injection and never reads environment variables itself.
/// </summary>
/// <param name="Port">The TCP port the app listens on, over plain HTTP on all interfaces.</param>
/// <param name="BaseUrl">The public URL of the app, normalised (no trailing slash on a sub-path).</param>
/// <param name="PathBase">The sub-path every route is served under, such as <c>/n8tracks</c>; empty when the app is served at the root.</param>
/// <param name="TimeZone">The time zone times are shown in.</param>
/// <param name="LogLevel">The minimum level written to the application log.</param>
/// <param name="DataPath">Absolute path of the writable data directory.</param>
/// <param name="MediaPath">Absolute path of the media mount. It may not exist.</param>
/// <param name="BackupPath">Absolute path of the backup directory. It may not exist.</param>
public sealed record N8TracksOptions(
    int Port,
    Uri BaseUrl,
    string PathBase,
    TimeZoneInfo TimeZone,
    N8TracksLogLevel LogLevel,
    string DataPath,
    string MediaPath,
    string BackupPath);
