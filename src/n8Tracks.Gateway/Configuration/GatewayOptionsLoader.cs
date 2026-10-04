using System.Globalization;

namespace n8Tracks.Gateway.Configuration;

/// <summary>
/// Binds the gateway's environment variables to <see cref="GatewayOptions"/> and validates them.
/// An empty or whitespace-only variable counts as unset. The gateway keeps its own copy of this small
/// loader: sharing the app's would need a project reference the isolation guard forbids.
/// </summary>
internal static class GatewayOptionsLoader
{
    public const string Port = "N8TRACKS_GATEWAY_PORT";
    public const string ApiUrl = "N8TRACKS_API_URL";
    public const string LogLevelVariable = "N8TRACKS_LOG_LEVEL";

    public const int DefaultPort = 8788;
    public const LogLevel DefaultLogLevel = LogLevel.Information;

    private const int MaxEchoedLength = 200;

    /// <summary>Microsoft's level names, without <c>None</c>: the log cannot be switched off.</summary>
    private static readonly Dictionary<string, LogLevel> LogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Trace"] = LogLevel.Trace,
        ["Debug"] = LogLevel.Debug,
        ["Information"] = LogLevel.Information,
        ["Warning"] = LogLevel.Warning,
        ["Error"] = LogLevel.Error,
        ["Critical"] = LogLevel.Critical,
    };

    /// <summary>Loads and validates the settings.</summary>
    /// <exception cref="ConfigurationValidationException">One or more values are invalid; every failure is listed.</exception>
    public static GatewayOptions Load(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var errors = new List<ConfigurationError>();
        var variables = environment.Variables;

        var port = ReadPort(variables, errors);
        var apiUrl = ReadApiUrl(variables, errors);
        var logLevel = ReadLogLevel(variables, errors);

        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        return new GatewayOptions(port, apiUrl!, logLevel);
    }

    /// <summary>
    /// The configured log level, or the default when it is unset or invalid. Logging is set up before
    /// the settings are validated, so this must not throw; <see cref="Load"/> reports the invalid value.
    /// </summary>
    public static LogLevel LogLevelOrDefault(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return Value(environment.Variables, LogLevelVariable) is { } value && LogLevels.TryGetValue(value, out var level)
            ? level
            : DefaultLogLevel;
    }

    private static string? Value(IReadOnlyDictionary<string, string> variables, string name) =>
        variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int ReadPort(IReadOnlyDictionary<string, string> variables, List<ConfigurationError> errors)
    {
        var value = Value(variables, Port);
        if (value is null)
        {
            return DefaultPort;
        }

        // Canonical decimal only: no sign, no leading zero, no separators.
        var canonical = value.Length is >= 1 and <= 5 && value[0] != '0' && value.All(char.IsAsciiDigit);
        if (canonical
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535)
        {
            return port;
        }

        errors.Add(new ConfigurationError(Port, $"must be a whole number from 1 to 65535, but was '{Echo(value)}'."));
        return DefaultPort;
    }

    private static Uri? ReadApiUrl(IReadOnlyDictionary<string, string> variables, List<ConfigurationError> errors)
    {
        var value = Value(variables, ApiUrl);
        if (value is null)
        {
            errors.Add(new ConfigurationError(
                ApiUrl,
                "is required: set it to the URL of n8Tracks, such as http://n8tracks:8787."));
            return null;
        }

        // The value is never echoed: a URL can carry credentials.
        var reason = TryParseApiUrl(value, out var apiUrl);
        if (reason is not null)
        {
            errors.Add(new ConfigurationError(ApiUrl, reason));
        }

        return apiUrl;
    }

    private static string? TryParseApiUrl(string value, out Uri? apiUrl)
    {
        apiUrl = null;

        const string notAbsolute = "must be an absolute http or https URL, such as http://n8tracks:8787.";

        var schemeLength =
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http://".Length
            : value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https://".Length
            : 0;

        if (schemeLength == 0
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || string.IsNullOrEmpty(parsed.Host))
        {
            return notAbsolute;
        }

        if (value.Contains('?', StringComparison.Ordinal))
        {
            return "must not have a query string.";
        }

        if (value.Contains('#', StringComparison.Ordinal))
        {
            return "must not have a fragment.";
        }

        var pathStart = value.IndexOf('/', schemeLength);
        var authority = pathStart < 0 ? value[schemeLength..] : value[schemeLength..pathStart];

        if (authority.Contains('@', StringComparison.Ordinal) || parsed.UserInfo.Length > 0)
        {
            return "must not have user info (a user name or password).";
        }

        if (authority.Length == 0)
        {
            return notAbsolute;
        }

        // A base address must end in a slash, or a relative request would replace its last segment.
        var path = parsed.AbsolutePath.TrimEnd('/') + "/";
        apiUrl = new Uri(parsed.GetLeftPart(UriPartial.Authority) + path);
        return null;
    }

    private static LogLevel ReadLogLevel(IReadOnlyDictionary<string, string> variables, List<ConfigurationError> errors)
    {
        var value = Value(variables, LogLevelVariable);
        if (value is null)
        {
            return DefaultLogLevel;
        }

        if (LogLevels.TryGetValue(value, out var level))
        {
            return level;
        }

        errors.Add(new ConfigurationError(
            LogLevelVariable,
            $"must be one of {string.Join(", ", LogLevels.Keys)}, but was '{Echo(value)}'."));
        return DefaultLogLevel;
    }

    private static string Echo(string value) =>
        value.Length <= MaxEchoedLength ? value : string.Concat(value.AsSpan(0, MaxEchoedLength), "…");
}
