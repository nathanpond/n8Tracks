using Microsoft.AspNetCore.Mvc;
using n8Tracks.Api.Problems;

namespace n8Tracks.Api.Logging;

/// <summary>
/// The one place an unhandled exception is logged: once, at Error. The client gets a Problem Details
/// 500 that carries the request ID and nothing about the exception. A request the client aborted is
/// not an error and is logged at Debug. A request the server could not read (malformed JSON, a body
/// of the wrong type) is the client's mistake, not a fault: it is answered with a Problem Details 400
/// <c>invalid_request</c> (or the status the framework chose) and logged at Debug.
/// </summary>
internal sealed partial class UnhandledExceptionMiddleware(RequestDelegate next, ILogger<UnhandledExceptionMiddleware> logger)
{
    public const string ErrorCode = "internal_error";

    /// <summary>The status recorded for a request the client gave up on; it is never sent.</summary>
    public const int ClientClosedRequest = 499;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context);
        }
        catch (Exception exception) when (IsClientAbort(context, exception))
        {
            LogRequestAborted(logger);
            RequestLog.MarkClientAborted(context);

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ClientClosedRequest;
            }
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            LogBadRequest(logger, exception.StatusCode);
            context.Response.Clear();

            await ApiProblem.For(
                context,
                exception.StatusCode,
                ApiProblem.InvalidRequestCode,
                "The request could not be read.").ExecuteAsync(context);
        }
        catch (Exception exception)
        {
            LogUnhandledException(logger, exception);
            RequestLog.MarkFailed(context);

            if (context.Response.HasStarted)
            {
                // The status and part of the body are already sent: cut the connection so the client
                // does not take a truncated response for a complete one.
                context.Abort();
                return;
            }

            context.Response.Clear();

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
            };
            problem.Extensions["code"] = ErrorCode;
            problem.Extensions["requestId"] = context.TraceIdentifier;

            await TypedResults.Problem(problem).ExecuteAsync(context);
        }
    }

    private static bool IsClientAbort(HttpContext context, Exception exception) =>
        context.RequestAborted.IsCancellationRequested && exception is OperationCanceledException or IOException;

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The request could not be read and was answered {StatusCode}")]
    private static partial void LogBadRequest(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The client aborted the request")]
    private static partial void LogRequestAborted(ILogger logger);
}
