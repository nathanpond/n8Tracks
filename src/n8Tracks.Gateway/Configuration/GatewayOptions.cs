namespace n8Tracks.Gateway.Configuration;

/// <summary>The gateway's validated settings.</summary>
/// <param name="Port">The port the gateway listens on (<c>N8TRACKS_GATEWAY_PORT</c>).</param>
/// <param name="ApiUrl">
/// The n8Tracks URL (<c>N8TRACKS_API_URL</c>), always ending in a slash so that a relative request
/// keeps any sub-path.
/// </param>
/// <param name="LogLevel">The minimum log level (<c>N8TRACKS_LOG_LEVEL</c>).</param>
internal sealed record GatewayOptions(int Port, Uri ApiUrl, LogLevel LogLevel)
{
    /// <summary>Where and how the log is also written to files (<c>N8TRACKS_GATEWAY_LOG_*</c>); null for none.</summary>
    public GatewayLogFiles? LogFiles { get; init; }
}

/// <summary>The gateway's log files (#234): its level is <c>N8TRACKS_LOG_LEVEL</c>'s, as for its console.</summary>
/// <param name="Folder">The folder the files go in (<c>N8TRACKS_GATEWAY_LOG_PATH</c>), absolute.</param>
/// <param name="RetentionDays">Days kept (<c>N8TRACKS_GATEWAY_LOG_RETENTION_DAYS</c>), 1 to 90, by the date in the file name.</param>
/// <param name="MaxMegabytes">The size cap in MB (<c>N8TRACKS_GATEWAY_LOG_MAX_MB</c>), 10 to 5120, the oldest deleted first.</param>
internal sealed record GatewayLogFiles(string Folder, int RetentionDays, int MaxMegabytes)
{
    /// <summary>The cap in bytes.</summary>
    public long MaxBytes => MaxMegabytes * 1024L * 1024L;
}
