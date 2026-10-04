using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Local development only: the app, the MCP gateway, and the frontend dev server, started together
// with the Aspire dashboard. This project is in neither Docker image.
//
// The app and the gateway are configured the way a container is: through N8TRACKS_* variables and
// nothing else. Each value below is a default for a local run; the same variable in the developer's
// own environment (or as a NAME=value argument) replaces it, with no code change.
var builder = DistributedApplication.CreateBuilder(args);

// Defaults: the container's ports, and a data and media folder next to this project (git-ignored).
const string DefaultApiPort = "8787";
const string DefaultGatewayPort = "8788";
const string DefaultLogLevel = "Information";
const int FrontendPort = 5173;

var localData = Path.Combine(builder.AppHostDirectory, ".localdata");
var localMedia = Path.Combine(localData, "media");

var apiPort = Port("N8TRACKS_PORT", DefaultApiPort);
var gatewayPort = Port("N8TRACKS_GATEWAY_PORT", DefaultGatewayPort);

var dataPath = DirectorySetting("N8TRACKS_DATA_PATH", localData);
var mediaPath = DirectorySetting("N8TRACKS_MEDIA_PATH", localMedia);

// Neither project's launch profile is used: every setting comes from here. The endpoints are fixed
// and unproxied, so each service listens on the port a developer expects and nothing sits in front.
// Aspire points OTEL_EXPORTER_OTLP_ENDPOINT of both at the dashboard, which turns their telemetry on.
var api = builder.AddProject<Projects.n8Tracks_Api>("api", static project => project.ExcludeLaunchProfile = true)
    .WithHttpEndpoint(port: apiPort, name: "http", env: "N8TRACKS_PORT", isProxied: false)
    .WithEnvironment("N8TRACKS_DATA_PATH", dataPath)
    .WithEnvironment("N8TRACKS_MEDIA_PATH", mediaPath)
    .WithEnvironment("N8TRACKS_LOG_LEVEL", Setting("N8TRACKS_LOG_LEVEL") ?? DefaultLogLevel)
    .WithHttpHealthCheck("/health");

// Settings with no local default are handed on only when the developer set them.
Forward(api, "N8TRACKS_BASE_URL");
Forward(api, "TZ");
if (Setting("N8TRACKS_BACKUP_PATH") is { } backupPath)
{
    api.WithEnvironment("N8TRACKS_BACKUP_PATH", Path.GetFullPath(backupPath));
}

var apiEndpoint = api.GetEndpoint("http");

// The gateway is not made to wait for the app: it is built to run degraded while the app is away.
var gateway = builder.AddProject<Projects.n8Tracks_Gateway>("gateway", static project => project.ExcludeLaunchProfile = true)
    .WithHttpEndpoint(port: gatewayPort, name: "http", env: "N8TRACKS_GATEWAY_PORT", isProxied: false)
    .WithEnvironment("N8TRACKS_LOG_LEVEL", Setting("N8TRACKS_LOG_LEVEL") ?? DefaultLogLevel)
    .WithHttpHealthCheck("/health");

if (Setting("N8TRACKS_API_URL") is { } apiUrl)
{
    gateway.WithEnvironment("N8TRACKS_API_URL", apiUrl);
}
else
{
    gateway.WithEnvironment("N8TRACKS_API_URL", apiEndpoint);
}

// The Vite dev server for web/, on its usual port. Dependencies are never installed from here:
// when they are missing the resource fails and says what to run, and the two services still start.
var webDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "web"));

builder.AddViteApp("frontend", webDirectory)
    .WithNpm(install: false)
    .WithEndpoint("http", static endpoint =>
    {
        endpoint.Port = FrontendPort;
        endpoint.TargetPort = FrontendPort;
        endpoint.IsProxied = false;
    })
    .WithEnvironment("N8TRACKS_API_URL", apiEndpoint)
    .OnBeforeResourceStarted((resource, started, _) =>
    {
        if (!Directory.Exists(Path.Combine(webDirectory, "node_modules", "vite")))
        {
            var message = $"The frontend's dependencies are not installed. Run `npm install` in {webDirectory} (web/), then start this resource again.";

            // Once in the resource's own log on the dashboard, once on this console.
            started.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource).LogError("{Message}", message);
            started.Services.GetRequiredService<ILoggerFactory>().CreateLogger("n8Tracks.AppHost.Frontend").LogError("{Message}", message);
            throw new DistributedApplicationException(message);
        }

        return Task.CompletedTask;
    });

await builder.Build().RunAsync().ConfigureAwait(false);

// A setting from the developer's environment or the command line. Blank counts as unset, as it does in the services.
string? Setting(string name) =>
    builder.Configuration[name] is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

// The port must be known here, to declare the endpoint; the service validates the same variable again.
int Port(string name, string defaultValue)
{
    var value = Setting(name) ?? defaultValue;

    return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
        ? port
        : throw new DistributedApplicationException($"{name} must be a whole number from 1 to 65535, but was '{value}'.");
}

// The developer's own folder (relative to where the command was run) is used as it is. The local
// default is created, so a fresh clone starts healthy.
string DirectorySetting(string name, string localDefault)
{
    if (Setting(name) is { } chosen)
    {
        return Path.GetFullPath(chosen);
    }

    Directory.CreateDirectory(localDefault);
    return localDefault;
}

void Forward(IResourceBuilder<ProjectResource> resource, string name)
{
    if (Setting(name) is { } value)
    {
        resource.WithEnvironment(name, value);
    }
}
