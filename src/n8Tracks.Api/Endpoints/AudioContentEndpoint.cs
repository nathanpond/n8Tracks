using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Net.Http.Headers;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The bytes of a cataloged audio file (#217), so the browser can play and seek it straight from the
/// media folder. <c>catalog.read</c>: a signed-in session or a read token. The file is named by its
/// audio file ID only, and nothing answered carries a path: <c>Content-Disposition</c> holds the
/// file's own name. Byte ranges are honoured (one range, open-ended or suffix; a range past the end is
/// 416; several ranges get the whole file), as are <c>If-None-Match</c>, <c>If-Modified-Since</c>, and
/// <c>If-Range</c> against a strong tag made from the open file's size and modified time. All the rules
/// are <see cref="AudioContentService"/>'s; this holds only HTTP.
/// </summary>
internal static partial class AudioContentEndpoint
{
    public const string ContentPath = MediaEndpoints.AudioFilePath + "/content";

    public const string UnavailableCode = "audio_file_unavailable";

    public const string MethodNotAllowedCode = "method_not_allowed";

    /// <summary>The browser keeps the bytes for the session and asks again, by tag, before each use.</summary>
    public const string CacheControl = "private, max-age=0, must-revalidate";

    public static IEndpointRouteBuilder MapAudioContent(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMethods(ContentPath, [HttpMethods.Get, HttpMethods.Head], ServeAsync)
            .WithName("GetAudioFileContent")
            .WithSummary("The audio file's bytes, as they are on disk, with its format's media type. One byte range is honoured (206; 416 past the end), and the strong entity tag and Last-Modified come from the file as opened. 404 audio_file_unavailable when the file is Missing, the media folder is unavailable, or the file cannot be read now.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces(StatusCodes.Status200OK, contentType: "audio/mpeg")
            .Produces(StatusCodes.Status206PartialContent)
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status416RangeNotSatisfiable)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Without this, the API's 404 fallback would answer every other method (it matches any).
        endpoints.MapMethods(ContentPath, [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete], NotAllowed)
            .RequireScope(CredentialScopes.CatalogRead)
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// 200, 206, 304, or 416 with the file; 404 <c>not_found</c> for an unknown ID, and 404
    /// <c>audio_file_unavailable</c> for a known file that cannot be served. A file that ends early or
    /// changes while it is sent cuts the connection, so a partial body is never taken for the file.
    /// </summary>
    private static async Task<IResult> ServeAsync(
        Guid id,
        AudioContentService audio,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var outcome = await audio.OpenAsync(id, cancellationToken);
        switch (outcome)
        {
            case AudioContentOutcome.Opened opened:
                await SendAsync(opened.Content, context, loggers.CreateLogger(typeof(AudioContentEndpoint)));
                return Results.Empty;

            case AudioContentOutcome.Unavailable unavailable:
                SessionEndpoints.NoStore(context);
                if (unavailable.OpenFailed)
                {
                    LogOpenFailed(loggers.CreateLogger(typeof(AudioContentEndpoint)), id);
                }

                return ApiProblem.For(context, StatusCodes.Status404NotFound, UnavailableCode, "The audio file cannot be read now.");

            default:
                SessionEndpoints.NoStore(context);
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such audio file.");
        }
    }

    /// <summary>405 <c>method_not_allowed</c>, with <c>Allow</c>: the content is only read.</summary>
    private static IResult NotAllowed(HttpContext context)
    {
        context.Response.Headers.Allow = "GET, HEAD";
        return ApiProblem.For(context, StatusCodes.Status405MethodNotAllowed, MethodNotAllowedCode, "Audio content is only read (GET or HEAD).");
    }

    private static async Task SendAsync(AudioContent content, HttpContext context, ILogger logger)
    {
        await using (content)
        {
            // A paused player reads nothing for minutes; that is not a slow client to drop.
            if (context.Features.Get<IHttpMinResponseDataRateFeature>() is { } rate)
            {
                rate.MinDataRate = null;
            }

            context.Response.Headers.CacheControl = CacheControl;
            var disposition = new ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(content.FileName);
            context.Response.Headers.ContentDisposition = disposition.ToString();

            var result = TypedResults.Stream(
                content.Content,
                content.MediaType,
                fileDownloadName: null,
                lastModified: content.ModifiedUtc,
                entityTag: new EntityTagHeaderValue(content.EntityTag),
                enableRangeProcessing: true);
            try
            {
                await result.ExecuteAsync(context);
            }
            catch (AudioContentChangedException)
            {
                // The length and tag already sent no longer describe the file: end the response unfinished.
                LogChangedWhileSent(logger, content.Id);
                context.Abort();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio file {AudioFileId} could not be opened and was answered as unavailable")]
    private static partial void LogOpenFailed(ILogger logger, Guid audioFileId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio file {AudioFileId} changed or ended early while it was sent; the connection was cut")]
    private static partial void LogChangedWhileSent(ILogger logger, Guid audioFileId);
}
