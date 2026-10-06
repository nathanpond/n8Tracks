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
using SkiaSharp;
using static n8Tracks.Api.Tests.Assets.ArtworkApi;
using static n8Tracks.Api.Tests.Assets.ArtworkImages;

namespace n8Tracks.Api.Tests.Assets;

/// <summary>
/// A Song's own artwork (#98): an uploaded asset is attached by the Song's PATCH
/// (<c>artworkAssetId</c>) under its revision, shown in every Song answer, replaced or removed the
/// same way, and the attachment it had goes into retention with the asset's files, which the prune
/// removes after the retention period unless something live still attaches the asset. Attaching
/// needs <c>artwork.write</c> as well as <c>songs.write</c>.
/// </summary>
public sealed class SongArtworkEndpointTests
{
    [Fact]
    public async Task AnUploadedImageBecomesTheSongsArtworkInEveryAnswerAndStaysServedPastTheUnattachedDay()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Harbour Lights");
        var other = await SongApi.CreateAsync(client, "No Cover");
        Assert.Equal(JsonValueKind.Null, song.GetProperty("artwork").ValueKind);
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));

        var edited = await SongApi.EditAsync(client, IdText(song), 1, $$"""{"artworkAssetId":"{{IdOf(asset)}}"}""");

        Assert.Equal(2, edited.GetProperty("revision").GetInt32());
        var artwork = edited.GetProperty("artwork");
        Assert.Equal(IdOf(asset), artwork.GetProperty("assetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, artwork.GetProperty("crop").ValueKind);
        foreach (var key in new[] { "original", "96", "320", "1024" })
        {
            Assert.Equal(UrlOf(asset, key), artwork.GetProperty("urls").GetProperty(key).GetString());
        }

        using (var read = await client.GetAsync(SongApi.Song(song.GetProperty("shortcode").GetString()!)))
        {
            Assert.Equal(IdOf(asset), (await SetupApi.JsonAsync(read)).GetProperty("artwork").GetProperty("assetId").GetGuid());
        }

        var listed = (await SongApi.ListAsync(client)).GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetGuid());
        Assert.Equal(IdOf(asset), listed[song.GetProperty("id").GetGuid()].GetProperty("artwork").GetProperty("assetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, listed[other.GetProperty("id").GetGuid()].GetProperty("artwork").ValueKind);

        // Attached, so it is live: the sweep keeps it after the unattached day, and it is served.
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(new ArtworkSweepSummary(0, 0), await SweepAsync(factory));
        using var served = await client.GetAsync(new Uri(UrlOf(asset, "320"), UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ReplacingPutsTheOldAttachmentIntoRetentionAndThePruneRemovesItsFilesAfterThePeriod()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Tidewater");
        var firstImage = Halves(SKEncodedImageFormat.Png, 400, 200);
        var first = await StoreAsync(client, firstImage);
        var second = await StoreAsync(client, Halves(SKEncodedImageFormat.Jpeg, 300, 300));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(first));
        var firstFiles = FilesOf(factory, firstImage);
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        Assert.Equal(3, firstFiles.Count);

        var replaced = await SongApi.EditAsync(client, IdText(song), 2, AttachJson(second));

        Assert.Equal(3, replaced.GetProperty("revision").GetInt32());
        Assert.Equal(IdOf(second), replaced.GetProperty("artwork").GetProperty("assetId").GetGuid());
        var group = Assert.Single(await ListGroupsAsync(factory));
        Assert.Equal(RetainedRecordTypes.ArtworkAttachment, group.Kind);
        Assert.Equal($"Artwork of {song.GetProperty("shortcode").GetString()}", group.Label);
        Assert.Null(group.Shortcode);
        Assert.Equal(firstFiles.Order(StringComparer.Ordinal), group.Files.Order(StringComparer.Ordinal));
        Assert.Equal(RetainedRecordTypes.ArtworkAttachment, Assert.Single(group.Records).RecordType);
        Assert.Equal(
            [IdOf(second).ToString().ToUpperInvariant()],
            TestDatabase.Rows(factory.DataPath, "SELECT upper(asset_id) FROM artwork_attachments;"));

        // Past the unattached day the replaced asset is kept, because the group lists its files.
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(new ArtworkSweepSummary(0, 0), await SweepAsync(factory));
        Assert.All(firstFiles, file => Assert.True(File.Exists(FullPath(factory, file)), file));

        // After the retention period the prune removes its files, and the next sweep its row.
        clock.Advance(RetentionService.RetentionPeriod);
        var pruned = await RetentionApi.PruneAsync(factory);
        Assert.Equal(1, pruned.GroupsPruned);
        Assert.All(firstFiles, file => Assert.False(File.Exists(FullPath(factory, file)), file));
        Assert.Equal(new ArtworkSweepSummary(1, 0), await SweepAsync(factory));
        Assert.Equal(1, AssetRows(factory));
        // (Sessions expire over the test clock's month; a token does not.)
        using var served = await GetAsync(client, UrlOf(second, "96"), reader);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    public async Task AnAssetStillAttachedToAnotherSongIsNeverPruned()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var first = await SongApi.CreateAsync(client, "Shared Sleeve");
        var second = await SongApi.CreateAsync(client, "Shared Sleeve, Reprise");
        var sharedImage = Halves(SKEncodedImageFormat.Png, 400, 200);
        var shared = await StoreAsync(client, sharedImage);
        await SongApi.EditAsync(client, IdText(first), 1, AttachJson(shared));
        await SongApi.EditAsync(client, IdText(second), 1, AttachJson(shared));
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);

        var removed = await SongApi.EditAsync(client, IdText(first), 2, """{"artworkAssetId":null}""");
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("artwork").ValueKind);
        Assert.Single(await ListGroupsAsync(factory));

        clock.Advance(RetentionService.RetentionPeriod + TimeSpan.FromDays(1));
        var pruned = await RetentionApi.PruneAsync(factory);
        Assert.Equal(1, pruned.GroupsPruned);
        Assert.All(FilesOf(factory, sharedImage), file => Assert.True(File.Exists(FullPath(factory, file)), file));
        Assert.Equal(new ArtworkSweepSummary(0, 0), await SweepAsync(factory));
        using var served = await GetAsync(client, UrlOf(shared, "original"), reader);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(sharedImage, await served.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RemovingRetainsTheAttachmentAndRestoringTheGroupPutsItBack()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Lanterns");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Webp, 200, 200));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));

        var removed = await SongApi.EditAsync(client, IdText(song), 2, """{"artworkAssetId":null}""");

        Assert.Equal(3, removed.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("artwork").ValueKind);
        Assert.Empty(TestDatabase.Rows(factory.DataPath, "SELECT id FROM artwork_attachments;"));
        var group = Assert.Single(await ListGroupsAsync(factory));

        // Removing it again changes nothing: no new revision, no new group.
        var again = await SongApi.EditAsync(client, IdText(song), 3, """{"artworkAssetId":null}""");
        Assert.Equal(3, again.GetProperty("revision").GetInt32());
        Assert.Single(await ListGroupsAsync(factory));

        Assert.IsType<RetentionRestoreOutcome.Restored>(await RetentionApi.RestoreAsync(factory, group.Id));
        using var read = await client.GetAsync(SongApi.Song(IdText(song)));
        Assert.Equal(IdOf(asset), (await SetupApi.JsonAsync(read)).GetProperty("artwork").GetProperty("assetId").GetGuid());
    }

    [Fact]
    public async Task TheSameArtworkAgainChangesNothing()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Same Again");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 200, 100));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));

        var again = await SongApi.EditAsync(client, IdText(song), 2, AttachJson(asset));

        Assert.Equal(2, again.GetProperty("revision").GetInt32());
        Assert.Empty(await ListGroupsAsync(factory));
    }

    [Theory]
    [InlineData("\"not-an-id\"", "There is no such artwork")]
    [InlineData("\"01990000-0000-7000-8000-000000000000\"", "There is no such artwork")]
    [InlineData("42", "Send text or null.")]
    public async Task AnAssetThatIsNotThereIsRefusedAndChangesNothing(string sent, string message)
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Refused");

        using var response = await SongApi.PatchAsync(client, IdText(song), SongApi.Quoted(1), $$"""{"title":"Changed","artworkAssetId":{{sent}}}""");

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.StartsWith(message, problem.GetProperty("errors").GetProperty("artworkAssetId")[0].GetString(), StringComparison.Ordinal);
        await AssertUnchangedAsync(client, song);
    }

    [Fact]
    public async Task AnUploadSweptForBeingUnattachedCannotBeAttached()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Too Late");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 200, 100));

        // A day unattached: no longer live, even before the sweep removes it.
        clock.Advance(ArtworkService.UnattachedLifetime);
        using (var stale = await SongApi.PatchAsync(client, IdText(song), SongApi.Quoted(1), AttachJson(asset)))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        Assert.Equal(new ArtworkSweepSummary(1, 0), await SweepAsync(factory));
        using (var swept = await SongApi.PatchAsync(client, IdText(song), SongApi.Quoted(1), AttachJson(asset)))
        {
            await SetupApi.ProblemAsync(swept, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        await AssertUnchangedAsync(client, song);
    }

    [Fact]
    public async Task AStaleRevisionChangesNoArtworkAndRetainsNothing()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Stale Sleeve");
        var first = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 200, 100));
        var second = await StoreAsync(client, Halves(SKEncodedImageFormat.Jpeg, 200, 100));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(first));

        using var response = await SongApi.PatchAsync(client, IdText(song), SongApi.Quoted(1), AttachJson(second));

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "revision_conflict");
        Assert.Equal(IdOf(first), problem.GetProperty("current").GetProperty("artwork").GetProperty("assetId").GetGuid());
        Assert.Empty(await ListGroupsAsync(factory));
    }

    [Fact]
    public async Task ChangingArtworkNeedsArtworkWriteAsWellAsSongsWrite()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Scoped");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 200, 100));
        var songsOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        var artworkOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.ArtworkWrite);
        var both = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite, CredentialScopes.ArtworkWrite);

        foreach (var sent in new[] { AttachJson(asset), """{"artworkAssetId":null}""" })
        {
            using var lacking = await PatchWithTokenAsync(client, song, 1, sent, songsOnly);
            var problem = await SetupApi.ProblemAsync(lacking, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.ArtworkWrite, problem.GetProperty("requiredScope").GetString());
        }

        using (var noSongs = await PatchWithTokenAsync(client, song, 1, AttachJson(asset), artworkOnly))
        {
            var problem = await SetupApi.ProblemAsync(noSongs, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
        }

        await AssertUnchangedAsync(client, song);

        // A Song edit that leaves the artwork alone still needs only songs.write.
        using (var titleOnly = await PatchWithTokenAsync(client, song, 1, """{"title":"Scoped Again"}""", songsOnly))
        {
            Assert.Equal(HttpStatusCode.OK, titleOnly.StatusCode);
        }

        using var attached = await PatchWithTokenAsync(client, song, 2, AttachJson(asset), both);
        Assert.Equal(HttpStatusCode.OK, attached.StatusCode);
        Assert.Equal(IdOf(asset), (await SetupApi.JsonAsync(attached)).GetProperty("artwork").GetProperty("assetId").GetGuid());
    }

    [Fact]
    public async Task AttachedArtworkUrlsNeedASessionOrAToken()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Private Sleeve");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 200, 100));
        var edited = await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));
        using var anonymous = factory.CreateClient();

        foreach (var key in new[] { "original", "96", "320", "1024" })
        {
            var url = edited.GetProperty("artwork").GetProperty("urls").GetProperty(key).GetString()!;
            using var refused = await anonymous.GetAsync(new Uri(url, UriKind.Relative));
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Unauthorized, "not_authenticated");
        }
    }

    [Fact]
    public async Task TheDatabaseHoldsOneAttachmentPerOwnerAndNeverLosesAnAttachedAssetsRow()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Constraints");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 200, 100));
        await SongApi.EditAsync(client, IdText(song), 1, AttachJson(asset));
        var owner = song.GetProperty("id").GetGuid().ToString().ToUpperInvariant();
        var assetId = IdOf(asset).ToString().ToUpperInvariant();

        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO artwork_attachments (id, owner_type, owner_id, asset_id, attached_utc) VALUES ('{Guid.CreateVersion7().ToString().ToUpperInvariant()}', 'song', '{owner}', '{assetId}', '2026-10-06T00:00:00.000Z');"));
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO artwork_attachments (id, owner_type, owner_id, asset_id, attached_utc) VALUES ('{Guid.CreateVersion7().ToString().ToUpperInvariant()}', 'version', '{owner}', '{assetId}', '2026-10-06T00:00:00.000Z');"));
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"UPDATE artwork_attachments SET crop_x = 0;"));
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"DELETE FROM assets WHERE upper(id) = '{assetId}';"));
        Assert.Equal(1, AssetRows(factory));
    }

    private static string IdText(JsonElement song) => song.GetProperty("id").GetString()!;

    private static string AttachJson(JsonElement asset) => $$"""{"artworkAssetId":"{{IdOf(asset)}}"}""";

    /// <summary>The asset's files as stored (its folder is named by the hash of <paramref name="image"/>).</summary>
    private static List<string> FilesOf(N8TracksApiFactory factory, byte[] image)
    {
        var hash = Hash(image);
        return [.. StoredFiles(factory).Where(file => file.Contains(hash, StringComparison.Ordinal))];
    }

    private static Task<IReadOnlyList<RetentionGroup>> ListGroupsAsync(N8TracksApiFactory factory) =>
        RetentionApi.WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None));

    /// <summary>Asserts the Song is still at revision 1 with its title and no artwork.</summary>
    private static async Task AssertUnchangedAsync(HttpClient client, JsonElement song)
    {
        using var read = await client.GetAsync(SongApi.Song(IdText(song)));
        var now = await SetupApi.JsonAsync(read);
        Assert.Equal(1, now.GetProperty("revision").GetInt32());
        Assert.Equal(song.GetProperty("title").GetString(), now.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, now.GetProperty("artwork").ValueKind);
    }

    /// <summary>PATCHes the Song with <paramref name="token"/> as a Bearer credential, at <paramref name="revision"/>.</summary>
    private static async Task<HttpResponseMessage> PatchWithTokenAsync(HttpClient client, JsonElement song, int revision, string json, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, SongApi.Song(IdText(song)))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", string.Create(CultureInfo.InvariantCulture, $"\"{revision}\"")));
        return await client.SendAsync(request);
    }
}
