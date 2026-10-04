namespace n8Tracks.Gateway.Health;

/// <summary>What one request to the n8Tracks health URL found.</summary>
/// <param name="Reachable">True when any HTTP response arrived in full within the time allowed.</param>
/// <param name="Version">The <c>version</c> field of the response, when it could be read.</param>
/// <param name="FailureKind">Why the upstream is unreachable: a short category, never a URL or an error message.</param>
internal sealed record UpstreamProbe(bool Reachable, string? Version, string? FailureKind)
{
    public static UpstreamProbe Answered(string? version) => new(true, version, null);

    public static UpstreamProbe Failed(string kind) => new(false, null, kind);
}
