using System.Reflection;

namespace n8Tracks.Api.Endpoints;

/// <summary>The product version of this build: the root <c>VERSION</c> file, stamped on the assembly by the build.</summary>
internal static class ProductVersion
{
    /// <summary>Reported only when the assembly carries no informational version at all.</summary>
    public const string Fallback = "0.0.0-dev";

    public static string Current { get; } = From(typeof(Program).Assembly);

    public static string From(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

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
}
