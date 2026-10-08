using System.Net;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Assets;
using SkiaSharp;
using static n8Tracks.Api.Tests.Assets.ArtworkApi;
using static n8Tracks.Api.Tests.Assets.ArtworkImages;

namespace n8Tracks.Api.Tests.Assets;

/// <summary>
/// The managed artwork store's upload and serving (#97): JPEG, PNG, and WebP up to 25 MB are stored
/// byte for byte, under the content hash, with WebP thumbnails at 96, 320, and 1,024 pixels; the
/// content decides the type, whatever the file is called; anything that is not a whole image of
/// acceptable size is refused and leaves nothing behind; serving needs a session or
/// <c>catalog.read</c>, and uploading <c>artwork.write</c>.
/// </summary>
public sealed class ArtworkUploadEndpointTests
{
    public static TheoryData<string, SKEncodedImageFormat, string> AcceptedTypes => new()
    {
        { "image/jpeg", SKEncodedImageFormat.Jpeg, "jpg" },
        { "image/png", SKEncodedImageFormat.Png, "png" },
        { "image/webp", SKEncodedImageFormat.Webp, "webp" },
    };

    [Theory]
    [MemberData(nameof(AcceptedTypes))]
    public async Task EachAcceptedTypeIsStoredByteForByteWithItsThumbnails(string mediaType, SKEncodedImageFormat format, string extension)
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var image = Halves(format, 2000, 1000);

        // Declared as something else entirely: the content decides.
        using var response = await UploadAsync(client, image, "upload.bin", "application/octet-stream");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var asset = await SetupApi.JsonAsync(response);
        var id = IdOf(asset);
        Assert.Equal($"/api/v1/artwork/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(mediaType, asset.GetProperty("mediaType").GetString());
        Assert.Equal(image.Length, asset.GetProperty("bytes").GetInt64());
        Assert.Equal(2000, asset.GetProperty("width").GetInt32());
        Assert.Equal(1000, asset.GetProperty("height").GetInt32());
        Assert.Equal([96, 320, 1024], asset.GetProperty("sizes").EnumerateArray().Select(static size => size.GetInt32()));

        // The files, named by the content hash: the original exactly as uploaded, and a WebP for each size.
        var hash = Hash(image);
        var folder = $"artwork/{hash[..2]}/{hash}";
        Assert.Equal([$"{folder}/1024.webp", $"{folder}/320.webp", $"{folder}/96.webp", $"{folder}/original.{extension}"], StoredFiles(factory));
        Assert.Equal(image, await File.ReadAllBytesAsync(FullPath(factory, $"{folder}/original.{extension}")));
        Assert.Equal(1, AssetRows(factory));

        // Served from n8Tracks: the original's bytes with the stored type, and each thumbnail at its size.
        using (var original = await GetAsync(client, UrlOf(asset, "original")))
        {
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.Equal(mediaType, original.Content.Headers.ContentType?.MediaType);
            Assert.Equal(image, await original.Content.ReadAsByteArrayAsync());
            Assert.Equal("nosniff", Assert.Single(original.Headers.GetValues("X-Content-Type-Options")));
        }

        foreach (var (size, width, height) in new[] { (96, 96, 48), (320, 320, 160), (1024, 1024, 512) })
        {
            using var thumbnail = await GetAsync(client, UrlOf(asset, size.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
            Assert.Equal("image/webp", thumbnail.Content.Headers.ContentType?.MediaType);
            var bytes = await thumbnail.Content.ReadAsByteArrayAsync();
            Assert.Equal((width, height), Dimensions(bytes));
            Assert.True(Near(Pixel(bytes, width / 4, height / 2), Red), "The left half stays red.");
            Assert.True(Near(Pixel(bytes, width * 3 / 4, height / 2), Blue), "The right half stays blue.");
        }
    }

    [Fact]
    public async Task IdenticalBytesAreStoredOnceAndAnswerTheSameAsset()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var image = Halves(SKEncodedImageFormat.Png, 400, 300);

        var first = await StoreAsync(client, image);
        var second = await StoreAsync(client, image, expected: HttpStatusCode.OK);

        Assert.Equal(IdOf(first), IdOf(second));
        Assert.Equal(1, AssetRows(factory));
        Assert.Equal(3, StoredFiles(factory).Count);
    }

    [Fact]
    public async Task SizesLargerThanTheOriginalAreSkippedAndServedByTheNextSmallerOneOrTheOriginal()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // 400 × 200: 96 and 320 are made; 1024 is served by 320.
        var medium = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        Assert.Equal([96, 320], medium.GetProperty("sizes").EnumerateArray().Select(static size => size.GetInt32()));
        using (var large = await GetAsync(client, UrlOf(medium, "1024")))
        {
            Assert.Equal("image/webp", large.Content.Headers.ContentType?.MediaType);
            Assert.Equal((320, 160), Dimensions(await large.Content.ReadAsByteArrayAsync()));
        }

        // 64 × 40: none is made, so every size serves the original.
        var small = Halves(SKEncodedImageFormat.Png, 64, 40);
        var tiny = await StoreAsync(client, small);
        Assert.Empty(tiny.GetProperty("sizes").EnumerateArray());
        foreach (var size in new[] { "96", "320", "1024" })
        {
            using var served = await GetAsync(client, UrlOf(tiny, size));
            Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
            Assert.Equal(small, await served.Content.ReadAsByteArrayAsync());
        }

        // A size that is not one of the three is not a thumbnail.
        using var other = await GetAsync(client, $"{UrlOf(tiny, "original")}/200");
        await SetupApi.ProblemAsync(other, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task ThumbnailsApplyTheExifOrientationKeepTransparencyAndAreSrgb()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // Stored 400 × 200, red left and blue right; orientation 6 displays it turned clockwise: 200 × 400, red on top.
        var rotated = await StoreAsync(client, WithExifOrientation(Halves(SKEncodedImageFormat.Jpeg, 400, 200), 6));
        Assert.Equal(200, rotated.GetProperty("width").GetInt32());
        Assert.Equal(400, rotated.GetProperty("height").GetInt32());
        using (var thumbnail = await GetAsync(client, UrlOf(rotated, "320")))
        {
            var bytes = await thumbnail.Content.ReadAsByteArrayAsync();
            Assert.Equal((160, 320), Dimensions(bytes));
            Assert.True(Near(Pixel(bytes, 80, 60), Red), "The top is the stored left half.");
            Assert.True(Near(Pixel(bytes, 80, 260), Blue), "The bottom is the stored right half.");
        }

        var transparent = await StoreAsync(client, HalfTransparentPng(400, 200));
        using (var thumbnail = await GetAsync(client, UrlOf(transparent, "320")))
        {
            var bytes = await thumbnail.Content.ReadAsByteArrayAsync();
            Assert.True(Pixel(bytes, 40, 80).Alpha < 16, "The transparent half stays transparent.");
            Assert.True(Near(Pixel(bytes, 280, 80), Blue), "The opaque half stays blue.");
        }

        // A colour given in Display P3 is converted: in sRGB the same colour has more red.
        var p3 = await StoreAsync(client, DisplayP3Png(400, 200, new SKColor(200, 100, 100)));
        using (var thumbnail = await GetAsync(client, UrlOf(p3, "96")))
        {
            var pixel = Pixel(await thumbnail.Content.ReadAsByteArrayAsync(), 48, 24);
            Assert.True(pixel.Red >= 208, $"Red should be converted up from 200, was {pixel.Red}.");
        }
    }

    [Fact]
    public async Task AnimatedImagesAreAcceptedAndShownAsTheirFirstFrame()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var png = AnimatedPng(Solid(SKEncodedImageFormat.Png, 200, 100, Red), Solid(SKEncodedImageFormat.Png, 200, 100, Blue));
        var webp = AnimatedWebp(Solid(SKEncodedImageFormat.Webp, 200, 100, Red), Solid(SKEncodedImageFormat.Webp, 200, 100, Blue), 200, 100);
        foreach (var (animation, mediaType) in new[] { (png, "image/png"), (webp, "image/webp") })
        {
            var asset = await StoreAsync(client, animation);
            Assert.Equal(mediaType, asset.GetProperty("mediaType").GetString());

            using var thumbnail = await GetAsync(client, UrlOf(asset, "96"));
            Assert.True(Near(Pixel(await thumbnail.Content.ReadAsByteArrayAsync(), 48, 24), Red), $"{mediaType}: the first frame is shown.");

            // The original is the animation itself, unchanged.
            using var original = await GetAsync(client, UrlOf(asset, "original"));
            Assert.Equal(animation, await original.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task ARenamedNonImageIsRefusedWhateverItsNameOrDeclaredTypeAndLeavesNothing()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var content in new[]
        {
            "This is a text file, not a picture."u8.ToArray(),
            "GIF89a\u0001\0\u0001\0\0\0\0;"u8.ToArray(),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(),
        })
        {
            using var response = await UploadAsync(client, content, "cover.jpg", "image/jpeg");
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "artwork_type_not_supported");
            Assert.Contains("not a JPEG, PNG, or WebP image", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }

        AssertNothingStored(factory);
    }

    [Fact]
    public async Task AnOverSizeFileIsRefusedAndLeavesNothing()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // A PNG signature, then one byte over 25 × 1,024 × 1,024.
        var content = new byte[ArtworkRules.MaximumBytes + 1];
        Halves(SKEncodedImageFormat.Png, 10, 10).CopyTo(content, 0);
        using var response = await UploadAsync(client, content);
        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "artwork_too_large");
        Assert.Equal(ArtworkRules.MaximumBytes, problem.GetProperty("maximumBytes").GetInt64());

        AssertNothingStored(factory);
    }

    [Fact]
    public async Task AnImageOfExactlyTheLimitsIsNotRefusedForThem()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // 12,000 pixels on a side is allowed (a strip, so the test stays small).
        var strip = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, ArtworkRules.MaximumSide, 2));
        Assert.Equal(ArtworkRules.MaximumSide, strip.GetProperty("width").GetInt32());
    }

    /// <summary>
    /// The 100-megapixel cap (#305), just under: a real 12,000 × 8,333 image (99,996,000 pixels) is
    /// stored with its thumbnails.
    /// </summary>
    [Fact]
    public async Task AnImageJustUnderOneHundredMegapixelsIsStored()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var asset = await StoreAsync(client, BlankPng(12_000, 8_333));
        Assert.Equal((12_000, 8_333), (asset.GetProperty("width").GetInt32(), asset.GetProperty("height").GetInt32()));
        Assert.Equal([96, 320, 1024], asset.GetProperty("sizes").EnumerateArray().Select(static size => size.GetInt32()));
        Assert.Equal(1, AssetRows(factory));
    }

    /// <summary>
    /// The 100-megapixel cap (#305), just over: a real, whole 12,000 × 8,334 image (100,008,000
    /// pixels), within 12,000 on a side and within the decode memory cap, is refused with 422 and a
    /// clear message, and leaves no row and no file.
    /// </summary>
    [Fact]
    public async Task AnImageJustOverOneHundredMegapixelsIsRefusedAndLeavesNothing()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await UploadAsync(client, BlankPng(12_000, 8_334));
        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "artwork_dimensions_exceeded");
        Assert.Equal((12_000, 8_334), (problem.GetProperty("width").GetInt32(), problem.GetProperty("height").GetInt32()));
        Assert.Equal(ArtworkRules.MaximumPixels, problem.GetProperty("maximumPixels").GetInt64());
        Assert.Equal(
            "The image is 12,000 × 8,334 pixels; artwork can be at most 100 megapixels (100,000,000 pixels).",
            problem.GetProperty("title").GetString());

        AssertNothingStored(factory);
    }

    [Fact]
    public async Task AnOverDimensionImageIsRefusedFromItsHeaderAndLeavesNothing()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // A real image one pixel too wide.
        using (var wide = await UploadAsync(client, Halves(SKEncodedImageFormat.Png, ArtworkRules.MaximumSide + 1, 2)))
        {
            var problem = await SetupApi.ProblemAsync(wide, HttpStatusCode.UnprocessableEntity, "artwork_dimensions_exceeded");
            Assert.Equal(12_001, problem.GetProperty("width").GetInt32());
            Assert.Equal(2, problem.GetProperty("height").GetInt32());
            Assert.Equal(ArtworkRules.MaximumSide, problem.GetProperty("maximumSide").GetInt32());
            Assert.Contains("at most 12,000 pixels on a side", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }

        // Headers alone, with almost no pixel data: refused before any decoding is tried.
        using (var tall = await UploadAsync(client, PngHeaderClaiming(100, 50_000)))
        {
            await SetupApi.ProblemAsync(tall, HttpStatusCode.UnprocessableEntity, "artwork_dimensions_exceeded");
        }

        // Within 12,000 a side, but over 100 megapixels (#305): refused from the header too.
        using (var huge = await UploadAsync(client, PngHeaderClaiming(12_000, 12_000)))
        {
            var problem = await SetupApi.ProblemAsync(huge, HttpStatusCode.UnprocessableEntity, "artwork_dimensions_exceeded");
            Assert.Contains("at most 100 megapixels", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }

        AssertNothingStored(factory);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public async Task ATruncatedImageIsRefusedAndLeavesNothing(SKEncodedImageFormat format)
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var image = Halves(format, 600, 400, quality: 90);

        using var response = await UploadAsync(client, image[..(image.Length / 2)]);
        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "artwork_undecodable");
        Assert.Contains("cannot be read", problem.GetProperty("title").GetString(), StringComparison.Ordinal);

        AssertNothingStored(factory);
    }

    [Fact]
    public async Task ContentThatOnlyStartsLikeAnImageIsRefusedAsUndecodable()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await UploadAsync(client, [0xFF, 0xD8, 0xFF, .. "not really a JPEG at all"u8.ToArray()], "cover.jpg", "image/jpeg");
        await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "artwork_undecodable");

        AssertNothingStored(factory);
    }

    [Fact]
    public async Task ARequestWithoutAFileIsABadRequest()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var json = await SessionApi.SendAsync(client, HttpMethod.Post, Artwork);
        await SetupApi.ProblemAsync(json, HttpStatusCode.BadRequest, "invalid_request");

        using var form = new MultipartFormDataContent { { new StringContent("no file here"), "note" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, Artwork) { Content = form };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        using var fieldOnly = await client.SendAsync(request);
        await SetupApi.ProblemAsync(fieldOnly, HttpStatusCode.BadRequest, "invalid_request");

        AssertNothingStored(factory);
    }

    [Fact]
    public async Task ArtworkIsServedOnlyToASessionOrACatalogReadTokenAndUploadedOnlyWithArtworkWrite()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.ArtworkWrite);

        // Without a session or token: 401, for the original and a thumbnail alike.
        using var anonymous = factory.CreateClient();
        foreach (var key in new[] { "original", "320" })
        {
            using var refused = await GetAsync(anonymous, UrlOf(asset, key));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // catalog.read reads; every other scope is refused, artwork.write included.
        using (var read = await GetAsync(anonymous, UrlOf(asset, "320"), reader))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var refused = await GetAsync(anonymous, UrlOf(asset, "original"), token);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        // artwork.write uploads; every other scope is refused, catalog.read included.
        var image = Halves(SKEncodedImageFormat.Jpeg, 300, 300);
        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.ArtworkWrite))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var refused = await UploadAsync(anonymous, image, token: token);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.ArtworkWrite, problem.GetProperty("requiredScope").GetString());
        }

        using (var unsigned = await UploadAsync(anonymous, image, token: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        }

        await StoreAsync(anonymous, image, writer);
    }

    [Fact]
    public async Task AnUnknownAssetIsNotFound()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await GetAsync(client, $"/api/v1/artwork/{Guid.CreateVersion7()}/96");
        await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task ServedArtworkIsCachedPrivatelyAndAnswersAConditionalRequestWithNotModified()
    {
        using var factory = Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));

        using var first = await GetAsync(client, UrlOf(asset, "96"));
        var cache = first.Headers.CacheControl ?? throw new InvalidOperationException("No Cache-Control.");
        Assert.True(cache.Private);
        Assert.Equal(TimeSpan.FromDays(365), cache.MaxAge);
        Assert.Contains(cache.Extensions, static extension => extension.Name == "immutable");
        var tag = first.Headers.ETag ?? throw new InvalidOperationException("No ETag.");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(UrlOf(asset, "96"), UriKind.Relative));
        request.Headers.IfNoneMatch.Add(tag);
        using var second = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    private static void AssertNothingStored(N8TracksApiFactory factory)
    {
        Assert.Equal(0, AssetRows(factory));
        Assert.Empty(StoredFiles(factory));
    }
}
