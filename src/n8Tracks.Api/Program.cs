using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.DependencyInjection;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Logging;
using n8Tracks.Application;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure;
using n8Tracks.Infrastructure.Logging;
using Serilog;

/// <summary>The entry point and composition root.</summary>
public sealed class Program
{
    private Program()
    {
    }

    private static Task<int> Main(string[] args) =>
        RunAsync(args, ProcessEnvironment.Read(), Console.Out, CancellationToken.None);

    /// <summary>
    /// Builds and runs the app until shutdown or <paramref name="cancellationToken"/>. Every log line
    /// goes to <paramref name="output"/> as JSON. Returns the process exit code: 1 when the
    /// configuration is invalid, the port cannot be bound, or startup fails unexpectedly, otherwise 0.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args,
        EnvironmentSnapshot environment,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        // One sink for the startup lines and the application log, so both have the same shape.
        var sink = new JsonLinesSink(output);
        using var startupLog = LoggingRegistration.CreateStartupLogger(sink);

        try
        {
            return await RunAsync(args, environment, sink, startupLog, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is not HostAbortedException)
        {
            startupLog.Fatal(exception, "n8Tracks stopped because of an unexpected error");
            return 1;
        }
    }

    private static async Task<int> RunAsync(
        string[] args,
        EnvironmentSnapshot environment,
        JsonLinesSink sink,
        Serilog.ILogger startupLog,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(args);

        // N8TRACKS_PORT is the only source of the listen address: drop anything that came from
        // ASPNETCORE_URLS, ASPNETCORE_HTTP_PORTS, ASPNETCORE_HTTPS_PORTS, --urls, or launch settings.
        builder.Configuration[WebHostDefaults.ServerUrlsKey] = string.Empty;
        builder.Configuration[WebHostDefaults.HttpPortsKey] = string.Empty;
        builder.Configuration[WebHostDefaults.HttpsPortsKey] = string.Empty;

        // Serilog is the only logging provider; the redaction policy sits in front of every sink.
        builder.Logging.ClearProviders();
        builder.Services.AddN8TracksLogging(sink);

        builder.Services.AddOpenApi();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure();
        builder.Services.AddEnvironmentConfiguration(environment);

        var app = builder.Build();
        await using (app.ConfigureAwait(false))
        {
            foreach (var name in EnvironmentOptionsLoader.FindUnknownVariables(app.Services.GetRequiredService<EnvironmentSnapshot>()))
            {
                startupLog.Warning("Unknown setting: {Variable} {Reason}", name, "is not a setting n8Tracks knows and is ignored.");
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
                    startupLog.Error("Invalid configuration: {Variable} {Reason}", error.Variable, error.Reason);
                }

                return 1;
            }

            // Outermost first: the request ID is on every line and every response, the completion line
            // covers every request, and an unhandled exception is logged once before that line is written.
            app.UseMiddleware<RequestIdMiddleware>();
            app.UseSerilogRequestLogging(
                requestLogging => RequestLog.Configure(requestLogging, app.Services.GetRequiredService<Serilog.ILogger>()));
            app.UseMiddleware<UnhandledExceptionMiddleware>();

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

    private static void ReportBindFailure(Serilog.ILogger startupLog, string reason) =>
        startupLog.Error("Startup failed: {Variable} {Reason}", EnvironmentOptionsLoader.Port, reason);
}
