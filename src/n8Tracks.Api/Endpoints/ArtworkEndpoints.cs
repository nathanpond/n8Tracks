using System.Globalization;
using System.Text.Json;
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
/// record's own edit (its PATCH's <c>artworkAssetId</c> and <c>artworkCrop</c>).
/// </summary>
internal static class ArtworkEndpoints
{
    public const string ArtworkPath = ApiProblem.VersionPrefix + "/artwork";
    public const string AssetPath = ArtworkPath + "/{assetId:guid}";
    public const string ThumbnailPath = AssetPath + "/{size}";
    public const string CropThumbnailPath = AssetPath + "/crops/{cropKey}/{size}";

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

        endpoints.MapGet(CropThumbnailPath, CropThumbnailAsync)
            .WithName("GetArtworkCropThumbnail")
            .WithSummary("A square thumbnail of a crop an owner set on the asset (the crop key is in the owner's artwork squareUrls), 96, 320, or 1024 pixels a side, as WebP; for a size larger than the crop, the next smaller one. 404 when no live owner sets that crop on the asset, or as for the original.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces(StatusCodes.Status200OK, contentType: "image/webp")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>
    /// The crop field of an owner's edit as sent: missing is left alone; null is the centred square;
    /// otherwise an object of whole numbers <c>x</c>, <c>y</c>, and <c>size</c> (or <c>width</c> and
    /// <c>height</c>, which must then be equal, since a crop is square). Anything else is an error
    /// keyed by <paramref name="name"/>. Whether it fits the image is the service's check.
    /// </summary>
    internal static ArtworkCropEdit ReadCrop(JsonElement? sent, string name, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        switch (sent?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                return default;
            case JsonValueKind.Null:
                return ArtworkCropEdit.Of(null);
            case JsonValueKind.Object:
                break;
            default:
                errors[name] = [CropShapeMessage];
                return default;
        }

        var crop = sent.Value;
        int? Member(string member, out bool present)
        {
            present = crop.TryGetProperty(member, out var value) && value.ValueKind != JsonValueKind.Null;
            return present && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        }

        var x = Member("x", out var hasX);
        var y = Member("y", out var hasY);
        var size = Member("size", out var hasSize);
        var width = Member("width", out var hasWidth);
        var height = Member("height", out var hasHeight);
        if (!hasX || !hasY || x is null || y is null
            || (hasSize && size is null) || (hasWidth && width is null) || (hasHeight && height is null)
            || hasWidth != hasHeight || (!hasSize && !hasWidth))
        {
            errors[name] = [CropShapeMessage];
            return default;
        }

        int[] sides = [.. new[] { size, width, height }.OfType<int>()];
        if (sides.Distinct().Count() > 1)
        {
            errors[name] = ["A crop is square: its width and height must be the same."];
            return default;
        }

        return ArtworkCropEdit.Of(new ArtworkCrop(x.Value, y.Value, sides[0]));
    }

    private const string CropShapeMessage = "Send the crop as whole numbers of pixels {x, y, size}, or null for the centred square.";

    /// <summary>
    /// The artwork fields of an Album's, Playlist's, or Artist's edit as sent: <c>artworkAssetId</c>
    /// (missing is left alone; text or null, or else an error) and <c>artworkCrop</c>
    /// (<see cref="ReadCrop"/>). Errors are keyed by the field names.
    /// </summary>
    internal static OwnerArtworkEdit ReadOwnerArtwork(JsonElement? assetId, JsonElement? crop, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var sent = false;
        string? id = null;
        switch (assetId?.ValueKind)
        {
            case null or JsonValueKind.Undefined:
                break;
            case JsonValueKind.Null:
                sent = true;
                break;
            case JsonValueKind.String:
                sent = true;
                id = assetId.Value.GetString();
                break;
            default:
                errors[ArtworkAttachmentService.AssetIdField] = ["Send text or null."];
                break;
        }

        return new OwnerArtworkEdit(sent, id, ReadCrop(crop, ArtworkAttachmentService.CropField, errors));
    }

    /// <summary>
    /// 403 <c>insufficient_scope</c> when a credential changes an owner's artwork without
    /// <c>artwork.write</c> (the owner's own scope is the endpoint's marker); null otherwise.
    /// </summary>
    internal static ProblemHttpResult? LackingArtworkScope(HttpContext context, OwnerArtworkEdit edit) =>
        edit.IsSent ? ScopeMiddleware.Lacking(context, CredentialScopes.ArtworkWrite) : null;

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

    /// <summary>200 with the crop's square thumbnail served for the size; 404 when no live owner sets that crop, or as for a thumbnail.</summary>
    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> CropThumbnailAsync(
        Guid assetId,
        string cropKey,
        string size,
        ArtworkAttachmentService attachments,
        HttpContext context,
        CancellationToken cancellationToken) =>
        ArtworkRules.ParseSize(size) is { } parsed
            ? Serve(await attachments.OpenCropAsync(assetId, cropKey, parsed, cancellationToken), context)
            : NotFound(context);

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> ServeAsync(
        ArtworkService artwork,
        Guid assetId,
        int? size,
        HttpContext context,
        CancellationToken cancellationToken) =>
        Serve(await artwork.OpenAsync(assetId, size, cancellationToken), context);

    private static Results<FileStreamHttpResult, ProblemHttpResult> Serve(ArtworkContent? content, HttpContext context)
    {
        if (content is not { } served)
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

        return new(
            asset.Id,
            ArtworkRules.MediaType(asset.Format),
            asset.Bytes,
            asset.Width,
            asset.Height,
            asset.ThumbnailSizes,
            UrlsOf(asset.Id, pathBase),
            asset.UploadedUtc.UtcDateTime);
    }

    /// <summary>Where to fetch the asset's original (<c>original</c>) and each thumbnail size (<c>"96"</c>, …), each starting with <paramref name="pathBase"/>.</summary>
    public static IReadOnlyDictionary<string, string> UrlsOf(Guid assetId, PathString pathBase)
    {
        var original = $"{pathBase}{ArtworkEndpoints.ArtworkPath}/{assetId}";
        var urls = new Dictionary<string, string>(StringComparer.Ordinal) { [OriginalKey] = original };
        foreach (var size in ArtworkRules.ThumbnailSizes)
        {
            urls[size.ToString(CultureInfo.InvariantCulture)] = $"{original}/{size.ToString(CultureInfo.InvariantCulture)}";
        }

        return urls;
    }
}

/// <summary>
/// An owner's artwork as its answers show it: the asset (what <c>artworkAssetId</c> takes), the
/// original's dimensions with its orientation applied (which a crop is in), where to fetch its
/// original and each thumbnail size of the whole image (as <see cref="ArtworkResponse"/>), the square
/// crop the owner set (null for the centred square), and <c>squareUrls</c>, where to fetch it as a
/// square at each size: the crop's own square thumbnails, whose URLs change with the crop, or, with
/// no crop, the whole-image thumbnails, which are shown cut to their centred square.
/// </summary>
internal sealed record AttachedArtworkResponse(
    Guid AssetId,
    int Width,
    int Height,
    IReadOnlyDictionary<string, string> Urls,
    ArtworkCropResponse? Crop,
    IReadOnlyDictionary<string, string> SquareUrls)
{
    public static AttachedArtworkResponse From(AttachedArtwork artwork, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(artwork);

        var urls = ArtworkResponse.UrlsOf(artwork.AssetId, pathBase);
        return new(
            artwork.AssetId,
            artwork.Width,
            artwork.Height,
            urls,
            artwork.Crop is { } crop ? new ArtworkCropResponse(crop.X, crop.Y, crop.Size) : null,
            artwork.Crop is { } cropped ? SquareUrlsOf(artwork.AssetId, cropped, pathBase) : ThumbnailsOf(urls));
    }

    private static Dictionary<string, string> ThumbnailsOf(IReadOnlyDictionary<string, string> urls) =>
        urls.Where(static url => url.Key != ArtworkResponse.OriginalKey).ToDictionary(StringComparer.Ordinal);

    private static Dictionary<string, string> SquareUrlsOf(Guid assetId, ArtworkCrop crop, PathString pathBase)
    {
        var folder = $"{pathBase}{ArtworkEndpoints.ArtworkPath}/{assetId}/crops/{ArtworkCropRules.Key(crop)}";
        return ArtworkRules.ThumbnailSizes.ToDictionary(
            static size => size.ToString(CultureInfo.InvariantCulture),
            size => $"{folder}/{size.ToString(CultureInfo.InvariantCulture)}",
            StringComparer.Ordinal);
    }
}

/// <summary>A square crop in pixels of the original, its orientation applied: the left and top edges and the side.</summary>
internal sealed record ArtworkCropResponse(int X, int Y, int Size);
