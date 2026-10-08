using Microsoft.Extensions.Logging;
using n8Tracks.Gateway.Configuration;

namespace n8Tracks.Gateway.Tests;

public class GatewayOptionsLoaderTests
{
    [Fact]
    public void OnlyTheApiUrlIsRequired()
    {
        var options = Load((GatewayOptionsLoader.ApiUrl, "http://n8tracks:8787"));

        Assert.Equal(8788, options.Port);
        Assert.Equal(new Uri("http://n8tracks:8787/"), options.ApiUrl);
        Assert.Equal(LogLevel.Information, options.LogLevel);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("9000", 9000)]
    [InlineData(" 65535 ", 65535)]
    public void AValidPortIsUsed(string value, int expected)
    {
        var options = Load((GatewayOptionsLoader.ApiUrl, "http://n8tracks"), (GatewayOptionsLoader.Port, value));

        Assert.Equal(expected, options.Port);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+80")]
    [InlineData("080")]
    [InlineData("8788.0")]
    [InlineData("http")]
    [InlineData("1e3")]
    public void AnInvalidPortIsRefusedNamingTheVariable(string value)
    {
        var error = Assert.Single(Errors((GatewayOptionsLoader.ApiUrl, "http://n8tracks"), (GatewayOptionsLoader.Port, value)));

        Assert.Equal("N8TRACKS_GATEWAY_PORT", error.Variable);
        Assert.Contains("1 to 65535", error.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingOrEmptyApiUrlIsRefusedAsRequired(string? value)
    {
        var variables = value is null ? [] : new[] { (GatewayOptionsLoader.ApiUrl, value) };

        var error = Assert.Single(Errors(variables));

        Assert.Equal("N8TRACKS_API_URL", error.Variable);
        Assert.Contains("required", error.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("n8tracks:8787", "absolute http or https URL")]
    [InlineData("/n8tracks", "absolute http or https URL")]
    [InlineData("ftp://n8tracks", "absolute http or https URL")]
    [InlineData("file:///etc/passwd", "absolute http or https URL")]
    [InlineData("http://", "absolute http or https URL")]
    [InlineData("http:///path", "absolute http or https URL")]
    [InlineData("http://n8 tracks", "absolute http or https URL")]
    [InlineData("http://n8tracks\\path", "absolute http or https URL")]
    [InlineData("http://n8tracks:8787?token=abc", "query string")]
    [InlineData("http://n8tracks:8787/sub?", "query string")]
    [InlineData("http://n8tracks:8787/#top", "fragment")]
    [InlineData("http://operator:hunter2@n8tracks:8787", "user info")]
    [InlineData("http://operator@n8tracks:8787", "user info")]
    public void AnInvalidApiUrlIsRefusedWithoutEchoingIt(string value, string expectedReason)
    {
        var error = Assert.Single(Errors((GatewayOptionsLoader.ApiUrl, value)));

        Assert.Equal("N8TRACKS_API_URL", error.Variable);
        Assert.Contains(expectedReason, error.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", error.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", error.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://n8tracks:8787", "http://n8tracks:8787/health")]
    [InlineData("http://n8tracks:8787/", "http://n8tracks:8787/health")]
    [InlineData("HTTPS://NAS.example/n8tracks", "https://nas.example/n8tracks/health")]
    [InlineData("https://nas.example/n8tracks/", "https://nas.example/n8tracks/health")]
    [InlineData("https://nas.example/apps/n8tracks", "https://nas.example/apps/n8tracks/health")]
    [InlineData("http://[::1]:8787", "http://[::1]:8787/health")]
    public void TheHealthUrlKeepsTheSubPathOfTheApiUrl(string value, string expectedHealthUrl)
    {
        var options = Load((GatewayOptionsLoader.ApiUrl, value));

        Assert.Equal(new Uri(expectedHealthUrl), new Uri(options.ApiUrl, new Uri("health", UriKind.Relative)));
    }

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("INFORMATION", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("Critical", LogLevel.Critical)]
    public void TheLogLevelUsesMicrosoftsNamesInAnyCase(string value, LogLevel expected)
    {
        var environment = Snapshot((GatewayOptionsLoader.ApiUrl, "http://n8tracks"), (GatewayOptionsLoader.LogLevelVariable, value));

        Assert.Equal(expected, GatewayOptionsLoader.Load(environment).LogLevel);
        Assert.Equal(expected, GatewayOptionsLoader.LogLevelOrDefault(environment));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Chatty")]
    [InlineData("Verbose")]
    [InlineData("2")]
    public void AnInvalidLogLevelIsRefusedNamingTheVariable(string value)
    {
        var environment = Snapshot((GatewayOptionsLoader.ApiUrl, "http://n8tracks"), (GatewayOptionsLoader.LogLevelVariable, value));

        var error = Assert.Single(Assert.Throws<ConfigurationValidationException>(() => GatewayOptionsLoader.Load(environment)).Errors);

        Assert.Equal("N8TRACKS_LOG_LEVEL", error.Variable);
        Assert.Equal(LogLevel.Information, GatewayOptionsLoader.LogLevelOrDefault(environment));
    }

    [Fact]
    public void EveryInvalidValueIsListed()
    {
        var errors = Errors(
            (GatewayOptionsLoader.Port, "0"),
            (GatewayOptionsLoader.ApiUrl, "ftp://n8tracks"),
            (GatewayOptionsLoader.LogLevelVariable, "None"));

        Assert.Equal(
            ["N8TRACKS_GATEWAY_PORT", "N8TRACKS_API_URL", "N8TRACKS_LOG_LEVEL"],
            errors.Select(error => error.Variable));
    }

    [Fact]
    public void TheAppsOwnPortVariableIsNotTheGatewaysPort()
    {
        var options = Load((GatewayOptionsLoader.ApiUrl, "http://n8tracks"), ("N8TRACKS_PORT", "9000"));

        Assert.Equal(8788, options.Port);
    }

    [Fact]
    public void WithoutALogPathThereAreNoLogFilesAndTheOtherTwoVariablesStillCount()
    {
        Assert.Null(Load((GatewayOptionsLoader.ApiUrl, "http://n8tracks")).LogFiles);

        var error = Assert.Single(Errors((GatewayOptionsLoader.ApiUrl, "http://n8tracks"), (GatewayOptionsLoader.LogRetentionDays, "0")));
        Assert.Equal("N8TRACKS_GATEWAY_LOG_RETENTION_DAYS", error.Variable);
    }

    [Fact]
    public void ALogPathTakesTheApplicationsDefaults()
    {
        var files = Load((GatewayOptionsLoader.ApiUrl, "http://n8tracks"), (GatewayOptionsLoader.LogPath, "/data/logs")).LogFiles;

        Assert.Equal(new GatewayLogFiles("/data/logs", 14, 200), files);
    }

    [Theory]
    [InlineData("N8TRACKS_GATEWAY_LOG_RETENTION_DAYS", "1", true)]
    [InlineData("N8TRACKS_GATEWAY_LOG_RETENTION_DAYS", "90", true)]
    [InlineData("N8TRACKS_GATEWAY_LOG_RETENTION_DAYS", "0", false)]
    [InlineData("N8TRACKS_GATEWAY_LOG_RETENTION_DAYS", "91", false)]
    [InlineData("N8TRACKS_GATEWAY_LOG_RETENTION_DAYS", "07", false)]
    [InlineData("N8TRACKS_GATEWAY_LOG_RETENTION_DAYS", "seven", false)]
    [InlineData("N8TRACKS_GATEWAY_LOG_MAX_MB", "10", true)]
    [InlineData("N8TRACKS_GATEWAY_LOG_MAX_MB", "5120", true)]
    [InlineData("N8TRACKS_GATEWAY_LOG_MAX_MB", "9", false)]
    [InlineData("N8TRACKS_GATEWAY_LOG_MAX_MB", "5121", false)]
    [InlineData("N8TRACKS_GATEWAY_LOG_MAX_MB", "1.5", false)]
    [InlineData("N8TRACKS_GATEWAY_LOG_MAX_MB", "-10", false)]
    public void EachLogFileBoundIsAcceptedAndOneBeyondItRefusesToStart(string variable, string value, bool accepted)
    {
        (string, string)[] variables = [(GatewayOptionsLoader.ApiUrl, "http://n8tracks"), (GatewayOptionsLoader.LogPath, "/data/logs"), (variable, value)];

        if (accepted)
        {
            var files = Load(variables).LogFiles!;
            Assert.Equal(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture), variable == GatewayOptionsLoader.LogMaxMegabytes ? files.MaxMegabytes : files.RetentionDays);
            Assert.NotNull(GatewayOptionsLoader.LogFilesOrDefault(Snapshot(variables)));
        }
        else
        {
            var error = Assert.Single(Errors(variables));
            Assert.Equal(variable, error.Variable);
            Assert.Contains("whole number", error.Reason, StringComparison.Ordinal);

            // Logging is set up before the settings are checked: it writes no files, and does not throw.
            Assert.Null(GatewayOptionsLoader.LogFilesOrDefault(Snapshot(variables)));
        }
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("./logs")]
    public void ARelativeLogPathIsRefused(string value)
    {
        var error = Assert.Single(Errors((GatewayOptionsLoader.ApiUrl, "http://n8tracks"), (GatewayOptionsLoader.LogPath, value)));

        Assert.Equal("N8TRACKS_GATEWAY_LOG_PATH", error.Variable);
        Assert.Contains("absolute", error.Reason, StringComparison.Ordinal);
    }

    private static GatewayOptions Load(params (string Name, string Value)[] variables) =>
        GatewayOptionsLoader.Load(Snapshot(variables));

    private static IReadOnlyList<ConfigurationError> Errors(params (string Name, string Value)[] variables) =>
        Assert.Throws<ConfigurationValidationException>(() => Load(variables)).Errors;

    private static EnvironmentSnapshot Snapshot(params (string Name, string Value)[] variables) =>
        new(variables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.Ordinal));
}
