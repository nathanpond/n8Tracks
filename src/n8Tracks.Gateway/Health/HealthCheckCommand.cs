using System.Net;
using n8Tracks.Gateway.Configuration;

namespace n8Tracks.Gateway.Health;

/// <summary>
/// The <c>--healthcheck</c> mode of the gateway binary, used by the image's <c>HEALTHCHECK</c> so the
/// image needs no HTTP client of its own. It asks the running gateway for its health on the loopback
/// interface, at the port the gateway itself was configured with, and turns the answer into an exit
/// code: 0 for 200 (healthy or degraded), 1 for anything else, no answer, or an invalid port. It reads
/// no other setting and never contacts n8Tracks itself.
/// </summary>
internal static partial class HealthCheckCommand
{
    public const string Argument = "--healthcheck";

    /// <summary>
    /// Shorter than the image's 5-second health check timeout, so a hang is reported, not killed, and
    /// longer than <see cref="UpstreamHealthClient.Budget"/>, so a gateway waiting for an upstream that
    /// does not answer still passes as degraded.
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static bool IsRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Contains(Argument, StringComparer.Ordinal);
    }

    public static async Task<int> RunAsync(EnvironmentSnapshot environment, ILogger log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(log);

        Uri target;
        try
        {
            target = Target(environment);
        }
        catch (ConfigurationValidationException exception)
        {
            foreach (var error in exception.Errors)
            {
                LogInvalidConfiguration(log, error.Variable, error.Reason);
            }

            return 1;
        }

        // Loopback only: a proxy from the environment must never sit between the check and the gateway.
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout };

        try
        {
            using var response = await client
                .GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                LogPassed(log, (int)response.StatusCode);
                return 0;
            }

            LogWrongStatus(log, (int)response.StatusCode);
            return 1;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogNoAnswer(log);
            return 1;
        }
    }

    /// <summary>The health endpoint of the gateway running in this environment: loopback, its port.</summary>
    /// <exception cref="ConfigurationValidationException">The port is invalid.</exception>
    internal static Uri Target(EnvironmentSnapshot environment) =>
        new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", GatewayOptionsLoader.LoadPort(environment), HealthEndpoint.Path).Uri;

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Invalid configuration: {Variable} {Reason}")]
    private static partial void LogInvalidConfiguration(ILogger logger, string variable, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Health check passed: the gateway answered {StatusCode}")]
    private static partial void LogPassed(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Health check failed: the gateway answered {StatusCode}")]
    private static partial void LogWrongStatus(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Health check failed: the gateway did not answer.")]
    private static partial void LogNoAnswer(ILogger logger);
}
