namespace n8Tracks.Gateway.Configuration;

/// <summary>The gateway's validated settings.</summary>
/// <param name="Port">The port the gateway listens on (<c>N8TRACKS_GATEWAY_PORT</c>).</param>
/// <param name="ApiUrl">
/// The n8Tracks URL (<c>N8TRACKS_API_URL</c>), always ending in a slash so that a relative request
/// keeps any sub-path.
/// </param>
/// <param name="LogLevel">The minimum log level (<c>N8TRACKS_LOG_LEVEL</c>).</param>
internal sealed record GatewayOptions(int Port, Uri ApiUrl, LogLevel LogLevel);
