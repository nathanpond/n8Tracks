using System.Net;
using System.Text.Json;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Retention;
using SkiaSharp;
using static n8Tracks.Api.Tests.Assets.ArtworkApi;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// Deleting Albums and Playlists (#103): <c>DELETE /api/v1/albums/{id}</c> and
/// <c>DELETE /api/v1/playlists/{id}</c>, both session only, under the record's revision. The record,
/// its memberships, its links, and its artwork go into one retention group; no Song is deleted, no
/// Song's revision or Versions change, and each Song's updated time moves. Restoring the group puts
/// the record back with the memberships whose Songs still exist.
/// </summary>
public sealed class CollectionDeletionEndpointTests
{
    [Fact]
    public async Task DeletingAPlaylistRetainsItWithItsEntriesAndArtworkAndKeepsEverySong()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var songs = await SongsAsync(factory, client, "Highway", "Sunrise", "Toll");
        var playlistId = await CreateAsync(client, "playlists", "Road Mix");
        var revision = 1;
        foreach (var song in (string[])["n8-3", "n8-1", "n8-2"])
        {
            revision = await AddAsync(client, $"/api/v1/playlists/{playlistId}/songs", revision, song);
        }

        revision = await AttachArtworkAsync(client, "playlists", playlistId, revision);

        // A second Playlist shares a Song and is untouched.
        var otherId = await CreateAsync(client, "playlists", "Other");
        await AddAsync(client, $"/api/v1/playlists/{otherId}/songs", 1, "n8-1");
        var otherBefore = await ReadAsync(client, "playlists", otherId);
        var before = SongState(factory);
        var revisions = await RevisionsAsync(client, songs);

        clock.Advance(TimeSpan.FromMinutes(1));
        using (var deleted = await DeleteAsync(client, "playlists", playlistId, revision))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
            Assert.Contains("no-store", deleted.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        }

        // One group: the Playlist, its three entries, and its artwork with the asset's files.
        var group = Assert.Single(await GroupsAsync(factory));
        Assert.Equal(RetainedRecordTypes.Playlist, group.Kind);
        Assert.Equal("Playlist Road Mix", group.Label);
        Assert.Null(group.Shortcode);
        Assert.Equal(Upper(playlistId), group.Records[0].OriginalId);
        Assert.Equal(3, group.Records.Count(static record => record.RecordType == RetainedRecordTypes.PlaylistEntry));
        Assert.Single(group.Records, static record => record.RecordType == RetainedRecordTypes.ArtworkAttachment);
        Assert.NotEmpty(group.Files);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM playlist_songs WHERE playlist_id = '{Upper(playlistId)}';"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM artwork_attachments WHERE owner_id = '{Upper(playlistId)}';"));

        // Every Song is still there, as it was but for its updated time; no Version changed.
        await ExpectSongsKeptAsync(factory, client, songs, before, revisions, clock.GetUtcNow());
        foreach (var song in songs)
        {
            var playlists = (await SongAsync(client, song)).GetProperty("playlists").EnumerateArray().Select(static playlist => playlist.GetProperty("id").GetGuid());
            Assert.Equal(song == "n8-1" ? [otherId] : [], playlists);
        }

        // The Playlist is gone from reads; the other Playlist is untouched.
        using (var read = await client.GetAsync(new Uri($"/api/v1/playlists/{playlistId}", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(read, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        var list = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/playlists", UriKind.Relative)));
        Assert.Equal(["Other"], list.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("title").GetString()));
        Assert.Equal(otherBefore.ToString(), (await ReadAsync(client, "playlists", otherId)).ToString());

        using (var again = await DeleteAsync(client, "playlists", playlistId, revision))
        {
            await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }
    }

    [Fact]
    public async Task DeletingAnAlbumRetainsItWithItsTracksLinksAndArtworkAndKeepsEverySong()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var songs = await SongsAsync(factory, client, "Highway", "Sunrise", "Toll");
        var artistId = await CreateArtistAsync(client, "Album Maker");
        var albumId = await CreateAsync(client, "albums", "Long Road");
        var revision = await PatchAsync(
            client,
            "albums",
            albumId,
            1,
            $$"""{"albumArtistId":"{{artistId}}","upc":"036000291452","links":[{"url":"https://example.com/long-road","label":"Listen"}]}""");
        foreach (var song in songs)
        {
            revision = await AddAsync(client, $"/api/v1/albums/{albumId}/tracks", revision, song);
        }

        revision = await AttachArtworkAsync(client, "albums", albumId, revision);

        // Another Album with the same UPC warns about this one until it is deleted.
        var twinId = await CreateAsync(client, "albums", "Twin");
        await PatchAsync(client, "albums", twinId, 1, """{"upc":"036000291452"}""");
        Assert.Single((await ReadAsync(client, "albums", twinId)).GetProperty("warnings").EnumerateArray());
        var before = SongState(factory);
        var revisions = await RevisionsAsync(client, songs);

        clock.Advance(TimeSpan.FromMinutes(1));
        using (var deleted = await DeleteAsync(client, "albums", albumId, revision))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        var group = Assert.Single(await GroupsAsync(factory));
        Assert.Equal(RetainedRecordTypes.Album, group.Kind);
        Assert.Equal("Album Long Road", group.Label);
        Assert.Equal(Upper(albumId), group.Records[0].OriginalId);
        Assert.Equal(3, group.Records.Count(static record => record.RecordType == RetainedRecordTypes.AlbumTrack));
        Assert.Single(group.Records, static record => record.RecordType == RetainedRecordTypes.AlbumLink);
        Assert.Single(group.Records, static record => record.RecordType == RetainedRecordTypes.ArtworkAttachment);
        Assert.NotEmpty(group.Files);
        foreach (var table in (string[])["album_songs", "album_links"])
        {
            Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM {table} WHERE album_id = '{Upper(albumId)}';"));
        }

        await ExpectSongsKeptAsync(factory, client, songs, before, revisions, clock.GetUtcNow());
        foreach (var song in songs)
        {
            Assert.Empty((await SongAsync(client, song)).GetProperty("albums").EnumerateArray());
        }

        // No read shows it: the Album, the list, the Artist's Album count, and the same-UPC warning.
        using (var read = await client.GetAsync(new Uri($"/api/v1/albums/{albumId}", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(read, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        var list = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/albums", UriKind.Relative)));
        Assert.Equal(["Twin"], list.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("title").GetString()));
        Assert.Equal(0, (await ReadAsync(client, "artists", artistId)).GetProperty("albumCount").GetInt32());
        Assert.Empty((await ReadAsync(client, "albums", twinId)).GetProperty("warnings").EnumerateArray());
    }

    [Theory]
    [InlineData("albums")]
    [InlineData("playlists")]
    public async Task MalformedOrStaleDeletesAreRefusedAndChangeNothing(string collection)
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Member");
        var id = await CreateAsync(client, collection, "Kept");
        var revision = await AddAsync(client, $"/api/v1/{collection}/{id}/{(collection == "albums" ? "tracks" : "songs")}", 1, "n8-1");

        using (var missing = await DeleteAsync(client, collection, id, revision: null))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        using (var malformed = await SendAsync(client, HttpMethod.Delete, $"/api/v1/{collection}/{id}", ifMatch: "two"))
        {
            await SetupApi.ProblemAsync(malformed, HttpStatusCode.BadRequest, Revisions.InvalidCode);
        }

        // A stale revision (a Song joined since the dialog opened) answers with the record as it is now.
        using (var stale = await DeleteAsync(client, collection, id, revision - 1))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
            Assert.Equal(revision, problem.GetProperty("current").GetProperty("revision").GetInt32());
            Assert.Equal(1, problem.GetProperty("current").GetProperty("songCount").GetInt32());
        }

        using (var unknown = await DeleteAsync(client, collection, Guid.CreateVersion7(), 1))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal(revision, (await ReadAsync(client, collection, id)).GetProperty("revision").GetInt32());
        Assert.Empty(await GroupsAsync(factory));

        // At the revision read now, it goes.
        using var deleted = await DeleteAsync(client, collection, id, revision);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task RestoringAPlaylistPutsItBackWithTheEntriesWhoseSongsStillExist()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var songs = await SongsAsync(factory, client, "Highway", "Sunrise", "Toll");
        var playlistId = await CreateAsync(client, "playlists", "Road Mix");
        var revision = await PatchAsync(client, "playlists", playlistId, 1, """{"description":"For the drive"}""");
        foreach (var song in (string[])["n8-3", "n8-1", "n8-2"])
        {
            revision = await AddAsync(client, $"/api/v1/playlists/{playlistId}/songs", revision, song);
        }

        revision = await AttachArtworkAsync(client, "playlists", playlistId, revision);
        var before = await ReadAsync(client, "playlists", playlistId);
        await DeleteAsync(client, "playlists", playlistId, revision, expectDeleted: true);

        // Meanwhile: n8-1 is deleted, and a new Playlist takes the same title (titles are not unique).
        clock.Advance(TimeSpan.FromMinutes(1));
        await DeleteSongAsync(client, "n8-1");
        await CreateAsync(client, "playlists", "Road Mix");
        var revisions = await RevisionsAsync(client, ["n8-2", "n8-3"]);

        clock.Advance(TimeSpan.FromMinutes(1));
        var group = Assert.Single(await GroupsAsync(factory), static candidate => candidate.Kind == RetainedRecordTypes.Playlist);
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        var note = Assert.Single(restored.Notes);
        Assert.StartsWith("A membership of a Playlist was not restored", note, StringComparison.Ordinal);

        // The Playlist is back with its fields, artwork, and the other two Songs in their order.
        var playlist = await ReadAsync(client, "playlists", playlistId);
        Assert.Equal(before.GetProperty("title").GetString(), playlist.GetProperty("title").GetString());
        Assert.Equal("For the drive", playlist.GetProperty("description").GetString());
        Assert.Equal(before.GetProperty("artwork").ToString(), playlist.GetProperty("artwork").ToString());
        Assert.Equal(["n8-3", "n8-2"], playlist.GetProperty("songs").EnumerateArray().Select(static song => song.GetProperty("shortcode").GetString()));
        Assert.Equal(2, playlist.GetProperty("songCount").GetInt32());
        using (var served = await GetAsync(client, playlist.GetProperty("artwork").GetProperty("urls").GetProperty("320").GetString()!))
        {
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        }

        // Those Songs show it again, with their updated time moved and their revisions kept.
        foreach (var song in (string[])["n8-2", "n8-3"])
        {
            var read = await SongAsync(client, song);
            Assert.Contains(playlistId, read.GetProperty("playlists").EnumerateArray().Select(static entry => entry.GetProperty("id").GetGuid()));
            Assert.Equal(revisions[song], read.GetProperty("revision").GetInt32());
            Assert.Equal(clock.GetUtcNow().UtcDateTime, read.GetProperty("updatedAt").GetDateTime());
        }

        var titles = (await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/playlists", UriKind.Relative))))
            .GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("title").GetString());
        Assert.Equal(["Road Mix", "Road Mix"], titles);
        Assert.Equal(["n8-2", "n8-3"], SongApi.Shortcodes(await SongApi.ListAsync(client)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RestoringAnAlbumPutsItBackWithItsTracksInPlaceAndLetsGoOfWhatIsGone()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongsAsync(factory, client, "One", "Two", "Three", "Four");
        var artistId = await CreateArtistAsync(client, "Gone Artist");
        var albumId = await CreateAsync(client, "albums", "Discs");
        var revision = await PatchAsync(client, "albums", albumId, 1, $$"""{"albumArtistId":"{{artistId}}","links":[{"url":"https://example.com/discs"}]}""");
        foreach (var song in (string[])["n8-1", "n8-2", "n8-3", "n8-4"])
        {
            revision = await AddAsync(client, $"/api/v1/albums/{albumId}/tracks", revision, song);
        }

        // Disc 1: n8-1 (1), n8-4 (3); disc 2: n8-2 alone; disc 3: n8-3 (5).
        using (var put = await SendAsync(
            client,
            HttpMethod.Put,
            $"/api/v1/albums/{albumId}/tracks",
            SongApi.Quoted(revision),
            """{"tracks":[{"songId":"n8-1","disc":1,"track":1},{"songId":"n8-4","disc":1,"track":3},{"songId":"n8-2","disc":2,"track":1},{"songId":"n8-3","disc":3,"track":5}]}"""))
        {
            Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
            revision = (await SetupApi.JsonAsync(put)).GetProperty("revision").GetInt32();
        }

        revision = await AttachArtworkAsync(client, "albums", albumId, revision);
        var before = await ReadAsync(client, "albums", albumId);
        await DeleteAsync(client, "albums", albumId, revision, expectDeleted: true);

        // Meanwhile n8-2, alone on disc 2, is deleted, and so is the Album Artist.
        clock.Advance(TimeSpan.FromMinutes(1));
        await DeleteSongAsync(client, "n8-2");
        TestDatabase.Execute(factory.DataPath, $"DELETE FROM artists WHERE id = '{Upper(artistId)}';");

        clock.Advance(TimeSpan.FromMinutes(1));
        var group = Assert.Single(await GroupsAsync(factory), static candidate => candidate.Kind == RetainedRecordTypes.Album);
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        Assert.Equal(2, restored.Notes.Count);
        Assert.Contains(restored.Notes, static note => note.StartsWith("A membership of an Album was not restored", StringComparison.Ordinal));
        Assert.Contains(restored.Notes, static note => note.Contains("restored without one", StringComparison.Ordinal));

        // Back with its links and artwork, without the Album Artist, and disc 3 closes up to disc 2.
        var album = await ReadAsync(client, "albums", albumId);
        Assert.Equal(JsonValueKind.Null, album.GetProperty("albumArtist").ValueKind);
        Assert.Equal(before.GetProperty("links").ToString(), album.GetProperty("links").ToString());
        Assert.Equal(before.GetProperty("artwork").ToString(), album.GetProperty("artwork").ToString());
        Assert.Equal(
            [("n8-1", 1, 1), ("n8-4", 1, 3), ("n8-3", 2, 5)],
            album.GetProperty("tracks").EnumerateArray().Select(static track => (track.GetProperty("shortcode").GetString(), track.GetProperty("disc").GetInt32(), track.GetProperty("track").GetInt32())));
        Assert.True(album.GetProperty("revision").GetInt32() > before.GetProperty("revision").GetInt32());

        // Its Songs show it again. n8-2's track went with the Album, not with n8-2: restoring the Song does not bring it back.
        Assert.Equal([albumId], (await SongAsync(client, "n8-1")).GetProperty("albums").EnumerateArray().Select(static entry => entry.GetProperty("id").GetGuid()));
        var songGroup = Assert.Single(await GroupsAsync(factory), static candidate => candidate.Kind == RetainedRecordTypes.Song);
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, songGroup.Id));
        Assert.Empty((await SongAsync(client, "n8-2")).GetProperty("albums").EnumerateArray());
    }

    /// <summary>
    /// Every column of each Song but its updated time, and every Version and Generation row, sorted:
    /// what deleting a collection must leave exactly as it was.
    /// </summary>
    private static List<string> SongState(N8TracksApiFactory factory)
    {
        var rows = new List<string>();
        foreach (var (table, except) in (ValueTuple<string, string>[])[("songs", "updated_utc"), ("versions", string.Empty), ("generations", string.Empty), ("song_artist_credits", string.Empty)])
        {
            var columns = TestDatabase.Rows(factory.DataPath, $"SELECT name FROM pragma_table_info('{table}') WHERE name <> '{except}' ORDER BY name;");
            rows.AddRange(TestDatabase.Rows(
                factory.DataPath,
                $"SELECT '{table}|' || {string.Join(" || '|' || ", columns.Select(static name => $"COALESCE(quote({name}), '∅')"))} FROM {table};"));
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static async Task ExpectSongsKeptAsync(
        N8TracksApiFactory factory,
        HttpClient client,
        IReadOnlyList<string> songs,
        List<string> before,
        Dictionary<string, int> revisions,
        DateTimeOffset deletedAt)
    {
        Assert.Equal(before, SongState(factory));
        Assert.Equal(songs.Count, SongApi.Shortcodes(await SongApi.ListAsync(client)).Count);
        foreach (var song in songs)
        {
            var read = await SongAsync(client, song);
            Assert.Equal(revisions[song], read.GetProperty("revision").GetInt32());
            Assert.Equal(deletedAt.UtcDateTime, read.GetProperty("updatedAt").GetDateTime());
        }
    }

    /// <summary>Creates the Songs, n8-1 onward, and freezes n8-1's Version 1 with a Generation; their shortcodes.</summary>
    private static async Task<List<string>> SongsAsync(N8TracksApiFactory factory, HttpClient client, params string[] titles)
    {
        var shortcodes = new List<string>();
        foreach (var title in titles)
        {
            shortcodes.Add((await SongApi.CreateAsync(client, title)).GetProperty("shortcode").GetString()!);
        }

        await SongApi.AttachGenerationAsync(factory, "n8-1-v1");
        return shortcodes;
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

    private static async Task<JsonElement> SongAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(SongApi.Song(reference));
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

    private static async Task<Guid> CreateAsync(HttpClient client, string collection, string title)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/{collection}", UriKind.Relative), JsonSerializer.Serialize(new { title }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateArtistAsync(HttpClient client, string name)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/artists", UriKind.Relative), JsonSerializer.Serialize(new { name, confirmDuplicate = true }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string collection, Guid id)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/{collection}/{id}", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Edits the record under <paramref name="revision"/>; its revision afterwards.</summary>
    private static async Task<int> PatchAsync(HttpClient client, string collection, Guid id, int revision, string json)
    {
        using var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/{collection}/{id}", SongApi.Quoted(revision), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("revision").GetInt32();
    }

    /// <summary>Adds the Song at the end of the Album or Playlist; its revision afterwards.</summary>
    private static async Task<int> AddAsync(HttpClient client, string path, int revision, string song)
    {
        using var response = await SendAsync(client, HttpMethod.Post, path, SongApi.Quoted(revision), $$"""{"songId":"{{song}}"}""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("revision").GetInt32();
    }

    /// <summary>Uploads an image and makes it the record's own artwork; its revision afterwards.</summary>
    private static async Task<int> AttachArtworkAsync(HttpClient client, string collection, Guid id, int revision)
    {
        var asset = await StoreAsync(client, Assets.ArtworkImages.Halves(SKEncodedImageFormat.Png, 400, 200));
        return await PatchAsync(client, collection, id, revision, $$"""{"artworkAssetId":"{{IdOf(asset)}}"}""");
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string collection, Guid id, int? revision) =>
        SendAsync(client, HttpMethod.Delete, $"/api/v1/{collection}/{id}", revision is { } value ? SongApi.Quoted(value) : null);

    private static async Task DeleteAsync(HttpClient client, string collection, Guid id, int revision, bool expectDeleted)
    {
        using var response = await DeleteAsync(client, collection, id, revision);
        Assert.True(expectDeleted && response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

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
