namespace n8Tracks.Gateway.Health;

/// <summary>The body of the gateway's <c>GET /health</c>.</summary>
/// <param name="Status"><c>healthy</c> or <c>degraded</c>.</param>
/// <param name="Upstream"><c>reachable</c> or <c>unreachable</c>.</param>
/// <param name="Version">The gateway's own product version.</param>
/// <param name="Compatible">
/// Whether the upstream's major.minor equals the gateway's; null when the upstream is unreachable or
/// its version cannot be read.
/// </param>
internal sealed record GatewayHealthResponse(string Status, string Upstream, string Version, bool? Compatible);

/// <summary>What the gateway knows about the upstream after a probe.</summary>
internal enum UpstreamState
{
    Compatible,
    Mismatched,
    VersionUnreadable,
    Unreachable,
}

/// <summary>Turns a probe into the upstream state and the health response.</summary>
internal static class GatewayHealth
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Reachable = "reachable";
    public const string Unreachable = "unreachable";

    public static UpstreamState StateOf(UpstreamProbe probe, string gatewayVersion)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (!probe.Reachable)
        {
            return UpstreamState.Unreachable;
        }

        if (!ProductVersion.TryReadMajorMinor(probe.Version, out var upstreamMajor, out var upstreamMinor)
            || !ProductVersion.TryReadMajorMinor(gatewayVersion, out var major, out var minor))
        {
            return UpstreamState.VersionUnreadable;
        }

        return upstreamMajor == major && upstreamMinor == minor ? UpstreamState.Compatible : UpstreamState.Mismatched;
    }

    public static GatewayHealthResponse Respond(UpstreamState state, string gatewayVersion) => state switch
    {
        UpstreamState.Compatible => new GatewayHealthResponse(Healthy, Reachable, gatewayVersion, true),
        UpstreamState.Mismatched => new GatewayHealthResponse(Degraded, Reachable, gatewayVersion, false),
        UpstreamState.VersionUnreadable => new GatewayHealthResponse(Degraded, Reachable, gatewayVersion, null),
        _ => new GatewayHealthResponse(Degraded, Unreachable, gatewayVersion, null),
    };
}
