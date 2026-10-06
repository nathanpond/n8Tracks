using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Assets;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The managed artwork store: uploading an image (<c>artwork.write</c>) and serving an asset's
/// original or thumbnails (<c>catalog.read</c>). An upload is judged by its content alone: the file
/// name and the declared content type are never read, and what is served carries n8Tracks' own media
/// type with <c>nosniff</c>. Attaching an asset to a Song, Album, Playlist, or Artist is that
/// record's own edit (later stories).
/// </summary>
internal static class ArtworkEndpoints
{
    public const string ArtworkPath = ApiProblem.VersionPrefix + "/artwork";
    public const string AssetPath = ArtworkPath + "/{assetId:guid}";
    public const string ThumbnailPath = AssetPath + "/{size}";

    public const string TooLargeCode = "artwork_too_large";
    public const string TypeNotSupportedCode = "artwork_type_not_supported";
    public const string UndecodableCode = "artwork_undecodable";
    public const string DimensionsExceededCode = "artwork_dimensions_exceeded";

    /// <summary>Room for the multipart framing around the file, beyond the file's own limit.</summary>
    private const long MultipartAllowance = 1024 * 1024;

    /// <summary>
    /// How served artwork may be cached: privately (it is behind sign-in) and for good, since an
    /// asset's bytes, and each of its thumbnails, never change.
    /// </summary>
    private const string ServedCacheControl = "private, max-age=31536000, immutable";

    public static IEndpointRouteBuilder MapArtwork(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(ArtworkPath, UploadAsync)
            .WithName("UploadArtwork")
            .WithSummary("Stores an uploaded JPEG, PNG, or WebP image (multipart/form-data, one file, at most 25 MB and 12,000 pixels a side) and makes its thumbnails. 201 with the new asset, or 200 with the existing one for identical bytes. 413 artwork_too_large, 415 artwork_type_not_supported, 422 artwork_undecodable or artwork_dimensions_exceeded.")
            .RequireScope(CredentialScopes.ArtworkWrite)
            .Produces<ArtworkResponse>(StatusCodes.Status201Created)
            .Produces<ArtworkResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(AssetPath, OriginalAsync)
            .WithName("GetArtworkOriginal")
            .WithSummary("The asset's original, byte for byte as uploaded. 404 when there is no such asset or nothing live attaches it (a fresh upload counts for a day).")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg", additionalContentTypes: ["image/png", "image/webp"])
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet(ThumbnailPath, ThumbnailAsync)
            .WithName("GetArtworkThumbnail")
            .WithSummary("The asset's thumbnail of size 96, 320, or 1024 pixels on the long side, as WebP; for a size larger than the original, the next smaller thumbnail, or the original when there is none.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces(StatusCodes.Status200OK, contentType: "image/webp", additionalContentTypes: ["image/jpeg", "image/png"])
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>
    /// Reads the first file part of the multipart body into memory, counted against the limit as it
    /// arrives (a body over it is never read to the end), then hands the bytes to the store.
    /// </summary>
    private static async Task<Results<Created<ArtworkResponse>, Ok<ArtworkResponse>, ProblemHttpResult>> UploadAsync(
        ArtworkService artwork,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = ArtworkRules.MaximumBytes + MultipartAllowance;
        }

        if (context.Request.ContentLength > ArtworkRules.MaximumBytes + MultipartAllowance)
        {
            return TooLarge(context);
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
        {
            return NoFile(context);
        }

        ReadOnlyMemory<byte> content;
        try
        {
            switch (await ReadFirstFileAsync(new MultipartReader(boundary, context.Request.Body), cancellationToken))
            {
                case { } read:
                    content = read;
                    break;
                default:
                    return NoFile(context);
            }
        }
        catch (FileTooLargeException)
        {
            return TooLarge(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(context);
        }
        catch (InvalidDataException)
        {
            return NoFile(context);
        }

        switch (await artwork.UploadAsync(content, cancellationToken))
        {
            case ArtworkUploadOutcome.Stored stored:
                var response = ArtworkResponse.From(stored.Asset, context.Request.PathBase);
                if (!stored.Created)
                {
                    return TypedResults.Ok(response);
                }

                loggers.CreateLogger(typeof(ArtworkEndpoints)).LogInformation("Artwork stored: {AssetId}", stored.Asset.Id);
                return TypedResults.Created(response.Urls[ArtworkResponse.OriginalKey], response);

            case ArtworkUploadOutcome.TooLarge:
                return TooLarge(context);

            case ArtworkUploadOutcome.UnsupportedType:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status415UnsupportedMediaType,
                    TypeNotSupportedCode,
                    "The file is not a JPEG, PNG, or WebP image. Its content decides, not its name.");

            case ArtworkUploadOutcome.Undecodable undecodable:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    UndecodableCode,
                    $"The file starts like a {FormatName(undecodable.Format)} image but cannot be read; it may be damaged or incomplete.");

            case ArtworkUploadOutcome.DimensionsExceeded exceeded:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    DimensionsExceededCode,
                    ArtworkRules.IsWithinMaximumSide(exceeded.Width, exceeded.Height)
                        ? $"The image is {Pixels(exceeded.Width, exceeded.Height)} pixels, too many to process. Use a smaller image."
                        : $"The image is {Pixels(exceeded.Width, exceeded.Height)} pixels; artwork can be at most {Number(ArtworkRules.MaximumSide)} pixels on a side.",
                    [new("width", exceeded.Width), new("height", exceeded.Height), new("maximumSide", ArtworkRules.MaximumSide)]);

            default:
                throw new InvalidOperationException("Unknown artwork upload outcome.");
        }
    }

    /// <summary>200 with the original; 404 when there is no such asset, nothing live attaches it, or its file is gone.</summary>
    private static Task<Results<FileStreamHttpResult, ProblemHttpResult>> OriginalAsync(
        Guid assetId,
        ArtworkService artwork,
        HttpContext context,
        CancellationToken cancellationToken) =>
        ServeAsync(artwork, assetId, null, context, cancellationToken);

    /// <summary>200 with the thumbnail served for the size; 404 for a size that is not 96, 320, or 1024, or as for the original.</summary>
    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> ThumbnailAsync(
        Guid assetId,
        string size,
        ArtworkService artwork,
        HttpContext context,
        CancellationToken cancellationToken) =>
        ArtworkRules.ParseSize(size) is { } parsed
            ? await ServeAsync(artwork, assetId, parsed, context, cancellationToken)
            : NotFound(context);

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> ServeAsync(
        ArtworkService artwork,
        Guid assetId,
        int? size,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (await artwork.OpenAsync(assetId, size, cancellationToken) is not { } served)
        {
            SessionEndpoints.NoStore(context);
            return NotFound(context);
        }

        var headers = context.Response.Headers;
        headers[HeaderNames.CacheControl] = ServedCacheControl;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.ContentSecurityPolicy] = "default-src 'none'; sandbox";
        return TypedResults.Stream(served.Content, served.MediaType, entityTag: new EntityTagHeaderValue($"\"{served.EntityTag}\""));
    }

    /// <summary>The first file part's bytes, or null when the body has none; throws <see cref="FileTooLargeException"/> past the limit.</summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadFirstFileAsync(MultipartReader reader, CancellationToken cancellationToken)
    {
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
        {
            if (section.GetContentDispositionHeader() is not { } disposition || !disposition.IsFileDisposition())
            {
                continue;
            }

            var content = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await section.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (content.Length + read > ArtworkRules.MaximumBytes)
                {
                    throw new FileTooLargeException();
                }

                content.Write(buffer, 0, read);
            }

            return content.GetBuffer().AsMemory(0, (int)content.Length);
        }

        return null;
    }

    private static ProblemHttpResult TooLarge(HttpContext context) =>
        ApiProblem.For(
            context,
            StatusCodes.Status413PayloadTooLarge,
            TooLargeCode,
            "The file is larger than 25 MB, the most artwork can be.",
            [new("maximumBytes", ArtworkRules.MaximumBytes)]);

    private static ProblemHttpResult NoFile(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status400BadRequest, ApiProblem.InvalidRequestCode, "Send the image as multipart/form-data with one file.");

    private static ProblemHttpResult NotFound(HttpContext context) =>
        ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such artwork.");

    private static string FormatName(ArtworkFormat format) => format switch
    {
        ArtworkFormat.Jpeg => "JPEG",
        ArtworkFormat.Png => "PNG",
        _ => "WebP",
    };

    private static string Pixels(int width, int height) => $"{Number(width)} × {Number(height)}";

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The file part ran past <see cref="ArtworkRules.MaximumBytes"/>.</summary>
    private sealed class FileTooLargeException : Exception
    {
    }
}

/// <summary>
/// A stored asset: its ID (what an owner's <c>artworkAssetId</c> takes), what it was found to be, its
/// dimensions with orientation applied, the thumbnail sizes made, and where to fetch the original
/// (<c>urls.original</c>) and each thumbnail size (<c>urls["96"]</c>, …; a size not made is served by
/// the next smaller one, or the original).
/// </summary>
internal sealed record ArtworkResponse(
    Guid Id,
    string MediaType,
    long Bytes,
    int Width,
    int Height,
    IReadOnlyList<int> Sizes,
    IReadOnlyDictionary<string, string> Urls,
    DateTime UploadedAt)
{
    public const string OriginalKey = "original";

    public static ArtworkResponse From(Asset asset, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var original = $"{pathBase}{ArtworkEndpoints.ArtworkPath}/{asset.Id}";
        var urls = new Dictionary<string, string>(StringComparer.Ordinal) { [OriginalKey] = original };
        foreach (var size in ArtworkRules.ThumbnailSizes)
        {
            urls[size.ToString(CultureInfo.InvariantCulture)] = $"{original}/{size.ToString(CultureInfo.InvariantCulture)}";
        }

        return new(
            asset.Id,
            ArtworkRules.MediaType(asset.Format),
            asset.Bytes,
            asset.Width,
            asset.Height,
            asset.ThumbnailSizes,
            urls,
            asset.UploadedUtc.UtcDateTime);
    }
}
