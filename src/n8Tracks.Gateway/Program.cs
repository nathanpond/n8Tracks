using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using n8Tracks.Gateway.Configuration;
using n8Tracks.Gateway.Health;
using n8Tracks.Gateway.Logging;

/// <summary>
/// The MCP gateway's entry point and composition root. The gateway reaches n8Tracks only through the
/// typed <see cref="UpstreamHealthClient"/> whose base address is <c>N8TRACKS_API_URL</c>; it holds no
/// business logic and no catalog data, and it exposes no MCP endpoint yet.
/// </summary>
public sealed partial class Program
{
    private Program()
    {
    }

    private static Task<int> Main(string[] args) =>
        RunAsync(args, ProcessEnvironment.Read(), CancellationToken.None);

    /// <summary>
    /// Builds and runs the gateway until shutdown or <paramref name="cancellationToken"/>. Returns the
    /// process exit code: 1 when a setting is invalid, the port cannot be bound, or startup fails
    /// unexpectedly, otherwise 0.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, EnvironmentSnapshot environment, CancellationToken cancellationToken)
    {
        try
        {
            return await RunHostAsync(args, environment, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is not HostAbortedException)
        {
            WriteStartupLines(log => LogUnexpectedError(log, exception));
            return 1;
        }
    }

    private static async Task<int> RunHostAsync(string[] args, EnvironmentSnapshot environment, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(args);

        // N8TRACKS_GATEWAY_PORT is the only source of the listen address: drop anything that came from
        // ASPNETCORE_URLS, ASPNETCORE_HTTP_PORTS, ASPNETCORE_HTTPS_PORTS, --urls, or launch settings.
        builder.Configuration[WebHostDefaults.ServerUrlsKey] = string.Empty;
        builder.Configuration[WebHostDefaults.HttpPortsKey] = string.Empty;
        builder.Configuration[WebHostDefaults.HttpsPortsKey] = string.Empty;

        builder.Logging.AddGatewayLogging();

        builder.Services.AddSingleton(environment);
        builder.Services.AddSingleton(static provider => GatewayOptionsLoader.Load(provider.GetRequiredService<EnvironmentSnapshot>()));

        // Plain HTTP on all interfaces.
        builder.Services.AddOptions<KestrelServerOptions>()
            .Configure<IServiceProvider>(static (kestrel, provider) =>
            {
                try
                {
                    kestrel.ListenAnyIP(provider.GetRequiredService<GatewayOptions>().Port);
                }
                catch (ConfigurationValidationException)
                {
                    // Reported below; the gateway exits before the server starts.
                }
            });

        // The one way to n8Tracks. The request's own time budget replaces the client timeout, redirects
        // are not followed, and the client's request logging (which would write the URL) is off.
        builder.Services
            .AddHttpClient<UpstreamHealthClient>(static (provider, http) =>
            {
                http.BaseAddress = provider.GetRequiredService<GatewayOptions>().ApiUrl;
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            })
            .RemoveAllLoggers();

        builder.Services.AddSingleton<UpstreamStateLog>();

        var app = builder.Build();
        await using (app.ConfigureAwait(false))
        {
            GatewayOptions options;
            try
            {
                options = app.Services.GetRequiredService<GatewayOptions>();
            }
            catch (ConfigurationValidationException exception)
            {
                WriteStartupLines(log =>
                {
                    foreach (var error in exception.Errors)
                    {
                        LogInvalidConfiguration(log, error.Variable, error.Reason);
                    }
                });

                return 1;
            }

            app.MapGatewayHealth();

            // A test host swaps Kestrel for an in-memory server that binds no port.
            var listens = app.Services.GetRequiredService<IServer>().GetType().Assembly == typeof(KestrelServerOptions).Assembly;

            if (listens && ListenPortProbe.TryBind(options.Port) is { } bindError)
            {
                WriteStartupLines(log => LogBindFailure(log, GatewayOptionsLoader.Port, ListenPortProbe.Describe(bindError, options.Port)));
                return 1;
            }

            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                // The port was taken between the probe and the bind.
                var error = exception.InnerException is AddressInUseException ? SocketError.AddressAlreadyInUse : SocketError.SocketError;
                WriteStartupLines(log => LogBindFailure(log, GatewayOptionsLoader.Port, ListenPortProbe.Describe(error, options.Port)));
                return 1;
            }

            if (listens)
            {
                LogListening(app.Logger, options.Port, ProductVersion.Current);
            }

            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);

            return 0;
        }
    }

    /// <summary>Writes through a logger of its own, flushed before returning, so the lines are out before the process exits.</summary>
    private static void WriteStartupLines(Action<ILogger> write)
    {
        using var factory = GatewayLogging.CreateStartupLoggerFactory();
        write(factory.CreateLogger("n8Tracks.Gateway.Startup"));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Invalid configuration: {Variable} {Reason}")]
    private static partial void LogInvalidConfiguration(ILogger logger, string variable, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Startup failed: {Variable} {Reason}")]
    private static partial void LogBindFailure(ILogger logger, string variable, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Critical, Message = "The n8Tracks gateway stopped because of an unexpected error.")]
    private static partial void LogUnexpectedError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "The n8Tracks gateway {Version} is listening on port {Port}.")]
    private static partial void LogListening(ILogger logger, int port, string version);
}
