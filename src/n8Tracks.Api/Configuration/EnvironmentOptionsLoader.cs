using System.Globalization;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Configuration;

/// <summary>
/// Binds the operator-facing environment variables to <see cref="N8TracksOptions"/> and validates them.
/// An empty or whitespace-only variable counts as unset.
/// </summary>
internal static class EnvironmentOptionsLoader
{
    public const string Port = "N8TRACKS_PORT";
    public const string BaseUrl = "N8TRACKS_BASE_URL";
    public const string TimeZone = "TZ";
    public const string LogLevel = "N8TRACKS_LOG_LEVEL";
    public const string DataPath = "N8TRACKS_DATA_PATH";
    public const string MediaPath = "N8TRACKS_MEDIA_PATH";
    public const string BackupPath = "N8TRACKS_BACKUP_PATH";
    public const string SunoAudioHosts = "N8TRACKS_SUNO_AUDIO_HOSTS";

    /// <summary>
    /// Test-only: <c>1</c> makes the <c>seed-generation</c> command available outside Development. The
    /// end-to-end containers set it; a real instance never should.
    /// </summary>
    public const string EnableTestSeeding = "N8TRACKS_ENABLE_TEST_SEEDING";

    /// <summary>
    /// The one variable of the .NET host that is honoured, read like every other from the environment
    /// snapshot: the environment name. It is for development: <c>Development</c> adds the OpenAPI document.
    /// </summary>
    public const string HostEnvironment = "ASPNETCORE_ENVIRONMENT";

    public const int DefaultPort = 8787;
    public const string DefaultTimeZone = "UTC";
    public const N8TracksLogLevel DefaultLogLevel = N8TracksLogLevel.Information;
    public const string DefaultDataPath = "/data";
    public const string DefaultMediaPath = "/media";
    public const string DefaultBackupPath = "/backup";

    internal const string WriteTestFileName = ".n8tracks-write-test";

    private const string ProductPrefix = "N8TRACKS_";
    private const int MaxEchoedLength = 200;

    private static readonly HashSet<string> KnownVariables = new(StringComparer.Ordinal)
    {
        Port, BaseUrl, TimeZone, LogLevel, DataPath, MediaPath, BackupPath, SunoAudioHosts, EnableTestSeeding,
    };

    private static readonly Dictionary<string, N8TracksLogLevel> LogLevels =
        Enum.GetValues<N8TracksLogLevel>().ToDictionary(level => level.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The host's environment name: the value of <see cref="HostEnvironment"/>, or <c>Production</c>
    /// when it is unset or blank. No argument, other variable, or settings file can name it.
    /// </summary>
    public static string HostEnvironmentName(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return Value(environment.Variables, HostEnvironment) ?? Environments.Production;
    }

    /// <summary>Loads and validates the settings.</summary>
    /// <exception cref="ConfigurationValidationException">One or more values are invalid; every failure is listed.</exception>
    public static N8TracksOptions Load(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var errors = new List<ConfigurationError>();
        var variables = environment.Variables;

        var port = ReadPort(variables, errors);
        var (baseUrl, pathBase) = ReadBaseUrl(variables, port, errors);
        var timeZone = ReadTimeZone(variables, errors);
        var logLevel = ReadLogLevel(variables, errors);
        var mediaPath = ResolvePath(Value(variables, MediaPath) ?? DefaultMediaPath, environment.WorkingDirectory);
        var dataPath = ReadDataPath(variables, environment.WorkingDirectory, mediaPath, errors);
        var backupPath = ReadBackupPath(variables, environment.WorkingDirectory, mediaPath, errors);
        var sunoAudioHosts = ReadSunoAudioHosts(variables, errors);

        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        return new N8TracksOptions(port, baseUrl!, pathBase, timeZone!, logLevel, dataPath, mediaPath, backupPath)
        {
            SunoAudioHosts = sunoAudioHosts!,
        };
    }

    /// <summary>
    /// Loads and validates only what locates the running app from inside its own container: the port
    /// and the path of the base URL. Unlike <see cref="Load"/> it touches no directory.
    /// </summary>
    /// <exception cref="ConfigurationValidationException">The port or the base URL is invalid.</exception>
    public static (int Port, string PathBase) LoadListenAddress(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var errors = new List<ConfigurationError>();
        var port = ReadPort(environment.Variables, errors);
        var (_, pathBase) = ReadBaseUrl(environment.Variables, port, errors);

        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        return (port, pathBase);
    }

    /// <summary>
    /// Loads and validates only the data path, for a command that works on the database of the app
    /// in its own container and listens on nothing. Every other setting has its default value here,
    /// whatever the environment says, so a setting the command has no use for cannot stop it.
    /// </summary>
    /// <exception cref="ConfigurationValidationException">The data path is not a writable directory.</exception>
    public static N8TracksOptions LoadDataPathOnly(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var errors = new List<ConfigurationError>();
        var dataPath = ReadDataPath(environment.Variables, environment.WorkingDirectory, ResolvePath(DefaultMediaPath, environment.WorkingDirectory), errors);

        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        return new N8TracksOptions(
            DefaultPort,
            new Uri($"http://localhost:{DefaultPort.ToString(CultureInfo.InvariantCulture)}"),
            string.Empty,
            TimeZoneInfo.Utc,
            DefaultLogLevel,
            dataPath,
            ResolvePath(DefaultMediaPath, environment.WorkingDirectory),
            ResolvePath(DefaultBackupPath, environment.WorkingDirectory));
    }

    /// <summary>
    /// Loads and validates the data path and the time zone, for a command that works on the database
    /// of the app in its own container and shows times as the app does. Every other setting has its
    /// default value here, as for <see cref="LoadDataPathOnly"/>.
    /// </summary>
    /// <exception cref="ConfigurationValidationException">The data path is not a writable directory, or the time zone is unknown.</exception>
    public static N8TracksOptions LoadDataPathAndTimeZone(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var errors = new List<ConfigurationError>();
        var timeZone = ReadTimeZone(environment.Variables, errors);
        N8TracksOptions? options = null;
        try
        {
            options = LoadDataPathOnly(environment);
        }
        catch (ConfigurationValidationException exception)
        {
            errors.InsertRange(0, exception.Errors);
        }

        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        return options! with { TimeZone = timeZone! };
    }

    /// <summary>
    /// Loads and validates only the three paths, for a command that works on the files of a stopped
    /// instance: the data path (which must be a writable directory), and the media and backup mounts
    /// as configured. Every other setting has its default value here, whatever the environment says.
    /// </summary>
    /// <exception cref="ConfigurationValidationException">The data path is not a writable directory.</exception>
    public static N8TracksOptions LoadPathsOnly(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var options = LoadDataPathOnly(environment);
        var variables = environment.Variables;
        return options with
        {
            MediaPath = ResolvePath(Value(variables, MediaPath) ?? DefaultMediaPath, environment.WorkingDirectory),
            BackupPath = ResolvePath(Value(variables, BackupPath) ?? DefaultBackupPath, environment.WorkingDirectory),
        };
    }

    /// <summary>
    /// Names that start with <c>N8TRACKS_</c>, have a value, and are not settings the app knows: usually a typo.
    /// </summary>
    public static IReadOnlyList<string> FindUnknownVariables(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return
        [
            .. environment.Variables
                .Where(variable => variable.Key.StartsWith(ProductPrefix, StringComparison.OrdinalIgnoreCase)
                    && !KnownVariables.Contains(variable.Key)
                    && !string.IsNullOrWhiteSpace(variable.Value))
                .Select(variable => variable.Key)
                .Order(StringComparer.Ordinal),
        ];
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

    private static (Uri? BaseUrl, string PathBase) ReadBaseUrl(
        IReadOnlyDictionary<string, string> variables,
        int port,
        List<ConfigurationError> errors)
    {
        var value = Value(variables, BaseUrl);
        if (value is null)
        {
            return (new Uri($"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}"), string.Empty);
        }

        // The value is never echoed: a base URL can carry credentials.
        var reason = TryParseBaseUrl(value, out var baseUrl, out var pathBase);
        if (reason is null)
        {
            return (baseUrl, pathBase);
        }

        errors.Add(new ConfigurationError(BaseUrl, reason));
        return (null, string.Empty);
    }

    private static string? TryParseBaseUrl(string value, out Uri? baseUrl, out string pathBase)
    {
        baseUrl = null;
        pathBase = string.Empty;

        const string notAbsolute = "must be an absolute http or https URL, such as https://nas.example/n8tracks.";

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
        var path = pathStart < 0 ? string.Empty : value[pathStart..];

        if (authority.Contains('@', StringComparison.Ordinal) || parsed.UserInfo.Length > 0)
        {
            return "must not have user info (a user name or password).";
        }

        if (authority.Length == 0)
        {
            return notAbsolute;
        }

        // One trailing slash is dropped; a path of "/" means no path.
        if (path.EndsWith('/'))
        {
            path = path[..^1];
        }

        if (path.Length > 0)
        {
            foreach (var segment in path[1..].Split('/'))
            {
                if (segment.Length == 0)
                {
                    return "must not have repeated slashes in its path.";
                }

                if (segment is "." or "..")
                {
                    return "must not have '.' or '..' segments in its path.";
                }

                if (!segment.All(IsPathCharacter))
                {
                    return "may only have letters, digits, '.', '_', '~', and '-' in its path segments.";
                }
            }
        }

        baseUrl = new Uri(parsed.GetLeftPart(UriPartial.Authority) + path);
        pathBase = path;
        return null;
    }

    private static bool IsPathCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '~' or '-';

    private static TimeZoneInfo? ReadTimeZone(IReadOnlyDictionary<string, string> variables, List<ConfigurationError> errors)
    {
        var value = Value(variables, TimeZone);
        var id = value ?? DefaultTimeZone;

        // POSIX allows a leading colon before a zone name (TZ=:Europe/Oslo).
        if (id.StartsWith(':'))
        {
            id = id[1..];
        }

        if (id.Length > 0 && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var timeZone))
        {
            return timeZone;
        }

        errors.Add(new ConfigurationError(
            TimeZone,
            $"must be a time zone ID this system knows, such as UTC or Europe/Oslo, but was '{Echo(value ?? id)}'."));
        return null;
    }

    private static N8TracksLogLevel ReadLogLevel(IReadOnlyDictionary<string, string> variables, List<ConfigurationError> errors)
    {
        var value = Value(variables, LogLevel);
        if (value is null)
        {
            return DefaultLogLevel;
        }

        if (LogLevels.TryGetValue(value, out var level))
        {
            return level;
        }

        errors.Add(new ConfigurationError(
            LogLevel,
            $"must be one of {string.Join(", ", Enum.GetNames<N8TracksLogLevel>())}, but was '{Echo(value)}'."));
        return DefaultLogLevel;
    }

    private static Domain.Suno.SunoAudioHosts? ReadSunoAudioHosts(IReadOnlyDictionary<string, string> variables, List<ConfigurationError> errors)
    {
        var value = Value(variables, SunoAudioHosts);
        if (value is null)
        {
            return Domain.Suno.SunoAudioHosts.Default;
        }

        var hosts = Domain.Suno.SunoAudioHosts.Parse(value, out var reason);
        if (hosts is null)
        {
            errors.Add(new ConfigurationError(SunoAudioHosts, reason!));
        }

        return hosts;
    }

    /// <summary>
    /// The data path, which must be a writable directory, and never the media folder
    /// <paramref name="mount"/> or inside it (#387, invariant 2): that is checked first, so the write
    /// probe never runs there.
    /// </summary>
    private static string ReadDataPath(
        IReadOnlyDictionary<string, string> variables,
        string workingDirectory,
        string mount,
        List<ConfigurationError> errors)
    {
        var path = ResolvePath(Value(variables, DataPath) ?? DefaultDataPath, workingDirectory);

        var reason = InsideTheMediaFolder(path, mount) ?? CheckWritableDirectory(path);
        if (reason is not null)
        {
            errors.Add(new ConfigurationError(DataPath, reason));
        }

        return path;
    }

    /// <summary>The backup path, which may be missing, but is never the media folder <paramref name="mount"/> or inside it (#387, invariant 2).</summary>
    private static string ReadBackupPath(
        IReadOnlyDictionary<string, string> variables,
        string workingDirectory,
        string mount,
        List<ConfigurationError> errors)
    {
        var path = ResolvePath(Value(variables, BackupPath) ?? DefaultBackupPath, workingDirectory);

        if (InsideTheMediaFolder(path, mount) is { } reason)
        {
            errors.Add(new ConfigurationError(BackupPath, reason));
        }

        return path;
    }

    /// <summary>
    /// Why <paramref name="path"/> may not be used, when it is the media folder <paramref name="mount"/>
    /// or inside it, by real path or (on Linux) by where it really is, so one folder mounted twice is
    /// seen; null otherwise.
    /// </summary>
    private static string? InsideTheMediaFolder(string path, string mount) =>
        MediaFolderOverlap.IsInside(path, mount)
            ? $"must not be the media folder or a folder inside it, but '{Echo(path)}' is inside the media folder '{Echo(mount)}' (perhaps the same host folder is mounted at both). n8Tracks writes there, and it never writes to your media. Use a folder outside the media folder."
            : null;

    private static string? CheckWritableDirectory(string path)
    {
        if (File.Exists(path))
        {
            return $"must be a directory, but '{Echo(path)}' is a file.";
        }

        if (!Directory.Exists(path))
        {
            return $"must be an existing directory, but '{Echo(path)}' does not exist.";
        }

        var probe = Path.Combine(path, WriteTestFileName);
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"must be writable, but a file could not be created in '{Echo(path)}'.";
        }
    }

    private static string ResolvePath(string path, string workingDirectory) => Path.GetFullPath(path, workingDirectory);

    private static string Echo(string value) =>
        value.Length <= MaxEchoedLength ? value : string.Concat(value.AsSpan(0, MaxEchoedLength), "…");
}
