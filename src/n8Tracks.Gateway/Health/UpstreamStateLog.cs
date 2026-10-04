namespace n8Tracks.Gateway.Health;

/// <summary>
/// Remembers the last upstream state and writes one line when it changes: a Warning when the upstream
/// becomes unreachable, mismatched, or of unknown version, and an Information line when it returns to
/// compatible. Repeated probes with the same result write nothing. The lines never carry the URL.
/// </summary>
internal sealed partial class UpstreamStateLog(ILogger<UpstreamStateLog> logger)
{
    private readonly Lock gate = new();
    private UpstreamState? last;

    public void Report(UpstreamState state, UpstreamProbe probe, string gatewayVersion)
    {
        ArgumentNullException.ThrowIfNull(probe);

        UpstreamState? previous;
        lock (gate)
        {
            previous = last;
            if (previous == state)
            {
                return;
            }

            last = state;
        }

        switch (state)
        {
            case UpstreamState.Unreachable:
                LogUnreachable(logger, probe.FailureKind ?? "unknown");
                break;
            case UpstreamState.VersionUnreadable:
                LogVersionUnreadable(logger);
                break;
            case UpstreamState.Mismatched:
                _ = ProductVersion.TryReadMajorMinor(probe.Version, out var major, out var minor);
                LogMismatched(logger, major, minor, gatewayVersion);
                break;
            case UpstreamState.Compatible when previous is not null:
                LogCompatibleAgain(logger);
                break;
            default:
                // Compatible on the first probe: nothing to report.
                break;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "n8Tracks is unreachable ({Reason}); the gateway is degraded.")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "n8Tracks answered without a readable version; compatibility is unknown and the gateway is degraded.")]
    private static partial void LogVersionUnreadable(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Version mismatch: n8Tracks is {UpstreamMajor}.{UpstreamMinor} but this gateway is {GatewayVersion}; major and minor must match. The gateway is degraded.")]
    private static partial void LogMismatched(ILogger logger, int upstreamMajor, int upstreamMinor, string gatewayVersion);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "n8Tracks is reachable and compatible again.")]
    private static partial void LogCompatibleAgain(ILogger logger);
}
