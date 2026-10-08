using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using n8Tracks.Gateway.Configuration;
using n8Tracks.Gateway.Health;
using n8Tracks.Gateway.Logging;
using n8Tracks.ServiceDefaults;

/// <summary>
/// The MCP gateway's entry point and composition root. The gateway reaches n8Tracks only through the
/// typed <see cref="UpstreamHealthClient"/> whose base address is <c>N8TRACKS_API_URL</c>; it holds no
/// business logic and no catalog data, and it exposes no MCP endpoint yet.
/// </summary>
public sealed partial class Program
{
    /// <summary>The name the gateway's telemetry is reported under.</summary>
    internal const string ServiceName = "n8tracks-gateway";

    private Program()
    {
    }

    private static Task<int> Main(string[] args) =>
        RunAsync(args, ProcessEnvironment.Read(), CancellationToken.None);

    /// <summary>
    /// Builds and runs the gateway until shutdown or <paramref name="cancellationToken"/>. Returns the
    /// process exit code: 1 when a setting is invalid, the port cannot be bound, or startup fails
    /// unexpectedly, otherwise 0. With <c>--healthcheck</c> among <paramref name="args"/> it starts
    /// nothing and returns the result of asking the running gateway for its health instead (see
    /// <see cref="HealthCheckCommand"/>). That is the only argument with a meaning: every other one
    /// is ignored, and none reaches the host's configuration.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, EnvironmentSnapshot environment, CancellationToken cancellationToken)
    {
        try
        {
            if (HealthCheckCommand.IsRequested(args))
            {
                // Its own logger, flushed on disposal, so the one line is out before the process exits.
                using var factory = GatewayLogging.CreateStartupLoggerFactory();
                return await HealthCheckCommand
                    .RunAsync(environment, factory.CreateLogger("n8Tracks.Gateway.HealthCheck"), cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RunHostAsync(environment, cancellationToken).ConfigureAwait(false);
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

    private static async Task<int> RunHostAsync(EnvironmentSnapshot environment, CancellationToken cancellationToken)
    {
        // The host starts empty: it reads no command-line argument, no environment variable (with an
        // ASPNETCORE_ or DOTNET_ prefix or without one), and no appsettings file, so no framework
        // setting from any of them (urls, a Kestrel or Logging section, AllowedHosts, ...) can change
        // what the gateway does. Everything it needs is set here, from the environment snapshot.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,

            // Nothing is read from the content root; it is kept away from the working directory.
            ContentRootPath = AppContext.BaseDirectory,
        });

        // What the default builder would have added: the server, without the loader that reads
        // endpoints and limits from a "Kestrel" configuration section (N8TRACKS_GATEWAY_PORT is the
        // only listen source); routing; and a container that checks its registrations.
        builder.WebHost.UseKestrelCore();
        builder.Services.AddRouting();
        builder.Host.UseDefaultServiceProvider(static provider =>
        {
            provider.ValidateScopes = true;
            provider.ValidateOnBuild = true;
        });

        builder.Logging.AddGatewayLogging();

        // OpenTelemetry, only when the environment variable OTEL_EXPORTER_OTLP_ENDPOINT is set;
        // otherwise this registers nothing. After the logging setup, which clears the providers this
        // may add one to.
        builder.AddServiceDefaults(ServiceName, ProductVersion.Current, environment.Variables, exportLogsFromLoggingProviders: true);

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

            // An unwritable log folder leaves the gateway logging to standard output only (#234).
            if (app.Services.GetRequiredService<GatewayFileLoggerProvider>().Problem is { } logFilesProblem)
            {
                LogLogFilesOff(app.Logger, GatewayOptionsLoader.LogPath, logFilesProblem);
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

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Log files are off: {Variable} {Reason} The gateway logs to standard output only, and tries the folder again hourly.")]
    private static partial void LogLogFilesOff(ILogger logger, string variable, string reason);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "The n8Tracks gateway {Version} is listening on port {Port}.")]
    private static partial void LogListening(ILogger logger, int port, string version);
}
