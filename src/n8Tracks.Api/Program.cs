using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.DependencyInjection;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Frontend;
using n8Tracks.Api.Logging;
using n8Tracks.Application;
using n8Tracks.Application.Configuration;
using n8Tracks.Infrastructure;
using n8Tracks.Infrastructure.Logging;
using n8Tracks.Infrastructure.Persistence;
using n8Tracks.ServiceDefaults;
using Serilog;

/// <summary>The entry point and composition root.</summary>
public sealed class Program
{
    /// <summary>The name the app's telemetry is reported under.</summary>
    internal const string ServiceName = "n8tracks";

    private Program()
    {
    }

    private static Task<int> Main(string[] args) =>
        RunAsync(args, ProcessEnvironment.Read(), Console.Out, CancellationToken.None);

    /// <summary>
    /// Builds and runs the app until shutdown or <paramref name="cancellationToken"/>. Every log line
    /// goes to <paramref name="output"/> as JSON. Returns the process exit code: 1 when the
    /// configuration is invalid, the database cannot be opened or upgraded, the port cannot be bound,
    /// or startup fails unexpectedly, otherwise 0. With <c>--healthcheck</c> among
    /// <paramref name="args"/> it starts nothing and reports on the app that is already running
    /// (see <see cref="HealthCheckCommand"/>). That is the only argument with a meaning: every other
    /// one is ignored, and none reaches the host's configuration.
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
            // The container health check: ask the running app for its health and exit, starting nothing.
            if (HealthCheckCommand.IsRequested(args))
            {
                return await HealthCheckCommand.RunAsync(environment, startupLog, cancellationToken).ConfigureAwait(false);
            }

            return await RunAsync(environment, sink, startupLog, cancellationToken).ConfigureAwait(false);
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
        EnvironmentSnapshot environment,
        JsonLinesSink sink,
        Serilog.ILogger startupLog,
        CancellationToken cancellationToken)
    {
        // The host starts empty: it reads no command-line argument, no environment variable (with an
        // ASPNETCORE_ or DOTNET_ prefix or without one), and no appsettings file, so no framework
        // setting from any of them (urls, a Kestrel or Logging section, AllowedHosts, ...) can change
        // what the app does. Everything it needs is set here, from the environment snapshot.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            EnvironmentName = EnvironmentOptionsLoader.HostEnvironmentName(environment),

            // The frontend is served from wwwroot under the working directory.
            ContentRootPath = environment.WorkingDirectory,
        });

        // What the default builder would have added: the server, without the loader that reads
        // endpoints and limits from a "Kestrel" configuration section (N8TRACKS_PORT is the only listen
        // source); routing; and a container that checks its registrations, in every environment.
        builder.WebHost.UseKestrelCore();
        builder.Services.AddRouting();
        builder.Host.UseDefaultServiceProvider(static provider =>
        {
            provider.ValidateScopes = true;
            provider.ValidateOnBuild = true;
        });

        // Serilog is the only logging provider; the redaction policy sits in front of every sink.
        builder.Logging.ClearProviders();
        builder.Services.AddN8TracksLogging(sink);

        // OpenTelemetry, only when the environment variable OTEL_EXPORTER_OTLP_ENDPOINT is set;
        // otherwise both calls register nothing. Log records leave
        // through one more sink of the application log, so they are redacted like every other line,
        // never through a logging provider.
        builder.AddServiceDefaults(ServiceName, ProductVersion.Current, environment.Variables, exportLogsFromLoggingProviders: false);
        builder.Services.AddN8TracksLogExport(environment.Variables, ServiceName, ProductVersion.Current);

        // Enums travel as camelCase strings ("healthy"), in responses and in the OpenAPI document.
        builder.Services.ConfigureHttpJsonOptions(static json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));

        builder.Services.AddOpenApi();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure();
        builder.Services.AddEnvironmentConfiguration(environment);
        builder.Services.AddFrontend();

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

            // The schema is brought up to date before anything listens; a failure stops the process.
            if (!await DatabaseStartup.RunAsync(app.Services, startupLog, cancellationToken).ConfigureAwait(false))
            {
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

            // After the endpoints, and inside the path base: the frontend answers only what no endpoint does.
            app.UseFrontend(options);

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
