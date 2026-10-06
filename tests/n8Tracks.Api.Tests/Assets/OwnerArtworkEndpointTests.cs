using System.Net;
using System.Text;
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
/// Albums', Playlists', and Artists' own artwork (#100): each owner's PATCH takes
/// <c>artworkAssetId</c> and <c>artworkCrop</c> under its revision, as a Song's does; each answer and
/// list item carries the owner's own artwork (never borrowed from its Songs); replaced or removed
/// artwork goes into retention; and each owner's attachment is independent of every other, even
/// when they share one uploaded asset. Changing artwork needs <c>artwork.write</c> as well as
/// <c>collections.write</c>.
/// </summary>
public sealed class OwnerArtworkEndpointTests
{
    /// <summary>The owner types this story adds, by the path segment their API lives under.</summary>
    public static TheoryData<string> Owners => ["albums", "playlists", "artists"];

    [Theory]
    [MemberData(nameof(Owners))]
    public async Task AnOwnersArtworkIsAttachedReplacedCroppedAndRemovedUnderItsRevision(string owner)
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, owner, "Night Drive");
        var other = await CreateAsync(client, owner, "No Cover");
        var first = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        var secondImage = Halves(SKEncodedImageFormat.Jpeg, 300, 300);
        var second = await StoreAsync(client, secondImage);

        var attached = await EditAsync(client, owner, id, 1, AttachJson(first));

        Assert.Equal(2, attached.GetProperty("revision").GetInt32());
        Assert.Equal("Night Drive", NameOf(attached, owner));
        var artwork = attached.GetProperty("artwork");
        Assert.Equal(IdOf(first), artwork.GetProperty("assetId").GetGuid());
        Assert.Equal(400, artwork.GetProperty("width").GetInt32());
        Assert.Equal(JsonValueKind.Null, artwork.GetProperty("crop").ValueKind);
        Assert.Equal(UrlOf(first, "96"), artwork.GetProperty("squareUrls").GetProperty("96").GetString());
        Assert.Equal(IdOf(first), (await ReadAsync(client, owner, id)).GetProperty("artwork").GetProperty("assetId").GetGuid());

        // The list carries each owner's own artwork, or null.
        var listed = await ListAsync(client, owner);
        Assert.Equal(IdOf(first), listed[id].GetProperty("artwork").GetProperty("assetId").GetGuid());
        Assert.Equal(UrlOf(first, "96"), listed[id].GetProperty("artwork").GetProperty("squareUrls").GetProperty("96").GetString());
        Assert.Equal(JsonValueKind.Null, listed[other].GetProperty("artwork").ValueKind);

        // A crop of the right half: square thumbnails of its own, and a new revision.
        var cropped = await EditAsync(client, owner, id, 2, """{"artworkCrop":{"x":200,"y":0,"size":200}}""");
        Assert.Equal(3, cropped.GetProperty("revision").GetInt32());
        Assert.Equal(200, cropped.GetProperty("artwork").GetProperty("crop").GetProperty("x").GetInt32());
        var cropUrl = cropped.GetProperty("artwork").GetProperty("squareUrls").GetProperty("96").GetString()!;
        Assert.Contains("/crops/", cropUrl, StringComparison.Ordinal);
        using (var square = await client.GetAsync(new Uri(cropUrl, UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, square.StatusCode);
        }

        Assert.Empty(await ListGroupsAsync(factory));

        // Replacing retains the old attachment, labelled with the owner, and resets the crop.
        var replaced = await EditAsync(client, owner, id, 3, AttachJson(second));
        Assert.Equal(4, replaced.GetProperty("revision").GetInt32());
        Assert.Equal(IdOf(second), replaced.GetProperty("artwork").GetProperty("assetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("artwork").GetProperty("crop").ValueKind);
        var group = Assert.Single(await ListGroupsAsync(factory));
        Assert.Equal(RetainedRecordTypes.ArtworkAttachment, group.Kind);
        Assert.Equal($"Artwork of the {Noun(owner)} Night Drive", group.Label);
        Assert.NotEmpty(group.Files);

        // The same asset again is no change.
        var same = await EditAsync(client, owner, id, 4, AttachJson(second));
        Assert.Equal(4, same.GetProperty("revision").GetInt32());

        // Removing retains it too; removing again changes nothing.
        var removed = await EditAsync(client, owner, id, 4, """{"artworkAssetId":null}""");
        Assert.Equal(5, removed.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("artwork").ValueKind);
        var again = await EditAsync(client, owner, id, 5, """{"artworkAssetId":null}""");
        Assert.Equal(5, again.GetProperty("revision").GetInt32());
        var groups = await ListGroupsAsync(factory);
        Assert.Equal(2, groups.Count);

        // A retained attachment comes back with its group: the owner shows it again.
        var removal = Assert.Single(groups, candidate => candidate.Files.Any(file => file.Contains(Hash(secondImage), StringComparison.Ordinal)));
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RetentionApi.RestoreAsync(factory, removal.Id));
        Assert.Equal(IdOf(second), (await ReadAsync(client, owner, id)).GetProperty("artwork").GetProperty("assetId").GetGuid());
    }

    [Theory]
    [MemberData(nameof(Owners))]
    public async Task ArtworkCanBeSetWithOtherFieldsInOneEdit(string owner)
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, owner, "Before");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        var name = owner == "artists" ? "name" : "title";

        var edited = await EditAsync(client, owner, id, 1, $$$"""{"{{{name}}}":"After","artworkAssetId":"{{{IdOf(asset)}}}","artworkCrop":{"x":0,"y":0,"size":200}}""");

        Assert.Equal(2, edited.GetProperty("revision").GetInt32());
        Assert.Equal("After", NameOf(edited, owner));
        Assert.Equal(0, edited.GetProperty("artwork").GetProperty("crop").GetProperty("x").GetInt32());
        Assert.Equal(200, edited.GetProperty("artwork").GetProperty("crop").GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task EachOwnersArtworkIsIndependentEvenWhenTheyShareOneAsset()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Track One");
        var songId = song.GetProperty("id").GetString()!;
        var album = await CreateAsync(client, "albums", "Shared Sleeve");
        var playlist = await CreateAsync(client, "playlists", "Shared Sleeve Mix");
        var artist = await CreateAsync(client, "artists", "Shared Sleeve Band");
        using (var track = await SendAsync(client, HttpMethod.Post, new Uri($"/api/v1/albums/{album}/tracks", UriKind.Relative), 1, JsonSerializer.Serialize(new { songId })))
        {
            Assert.Equal(HttpStatusCode.OK, track.StatusCode);
        }

        var sharedImage = Halves(SKEncodedImageFormat.Png, 400, 200);
        var shared = await StoreAsync(client, sharedImage);
        var songOnly = await StoreAsync(client, Halves(SKEncodedImageFormat.Jpeg, 200, 200));
        var albumOnly = await StoreAsync(client, Halves(SKEncodedImageFormat.Webp, 200, 200));
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);

        // The Song has artwork; its Album shows none of its own (nothing is borrowed).
        await SongApi.EditAsync(client, songId, 1, AttachJson(songOnly));
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(client, "albums", album)).GetProperty("artwork").ValueKind);

        // One asset attached to four owners.
        await SongApi.EditAsync(client, songId, 2, AttachJson(shared));
        await EditAsync(client, "albums", album, 2, AttachJson(shared));
        await EditAsync(client, "playlists", playlist, 1, AttachJson(shared));
        await EditAsync(client, "artists", artist, 1, AttachJson(shared));
        Assert.Equal(4, TestDatabase.Rows(factory.DataPath, "SELECT id FROM artwork_attachments;").Count);

        // Changing the Song's artwork leaves the Album's alone, and the reverse.
        await SongApi.EditAsync(client, songId, 3, """{"artworkCrop":{"x":200,"y":0,"size":200}}""");
        await SongApi.EditAsync(client, songId, 4, AttachJson(songOnly));
        var albumNow = await ReadAsync(client, "albums", album);
        Assert.Equal(IdOf(shared), albumNow.GetProperty("artwork").GetProperty("assetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, albumNow.GetProperty("artwork").GetProperty("crop").ValueKind);
        Assert.Equal(3, albumNow.GetProperty("revision").GetInt32());

        await EditAsync(client, "albums", album, 3, AttachJson(albumOnly));
        using (var songRead = await client.GetAsync(SongApi.Song(songId)))
        {
            Assert.Equal(IdOf(songOnly), (await SetupApi.JsonAsync(songRead)).GetProperty("artwork").GetProperty("assetId").GetGuid());
        }

        Assert.Equal(IdOf(shared), (await ReadAsync(client, "playlists", playlist)).GetProperty("artwork").GetProperty("assetId").GetGuid());
        Assert.Equal(IdOf(shared), (await ReadAsync(client, "artists", artist)).GetProperty("artwork").GetProperty("assetId").GetGuid());

        // Each replaced attachment is retained on its own; the shared asset's files outlive every
        // group, because the Playlist and the Artist still show it.
        Assert.Equal(3, (await ListGroupsAsync(factory)).Count);
        clock.Advance(RetentionService.RetentionPeriod + TimeSpan.FromDays(1));
        var pruned = await RetentionApi.PruneAsync(factory);
        Assert.Equal(3, pruned.GroupsPruned);
        var hash = Hash(sharedImage);
        var sharedFiles = StoredFiles(factory).Where(file => file.Contains(hash, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(sharedFiles);
        Assert.Equal(new ArtworkSweepSummary(0, 0), await SweepAsync(factory));
        using var served = await GetAsync(client, UrlOf(shared, "original"), reader);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(sharedImage, await served.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(Owners))]
    public async Task ChangingArtworkNeedsArtworkWriteAsWellAsCollectionsWrite(string owner)
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, owner, "Scoped");
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        var collectionsOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CollectionsWrite);
        var artworkOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.ArtworkWrite);
        var both = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CollectionsWrite, CredentialScopes.ArtworkWrite);

        foreach (var sent in new[] { AttachJson(asset), """{"artworkAssetId":null}""", """{"artworkCrop":null}""" })
        {
            using var lacking = await SendAsTokenAsync(client, OwnerUri(owner, id), collectionsOnly, sent, 1);
            var problem = await SetupApi.ProblemAsync(lacking, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.ArtworkWrite, problem.GetProperty("requiredScope").GetString());
        }

        using (var noCollections = await SendAsTokenAsync(client, OwnerUri(owner, id), artworkOnly, AttachJson(asset), 1))
        {
            var problem = await SetupApi.ProblemAsync(noCollections, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CollectionsWrite, problem.GetProperty("requiredScope").GetString());
        }

        await AssertUnchangedAsync(client, owner, id);

        // An edit that leaves the artwork alone still needs only collections.write.
        var name = owner == "artists" ? "name" : "title";
        using (var plain = await SendAsTokenAsync(client, OwnerUri(owner, id), collectionsOnly, $$"""{"{{name}}":"Scoped Again"}""", 1))
        {
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        }

        using var attached = await SendAsTokenAsync(client, OwnerUri(owner, id), both, AttachJson(asset), 2);
        Assert.Equal(HttpStatusCode.OK, attached.StatusCode);
        Assert.Equal(IdOf(asset), (await SetupApi.JsonAsync(attached)).GetProperty("artwork").GetProperty("assetId").GetGuid());
    }

    [Theory]
    [MemberData(nameof(Owners))]
    public async Task WrongArtworkIsRefusedAndChangesNothing(string owner)
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, owner, "Refused");
        var name = owner == "artists" ? "name" : "title";

        var refusals = new (string Json, string Field, string Message)[]
        {
            ($$"""{"{{name}}":"Changed","artworkAssetId":"not-an-id"}""", ArtworkAttachmentService.AssetIdField, "There is no such artwork"),
            ($$"""{"{{name}}":"Changed","artworkAssetId":"01990000-0000-7000-8000-000000000000"}""", ArtworkAttachmentService.AssetIdField, "There is no such artwork"),
            ($$"""{"{{name}}":"Changed","artworkAssetId":42}""", ArtworkAttachmentService.AssetIdField, "Send text or null."),
            ($$"""{"{{name}}":"Changed","artworkCrop":"centre"}""", ArtworkAttachmentService.CropField, "Send the crop"),
            ($$$"""{"{{{name}}}":"Changed","artworkCrop":{"x":0,"y":0,"size":100}}""", ArtworkAttachmentService.CropField, ArtworkAttachmentService.NothingToCropMessage),
        };
        foreach (var (json, field, message) in refusals)
        {
            using var response = await SendAsync(client, HttpMethod.Patch, OwnerUri(owner, id), 1, json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.StartsWith(message, problem.GetProperty("errors").GetProperty(field)[0].GetString(), StringComparison.Ordinal);
        }

        // A crop that does not fit the image sent with it.
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 400, 200));
        using (var outside = await SendAsync(client, HttpMethod.Patch, OwnerUri(owner, id), 1, $$$"""{"artworkAssetId":"{{{IdOf(asset)}}}","artworkCrop":{"x":300,"y":0,"size":200}}"""))
        {
            var problem = await SetupApi.ProblemAsync(outside, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(ArtworkAttachmentService.CropField, out _));
        }

        // An upload left unattached for a day is no longer live.
        clock.Advance(ArtworkService.UnattachedLifetime);
        using (var stale = await SendAsync(client, HttpMethod.Patch, OwnerUri(owner, id), 1, AttachJson(asset)))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        await AssertUnchangedAsync(client, owner, id, "Refused");
        Assert.Empty(TestDatabase.Rows(factory.DataPath, "SELECT id FROM artwork_attachments;"));
    }

    [Theory]
    [MemberData(nameof(Owners))]
    public async Task AStaleRevisionChangesNoArtworkAndRetainsNothing(string owner)
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, owner, "Stale Sleeve");
        var first = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 200, 100));
        var second = await StoreAsync(client, Halves(SKEncodedImageFormat.Jpeg, 200, 100));
        await EditAsync(client, owner, id, 1, AttachJson(first));

        using var response = await SendAsync(client, HttpMethod.Patch, OwnerUri(owner, id), 1, AttachJson(second));

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "revision_conflict");
        Assert.Equal(IdOf(first), problem.GetProperty("current").GetProperty("artwork").GetProperty("assetId").GetGuid());
        Assert.Empty(await ListGroupsAsync(factory));
    }

    [Fact]
    public async Task AnArtistIsCreatedWithoutArtworkWhateverItsCreateSends()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var asset = await StoreAsync(client, Halves(SKEncodedImageFormat.Png, 200, 100));

        using var created = await SendAsync(client, HttpMethod.Post, new Uri("/api/v1/artists", UriKind.Relative), null, $$"""{"name":"Fresh","artworkAssetId":"{{IdOf(asset)}}"}""");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await SetupApi.JsonAsync(created)).GetProperty("artwork").ValueKind);
        Assert.Empty(TestDatabase.Rows(factory.DataPath, "SELECT id FROM artwork_attachments;"));
    }

    private static string Noun(string owner) => owner switch
    {
        "albums" => "Album",
        "playlists" => "Playlist",
        _ => "Artist",
    };

    private static string NameOf(JsonElement record, string owner) =>
        record.GetProperty(owner == "artists" ? "name" : "title").GetString()!;

    private static Uri OwnerUri(string owner, string id) => new($"/api/v1/{owner}/{id}", UriKind.Relative);

    private static string AttachJson(JsonElement asset) => $$"""{"artworkAssetId":"{{IdOf(asset)}}"}""";

    /// <summary>Creates an owner named <paramref name="name"/> (an Artist's name, otherwise a title); its ID.</summary>
    private static async Task<string> CreateAsync(HttpClient client, string owner, string name)
    {
        var json = owner == "artists" ? JsonSerializer.Serialize(new { name }) : JsonSerializer.Serialize(new { title = name });
        using var created = await SendAsync(client, HttpMethod.Post, new Uri($"/api/v1/{owner}", UriKind.Relative), null, json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await SetupApi.JsonAsync(created)).GetProperty("id").GetString()!;
    }

    /// <summary>PATCHes the owner at <paramref name="revision"/> and asserts 200; the answer.</summary>
    private static async Task<JsonElement> EditAsync(HttpClient client, string owner, string id, int revision, string json)
    {
        using var response = await SendAsync(client, HttpMethod.Patch, OwnerUri(owner, id), revision, json);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string owner, string id)
    {
        using var response = await client.GetAsync(OwnerUri(owner, id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>The first page of the owner's list, by ID.</summary>
    private static async Task<Dictionary<string, JsonElement>> ListAsync(HttpClient client, string owner)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/{owner}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray().ToDictionary(static item => item.GetProperty("id").GetString()!);
    }

    /// <summary>Asserts the owner is still at revision 1 with <paramref name="name"/> (when given) and no artwork.</summary>
    private static async Task AssertUnchangedAsync(HttpClient client, string owner, string id, string? name = null)
    {
        var now = await ReadAsync(client, owner, id);
        Assert.Equal(1, now.GetProperty("revision").GetInt32());
        if (name is not null)
        {
            Assert.Equal(name, NameOf(now, owner));
        }

        Assert.Equal(JsonValueKind.Null, now.GetProperty("artwork").ValueKind);
    }

    private static Task<IReadOnlyList<RetentionGroup>> ListGroupsAsync(N8TracksApiFactory factory) =>
        RetentionApi.WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, int? revision, string json)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (revision is { } value)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsTokenAsync(HttpClient client, Uri uri, string token, string json, int revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }
}
