namespace n8Tracks.Domain.Suno;

/// <summary>
/// The hosts Suno streams a clip's audio from that a browser may play directly (#221): the one
/// server-side list of Suno hosts. The operator can replace it with the setting
/// <c>N8TRACKS_SUNO_AUDIO_HOSTS</c>, in case Suno moves its audio; the instance in use is the one
/// the settings hold, and every reader takes it from there.
/// <para>
/// The default (<see cref="Default"/>) holds only the playback host seen in the fixtures' and the
/// spikes' <c>media_urls</c> (TS-003, TS-004: unsigned, playable with no credentials). Suno's API host
/// (<c>audio_url</c> there answers <c>/api/forbidden</c>) and the signed-download host are left out.
/// The browser extension keeps its own lists in its adapter; the default is a subset of its audio hosts
/// (checked by a test). An operator's list is outside that check.
/// </para>
/// <para>
/// The server never requests these addresses: it answers a stored one to the browser, which plays it,
/// and the Content Security Policy's <c>media-src</c> allows exactly these hosts beside the app's own.
/// </para>
/// </summary>
public sealed class SunoAudioHosts
{
    private const int MaxHostLength = 253;
    private const int MaxLabelLength = 63;

    private SunoAudioHosts(IReadOnlyList<string> hosts) => Hosts = hosts;

    /// <summary>The list when the operator sets none: Suno's CloudFront playback host.</summary>
    public static SunoAudioHosts Default { get; } = new(["d2lwuy8qc234o3.cloudfront.net"]);

    /// <summary>The listed hosts, lower case, each once, in the order given.</summary>
    public IReadOnlyList<string> Hosts { get; }

    /// <summary>
    /// Reads <paramref name="value"/>, a comma-separated list of bare DNS host names (spaces around a
    /// name are ignored, and a name listed twice counts once). The list, or null and why it was refused
    /// (<paramref name="reason"/>): no name at all, or a name with a scheme, user info, a port, a path, a
    /// wildcard, or anything else that is not a DNS host name. A refused name is echoed unless it may
    /// carry a password.
    /// </summary>
    public static SunoAudioHosts? Parse(string value, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(value);

        var names = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0)
        {
            reason = "must name at least one host, such as " + Default.Hosts[0] + ".";
            return null;
        }

        var hosts = new List<string>();
        foreach (var name in names)
        {
            reason = Refusal(name);
            if (reason is not null)
            {
                return null;
            }

            var host = name.ToLowerInvariant();
            if (!hosts.Contains(host, StringComparer.Ordinal))
            {
                hosts.Add(host);
            }
        }

        reason = null;
        return new SunoAudioHosts(hosts);
    }

    /// <summary>
    /// <paramref name="address"/> when it is an absolute HTTPS address on a listed host, on HTTPS's own
    /// port, with no user name or password; null otherwise (an address anywhere else is no address).
    /// </summary>
    public string? Playable(string? address)
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

    private static string? Refusal(string name)
    {
        const string bare = "must be bare host names, such as d2lwuy8qc234o3.cloudfront.net";

        if (name.Contains('@', StringComparison.Ordinal))
        {
            return $"{bare}, with no user name or password.";
        }

        if (name.Contains("://", StringComparison.Ordinal))
        {
            return $"{bare}, with no scheme, but '{name}' has one.";
        }

        if (name.Contains('*', StringComparison.Ordinal))
        {
            return $"{bare}, with no wildcard, but '{name}' has one.";
        }

        if (name.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            return $"{bare}, with no path, but '{name}' has one.";
        }

        if (name.Contains(':', StringComparison.Ordinal))
        {
            return $"{bare}, with no port, but '{name}' has one.";
        }

        return IsDnsHostName(name) ? null : $"{bare}, but '{name}' is not a DNS host name.";
    }

    /// <summary>
    /// Letters, digits, and hyphens in dot-separated labels of 1 to 63 characters, with no hyphen at a
    /// label's start or end, at most 253 characters, and a last label that is not all digits (so not an
    /// IP address).
    /// </summary>
    private static bool IsDnsHostName(string name)
    {
        if (name.Length > MaxHostLength)
        {
            return false;
        }

        var labels = name.Split('.');
        return labels.All(static label => label.Length is >= 1 and <= MaxLabelLength
                && label.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-')
                && label[0] != '-'
                && label[^1] != '-')
            && !labels[^1].All(char.IsAsciiDigit);
    }
}
