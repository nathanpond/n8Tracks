using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Configuration;

internal static class PathBaseExtensions
{
    /// <summary>
    /// Serves every route under the configured sub-path, and answers 404 with an empty body for any
    /// request outside it. Does nothing when the base URL has no path.
    /// </summary>
    public static IApplicationBuilder UseConfiguredPathBase(this IApplicationBuilder app, N8TracksOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        if (options.PathBase.Length == 0)
        {
            return app;
        }

        var pathBase = options.PathBase;

        app.UsePathBase(pathBase);

        // UsePathBase lets a request that did not match fall through unchanged; refuse it here.
        return app.Use(async (context, next) =>
        {
            var matched = context.Request.PathBase.Value;
            if (matched is null || !matched.EndsWith(pathBase, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });
    }
}
