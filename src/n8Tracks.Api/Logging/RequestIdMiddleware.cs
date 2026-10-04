using Serilog.Context;

namespace n8Tracks.Api.Logging;

/// <summary>
/// Gives every request a server-generated ID: returned in the <c>X-Request-ID</c> header, put on each
/// of the request's log lines as <c>requestId</c>, and used as the request's trace identifier.
/// An ID sent by the client is ignored.
/// </summary>
internal sealed class RequestIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-ID";
    public const string PropertyName = "requestId";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var requestId = Guid.CreateVersion7().ToString();
        context.TraceIdentifier = requestId;

        // Set when the response starts, so it survives a handler that resets the headers.
        context.Response.OnStarting(
            static state =>
            {
                var (response, id) = ((HttpResponse, string))state;
                response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            },
            (context.Response, requestId));

        using (LogContext.PushProperty(PropertyName, requestId))
        {
            await next(context);
        }
    }
}
