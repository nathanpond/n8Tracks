using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Endpoints;
using n8Tracks.Application;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure;

/// <summary>The entry point and composition root.</summary>
public sealed class Program
{
    private Program()
    {
    }

    private static Task<int> Main(string[] args) =>
        RunAsync(args, ProcessEnvironment.Read(), Console.Out, CancellationToken.None);

    /// <summary>
    /// Builds and runs the app until shutdown or <paramref name="cancellationToken"/>. Returns the process
    /// exit code: 1 when the configuration is invalid or the port cannot be bound, otherwise 0.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args,
        EnvironmentSnapshot environment,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(args);

        // N8TRACKS_PORT is the only source of the listen address: drop anything that came from
        // ASPNETCORE_URLS, ASPNETCORE_HTTP_PORTS, ASPNETCORE_HTTPS_PORTS, --urls, or launch settings.
        builder.Configuration[WebHostDefaults.ServerUrlsKey] = string.Empty;
        builder.Configuration[WebHostDefaults.HttpPortsKey] = string.Empty;
        builder.Configuration[WebHostDefaults.HttpsPortsKey] = string.Empty;

        builder.Services.AddOpenApi();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure();
        builder.Services.AddEnvironmentConfiguration(environment);

        var app = builder.Build();
        await using (app.ConfigureAwait(false))
        {
            var startupLog = new StartupLog(output, TimeProvider.System);

            foreach (var name in EnvironmentOptionsLoader.FindUnknownVariables(app.Services.GetRequiredService<EnvironmentSnapshot>()))
            {
                const string reason = "is not a setting n8Tracks knows and is ignored.";
                startupLog.Warning($"Unknown setting: {name} {reason}", name, reason);
            }

            N8TracksOptions options;
            try
            {
                options = app.Services.GetRequiredService<N8TracksOptions>();
            }
            catch (ConfigurationValidationException exception)
            {
                foreach (var error in exception.Errors)
                {
                    startupLog.Error($"Invalid configuration: {error.Variable} {error.Reason}", error.Variable, error.Reason);
                }

                return 1;
            }

            app.UseConfiguredPathBase(options);

            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.MapHealth();

            if (ListensWithKestrel(app) && ListenPortProbe.TryBind(options.Port) is { } bindError)
            {
                ReportBindFailure(startupLog, ListenPortProbe.Describe(bindError, options.Port));
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
                ReportBindFailure(startupLog, ListenPortProbe.Describe(error, options.Port));
                return 1;
            }

            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);

            return 0;
        }
    }
    /// <summary>False under a test host, which swaps Kestrel for an in-memory server and binds no port.</summary>
    private static bool ListensWithKestrel(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().GetType().Assembly == typeof(KestrelServerOptions).Assembly;

    private static void ReportBindFailure(StartupLog startupLog, string reason) =>
        startupLog.Error($"Startup failed: {EnvironmentOptionsLoader.Port} {reason}", EnvironmentOptionsLoader.Port, reason);
}
