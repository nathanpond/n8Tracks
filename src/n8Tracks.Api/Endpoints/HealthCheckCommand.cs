using System.Net;
using n8Tracks.Api.Configuration;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The <c>--healthcheck</c> mode of the app binary, used by the image's <c>HEALTHCHECK</c> so the
/// image needs no HTTP client of its own. It asks the running app for its health on the loopback
/// interface, at the port and base path the app itself was configured with, and turns the answer into
/// an exit code: 0 for 200 (healthy or degraded), 1 for anything else, no answer, or invalid settings.
/// It opens no database and writes no file.
/// </summary>
internal static class HealthCheckCommand
{
    public const string Argument = "--healthcheck";

    /// <summary>Shorter than the image's 5-second health check timeout, so a hang is reported, not killed.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static bool IsRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Contains(Argument, StringComparer.Ordinal);
    }

    public static async Task<int> RunAsync(EnvironmentSnapshot environment, Serilog.ILogger log, CancellationToken cancellationToken)
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
                log.Error("Invalid configuration: {Variable} {Reason}", error.Variable, error.Reason);
            }

            return 1;
        }

        // Loopback only: a proxy from the environment must never sit between the check and the app.
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout };

        try
        {
            using var response = await client
                .GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                log.Information("Health check passed: the app answered {StatusCode}", (int)response.StatusCode);
                return 0;
            }

            log.Error("Health check failed: the app answered {StatusCode}", (int)response.StatusCode);
            return 1;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            log.Error("Health check failed: {Reason}", "the app did not answer.");
            return 1;
        }
    }

    /// <summary>The health endpoint of the app running in this environment: loopback, its port, its base path.</summary>
    /// <exception cref="ConfigurationValidationException">The port or the base URL is invalid.</exception>
    internal static Uri Target(EnvironmentSnapshot environment)
    {
        var (port, pathBase) = EnvironmentOptionsLoader.LoadListenAddress(environment);

        return new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port, pathBase + HealthEndpoint.Path).Uri;
    }
}
