using n8Tracks.Api.Configuration;
using n8Tracks.Application.Configuration;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests;

public sealed class EnvironmentOptionsLoaderTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public void DefaultsApplyWhenNothingIsSet()
    {
        // Only the data path is given: the default /data does not exist on a development machine.
        var options = Load();

        Assert.Equal(8787, options.Port);
        Assert.Equal(new Uri("http://localhost:8787"), options.BaseUrl);
        Assert.Equal(string.Empty, options.PathBase);
        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("UTC").Id, options.TimeZone.Id);
        Assert.Equal(N8TracksLogLevel.Information, options.LogLevel);
        Assert.Equal(directory.Path, options.DataPath);
        Assert.Equal(Path.GetFullPath("/media"), options.MediaPath);
        Assert.Equal(Path.GetFullPath("/backup"), options.BackupPath);
        Assert.Equal(["d2lwuy8qc234o3.cloudfront.net"], options.SunoAudioHosts.Hosts);
        Assert.Same(SunoAudioHosts.Default, options.SunoAudioHosts);
    }

    [Fact]
    public void TheDataPathDefaultsToSlashData()
    {
        // No data path given, so the default is checked. It fails here only because /data is absent.
        var exception = Assert.Throws<ConfigurationValidationException>(
            () => EnvironmentOptionsLoader.Load(new EnvironmentSnapshot(new Dictionary<string, string>(), directory.Path)));

        if (!Directory.Exists("/data"))
        {
            var error = Assert.Single(exception.Errors);
            Assert.Equal("N8TRACKS_DATA_PATH", error.Variable);
            Assert.Contains($"'{Path.GetFullPath("/data")}'", error.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryVariableIsRead()
    {
        var options = Load(
            ("N8TRACKS_PORT", "9000"),
            ("N8TRACKS_BASE_URL", "https://nas.example/n8tracks"),
            ("TZ", "Europe/Oslo"),
            ("N8TRACKS_LOG_LEVEL", "Debug"),
            ("N8TRACKS_MEDIA_PATH", "/mnt/music"),
            ("N8TRACKS_BACKUP_PATH", "/mnt/backup"));

        Assert.Equal(9000, options.Port);
        Assert.Equal(new Uri("https://nas.example/n8tracks"), options.BaseUrl);
        Assert.Equal("/n8tracks", options.PathBase);
        Assert.Equal("Europe/Oslo", options.TimeZone.Id);
        Assert.Equal(N8TracksLogLevel.Debug, options.LogLevel);
        Assert.Equal(Path.GetFullPath("/mnt/music"), options.MediaPath);
        Assert.Equal(Path.GetFullPath("/mnt/backup"), options.BackupPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void AnEmptyOrWhitespaceVariableIsTreatedAsUnset(string value)
    {
        var options = Load(
            ("N8TRACKS_PORT", value),
            ("N8TRACKS_BASE_URL", value),
            ("TZ", value),
            ("N8TRACKS_LOG_LEVEL", value),
            ("N8TRACKS_MEDIA_PATH", value),
            ("N8TRACKS_BACKUP_PATH", value),
            ("N8TRACKS_SUNO_AUDIO_HOSTS", value));

        Assert.Equal(8787, options.Port);
        Assert.Equal(new Uri("http://localhost:8787"), options.BaseUrl);
        Assert.Equal("UTC", options.TimeZone.Id);
        Assert.Equal(N8TracksLogLevel.Information, options.LogLevel);
        Assert.Equal(Path.GetFullPath("/media"), options.MediaPath);
        Assert.Equal(Path.GetFullPath("/backup"), options.BackupPath);
        Assert.Same(SunoAudioHosts.Default, options.SunoAudioHosts);
    }

    [Fact]
    public void AnEmptyDataPathFallsBackToTheDefault()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(
            () => EnvironmentOptionsLoader.Load(Snapshot(("N8TRACKS_DATA_PATH", " "))));

        if (!Directory.Exists("/data"))
        {
            Assert.Contains($"'{Path.GetFullPath("/data")}'", Assert.Single(exception.Errors).Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDefaultBaseUrlUsesTheConfiguredPort()
    {
        var options = Load(("N8TRACKS_PORT", "9123"));

        Assert.Equal(new Uri("http://localhost:9123"), options.BaseUrl);
        Assert.Equal(string.Empty, options.PathBase);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData(" 8080 ", 8080)]
    public void AValidPortIsAccepted(string value, int expected)
    {
        Assert.Equal(expected, Load(("N8TRACKS_PORT", value)).Port);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+8787")]
    [InlineData("08787")]
    [InlineData("8787.0")]
    [InlineData("8 787")]
    [InlineData("8,787")]
    [InlineData("99999999999999999999")]
    public void AnInvalidPortIsRefusedAndEchoed(string value)
    {
        var error = SingleError(("N8TRACKS_PORT", value));

        Assert.Equal("N8TRACKS_PORT", error.Variable);
        Assert.Contains("1 to 65535", error.Reason, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", error.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://nas.example", "https://nas.example/", "")]
    [InlineData("https://nas.example/", "https://nas.example/", "")]
    [InlineData("http://nas.example:8080", "http://nas.example:8080/", "")]
    [InlineData("https://nas.example/n8tracks", "https://nas.example/n8tracks", "/n8tracks")]
    [InlineData("https://nas.example/n8tracks/", "https://nas.example/n8tracks", "/n8tracks")]
    [InlineData("HTTPS://NAS.example/N8Tracks", "https://nas.example/N8Tracks", "/N8Tracks")]
    [InlineData("https://nas.example/apps/n8-tracks_1.0~x/", "https://nas.example/apps/n8-tracks_1.0~x", "/apps/n8-tracks_1.0~x")]
    [InlineData("  https://nas.example/n8tracks  ", "https://nas.example/n8tracks", "/n8tracks")]
    [InlineData("http://192.168.1.10:8787/music", "http://192.168.1.10:8787/music", "/music")]
    [InlineData("http://[::1]:8787/music", "http://[::1]:8787/music", "/music")]
    public void AValidBaseUrlIsNormalised(string value, string expectedUrl, string expectedPathBase)
    {
        var options = Load(("N8TRACKS_BASE_URL", value));

        Assert.Equal(expectedUrl, options.BaseUrl.AbsoluteUri);
        Assert.Equal(expectedPathBase, options.PathBase);
    }

    [Theory]
    [InlineData("media.internal/music", "absolute http or https URL")]
    [InlineData("/music", "absolute http or https URL")]
    [InlineData("ftp://nas.example/n8tracks", "absolute http or https URL")]
    [InlineData("http:/nas.example", "absolute http or https URL")]
    [InlineData("http://", "absolute http or https URL")]
    [InlineData("http:///n8tracks", "absolute http or https URL")]
    [InlineData("https://nas.example:99999/n8tracks", "absolute http or https URL")]
    [InlineData("https://nas.example\\n8tracks", "absolute http or https URL")]
    [InlineData("https://nas.example/n8 tracks", "absolute http or https URL")]
    [InlineData("https://nas.example/n8tracks?x=1", "query string")]
    [InlineData("https://nas.example/n8tracks?", "query string")]
    [InlineData("https://nas.example/n8tracks#top", "fragment")]
    [InlineData("https://operator:hunter2@nas.example/n8tracks", "user info")]
    [InlineData("https://operator@nas.example", "user info")]
    [InlineData("https://nas.example//n8tracks", "repeated slashes")]
    [InlineData("https://nas.example/apps//n8tracks", "repeated slashes")]
    [InlineData("https://nas.example/n8tracks//", "repeated slashes")]
    [InlineData("https://nas.example//", "repeated slashes")]
    [InlineData("https://nas.example/./n8tracks", "'.' or '..'")]
    [InlineData("https://nas.example/apps/../n8tracks", "'.' or '..'")]
    [InlineData("https://nas.example/n8%20tracks", "letters, digits")]
    [InlineData("https://nas.example/n8tracks;v=1", "letters, digits")]
    [InlineData("https://nas.example/müsik", "letters, digits")]
    public void AnInvalidBaseUrlIsRefusedWithoutEchoingIt(string value, string expectedReason)
    {
        var error = SingleError(("N8TRACKS_BASE_URL", value));

        Assert.Equal("N8TRACKS_BASE_URL", error.Variable);
        Assert.Contains(expectedReason, error.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(value.Trim(), error.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", error.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Trace", N8TracksLogLevel.Trace)]
    [InlineData("Debug", N8TracksLogLevel.Debug)]
    [InlineData("Information", N8TracksLogLevel.Information)]
    [InlineData("Warning", N8TracksLogLevel.Warning)]
    [InlineData("Error", N8TracksLogLevel.Error)]
    [InlineData("Critical", N8TracksLogLevel.Critical)]
    [InlineData("warning", N8TracksLogLevel.Warning)]
    [InlineData(" DEBUG ", N8TracksLogLevel.Debug)]
    public void AKnownLogLevelIsAcceptedInAnyCase(string value, N8TracksLogLevel expected)
    {
        Assert.Equal(expected, Load(("N8TRACKS_LOG_LEVEL", value)).LogLevel);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Verbose")]
    [InlineData("Info")]
    [InlineData("2")]
    [InlineData("Debug,Error")]
    public void AnUnknownLogLevelIsRefusedAndEchoed(string value)
    {
        var error = SingleError(("N8TRACKS_LOG_LEVEL", value));

        Assert.Equal("N8TRACKS_LOG_LEVEL", error.Variable);
        Assert.Contains("Trace, Debug, Information, Warning, Error, Critical", error.Reason, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", error.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UTC", "UTC")]
    [InlineData("Europe/Oslo", "Europe/Oslo")]
    [InlineData(":Europe/Oslo", "Europe/Oslo")]
    [InlineData(" America/New_York ", "America/New_York")]
    public void AKnownTimeZoneIsResolved(string value, string expectedId)
    {
        Assert.Equal(expectedId, Load(("TZ", value)).TimeZone.Id);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Europe/Olso")]
    [InlineData(":")]
    public void AnUnknownTimeZoneIsRefusedAndEchoed(string value)
    {
        var error = SingleError(("TZ", value));

        Assert.Equal("TZ", error.Variable);
        Assert.Contains("time zone ID", error.Reason, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDataPathIsRefused()
    {
        var missing = Path.Combine(directory.Path, "missing");

        var error = SingleError(("N8TRACKS_DATA_PATH", missing));

        Assert.Equal("N8TRACKS_DATA_PATH", error.Variable);
        Assert.Contains("does not exist", error.Reason, StringComparison.Ordinal);
        Assert.Contains($"'{missing}'", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADataPathThatIsAFileIsRefused()
    {
        var file = Path.Combine(directory.Path, "a-file");
        File.WriteAllText(file, string.Empty);

        var error = SingleError(("N8TRACKS_DATA_PATH", file));

        Assert.Equal("N8TRACKS_DATA_PATH", error.Variable);
        Assert.Contains("is a file", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADataPathThatCannotBeWrittenIsRefused()
    {
        // Permission bits do not stop a privileged user, and Windows has none: nothing to observe there.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        var readOnly = Directory.CreateDirectory(Path.Combine(directory.Path, "read-only")).FullName;
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var error = SingleError(("N8TRACKS_DATA_PATH", readOnly));

            Assert.Equal("N8TRACKS_DATA_PATH", error.Variable);
            Assert.Contains("must be writable", error.Reason, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TheWritabilityCheckLeavesNoFileBehind()
    {
        Load();

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void RelativePathsResolveAgainstTheWorkingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(directory.Path, "state"));

        var options = EnvironmentOptionsLoader.Load(new EnvironmentSnapshot(
            new Dictionary<string, string>
            {
                ["N8TRACKS_DATA_PATH"] = "./state",
                ["N8TRACKS_MEDIA_PATH"] = "music",
                ["N8TRACKS_BACKUP_PATH"] = "../backup",
            },
            directory.Path));

        Assert.Equal(Path.Combine(directory.Path, "state"), options.DataPath);
        Assert.Equal(Path.Combine(directory.Path, "music"), options.MediaPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory.Path, "..", "backup")), options.BackupPath);
    }

    [Fact]
    public void MissingMediaAndBackupPathsDoNotFail()
    {
        var media = Path.Combine(directory.Path, "no-media");
        var backup = Path.Combine(directory.Path, "no-backup");

        var options = Load(("N8TRACKS_MEDIA_PATH", media), ("N8TRACKS_BACKUP_PATH", backup));

        Assert.Equal(media, options.MediaPath);
        Assert.Equal(backup, options.BackupPath);
        Assert.False(Directory.Exists(media));
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void MediaAndBackupPathsThatAreFilesDoNotFail()
    {
        var file = Path.Combine(directory.Path, "a-file");
        File.WriteAllText(file, string.Empty);

        var options = Load(("N8TRACKS_MEDIA_PATH", file), ("N8TRACKS_BACKUP_PATH", file));

        Assert.Equal(file, options.MediaPath);
        Assert.Equal(file, options.BackupPath);
    }

    [Fact]
    public void EveryInvalidValueIsReported()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() => Load(
            ("N8TRACKS_PORT", "eighty"),
            ("N8TRACKS_BASE_URL", "nas.example"),
            ("TZ", "Nowhere/Land"),
            ("N8TRACKS_LOG_LEVEL", "Loud"),
            ("N8TRACKS_DATA_PATH", Path.Combine(directory.Path, "missing"))));

        Assert.Equal(
            ["N8TRACKS_PORT", "N8TRACKS_BASE_URL", "TZ", "N8TRACKS_LOG_LEVEL", "N8TRACKS_DATA_PATH"],
            exception.Errors.Select(error => error.Variable));
    }

    [Theory]
    [InlineData("audio.example.net", new[] { "audio.example.net" })]
    [InlineData(" Audio.Example.NET , cdn-2.example.net ", new[] { "audio.example.net", "cdn-2.example.net" })]
    [InlineData("a.example.net,A.EXAMPLE.NET,,b.example.net", new[] { "a.example.net", "b.example.net" })]
    [InlineData("localhost", new[] { "localhost" })]
    public void AListOfSunoAudioHostsReplacesTheDefault(string value, string[] expected)
    {
        Assert.Equal(expected, Load(("N8TRACKS_SUNO_AUDIO_HOSTS", value)).SunoAudioHosts.Hosts);
    }

    [Theory]
    [InlineData(",", "must name at least one host")]
    [InlineData(" , ,", "must name at least one host")]
    [InlineData("https://audio.example.net", "with no scheme, but 'https://audio.example.net' has one.")]
    [InlineData("a.example.net,//audio.example.net", "with no path, but '//audio.example.net' has one.")]
    [InlineData("audio.example.net:443", "with no port, but 'audio.example.net:443' has one.")]
    [InlineData("audio.example.net/clips", "with no path, but 'audio.example.net/clips' has one.")]
    [InlineData("audio.example.net?x=1", "with no path, but 'audio.example.net?x=1' has one.")]
    [InlineData("*.cloudfront.net", "with no wildcard, but '*.cloudfront.net' has one.")]
    [InlineData("*", "with no wildcard, but '*' has one.")]
    [InlineData("audio_files.example.net", "'audio_files.example.net' is not a DNS host name.")]
    [InlineData("audio..example.net", "'audio..example.net' is not a DNS host name.")]
    [InlineData("audio.example.net.", "'audio.example.net.' is not a DNS host name.")]
    [InlineData("-audio.example.net", "'-audio.example.net' is not a DNS host name.")]
    [InlineData("audio example.net", "'audio example.net' is not a DNS host name.")]
    [InlineData("192.168.1.10", "'192.168.1.10' is not a DNS host name.")]
    [InlineData("[::1]", "with no port, but '[::1]' has one.")]
    public void AnInvalidSunoAudioHostListIsRefusedAndSaysWhy(string value, string reason)
    {
        var error = SingleError(("N8TRACKS_SUNO_AUDIO_HOSTS", value));

        Assert.Equal("N8TRACKS_SUNO_AUDIO_HOSTS", error.Variable);
        Assert.Contains(reason, error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASunoAudioHostWithAPasswordIsRefusedWithoutEchoingIt()
    {
        var error = SingleError(("N8TRACKS_SUNO_AUDIO_HOSTS", "user:hunter2@audio.example.net"));

        Assert.Equal("N8TRACKS_SUNO_AUDIO_HOSTS", error.Variable);
        Assert.Contains("no user name or password", error.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongestValidHostNameIsAcceptedAndOneCharacterMoreIsRefused()
    {
        var label = new string('a', 63);
        var longest = $"{label}.{label}.{label}.{new string('b', 61)}";
        Assert.Equal(253, longest.Length);
        Assert.Equal([longest], Load(("N8TRACKS_SUNO_AUDIO_HOSTS", longest)).SunoAudioHosts.Hosts);

        Assert.Contains("is not a DNS host name", SingleError(("N8TRACKS_SUNO_AUDIO_HOSTS", longest + "b")).Reason, StringComparison.Ordinal);
        Assert.Contains("is not a DNS host name", SingleError(("N8TRACKS_SUNO_AUDIO_HOSTS", new string('a', 64) + ".example.net")).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownProductVariablesAreFound()
    {
        var unknown = EnvironmentOptionsLoader.FindUnknownVariables(Snapshot(
            ("N8TRACKS_PROT", "8787"),
            ("N8TRACKS_PORT", "8787"),
            ("N8TRACKS_BASEURL", "http://x"),
            ("N8TRACKS_EMPTY", " "),
            ("TZ", "UTC"),
            ("PATH", "/usr/bin"),
            ("ASPNETCORE_URLS", "http://+:80")));

        Assert.Equal(["N8TRACKS_BASEURL", "N8TRACKS_PROT"], unknown);
    }

    [Fact]
    public void KnownVariablesAreNotReportedAsUnknown()
    {
        var unknown = EnvironmentOptionsLoader.FindUnknownVariables(Snapshot(
            ("N8TRACKS_PORT", "1"),
            ("N8TRACKS_BASE_URL", "1"),
            ("N8TRACKS_LOG_LEVEL", "1"),
            ("N8TRACKS_DATA_PATH", "1"),
            ("N8TRACKS_MEDIA_PATH", "1"),
            ("N8TRACKS_BACKUP_PATH", "1"),
            ("N8TRACKS_SUNO_AUDIO_HOSTS", "1")));

        Assert.Empty(unknown);
    }

    private N8TracksOptions Load(params (string Name, string Value)[] variables) =>
        EnvironmentOptionsLoader.Load(Snapshot(variables));

    private ConfigurationError SingleError(params (string Name, string Value)[] variables) =>
        Assert.Single(Assert.Throws<ConfigurationValidationException>(() => Load(variables)).Errors);

    /// <summary>The given variables, plus a data path that exists unless one is given.</summary>
    private EnvironmentSnapshot Snapshot(params (string Name, string Value)[] variables)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["N8TRACKS_DATA_PATH"] = directory.Path,
        };

        foreach (var (name, value) in variables)
        {
            all[name] = value;
        }

        return new EnvironmentSnapshot(all, directory.Path);
    }
}
