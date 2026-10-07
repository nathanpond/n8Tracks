using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Assets;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Retention;
using n8Tracks.Infrastructure.Retention;
using SkiaSharp;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Generations;

/// <summary>
/// Each Generation's cover image and its use as Song artwork (#121). <c>PUT /api/v1/generations/{reference}/artwork</c>
/// stores a Generation's image (validated as any artwork upload); a Song without artwork of its own
/// shows its Selected Generation's image, worked out when it is read; <c>POST
/// /api/v1/songs/{reference}/artwork/from-generation</c> makes a Generation's image the Song's own,
/// independent of the Generation.
/// </summary>
public sealed class GenerationArtworkEndpointTests
{
    private static readonly byte[] Red = ArtworkImages.Solid(SKEncodedImageFormat.Png, 640, 480, ArtworkImages.Red);
    private static readonly byte[] Blue = ArtworkImages.Solid(SKEncodedImageFormat.Png, 500, 500, ArtworkImages.Blue);
    private static readonly byte[] Green = ArtworkImages.Solid(SKEncodedImageFormat.Jpeg, 400, 300, new SKColor(0, 160, 0));

    [Fact]
    public async Task AGenerationsImageIsTheSongsArtworkUntilTheUserPicksOneAndRemovingItGoesBackToTheDefault()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);

        // 1. An image for g1: the Generation shows it whole; its revision stays, the Song's updated time moves.
        var before = await GenerationAsync(client, "n8-1-v1-g1");
        var songBefore = await SongAsync(client);
        clock.Advance(TimeSpan.FromMinutes(3));
        var g1 = await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK);
        var red = g1.GetProperty("artwork");
        Assert.Equal(640, red.GetProperty("width").GetInt32());
        Assert.Equal(480, red.GetProperty("height").GetInt32());
        Assert.Equal(JsonValueKind.Null, red.GetProperty("crop").ValueKind);
        Assert.Equal(before.GetProperty("revision").GetInt32(), g1.GetProperty("revision").GetInt32());
        var songAfterUpload = await SongAsync(client);
        Assert.Equal(songBefore.GetProperty("revision").GetInt32(), songAfterUpload.GetProperty("revision").GetInt32());
        Assert.Equal(clock.GetUtcNow().UtcDateTime, songAfterUpload.GetProperty("updatedAt").GetDateTime());
        Assert.Equal(red.GetProperty("assetId").GetString(), (await ListedAsync(client, "n8-1-v1-g1")).GetProperty("artwork").GetProperty("assetId").GetString());
        await AssertOriginalAsync(client, red, Red);

        // With no Selected Generation the Song shows its newest Generation's image (#318), stored nowhere.
        AssertShows(songAfterUpload, red, "newestGeneration");

        // Select g1: the Song shows its image, centred and uncropped, in its answer and in the list.
        var selected = await SelectAsync(client, "n8-1-v1-g1");
        AssertShows(selected, red, "selectedGeneration");
        Assert.Equal(JsonValueKind.Null, selected.GetProperty("artwork").GetProperty("crop").ValueKind);
        AssertShows((await SongApi.ListAsync(client)).GetProperty("items")[0], red, "selectedGeneration");
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM artwork_attachments;"));

        // 2. Pick g2's image in the artwork control: the Song's own, and its revision rises.
        var blue = (await UploadAsync(client, "n8-1-v1-g2", Blue, HttpStatusCode.OK)).GetProperty("artwork");
        var picked = await PickAsync(client, selected.GetProperty("revision").GetInt32(), "n8-1-v1-g2", HttpStatusCode.OK);
        AssertShows(picked, blue, "own");
        Assert.Equal(selected.GetProperty("revision").GetInt32() + 1, picked.GetProperty("revision").GetInt32());

        // 3. Select another Generation, then clear the selection: the Song's own artwork stays.
        var reselected = await SelectAsync(client, "n8-1-v2-g1");
        AssertShows(reselected, blue, "own");
        var cleared = await ClearAsync(client);
        AssertShows(cleared, blue, "own");

        // 4. Remove the Song's own artwork: it shows its Selected Generation's again (while cleared, its
        // newest Generation's with an image, g2's, #318).
        var removed = await SongApi.EditAsync(client, "n8-1", cleared.GetProperty("revision").GetInt32(), """{"artworkAssetId":null}""");
        AssertShows(removed, blue, "newestGeneration");
        AssertShows(await SelectAsync(client, "n8-1-v1-g1"), red, "selectedGeneration");

        // A Selected Generation without an image gives nothing.
        Assert.Equal(JsonValueKind.Null, (await SelectAsync(client, "n8-1-v2-g1")).GetProperty("artwork").ValueKind);
    }

    [Fact]
    public async Task ADefaultIsShownWhateverTheSelectedGenerationsState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);
        var red = (await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK)).GetProperty("artwork");
        await SelectAsync(client, "n8-1-v1-g1");

        var generation = await GenerationAsync(client, "n8-1-v1-g1");
        using (var archived = await SendAsync(client, HttpMethod.Patch, "generations/n8-1-v1-g1", SongApi.Quoted(generation.GetProperty("revision").GetInt32()), """{"state":"archived"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        }

        TestDatabase.Execute(factory.DataPath, "UPDATE generations SET remote_state = 'trashed';");
        AssertShows(await SongAsync(client), red, "selectedGeneration");
    }

    [Fact]
    public async Task WithItsOwnArtworkChangingAndClearingTheSelectionLeavesTheSongsArtworkByteIdentical()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);
        await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK);
        await UploadAsync(client, "n8-1-v1-g2", Blue, HttpStatusCode.OK);
        var own = await ArtworkApi.StoreAsync(client, Green);
        var song = await SongAsync(client);
        var cropped = await SongApi.EditAsync(
            client,
            "n8-1",
            song.GetProperty("revision").GetInt32(),
            $$"""{"artworkAssetId":"{{ArtworkApi.IdOf(own)}}","artworkCrop":{"x":10,"y":20,"size":200} }""");
        var artwork = cropped.GetProperty("artwork").GetRawText();
        var attachments = AttachmentRows(factory);

        foreach (var generation in new[] { "n8-1-v1-g1", "n8-1-v1-g2", "n8-1-v2-g1" })
        {
            Assert.Equal(artwork, (await SelectAsync(client, generation)).GetProperty("artwork").GetRawText());
            Assert.Equal(attachments, AttachmentRows(factory));
        }

        Assert.Equal(artwork, (await ClearAsync(client)).GetProperty("artwork").GetRawText());
        Assert.Equal(attachments, AttachmentRows(factory));
        Assert.Contains("\"source\":\"own\"", artwork, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickingReplacesTheSongsArtworkUnderTheReplacementRuleAndResetsTheCrop()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);
        var red = (await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK)).GetProperty("artwork");
        var own = await ArtworkApi.StoreAsync(client, Green);
        var song = await SongApi.EditAsync(
            client,
            "n8-1",
            (await SongAsync(client)).GetProperty("revision").GetInt32(),
            $$"""{"artworkAssetId":"{{ArtworkApi.IdOf(own)}}","artworkCrop":{"x":0,"y":0,"size":300} }""");

        var picked = await PickAsync(client, song.GetProperty("revision").GetInt32(), "n8-1-v1-g1", HttpStatusCode.OK);
        AssertShows(picked, red, "own");
        Assert.Equal(JsonValueKind.Null, picked.GetProperty("artwork").GetProperty("crop").ValueKind);

        // The artwork it had is retained, as a replacement's is.
        Assert.Equal(["artwork-attachment"], TestDatabase.Rows(factory.DataPath, "SELECT kind FROM retention_groups;"));

        // Picking the image the Song already shows as its own, uncropped, changes nothing.
        var again = await PickAsync(client, picked.GetProperty("revision").GetInt32(), "n8-1-v1-g1", HttpStatusCode.OK);
        Assert.Equal(picked.GetProperty("revision").GetInt32(), again.GetProperty("revision").GetInt32());

        // Cropped, then picked again: the crop is reset and the revision rises.
        var recropped = await SongApi.EditAsync(client, "n8-1", again.GetProperty("revision").GetInt32(), """{"artworkCrop":{"x":0,"y":0,"size":400}}""");
        var reset = await PickAsync(client, recropped.GetProperty("revision").GetInt32(), "n8-1-v1-g1", HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, reset.GetProperty("artwork").GetProperty("crop").ValueKind);
        Assert.Equal(recropped.GetProperty("revision").GetInt32() + 1, reset.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task ThePickedCopyIsIndependentOfTheGenerationsImageWhichIsReplacedInPlace()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);
        var red = (await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK)).GetProperty("artwork");
        var picked = await PickAsync(client, (await SongAsync(client)).GetProperty("revision").GetInt32(), "n8-1-v1-g1", HttpStatusCode.OK);

        // The Generation's image is replaced: the Song's copy is unchanged, byte for byte.
        var blue = (await UploadAsync(client, "n8-1-v1-g1", Blue, HttpStatusCode.OK)).GetProperty("artwork");
        Assert.NotEqual(red.GetProperty("assetId").GetString(), blue.GetProperty("assetId").GetString());
        var song = await SongAsync(client);
        Assert.Equal(picked.GetProperty("artwork").GetRawText(), song.GetProperty("artwork").GetRawText());
        await AssertOriginalAsync(client, song.GetProperty("artwork"), Red);

        // An image nothing else uses leaves the store at once when it is replaced (it is not retained).
        var green = (await UploadAsync(client, "n8-1-v1-g1", Green, HttpStatusCode.OK)).GetProperty("artwork");
        Assert.DoesNotContain(ArtworkApi.StoredFiles(factory), file => file.Contains(ArtworkApi.Hash(Blue), StringComparison.Ordinal));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM assets WHERE id = '{Upper(blue.GetProperty("assetId").GetGuid())}';"));
        using (var gone = await ArtworkApi.GetAsync(client, blue.GetProperty("urls").GetProperty("original").GetString()!))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        // Complement: the image the Song uses is kept, and so is the new one.
        Assert.Contains(ArtworkApi.StoredFiles(factory), file => file.Contains(ArtworkApi.Hash(Red), StringComparison.Ordinal));
        await AssertOriginalAsync(client, green, Green);
        Assert.Equal(green.GetProperty("assetId").GetString(), (await GenerationAsync(client, "n8-1-v1-g1")).GetProperty("artwork").GetProperty("assetId").GetString());
        Assert.Empty(TestDatabase.Rows(factory.DataPath, "SELECT id FROM retention_groups;"));
    }

    [Fact]
    public async Task TheSongsCoverSurvivesTheGenerationItCameFromBeingDeletedAndARestoreBringsTheImageBack()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);
        var red = (await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK)).GetProperty("artwork");
        var blue = (await UploadAsync(client, "n8-1-v1-g2", Blue, HttpStatusCode.OK)).GetProperty("artwork");
        await PickAsync(client, (await SongAsync(client)).GetProperty("revision").GetInt32(), "n8-1-v1-g1", HttpStatusCode.OK);

        // Version 1, with both Generations, is deleted (a Generation's own deletion is #124's).
        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v1", SongApi.Quoted(version.GetProperty("revision").GetInt32())))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        var song = await SongAsync(client);
        AssertShows(song, red, "own");
        await AssertOriginalAsync(client, song.GetProperty("artwork"), Red);

        // The group lists the Generations' images, so the sweep keeps them and a restore finds them.
        var group = await WithServiceAsync(factory, service => service.FindByShortcodeAsync("n8-1-v1", CancellationToken.None));
        Assert.Contains(group!.Files, file => file.Contains(ArtworkApi.Hash(Blue), StringComparison.Ordinal));
        Assert.Contains(group.Files, file => file.Contains(ArtworkApi.Hash(Red), StringComparison.Ordinal));
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        Assert.Empty(restored.Notes);
        Assert.Equal(blue.GetProperty("assetId").GetString(), (await GenerationAsync(client, "n8-1-v1-g2")).GetProperty("artwork").GetProperty("assetId").GetString());
    }

    [Fact]
    public async Task AGenerationWhoseImageWasRemovedMeanwhileIsRestoredWithoutItWithANote()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);
        await UploadAsync(client, "n8-1-v1-g2", Blue, HttpStatusCode.OK);
        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v1", SongApi.Quoted(version.GetProperty("revision").GetInt32())))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        // As if the asset were gone all the same (the group keeps it in practice).
        TestDatabase.Execute(factory.DataPath, "DELETE FROM assets;");
        var group = await WithServiceAsync(factory, service => service.FindByShortcodeAsync("n8-1-v1", CancellationToken.None));
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group!.Id));

        Assert.Contains(restored.Notes, static note => note.Contains("image was no longer stored", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, (await GenerationAsync(client, "n8-1-v1-g2")).GetProperty("artwork").ValueKind);
    }

    [Fact]
    public void AGenerationRetainedBeforeItsImageRestoresWithNone()
    {
        var shape3 = new JsonObject { ["id"] = "A", ["rating"] = 4 };

        var shape4 = RetainedTypes.GenerationShape3To4(shape3);

        Assert.True(shape4.ContainsKey("artwork_asset_id"));
        Assert.Null(shape4["artwork_asset_id"]);
        Assert.True(RetainedTypes.Generation.Upgraders.ContainsKey(3));
    }

    [Fact]
    public async Task AnUploadThatIsNotAcceptedArtworkIsRefusedAsAnyArtworkIsAndStoresNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);

        using (var text = await UploadRawAsync(client, "n8-1-v1-g1", "not an image at all"u8.ToArray(), "cover.jpg", "image/jpeg"))
        {
            await SetupApi.ProblemAsync(text, HttpStatusCode.UnsupportedMediaType, ArtworkEndpoints.TypeNotSupportedCode);
        }

        using (var truncated = await UploadRawAsync(client, "n8-1-v1-g1", Red[..(Red.Length / 2)]))
        {
            await SetupApi.ProblemAsync(truncated, HttpStatusCode.UnprocessableEntity, ArtworkEndpoints.UndecodableCode);
        }

        using (var noFile = await SendAsync(client, HttpMethod.Put, "generations/n8-1-v1-g1/artwork", ifMatch: null, json: "{}"))
        {
            await SetupApi.ProblemAsync(noFile, HttpStatusCode.BadRequest, ApiProblem.InvalidRequestCode);
        }

        Assert.Equal(0, ArtworkApi.AssetRows(factory));
        Assert.Empty(ArtworkApi.StoredFiles(factory));
        Assert.Equal(JsonValueKind.Null, (await GenerationAsync(client, "n8-1-v1-g1")).GetProperty("artwork").ValueKind);

        // An unknown Generation is 404, before anything is stored.
        using (var unknown = await UploadRawAsync(client, "n8-1-v1-g9", Red))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal(0, ArtworkApi.AssetRows(factory));

        // Complement: a real image is accepted.
        await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK);
        Assert.Equal(1, ArtworkApi.AssetRows(factory));
    }

    [Fact]
    public async Task PickingIsRefusedForAnotherSongsGenerationOneWithNoImageAVanishedImageOrAStaleRevision()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);
        await SongApi.CreateAsync(client, "Other");
        await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal("other-1"));
        await UploadAsync(client, "n8-2-v1-g1", Blue, HttpStatusCode.OK);
        var red = (await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK)).GetProperty("artwork");
        var revision = (await SongAsync(client)).GetProperty("revision").GetInt32();
        var snapshot = AttachmentRows(factory);

        using (var other = await SendAsync(client, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(revision), Body("n8-2-v1-g1")))
        {
            var problem = await SetupApi.ProblemAsync(other, HttpStatusCode.UnprocessableEntity, "generation_not_in_song");
            Assert.Equal("n8-2-v1-g1", problem.GetProperty("shortcode").GetString());
        }

        using (var none = await SendAsync(client, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(revision), Body("n8-1-v1-g2")))
        {
            await SetupApi.ProblemAsync(none, HttpStatusCode.UnprocessableEntity, "generation_has_no_artwork");
        }

        using (var unknown = await SendAsync(client, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(revision), Body("n8-1-v1-g9")))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using (var unnamed = await SendAsync(client, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(revision), "{}"))
        {
            var problem = await SetupApi.ProblemAsync(unnamed, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("generation", out _));
        }

        using (var noSong = await SendAsync(client, HttpMethod.Post, "songs/n8-9/artwork/from-generation", SongApi.Quoted(revision), Body("n8-1-v1-g1")))
        {
            await SetupApi.ProblemAsync(noSong, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using (var stale = await SendAsync(client, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(revision + 5), Body("n8-1-v1-g1")))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(revision, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var missing = await SendAsync(client, HttpMethod.Post, "songs/n8-1/artwork/from-generation", ifMatch: null, Body("n8-1-v1-g1")))
        {
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        }

        // The original has vanished from the store: 409 artwork_unavailable.
        File.Delete(ArtworkApi.FullPath(factory, $"artwork/{ArtworkApi.Hash(Red)[..2]}/{ArtworkApi.Hash(Red)}/original.png"));
        using (var vanished = await SendAsync(client, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(revision), Body("n8-1-v1-g1")))
        {
            await SetupApi.ProblemAsync(vanished, HttpStatusCode.Conflict, "artwork_unavailable");
        }

        // None of them stored anything.
        Assert.Equal(snapshot, AttachmentRows(factory));
        Assert.Equal(revision, (await SongAsync(client)).GetProperty("revision").GetInt32());
        Assert.NotNull(red.GetProperty("assetId").GetString());
    }

    [Fact]
    public async Task ASyncOrGenerationCredentialMayOnlyGiveAnImageArtworkWriteMayReplaceItAndPickingNeedsArtworkWrite()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithGenerationsAsync(factory, client);
        var sync = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        var generate = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var artworkWrite = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.ArtworkWrite);
        var others = await CredentialApi.CreateTokenAsync(
            factory,
            [.. CredentialScopes.All.Except([CredentialScopes.SunoSync, CredentialScopes.SunoGenerate, CredentialScopes.ArtworkWrite])]);

        // A sync credential gives g1 its first image; the same bytes again change nothing.
        var first = await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK, sync);
        await UploadAsync(client, "n8-1-v1-g1", Red, HttpStatusCode.OK, sync);

        // A different image over it is refused for sync and generation credentials, and nothing is stored.
        foreach (var token in new[] { sync, generate })
        {
            using var refused = await UploadRawAsync(client, "n8-1-v1-g1", Blue, token: token);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "artwork_exists");
            Assert.Equal("n8-1-v1-g1", problem.GetProperty("shortcode").GetString());
        }

        Assert.Equal(1, ArtworkApi.AssetRows(factory));
        Assert.Equal(
            first.GetProperty("artwork").GetProperty("assetId").GetString(),
            (await GenerationAsync(client, "n8-1-v1-g1")).GetProperty("artwork").GetProperty("assetId").GetString());

        // A generation credential gives g2 its first image; artwork.write replaces g1's.
        await UploadAsync(client, "n8-1-v1-g2", Green, HttpStatusCode.OK, generate);
        var replaced = await UploadAsync(client, "n8-1-v1-g1", Blue, HttpStatusCode.OK, artworkWrite);
        Assert.Equal(500, replaced.GetProperty("artwork").GetProperty("width").GetInt32());

        // Every other scope is refused, naming the three that would do.
        using (var lacking = await UploadRawAsync(client, "n8-1-v1-g1", Red, token: others))
        {
            var problem = await SetupApi.ProblemAsync(lacking, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(
                [CredentialScopes.SunoSync, CredentialScopes.SunoGenerate, CredentialScopes.ArtworkWrite],
                problem.GetProperty("requiredScope").EnumerateArray().Select(static scope => scope.GetString()));
        }

        // Picking needs artwork.write; a credential with every other scope is refused.
        var revision = (await SongAsync(client)).GetProperty("revision").GetInt32();
        var withoutArtwork = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Except([CredentialScopes.ArtworkWrite])]);
        using (var refused = await TokenSendAsync(client, withoutArtwork, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(revision), Body("n8-1-v1-g1")))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(CredentialScopes.ArtworkWrite, problem.GetProperty("requiredScope").GetString());
        }

        using (var picked = await TokenSendAsync(client, artworkWrite, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(revision), Body("n8-1-v1-g1")))
        {
            Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
            Assert.Equal("own", (await SetupApi.JsonAsync(picked)).GetProperty("artwork").GetProperty("source").GetString());
        }
    }

    [Fact]
    public async Task UnsignedCallersCannotUploadOrPick()
    {
        using var factory = SongApi.Host();
        using (var signedIn = await SessionApi.SignedInClientAsync(factory))
        {
            await SongWithGenerationsAsync(factory, signedIn);
        }

        using var anonymous = factory.CreateClient();
        using (var upload = await UploadRawAsync(anonymous, "n8-1-v1-g1", Red))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        }

        using (var pick = await SendAsync(anonymous, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(1), Body("n8-1-v1-g1")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, pick.StatusCode);
        }
    }

    /// <summary>Song n8-1 ("Covers") with Version 1 (Generations g1, g2) and Version 2 (g1), none with an image.</summary>
    private static async Task SongWithGenerationsAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await SongApi.CreateAsync(client, "Covers");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("cover-1"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("cover-2"));
        using (var branched = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative), """{"sourceVersionId":"n8-1-v1","number":"2"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, branched.StatusCode);
        }

        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", Clips.Minimal("cover-3"));
    }

    private static void AssertShows(JsonElement song, JsonElement image, string source)
    {
        var artwork = song.GetProperty("artwork");
        Assert.Equal(JsonValueKind.Object, artwork.ValueKind);
        Assert.Equal(image.GetProperty("assetId").GetString(), artwork.GetProperty("assetId").GetString());
        Assert.Equal(source, artwork.GetProperty("source").GetString());
        Assert.Equal(image.GetProperty("urls").GetProperty("96").GetString(), artwork.GetProperty("urls").GetProperty("96").GetString());
    }

    private static async Task AssertOriginalAsync(HttpClient client, JsonElement artwork, byte[] expected)
    {
        using var response = await ArtworkApi.GetAsync(client, artwork.GetProperty("urls").GetProperty("original").GetString()!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Every artwork attachment row, every column, in order.</summary>
    private static List<string> AttachmentRows(N8TracksApiFactory factory)
    {
        var columns = TestDatabase.Rows(factory.DataPath, "SELECT name FROM pragma_table_info('artwork_attachments') ORDER BY cid;").Select(static column => $"quote({column})");
        return TestDatabase.Rows(factory.DataPath, $"SELECT {string.Join(" || '|' || ", columns)} FROM artwork_attachments ORDER BY id;");
    }

    private static string Body(string generation) => JsonSerializer.Serialize(new { generation });

    private static async Task<JsonElement> SongAsync(HttpClient client) => await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));

    private static async Task<JsonElement> GenerationAsync(HttpClient client, string reference) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/generations/{reference}", UriKind.Relative)));

    /// <summary>The Generation as the Song's Generation list answers it.</summary>
    private static async Task<JsonElement> ListedAsync(HttpClient client, string shortcode) =>
        (await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/songs/n8-1/generations", UriKind.Relative)))).GetProperty("items").EnumerateArray()
            .Single(generation => generation.GetProperty("shortcode").GetString() == shortcode);

    private static async Task<JsonElement> SelectAsync(HttpClient client, string generation)
    {
        var revision = (await SongAsync(client)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Put, "songs/n8-1/selected-generation", SongApi.Quoted(revision), Body(generation));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> ClearAsync(HttpClient client)
    {
        var revision = (await SongAsync(client)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Delete, "songs/n8-1/selected-generation", SongApi.Quoted(revision));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> PickAsync(HttpClient client, int revision, string generation, HttpStatusCode status)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "songs/n8-1/artwork/from-generation", SongApi.Quoted(revision), Body(generation));
        Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
        var body = await SetupApi.JsonAsync(response);
        Assert.Equal(SongApi.Quoted(body.GetProperty("revision").GetInt32()), response.Headers.ETag?.Tag);
        return body;
    }

    private static async Task<JsonElement> UploadAsync(HttpClient client, string generation, byte[] content, HttpStatusCode status, string? token = null)
    {
        using var response = await UploadRawAsync(client, generation, content, token: token);
        Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>A <c>PUT</c> of <paramref name="content"/> as the Generation's image: as the browser sends it, or with <paramref name="token"/>.</summary>
    private static async Task<HttpResponseMessage> UploadRawAsync(
        HttpClient client,
        string generation,
        byte[] content,
        string fileName = "cover.png",
        string contentType = "image/png",
        string? token = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/generations/{generation}/artwork", UriKind.Relative)) { Content = form };
        if (token is null)
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? ifMatch, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri("/api/v1/" + path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> TokenSendAsync(HttpClient client, string token, HttpMethod method, string path, string? ifMatch, string? json)
    {
        using var request = new HttpRequestMessage(method, new Uri("/api/v1/" + path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }
}
