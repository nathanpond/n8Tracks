using Serilog.AspNetCore;
using Serilog.Events;

namespace n8Tracks.Api.Logging;

/// <summary>Marks an endpoint whose requests are logged at Debug (the health check, polled constantly).</summary>
internal sealed class QuietRequestLogMetadata
{
    public static QuietRequestLogMetadata Instance { get; } = new();
}

/// <summary>
/// What the request-completion line records: method, path (never the query string), status, and
/// duration. Nothing else about the request or response is logged: no headers, body, client address,
/// or user agent.
/// </summary>
internal static class RequestLog
{
    private static readonly object ClientAbortedKey = new();
    private static readonly object FailedKey = new();
    private static readonly object ExpectedRefusalKey = new();

    public const string MessageTemplate = "{Method} {Path} responded {Status} in {DurationMs:0.0} ms";

    public static void Configure(RequestLoggingOptions options, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The host's own logger, never Serilog's static one.
        options.Logger = logger;
        options.MessageTemplate = MessageTemplate;
        options.IncludeQueryInRequestPath = false;
        options.GetLevel = static (context, _, _) => Level(context);
        options.GetMessageTemplateProperties = static (context, _, elapsedMilliseconds, status) =>
        [
            new LogEventProperty("Method", new ScalarValue(context.Request.Method)),
            new LogEventProperty("Path", new ScalarValue(Path(context))),
            new LogEventProperty("Status", new ScalarValue(status)),
            new LogEventProperty("DurationMs", new ScalarValue(Math.Round(elapsedMilliseconds, 3))),
        ];
    }

    /// <summary>Records that the client gave up on the request, so its completion line is written at Debug.</summary>
    public static void MarkClientAborted(HttpContext context) => context.Items[ClientAbortedKey] = true;

    /// <summary>Records that the request failed with an unhandled exception, so its completion line is written at Error.</summary>
    public static void MarkFailed(HttpContext context) => context.Items[FailedKey] = true;

    /// <summary>
    /// Records that a 5xx answer is a deliberate refusal and not a fault (503 <c>setup_required</c>
    /// before setup), so its completion line is written at Information.
    /// </summary>
    public static void MarkExpectedRefusal(HttpContext context) => context.Items[ExpectedRefusalKey] = true;

    private static LogEventLevel Level(HttpContext context)
    {
        if (context.Items.ContainsKey(ClientAbortedKey))
        {
            return LogEventLevel.Debug;
        }

        if (context.Items.ContainsKey(ExpectedRefusalKey) && !context.Items.ContainsKey(FailedKey))
        {
            return LogEventLevel.Information;
        }

        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError || context.Items.ContainsKey(FailedKey))
        {
            return LogEventLevel.Error;
        }

        // Quiet only when the endpoint answered: a refused or failed health request is worth a normal line.
        var quiet = context.Response.StatusCode < StatusCodes.Status400BadRequest
            && context.GetEndpoint()?.Metadata.GetMetadata<QuietRequestLogMetadata>() is not null;

        return quiet ? LogEventLevel.Debug : LogEventLevel.Information;
    }

    /// <summary>The path as the client sent it, sub-path prefix included, without the query string.</summary>
    private static string Path(HttpContext context) => context.Request.PathBase.Value + context.Request.Path.Value;
}
