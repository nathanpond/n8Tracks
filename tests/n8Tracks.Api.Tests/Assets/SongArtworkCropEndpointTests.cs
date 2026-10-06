using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Retention;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Assets;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Retention;
using n8Tracks.Domain.Assets;
using SkiaSharp;
using static n8Tracks.Api.Tests.Assets.ArtworkApi;
using static n8Tracks.Api.Tests.Assets.ArtworkImages;

namespace n8Tracks.Api.Tests.Assets;

/// <summary>
/// A square crop on a Song's artwork (#99): set, changed, and reset by the Song's PATCH
/// (<c>artworkCrop</c>) under its revision, stored beside the attachment while the original is never
/// changed, and shown through square thumbnails made from the original in the same request, whose
/// URLs change with the crop. A crop outside the image, under the minimum, or not square is refused.
/// Replacing the artwork resets the crop unless one is sent with it.
/// </summary>
public sealed class SongArtworkCropEndpointTests
{
    [Fact]
    public async Task ACropIsSetChangedAndResetAndItsSquareThumbnailsShowTheSelectedRegion()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Left Of Centre");
        var image = Halves(SKEncodedImageFormat.Png, 1200, 600);
        var asset = await StoreAsync(client, image);
        var attached = (await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset))).GetProperty("artwork");
        var original = FullPath(factory, ArtworkPaths.Original(Hash(image), ArtworkFormat.Png));

        // No crop: the whole-image thumbnails, shown as their centred square.
        Assert.Equal(1200, attached.GetProperty("width").GetInt32());
        Assert.Equal(600, attached.GetProperty("height").GetInt32());
        Assert.Equal(JsonValueKind.Null, attached.GetProperty("crop").ValueKind);
        foreach (var key in new[] { "96", "320", "1024" })
        {
            Assert.Equal(UrlOf(asset, key), SquareUrl(attached, key));
        }

        Assert.False(attached.GetProperty("squareUrls").TryGetProperty("original", out _));

        // The left square: all red.
        var left = await SongApi.EditAsync(client, IdText(song), 2, CropJson(0, 0, 600));
        Assert.Equal(3, left.GetProperty("revision").GetInt32());
        var leftArtwork = left.GetProperty("artwork");
        AssertCrop(leftArtwork, 0, 0, 600);
        var leftUrl = SquareUrl(leftArtwork, "320");
        Assert.Contains($"/crops/{ArtworkCropRules.Key(new ArtworkCrop(0, 0, 600))}/320", leftUrl, StringComparison.Ordinal);
        Assert.Equal(UrlOf(asset, "320"), leftArtwork.GetProperty("urls").GetProperty("320").GetString());
        var leftSquare = await ServedAsync(client, leftUrl);
        Assert.Equal((320, 320), Dimensions(leftSquare));
        AssertAll(leftSquare, Red);
        Assert.Equal((96, 96), Dimensions(await ServedAsync(client, SquareUrl(leftArtwork, "96"))));
        Assert.Equal((320, 320), Dimensions(await ServedAsync(client, SquareUrl(leftArtwork, "1024"))));

        // The right square: all blue, at a new URL; the left square's thumbnails are gone.
        var right = await SongApi.EditAsync(client, IdText(song), 3, CropJson(600, 0, 600));
        Assert.Equal(4, right.GetProperty("revision").GetInt32());
        var rightUrl = SquareUrl(right.GetProperty("artwork"), "320");
        Assert.NotEqual(leftUrl, rightUrl);
        AssertAll(await ServedAsync(client, rightUrl), Blue);
        Assert.DoesNotContain(StoredFiles(factory), file => file.Contains(ArtworkCropRules.Key(new ArtworkCrop(0, 0, 600)), StringComparison.Ordinal));
        using (var gone = await client.GetAsync(new Uri(leftUrl, UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, "not_found");
        }

        // The same crop again changes nothing.
        Assert.Equal(4, (await SongApi.EditAsync(client, IdText(song), 4, CropJson(600, 0, 600))).GetProperty("revision").GetInt32());

        // Reset: the centred default, and the whole-image thumbnails again.
        var reset = await SongApi.EditAsync(client, IdText(song), 4, """{"artworkCrop":null}""");
        Assert.Equal(5, reset.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, reset.GetProperty("artwork").GetProperty("crop").ValueKind);
        Assert.Equal(UrlOf(asset, "320"), SquareUrl(reset.GetProperty("artwork"), "320"));
        Assert.DoesNotContain(StoredFiles(factory), ArtworkPaths.IsCropFile);
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM artwork_attachments WHERE crop_x IS NULL AND crop_y IS NULL AND crop_size IS NULL;"));

        // The original was never changed: the same bytes on disk and served.
        Assert.Equal(Hash(image), Hash(await File.ReadAllBytesAsync(original)));
        Assert.Equal(image, await ServedAsync(client, UrlOf(asset, "original")));
    }

    [Fact]
    public async Task TheCropIsStoredBesideTheAttachmentAndShownInEveryAnswer()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Stored Crop");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));

        await SongApi.EditAsync(client, IdText(song), 2, CropJson(10, 20, 150));

        Assert.Equal(["10|20|150"], TestDatabase.Rows(factory.DataPath, "SELECT crop_x || '|' || crop_y || '|' || crop_size FROM artwork_attachments;"));
        using (var read = await client.GetAsync(SongApi.Song(song.GetProperty("shortcode").GetString()!)))
        {
            AssertCrop((await SetupApi.JsonAsync(read)).GetProperty("artwork"), 10, 20, 150);
        }

        var listed = Assert.Single((await SongApi.ListAsync(client)).GetProperty("items").EnumerateArray());
        AssertCrop(listed.GetProperty("artwork"), 10, 20, 150);
        Assert.Equal(400, listed.GetProperty("artwork").GetProperty("width").GetInt32());
    }

    public static TheoryData<string, string> RefusedCrops => new()
    {
        { """{"artworkCrop":{"x":700,"y":0,"size":600}}""", "inside the image" },
        { """{"artworkCrop":{"x":-1,"y":0,"size":200}}""", "inside the image" },
        { """{"artworkCrop":{"x":0,"y":1,"size":600}}""", "inside the image" },
        { """{"artworkCrop":{"x":0,"y":0,"size":601}}""", "inside the image" },
        { """{"artworkCrop":{"x":0,"y":0,"size":63}}""", "at least 64 pixels" },
        { """{"artworkCrop":{"x":0,"y":0,"width":100,"height":200}}""", "square" },
        { """{"artworkCrop":{"x":0,"y":0,"size":100,"width":100,"height":120}}""", "square" },
        { """{"artworkCrop":{"x":0,"y":0,"width":100}}""", "whole numbers" },
        { """{"artworkCrop":{"x":0,"y":0}}""", "whole numbers" },
        { """{"artworkCrop":{"y":0,"size":100}}""", "whole numbers" },
        { """{"artworkCrop":{"x":0.5,"y":0,"size":100}}""", "whole numbers" },
        { """{"artworkCrop":{"x":"0","y":0,"size":100}}""", "whole numbers" },
        { """{"artworkCrop":[0,0,100]}""", "whole numbers" },
        { """{"artworkCrop":"left"}""", "whole numbers" },
    };

    [Theory]
    [MemberData(nameof(RefusedCrops))]
    public async Task ACropOutsideTheImageUnderTheMinimumOrNotSquareIsRefusedAndChangesNothing(string json, string message)
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Out Of Bounds");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 1200, 600));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));

        using var refused = await SongApi.PatchAsync(client, IdText(song), SongApi.Quoted(2), json);

        var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains(message, problem.GetProperty("errors").GetProperty("artworkCrop")[0].GetString(), StringComparison.Ordinal);
        using var read = await client.GetAsync(SongApi.Song(IdText(song)));
        var now = await SetupApi.JsonAsync(read);
        Assert.Equal(2, now.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, now.GetProperty("artwork").GetProperty("crop").ValueKind);
        Assert.DoesNotContain(StoredFiles(factory), ArtworkPaths.IsCropFile);
    }

    [Fact]
    public async Task ASmallOriginalCanOnlyBeCroppedToItsFullShorterSide()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Tiny Sleeve");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 50, 40));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));

        using (var refused = await SongApi.PatchAsync(client, IdText(song), SongApi.Quoted(2), CropJson(0, 0, 30)))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        var cropped = await SongApi.EditAsync(client, IdText(song), 2, CropJson(10, 0, 40));
        var square = await ServedAsync(client, SquareUrl(cropped.GetProperty("artwork"), "96"));
        Assert.Equal((40, 40), Dimensions(square));
    }

    [Fact]
    public async Task ACropNeedsArtworkAndAFreshRevision()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Nothing To Crop");

        using (var refused = await SongApi.PatchAsync(client, IdText(song), SongApi.Quoted(1), CropJson(0, 0, 100)))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal(ArtworkAttachmentService.NothingToCropMessage, problem.GetProperty("errors").GetProperty("artworkCrop")[0].GetString());
        }

        // Resetting a crop that is not there changes nothing.
        Assert.Equal(1, (await SongApi.EditAsync(client, IdText(song), 1, """{"artworkCrop":null}""")).GetProperty("revision").GetInt32());

        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));

        using (var stale = await SongApi.PatchAsync(client, IdText(song), SongApi.Quoted(1), CropJson(0, 0, 100)))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("current").GetProperty("artwork").GetProperty("crop").ValueKind);
        }

        using (var missing = await SongApi.PatchAsync(client, IdText(song), null, CropJson(0, 0, 100)))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        // Removing the artwork with a crop is refused: there would be nothing to crop.
        using (var removing = await SongApi.PatchAsync(client, IdText(song), SongApi.Quoted(2), """{"artworkAssetId":null,"artworkCrop":{"x":0,"y":0,"size":100}}"""))
        {
            await SetupApi.ProblemAsync(removing, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }
    }

    [Fact]
    public async Task ReplacingTheArtworkResetsTheCropUnlessACropIsSentWithIt()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Kept Or Not");
        var first = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 1200, 600));
        var second = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 800, 600));
        var third = await StoreAsync(client, Halves(SKEncodedImageFormat.Jpeg, 1000, 700));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(first));
        await SongApi.EditAsync(client, IdText(song), 2, CropJson(0, 0, 600));

        var replaced = await SongApi.EditAsync(client, IdText(song), 3, AttachJson(second));
        Assert.Equal(IdOf(second), replaced.GetProperty("artwork").GetProperty("assetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("artwork").GetProperty("crop").ValueKind);

        // The first asset's crop thumbnails went with its attachment (it is in retention; they can be made again).
        Assert.DoesNotContain(StoredFiles(factory), ArtworkPaths.IsCropFile);

        await SongApi.EditAsync(client, IdText(song), 4, CropJson(200, 0, 600));
        var kept = await SongApi.EditAsync(
            client,
            IdText(song),
            5,
            $$$"""{"artworkAssetId":"{{{IdOf(third)}}}","artworkCrop":{"x":200,"y":0,"size":600}}""");
        Assert.Equal(6, kept.GetProperty("revision").GetInt32());
        AssertCrop(kept.GetProperty("artwork"), 200, 0, 600);
        Assert.Equal(1000, kept.GetProperty("artwork").GetProperty("width").GetInt32());
        AssertAll(await ServedAsync(client, SquareUrl(kept.GetProperty("artwork"), "320")), x => x < 300 ? Red : Blue, tolerance: 40, margin: 12);

        // A crop that does not fit the new image is refused with it, and nothing changes.
        using var tooBig = await SongApi.PatchAsync(
            client,
            IdText(song),
            SongApi.Quoted(6),
            $$$"""{"artworkAssetId":"{{{IdOf(second)}}}","artworkCrop":{"x":300,"y":0,"size":700}}""");
        await SetupApi.ProblemAsync(tooBig, HttpStatusCode.UnprocessableEntity, "validation_failed");
    }

    [Fact]
    public async Task TheCropIsInTheOrientedOriginal()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Turned");

        // Stored 400 × 200, red left and blue right; orientation 6 turns it a quarter clockwise, so red is on top.
        var asset = await StoreAsync(client, WithExifOrientation(Halves(SKEncodedImageFormat.Jpeg, 400, 200), 6));
        var attached = (await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset))).GetProperty("artwork");
        Assert.Equal((200, 400), (attached.GetProperty("width").GetInt32(), attached.GetProperty("height").GetInt32()));

        var top = await SongApi.EditAsync(client, IdText(song), 2, CropJson(0, 0, 200));
        AssertAll(await ServedAsync(client, SquareUrl(top.GetProperty("artwork"), "96")), Red, tolerance: 40);
        var bottom = await SongApi.EditAsync(client, IdText(song), 3, CropJson(0, 200, 200));
        AssertAll(await ServedAsync(client, SquareUrl(bottom.GetProperty("artwork"), "96")), Blue, tolerance: 40);
    }

    [Fact]
    public async Task AMissingSquareThumbnailIsMadeAgainAndOnlyLiveCropsAreServed()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Remade");
        var image = Halves(SKEncodedImageFormat.Png, 1200, 600);
        var asset = await StoreAsync(client, image);
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));
        var cropped = await SongApi.EditAsync(client, IdText(song), 2, CropJson(0, 0, 600));
        var url = SquareUrl(cropped.GetProperty("artwork"), "320");
        var cropFiles = StoredFiles(factory).Where(ArtworkPaths.IsCropFile).ToList();
        Assert.Equal(2, cropFiles.Count);
        foreach (var file in cropFiles)
        {
            File.Delete(FullPath(factory, file));
        }

        AssertAll(await ServedAsync(client, url), Red);
        Assert.Equal(cropFiles, StoredFiles(factory).Where(ArtworkPaths.IsCropFile).ToList());

        // A crop key no live attachment sets, a malformed one, and a size not offered are 404.
        var hash = asset.GetProperty("id").GetString();
        var other = ArtworkCropRules.Key(new ArtworkCrop(1, 0, 600));
        foreach (var path in new[]
        {
            $"/api/v1/artwork/{hash}/crops/{other}/320",
            $"/api/v1/artwork/{hash}/crops/..%2F..%2Fsecret/320",
            $"/api/v1/artwork/{hash}/crops/XYZ/320",
            url[..^3] + "500",
            $"/api/v1/artwork/{Guid.CreateVersion7()}/crops/{ArtworkCropRules.Key(new ArtworkCrop(0, 0, 600))}/320",
        })
        {
            using var missing = await client.GetAsync(new Uri(path, UriKind.Relative));
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }

        // Signed out: 401, as for the other artwork URLs.
        using var anonymous = factory.CreateClient();
        using var refused = await anonymous.GetAsync(new Uri(url, UriKind.Relative));
        await SetupApi.ProblemAsync(refused, HttpStatusCode.Unauthorized, "not_authenticated");
    }

    [Fact]
    public async Task TheSweepRemovesASquareThumbnailLeftInTheFolderOfAnAssetItRemoves()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Swept Square");
        var image = Halves(SKEncodedImageFormat.Png, 400, 200);
        var asset = await StoreAsync(client, image);
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));
        await SongApi.EditAsync(client, IdText(song), 2, CropJson(0, 0, 200));
        var stray = ArtworkPaths.CropThumbnail(Hash(image), ArtworkCropRules.Key(new ArtworkCrop(5, 0, 200)), 96);
        await File.WriteAllBytesAsync(FullPath(factory, stray), [1, 2, 3]);
        await SongApi.EditAsync(client, IdText(song), 3, """{"artworkAssetId":null}""");

        clock.Advance(RetentionService.RetentionPeriod + TimeSpan.FromDays(1));
        Assert.Equal(1, (await RetentionApi.PruneAsync(factory)).GroupsPruned);
        Assert.Equal(new ArtworkSweepSummary(1, 0), await SweepAsync(factory));

        Assert.Empty(StoredFiles(factory));
        Assert.False(Directory.Exists(Path.GetDirectoryName(FullPath(factory, stray))));
    }

    [Fact]
    public async Task ATokenNeedsArtworkWriteToCrop()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Scoped Crop");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));
        var songsOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        var both = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite, CredentialScopes.ArtworkWrite);

        using (var refused = await PatchWithTokenAsync(client, song, 2, CropJson(0, 0, 200), songsOnly))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.ArtworkWrite, problem.GetProperty("requiredScope").GetString());
        }

        using var cropped = await PatchWithTokenAsync(client, song, 2, CropJson(0, 0, 200), both);
        Assert.Equal(HttpStatusCode.OK, cropped.StatusCode);
        AssertCrop((await SetupApi.JsonAsync(cropped)).GetProperty("artwork"), 0, 0, 200);
    }

    private static string IdText(JsonElement song) => song.GetProperty("id").GetString()!;

    private static string AttachJson(JsonElement asset) => $$"""{"artworkAssetId":"{{IdOf(asset)}}"}""";

    private static string CropJson(int x, int y, int size) =>
        string.Create(CultureInfo.InvariantCulture, $$$"""{"artworkCrop":{"x":{{{x}}},"y":{{{y}}},"size":{{{size}}}}}""");

    private static string SquareUrl(JsonElement artwork, string size) => artwork.GetProperty("squareUrls").GetProperty(size).GetString()!;

    private static void AssertCrop(JsonElement artwork, int x, int y, int size)
    {
        var crop = artwork.GetProperty("crop");
        Assert.Equal((x, y, size), (crop.GetProperty("x").GetInt32(), crop.GetProperty("y").GetInt32(), crop.GetProperty("size").GetInt32()));
    }

    private static async Task<byte[]> ServedAsync(HttpClient client, string url)
    {
        using var served = await client.GetAsync(new Uri(url, UriKind.Relative));
        Assert.True(served.StatusCode == HttpStatusCode.OK, $"{url}: {served.StatusCode}");
        return await served.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Asserts every pixel of the image, sampled on a grid, is near <paramref name="colour"/>.</summary>
    private static void AssertAll(byte[] image, SKColor colour, int tolerance = 24) =>
        AssertAll(image, _ => colour, tolerance, margin: 0);

    /// <summary>
    /// Asserts the image's pixels, sampled on a grid, are near the colour <paramref name="expected"/>
    /// gives for their column in pixels of a 600-pixel square, skipping <paramref name="margin"/> of
    /// them each side of a colour change.
    /// </summary>
    private static void AssertAll(byte[] image, Func<int, SKColor> expected, int tolerance, int margin)
    {
        var (width, height) = Dimensions(image);
        for (var row = 0; row < 8; row++)
        {
            for (var column = 0; column < 8; column++)
            {
                var x = ((column * 2) + 1) * width / 16;
                var y = ((row * 2) + 1) * height / 16;
                var at = x * 600 / width;
                if (margin > 0 && expected(at - margin) != expected(at + margin))
                {
                    continue;
                }

                var actual = Pixel(image, x, y);
                Assert.True(Near(actual, expected(at), tolerance), $"({x}, {y}) is {actual}, not near {expected(at)}");
            }
        }
    }

    private static async Task<HttpResponseMessage> PatchWithTokenAsync(HttpClient client, JsonElement song, int revision, string json, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, SongApi.Song(IdText(song)))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }
}
