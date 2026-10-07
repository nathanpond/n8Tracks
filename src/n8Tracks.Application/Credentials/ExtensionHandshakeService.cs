using System.Globalization;
using System.Text.RegularExpressions;

namespace n8Tracks.Application.Credentials;

/// <summary>What the handshake answers: the application's version, the credential, and whether the two sides fit.</summary>
/// <param name="ApplicationVersion">The product version of this server, as the health endpoint reports it.</param>
/// <param name="CredentialName">The name of the credential whose token made the handshake.</param>
/// <param name="Scopes">The scopes it holds, whichever they are.</param>
/// <param name="Compatible">Whether the extension's version fits the application's (<see cref="ExtensionCompatibility"/>).</param>
public sealed record ExtensionHandshake(string ApplicationVersion, string CredentialName, IReadOnlyList<string> Scopes, bool Compatible);

/// <summary>
/// The versioning rule of the product (<c>.n8/config.yml</c>): an extension and an application are
/// compatible when their major and minor numbers are equal. The patch number and any pre-release
/// suffix are ignored. A version that cannot be read, or none at all, is never compatible.
/// </summary>
public static partial class ExtensionCompatibility
{
    /// <summary>Whether <paramref name="extensionVersion"/> works with <paramref name="applicationVersion"/>.</summary>
    public static bool IsCompatible(string? extensionVersion, string? applicationVersion) =>
        MajorMinor(extensionVersion) is { } extension
        && MajorMinor(applicationVersion) is { } application
        && extension == application;

    /// <summary>The major and minor numbers of <c>major.minor[.patch][-suffix]</c>, or null when it is not that.</summary>
    public static (int Major, int Minor)? MajorMinor(string? version)
    {
        if (version is null || VersionPattern().Match(version.Trim()) is not { Success: true } match)
        {
            return null;
        }

        return int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                ? (major, minor)
                : null;
    }

    [GeneratedRegex(@"^(?<major>\d{1,9})\.(?<minor>\d{1,9})(\.\d{1,9})?(-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPattern();
}

/// <summary>
/// The browser extension's handshake: any valid token may make it. It tells the extension which
/// version of n8Tracks it reached and which credential it holds, and records on that credential
/// the extension and adapter versions it reported, so Settings can show them. The adapter version
/// is recorded only; compatibility compares the product versions.
/// </summary>
public sealed class ExtensionHandshakeService(ICredentialStore credentials, TimeProvider time)
{
    /// <summary>A reported version longer than this is not kept.</summary>
    public const int ReportedVersionMaximumLength = 64;

    /// <summary>
    /// Records the sighting and answers. <paramref name="extensionVersion"/> and
    /// <paramref name="adapterVersion"/> are the raw header values: each is kept trimmed when it is
    /// 1 to <see cref="ReportedVersionMaximumLength"/> visible ASCII characters, and stored as null
    /// otherwise (a missing header included).
    /// </summary>
    public async Task<ExtensionHandshake> HandshakeAsync(
        VerifiedCredential credential,
        string? extensionVersion,
        string? adapterVersion,
        string applicationVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(applicationVersion);

        var extension = Reported(extensionVersion);
        var sighting = new ExtensionSighting(extension, Reported(adapterVersion), time.GetUtcNow());
        await credentials.RecordSightingAsync(credential.Id, sighting, cancellationToken).ConfigureAwait(false);

        return new ExtensionHandshake(
            applicationVersion,
            credential.Name,
            credential.Scopes,
            ExtensionCompatibility.IsCompatible(extension, applicationVersion));
    }

    /// <summary>The value as it is kept: trimmed, or null when it is missing, too long, or not visible ASCII.</summary>
    public static string? Reported(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed)
            || trimmed.Length > ReportedVersionMaximumLength
            || trimmed.Any(static character => character is < '!' or > '~')
                ? null
                : trimmed;
    }
}
