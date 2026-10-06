using System.Net;
using System.Text.Json;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Retention;
using SkiaSharp;
using static n8Tracks.Api.Tests.Assets.ArtworkApi;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// Deleting an Artist (#104): <c>DELETE /api/v1/artists/{id}</c>, session only, under the Artist's
/// revision, with its credits reassigned (<c>reassignTo</c>) or removed (<c>removeCredits=true</c>).
/// No Song or Album is deleted; those whose credits change have their revisions raised; a default
/// Artist that is this one is cleared. The Artist, its aliases, links, and artwork, and any removed
/// credits go into one retention group; restoring it puts back the Artist and the removed credits
/// where there is still room, and leaves reassigned credits and the default-Artist setting alone.
/// </summary>
public sealed class ArtistDeletionEndpointTests
{
    private static readonly Uri Catalog = new("/api/v1/settings/catalog", UriKind.Relative);

    [Fact]
    public async Task ReassigningMovesEveryCreditToTheOtherArtistAndASongAlreadyCreditingItKeepsOneCredit()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var old = await ArtistAsync(client, "Old Name");
        var heir = await ArtistAsync(client, "Heir");
        var guest = await ArtistAsync(client, "Guest");
        foreach (var title in (string[])["Primary only", "Featured only", "Both, old primary", "Both, heir primary", "Both featured", "Uncredited"])
        {
            await SongApi.CreateAsync(client, title);
        }

        await CreditAsync(client, "n8-1", old, []);
        await CreditAsync(client, "n8-2", guest, [heir, old]);
        await CreditAsync(client, "n8-3", old, [guest, heir]);
        await CreditAsync(client, "n8-4", heir, [old, guest]);
        await CreditAsync(client, "n8-5", null, [guest, heir, old]);
        var albumId = await AlbumAsync(client, "By Old", old);
        var otherAlbumId = await AlbumAsync(client, "By Guest", guest);
        await SetDefaultAsync(client, old);
        var revision = await PatchArtistAsync(client, old, 1, """{"aliases":["Former Name"],"links":[{"url":"https://example.com/old","label":"Site"}]}""");
        revision = await AttachArtworkAsync(client, old, revision);
        var songCount = SongApi.Shortcodes(await SongApi.ListAsync(client)).Count;
        var revisions = await RevisionsAsync(client, ["n8-1", "n8-2", "n8-3", "n8-4", "n8-5", "n8-6"]);
        var album = await ReadAsync(client, $"/api/v1/albums/{albumId}");
        var otherAlbum = await ReadAsync(client, $"/api/v1/albums/{otherAlbumId}");
        var settings = await ReadAsync(client, Catalog.ToString());

        clock.Advance(TimeSpan.FromMinutes(1));
        using (var deleted = await DeleteAsync(client, old, revision, $"reassignTo={heir}"))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
            Assert.Contains("no-store", deleted.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        }

        // No Song is deleted. Each credit went to Heir: in the same role and place where the Song did
        // not credit Heir, otherwise as one credit in the more senior role, in Heir's place.
        Assert.Equal(songCount, SongApi.Shortcodes(await SongApi.ListAsync(client)).Count);
        Assert.Equal("Heir | -", Credits(await SongAsync(client, "n8-1")));
        Assert.Equal("Guest | Heir", Credits(await SongAsync(client, "n8-2")));
        Assert.Equal("Heir | Guest", Credits(await SongAsync(client, "n8-3")));
        Assert.Equal("Heir | Guest", Credits(await SongAsync(client, "n8-4")));
        Assert.Equal("- | Guest, Heir", Credits(await SongAsync(client, "n8-5")));
        Assert.Equal("- | -", Credits(await SongAsync(client, "n8-6")));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM song_artist_credits WHERE artist_id = '{Upper(old)}';"));
        Assert.Equal("5", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM song_artist_credits WHERE artist_id = '{Upper(heir)}';"));

        // The credited Songs and the Album are at their next revision, at the deletion's time; nothing else changed.
        foreach (var song in (string[])["n8-1", "n8-2", "n8-3", "n8-4", "n8-5"])
        {
            var read = await SongAsync(client, song);
            Assert.Equal(revisions[song] + 1, read.GetProperty("revision").GetInt32());
            Assert.Equal(clock.GetUtcNow().UtcDateTime, read.GetProperty("updatedAt").GetDateTime());
        }

        Assert.Equal(revisions["n8-6"], (await SongAsync(client, "n8-6")).GetProperty("revision").GetInt32());
        var albumAfter = await ReadAsync(client, $"/api/v1/albums/{albumId}");
        Assert.Equal(heir, albumAfter.GetProperty("albumArtist").GetProperty("id").GetGuid());
        Assert.Equal(album.GetProperty("revision").GetInt32() + 1, albumAfter.GetProperty("revision").GetInt32());
        Assert.Equal(otherAlbum.ToString(), (await ReadAsync(client, $"/api/v1/albums/{otherAlbumId}")).ToString());
        var heirAfter = await ReadAsync(client, $"/api/v1/artists/{heir}");
        Assert.Equal(5, heirAfter.GetProperty("songCount").GetInt32());
        Assert.Equal(1, heirAfter.GetProperty("albumCount").GetInt32());

        // It was the default Artist: the default is cleared, at the settings' next revision.
        var settingsAfter = await ReadAsync(client, Catalog.ToString());
        Assert.Equal(JsonValueKind.Null, settingsAfter.GetProperty("defaultArtist").ValueKind);
        Assert.Equal(settings.GetProperty("revision").GetInt32() + 1, settingsAfter.GetProperty("revision").GetInt32());

        // One group: the Artist, its alias and link, and its artwork with the asset's files; the
        // reassigned credits are not in it.
        var group = Assert.Single(await GroupsAsync(factory));
        Assert.Equal(RetainedRecordTypes.Artist, group.Kind);
        Assert.Equal("Artist Old Name", group.Label);
        Assert.Null(group.Shortcode);
        Assert.Equal(Upper(old), group.Records[0].OriginalId);
        Assert.Equal(
            [RetainedRecordTypes.Artist, RetainedRecordTypes.ArtworkAttachment, RetainedRecordTypes.ArtistAlias, RetainedRecordTypes.ArtistLink],
            group.Records.Select(static record => record.RecordType));
        Assert.NotEmpty(group.Files);
        foreach (var table in (string[])["artist_aliases", "artist_links"])
        {
            Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM {table} WHERE artist_id = '{Upper(old)}';"));
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM artwork_attachments WHERE owner_id = '{Upper(old)}';"));

        // No read shows it: the Artist, the list, a search by its alias, the Songs filter.
        using (var read = await client.GetAsync(new Uri($"/api/v1/artists/{old}", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(read, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal(["Guest", "Heir"], Names(await ReadAsync(client, "/api/v1/artists")));
        Assert.Empty(Names(await ReadAsync(client, "/api/v1/artists?search=Former")));
        using (var again = await DeleteAsync(client, old, revision, $"reassignTo={heir}"))
        {
            await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // Restored: the Artist is back with its alias, link, and artwork; the credits stay with Heir;
        // the default is not set again.
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        Assert.Empty(restored.Notes);
        var back = await ReadAsync(client, $"/api/v1/artists/{old}");
        Assert.Equal("Old Name", back.GetProperty("name").GetString());
        Assert.Equal(["Former Name"], back.GetProperty("aliases").EnumerateArray().Select(static alias => alias.GetString()));
        Assert.Equal("https://example.com/old", back.GetProperty("links")[0].GetProperty("url").GetString());
        Assert.Equal(0, back.GetProperty("songCount").GetInt32());
        Assert.Equal(0, back.GetProperty("albumCount").GetInt32());
        using (var served = await GetAsync(client, back.GetProperty("artwork").GetProperty("urls").GetProperty("320").GetString()!))
        {
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        }

        Assert.Equal("Heir | -", Credits(await SongAsync(client, "n8-1")));
        Assert.Equal(heir, (await ReadAsync(client, $"/api/v1/albums/{albumId}")).GetProperty("albumArtist").GetProperty("id").GetGuid());
        Assert.Equal(settingsAfter.ToString(), (await ReadAsync(client, Catalog.ToString())).ToString());
    }

    [Fact]
    public async Task RemovingTakesTheCreditsWithTheArtistAndARestorePutsBackThoseThatStillFit()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var gone = await ArtistAsync(client, "Gone");
        var first = await ArtistAsync(client, "First");
        var last = await ArtistAsync(client, "Last");
        foreach (var title in (string[])["Gone leads", "Gone featured", "Gone leads too", "Deleted later", "Taken over"])
        {
            await SongApi.CreateAsync(client, title);
        }

        await CreditAsync(client, "n8-1", gone, [first]);
        await CreditAsync(client, "n8-2", first, [last, gone]);
        await CreditAsync(client, "n8-3", gone, []);
        await CreditAsync(client, "n8-4", null, [gone]);
        await CreditAsync(client, "n8-5", null, [gone]);
        var keptAlbum = await AlbumAsync(client, "Kept", gone);
        var takenAlbum = await AlbumAsync(client, "Taken", gone);
        var songCount = SongApi.Shortcodes(await SongApi.ListAsync(client)).Count;
        var revisions = await RevisionsAsync(client, ["n8-1", "n8-2", "n8-3", "n8-4", "n8-5"]);
        var albumRevision = (await ReadAsync(client, $"/api/v1/albums/{keptAlbum}")).GetProperty("revision").GetInt32();

        clock.Advance(TimeSpan.FromMinutes(1));
        using (var deleted = await DeleteAsync(client, gone, 1, "removeCredits=true"))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        // Every Song is still there without the credit; a Song it led has no primary Artist.
        Assert.Equal(songCount, SongApi.Shortcodes(await SongApi.ListAsync(client)).Count);
        Assert.Equal("- | First", Credits(await SongAsync(client, "n8-1")));
        Assert.Equal("First | Last", Credits(await SongAsync(client, "n8-2")));
        Assert.Equal("- | -", Credits(await SongAsync(client, "n8-3")));
        foreach (var (song, before) in revisions)
        {
            var read = await SongAsync(client, song);
            Assert.Equal(before + 1, read.GetProperty("revision").GetInt32());
            Assert.Equal(clock.GetUtcNow().UtcDateTime, read.GetProperty("updatedAt").GetDateTime());
        }

        // The Albums stay, without an Album Artist, at their next revision.
        var album = await ReadAsync(client, $"/api/v1/albums/{keptAlbum}");
        Assert.Equal(JsonValueKind.Null, album.GetProperty("albumArtist").ValueKind);
        Assert.Equal(albumRevision + 1, album.GetProperty("revision").GetInt32());
        Assert.Equal(2, (await ReadAsync(client, "/api/v1/albums")).GetProperty("items").GetArrayLength());

        // The group holds the Artist, its five credits, and the two Album Artists it was.
        var group = Assert.Single(await GroupsAsync(factory));
        Assert.Equal(5, group.Records.Count(static record => record.RecordType == RetainedRecordTypes.SongCredit));
        Assert.Equal(2, group.Records.Count(static record => record.RecordType == RetainedRecordTypes.AlbumArtist));
        Assert.Equal(Upper(gone), group.Records[0].OriginalId);
        Assert.Equal(RetainedRecordTypes.AlbumArtist, group.Records[^1].RecordType);

        // Meanwhile: n8-3 gets another primary Artist, n8-4 is deleted, n8-5 gets another featured
        // Artist in the credit's place, and the second Album gets another Album Artist.
        clock.Advance(TimeSpan.FromMinutes(1));
        await CreditAsync(client, "n8-3", last, []);
        await DeleteSongAsync(client, "n8-4");
        await CreditAsync(client, "n8-5", null, [last]);
        var takenRevision = (await ReadAsync(client, $"/api/v1/albums/{takenAlbum}")).GetProperty("revision").GetInt32();
        await PatchAsync(client, $"/api/v1/albums/{takenAlbum}", takenRevision, $$"""{"albumArtistId":"{{first}}"}""");
        var afterChanges = await RevisionsAsync(client, ["n8-1", "n8-2", "n8-3", "n8-5"]);
        albumRevision = (await ReadAsync(client, $"/api/v1/albums/{keptAlbum}")).GetProperty("revision").GetInt32();

        clock.Advance(TimeSpan.FromMinutes(1));
        var restoreGroup = Assert.Single(await GroupsAsync(factory), static candidate => candidate.Kind == RetainedRecordTypes.Artist);
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, restoreGroup.Id));

        // Each credit that does not fit is reported; the rest are back in their roles and places.
        Assert.Equal(4, restored.Notes.Count);
        Assert.Contains("An Artist credit was not restored: the Song has another primary Artist now.", restored.Notes);
        Assert.Contains("An Artist credit was not restored: the Song it belongs to no longer exists.", restored.Notes);
        Assert.Contains("An Artist credit was not restored: another featured Artist holds its place on the Song now.", restored.Notes);
        Assert.Contains("An Album Artist was not restored: the Album has another one now.", restored.Notes);
        Assert.Equal("Gone | First", Credits(await SongAsync(client, "n8-1")));
        Assert.Equal("First | Last, Gone", Credits(await SongAsync(client, "n8-2")));
        Assert.Equal("Last | -", Credits(await SongAsync(client, "n8-3")));
        Assert.Equal("- | Last", Credits(await SongAsync(client, "n8-5")));
        foreach (var song in (string[])["n8-1", "n8-2"])
        {
            var read = await SongAsync(client, song);
            Assert.Equal(afterChanges[song] + 1, read.GetProperty("revision").GetInt32());
            Assert.Equal(clock.GetUtcNow().UtcDateTime, read.GetProperty("updatedAt").GetDateTime());
        }

        Assert.Equal(afterChanges["n8-3"], (await SongAsync(client, "n8-3")).GetProperty("revision").GetInt32());
        Assert.Equal(afterChanges["n8-5"], (await SongAsync(client, "n8-5")).GetProperty("revision").GetInt32());
        album = await ReadAsync(client, $"/api/v1/albums/{keptAlbum}");
        Assert.Equal(gone, album.GetProperty("albumArtist").GetProperty("id").GetGuid());
        Assert.Equal(albumRevision + 1, album.GetProperty("revision").GetInt32());
        Assert.Equal(first, (await ReadAsync(client, $"/api/v1/albums/{takenAlbum}")).GetProperty("albumArtist").GetProperty("id").GetGuid());
        var back = await ReadAsync(client, $"/api/v1/artists/{gone}");
        Assert.Equal(2, back.GetProperty("songCount").GetInt32());
        Assert.Equal(1, back.GetProperty("albumCount").GetInt32());
    }

    [Fact]
    public async Task AnArtistWithCreditsAndNoChoiceIsRefusedWithTheCountsAndWrongChoicesAreRefusedToo()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var artist = await ArtistAsync(client, "Busy");
        var other = await ArtistAsync(client, "Other");
        await SongApi.CreateAsync(client, "One");
        await SongApi.CreateAsync(client, "Two");
        await CreditAsync(client, "n8-1", artist, []);
        await CreditAsync(client, "n8-2", other, [artist]);
        await AlbumAsync(client, "Album", artist);
        await SetDefaultAsync(client, artist);
        var before = Dump(factory, "song_artist_credits");

        using (var inUse = await DeleteAsync(client, artist, 1, query: null))
        {
            var problem = await SetupApi.ProblemAsync(inUse, HttpStatusCode.Conflict, "artist_in_use");
            Assert.Equal(2, problem.GetProperty("songCount").GetInt32());
            Assert.Equal(1, problem.GetProperty("albumCount").GetInt32());
            Assert.True(problem.GetProperty("isDefaultArtist").GetBoolean());
            Assert.Equal(1, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var notRemoved = await DeleteAsync(client, artist, 1, "removeCredits=false"))
        {
            await SetupApi.ProblemAsync(notRemoved, HttpStatusCode.Conflict, "artist_in_use");
        }

        foreach (var (query, field) in (ValueTuple<string, string>[])
        [
            ($"reassignTo={other}&removeCredits=true", "reassignTo"),
            ("reassignTo=someone", "reassignTo"),
            ($"reassignTo={artist}", "reassignTo"),
            ($"reassignTo={Guid.CreateVersion7()}", "reassignTo"),
            ("removeCredits=yes", "removeCredits"),
        ])
        {
            using var refused = await DeleteAsync(client, artist, 1, query);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), query);
        }

        using (var twice = await DeleteAsync(client, artist, 1, "removeCredits=true&removeCredits=true"))
        {
            await SetupApi.ProblemAsync(twice, HttpStatusCode.BadRequest, ApiProblem.InvalidRequestCode);
        }

        using (var missing = await SendAsync(client, HttpMethod.Delete, $"/api/v1/artists/{artist}?removeCredits=true", ifMatch: null))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        using (var stale = await DeleteAsync(client, artist, 7, "removeCredits=true"))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
            Assert.Equal(2, problem.GetProperty("current").GetProperty("songCount").GetInt32());
        }

        using (var unknown = await DeleteAsync(client, Guid.CreateVersion7(), 1, "removeCredits=true"))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // A token never deletes an Artist, whatever its scopes.
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        using (var bearer = await CredentialApi.SendAsync(client, HttpMethod.Delete, new Uri($"/api/v1/artists/{artist}?removeCredits=true", UriKind.Relative), token))
        {
            await SetupApi.ProblemAsync(bearer, HttpStatusCode.Forbidden, "session_required");
        }

        // Nothing changed.
        Assert.Equal(before, Dump(factory, "song_artist_credits"));
        Assert.Empty(await GroupsAsync(factory));
        Assert.Equal(artist, (await ReadAsync(client, Catalog.ToString())).GetProperty("defaultArtist").GetProperty("id").GetGuid());
        Assert.Equal(1, (await ReadAsync(client, $"/api/v1/artists/{artist}")).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AnUncreditedArtistNeedsNoChoiceAndADefaultArtistIsCleared()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var lonely = await ArtistAsync(client, "Lonely");
        var kept = await ArtistAsync(client, "Kept");
        await SongApi.CreateAsync(client, "Before the default");
        await SetDefaultAsync(client, lonely);

        // A Song created now would be credited to it; this one is credited to Kept instead.
        await CreditAsync(client, "n8-1", kept, []);

        using (var deleted = await DeleteAsync(client, lonely, 1, query: null))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Equal(JsonValueKind.Null, (await ReadAsync(client, Catalog.ToString())).GetProperty("defaultArtist").ValueKind);
        Assert.Equal("- | -", Credits(await SongApi.CreateAsync(client, "After the deletion")));
        Assert.Equal("Kept | -", Credits(await SongAsync(client, "n8-1")));
        var group = Assert.Single(await GroupsAsync(factory));
        Assert.Equal([RetainedRecordTypes.Artist], group.Records.Select(static record => record.RecordType));

        // An Artist nothing credits may also be deleted with either choice sent.
        var spare = await ArtistAsync(client, "Spare");
        using var withChoice = await DeleteAsync(client, spare, 1, $"reassignTo={kept}");
        Assert.Equal(HttpStatusCode.NoContent, withChoice.StatusCode);
        Assert.Equal(["Kept"], Names(await ReadAsync(client, "/api/v1/artists")));
    }

    /// <summary>"primary | featured, featured", with "-" for none.</summary>
    private static string Credits(JsonElement song)
    {
        var credits = song.GetProperty("credits");
        var primary = credits.GetProperty("primary");
        var featured = credits.GetProperty("featured").EnumerateArray().Select(static artist => artist.GetProperty("name").GetString()).ToList();
        return $"{(primary.ValueKind == JsonValueKind.Null ? "-" : primary.GetProperty("name").GetString())} | {(featured.Count == 0 ? "-" : string.Join(", ", featured))}";
    }

    private static List<string?> Names(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("name").GetString())];

    private static async Task<Guid> ArtistAsync(HttpClient client, string name)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/artists", UriKind.Relative), JsonSerializer.Serialize(new { name, confirmDuplicate = true }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Sets the Song's credits under its revision as it is now.</summary>
    private static async Task CreditAsync(HttpClient client, string reference, Guid? primary, Guid[] featured)
    {
        var revision = (await SongAsync(client, reference)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(
            client,
            HttpMethod.Put,
            $"/api/v1/songs/{reference}/credits",
            SongApi.Quoted(revision),
            JsonSerializer.Serialize(new { primaryArtistId = primary, featuredArtistIds = featured }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Creates an Album with <paramref name="artist"/> as its Album Artist; its ID.</summary>
    private static async Task<Guid> AlbumAsync(HttpClient client, string title, Guid artist)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/albums", UriKind.Relative), JsonSerializer.Serialize(new { title }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var id = (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
        await PatchAsync(client, $"/api/v1/albums/{id}", 1, $$"""{"albumArtistId":"{{artist}}"}""");
        return id;
    }

    private static async Task SetDefaultAsync(HttpClient client, Guid artist)
    {
        var revision = (await ReadAsync(client, Catalog.ToString())).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Put, Catalog.ToString(), SongApi.Quoted(revision), $$"""{"defaultArtistId":"{{artist}}"}""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static Task<int> PatchArtistAsync(HttpClient client, Guid artist, int revision, string json) =>
        PatchAsync(client, $"/api/v1/artists/{artist}", revision, json);

    /// <summary>Uploads an image and makes it the Artist's own artwork; its revision afterwards.</summary>
    private static async Task<int> AttachArtworkAsync(HttpClient client, Guid artist, int revision)
    {
        var asset = await StoreAsync(client, Assets.ArtworkImages.Halves(SKEncodedImageFormat.Png, 400, 200));
        return await PatchArtistAsync(client, artist, revision, $$"""{"artworkAssetId":"{{IdOf(asset)}}"}""");
    }

    /// <summary>Edits the record at <paramref name="path"/> under <paramref name="revision"/>; its revision afterwards.</summary>
    private static async Task<int> PatchAsync(HttpClient client, string path, int revision, string json)
    {
        using var response = await SendAsync(client, HttpMethod.Patch, path, SongApi.Quoted(revision), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("revision").GetInt32();
    }

    private static async Task<Dictionary<string, int>> RevisionsAsync(HttpClient client, IEnumerable<string> songs)
    {
        var revisions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var song in songs)
        {
            revisions[song] = (await SongAsync(client, song)).GetProperty("revision").GetInt32();
        }

        return revisions;
    }

    private static Task<JsonElement> SongAsync(HttpClient client, string reference) => ReadAsync(client, SongApi.Song(reference).ToString());

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task DeleteSongAsync(HttpClient client, string reference)
    {
        var song = await SongAsync(client, reference);
        using var response = await SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/v1/songs/{reference}",
            SongApi.Quoted(song.GetProperty("revision").GetInt32()),
            JsonSerializer.Serialize(new { confirmTitle = song.GetProperty("title").GetString() }));
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid artist, int revision, string? query) =>
        SendAsync(client, HttpMethod.Delete, $"/api/v1/artists/{artist}{(query is null ? string.Empty : "?" + query)}", SongApi.Quoted(revision));

    private static Task<IReadOnlyList<RetentionGroup>> GroupsAsync(N8TracksApiFactory factory) =>
        WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? ifMatch, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
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
}
