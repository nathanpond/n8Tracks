namespace n8Tracks.Domain.Suno;

/// <summary>
/// The hosts Suno streams a clip's audio from that a browser may play directly (#221): the one
/// server-side list of Suno hosts. It holds only the playback host seen in the fixtures' and the
/// spikes' <c>media_urls</c> (TS-003, TS-004: unsigned, playable with no credentials). Suno's API host
/// (<c>audio_url</c> there answers <c>/api/forbidden</c>) and the signed-download host are left out.
/// The browser extension keeps its own lists in its adapter; this list is a subset of its audio hosts
/// (checked by a test).
/// <para>
/// The server never requests these addresses: it answers a stored one to the browser, which plays it,
/// and the Content Security Policy's <c>media-src</c> allows exactly these hosts beside the app's own.
/// </para>
/// </summary>
public static class SunoAudioHosts
{
    /// <summary>The listed hosts, lower case.</summary>
    public static IReadOnlyList<string> Hosts { get; } = ["d2lwuy8qc234o3.cloudfront.net"];

    /// <summary>
    /// <paramref name="address"/> when it is an absolute HTTPS address on a listed host, on HTTPS's own
    /// port, with no user name or password; null otherwise (an address anywhere else is no address).
    /// </summary>
    public static string? Playable(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0)
        {
            return null;
        }

        return Hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) ? address : null;
    }
}
