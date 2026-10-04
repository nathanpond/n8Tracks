using System.Reflection;

namespace n8Tracks.Gateway.Health;

/// <summary>The product version of this build: the root <c>VERSION</c> file, stamped on the assembly by the build.</summary>
internal static class ProductVersion
{
    /// <summary>Reported only when the assembly carries no informational version at all.</summary>
    public const string Fallback = "0.0.0-dev";

    public static string Current { get; } =
        Parse(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The informational version without its <c>+&lt;source revision&gt;</c> suffix.</summary>
    public static string Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return Fallback;
        }

        var suffix = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var version = (suffix < 0 ? informationalVersion : informationalVersion[..suffix]).Trim();

        return version.Length == 0 ? Fallback : version;
    }

    /// <summary>
    /// Reads major and minor from the numeric prefix of a version (<c>0.1.7-edge.abc</c> gives 0 and 1).
    /// Components are compatible when both numbers match.
    /// </summary>
    public static bool TryReadMajorMinor(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        if (version is null)
        {
            return false;
        }

        var text = version.AsSpan();
        var dot = text.IndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        var minorLength = 0;
        var rest = text[(dot + 1)..];
        while (minorLength < rest.Length && char.IsAsciiDigit(rest[minorLength]))
        {
            minorLength++;
        }

        return TryReadNumber(text[..dot], out major) && TryReadNumber(rest[..minorLength], out minor);
    }

    private static bool TryReadNumber(ReadOnlySpan<char> digits, out int number)
    {
        number = 0;

        // At most nine digits, so the value always fits.
        if (digits.Length is < 1 or > 9)
        {
            return false;
        }

        foreach (var digit in digits)
        {
            if (!char.IsAsciiDigit(digit))
            {
                return false;
            }

            number = (number * 10) + (digit - '0');
        }

        return true;
    }
}
