using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using n8Tracks.Api.Logging;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Maintenance;

namespace n8Tracks.Api.Maintenance;

/// <summary>
/// While the instance is in maintenance every <c>/api/v1</c> request except the maintenance status
/// is answered 503 <c>maintenance</c> before it reaches the setup gate, authentication, or any
/// endpoint, so nothing reads or writes the catalog. The maintenance status itself is answered here,
/// in and out of maintenance, so reading it never opens the database. Health, the frontend's files, and anything
/// outside <c>/api/v1</c> are untouched. Outside maintenance each API request is counted while it
/// runs, so a restore can let the ones in flight finish (<see cref="RequestDrain"/>).
/// </summary>
internal sealed class MaintenanceMiddleware(RequestDelegate next)
{
    public const string MaintenanceCode = "maintenance";

    /// <summary>How long a client is asked to wait before it tries again.</summary>
    public const int RetryAfterSeconds = 5;

    private static readonly PathString VersionPrefix = new(ApiProblem.VersionPrefix);
    private static readonly PathString StatusPath = new(MaintenanceEndpoints.StatusPath);

    public async Task InvokeAsync(HttpContext context, MaintenanceMode maintenance, RequestDrain drain)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(drain);

        // Routing matches in any letter case, so the gate does too.
        var path = context.Request.Path;
        if (path.Equals(StatusPath, StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(context.Request.Method))
        {
            // Answered here, never past the setup gate or authentication: both read the database,
            // which a restore may be moving at this moment, and a session cookie would be looked up.
            context.Response.Headers[HeaderNames.CacheControl] = "no-store";
            await TypedResults.Ok(MaintenanceResponse.From(maintenance.Current)).ExecuteAsync(context);
            return;
        }

        if (!path.StartsWithSegments(VersionPrefix, StringComparison.OrdinalIgnoreCase)
            || path.Equals(StatusPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Counted before the check, so a request is either refused or waited for: never neither.
        using (drain.Track(context))
        {
            if (!maintenance.IsActive)
            {
                await next(context);
                return;
            }
        }

        RequestLog.MarkExpectedRefusal(context);
        await Refusal(context).ExecuteAsync(context);
    }

    /// <summary>The 503 <c>maintenance</c> answer, asking the client to try again in a few seconds.</summary>
    public static ProblemHttpResult Refusal(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers[HeaderNames.RetryAfter] = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return ApiProblem.For(
            context,
            StatusCodes.Status503ServiceUnavailable,
            MaintenanceCode,
            "n8Tracks is in maintenance while a backup is restored. Try again when it has finished.");
    }
}

/// <summary>The API requests in flight, so maintenance can let them finish and abort the rest.</summary>
internal sealed class RequestDrain(TimeProvider time) : IRequestDrain
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ConcurrentDictionary<HttpContext, byte> inFlight = new();

    /// <summary>The number of API requests in flight now.</summary>
    public int Count => inFlight.Count;

    /// <summary>Counts <paramref name="context"/> as in flight until the returned handle is disposed.</summary>
    public IDisposable Track(HttpContext context)
    {
        inFlight[context] = 0;
        return new Untrack(this, context);
    }

    public async Task<int> DrainAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + grace;
        while (!inFlight.IsEmpty && time.GetUtcNow() < deadline)
        {
            await Task.Delay(PollInterval, time, cancellationToken).ConfigureAwait(false);
        }

        var aborted = 0;
        foreach (var context in inFlight.Keys)
        {
            try
            {
                context.Abort();
                aborted++;
            }
            catch (ObjectDisposedException)
            {
                // It finished as it was aborted.
            }
        }

        return aborted;
    }

    private sealed class Untrack(RequestDrain owner, HttpContext context) : IDisposable
    {
        public void Dispose() => owner.inFlight.TryRemove(context, out _);
    }
}
