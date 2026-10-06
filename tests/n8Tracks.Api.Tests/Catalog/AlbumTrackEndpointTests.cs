using System.Net;
using System.Text;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// An Album's tracks: <c>POST /api/v1/albums/{id}/tracks</c> (a Song at the end of the last disc),
/// <c>DELETE .../tracks/{reference}</c>, and the full-list <c>PUT .../tracks</c> (reorder, renumber,
/// type a number, move to another disc), each under the Album's revision and needing
/// <c>collections.write</c>. Tracks are embedded in the Album, and a Song lists its Albums. Changing
/// an Album's tracks never changes its Songs.
/// </summary>
public sealed class AlbumTrackEndpointTests
{
    private static readonly Uri Albums = new("/api/v1/albums", UriKind.Relative);

    [Fact]
    public async Task TracksAreAddedReorderedRenumberedMovedBetweenDiscsAndRemovedWithoutChangingTheSongs()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var first = await SongApi.CreateAsync(client, "First");
        await SongApi.CreateAsync(client, "Second");
        await SongApi.CreateAsync(client, "Third");
        await SongApi.CreateAsync(client, "Fourth");
        var id = await CreateAsync(client, "Pack EP");
        var songsBefore = SongRows(factory);
        clock.Advance(TimeSpan.FromMinutes(5));

        // Added by ID or shortcode, each at the end of disc 1 with the next number, each raising the revision.
        var revision = 1;
        foreach (var reference in new[] { first.GetProperty("id").GetString()!, "N8-2", "n8-3" })
        {
            using var added = await SendAsync(client, HttpMethod.Post, TracksUri(id), revision, JsonSerializer.Serialize(new { songId = reference }));
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            revision++;
            Assert.Equal(SongApi.Quoted(revision), added.Headers.ETag?.ToString());
        }

        var album = await ReadAsync(client, id);
        Assert.Equal(["n8-1 1.1", "n8-2 1.2", "n8-3 1.3"], Tracks(album));
        Assert.Equal(3, album.GetProperty("songCount").GetInt32());
        Assert.Equal(revision, album.GetProperty("revision").GetInt32());
        Assert.NotEqual(album.GetProperty("createdAt").GetString(), album.GetProperty("updatedAt").GetString());
        var track = album.GetProperty("tracks")[0];
        Assert.Equal(first.GetProperty("id").GetString(), track.GetProperty("songId").GetString());
        Assert.Equal("First", track.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, track.GetProperty("primaryArtist").ValueKind);
        Assert.Equal(first.GetProperty("state").GetProperty("id").GetString(), track.GetProperty("state").GetProperty("id").GetString());
        Assert.False(track.GetProperty("hasSelectedGeneration").GetBoolean());

        // The list shows the Song count and the tracks too.
        var row = (await ListAsync(client)).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(3, row.GetProperty("songCount").GetInt32());
        Assert.Equal(3, row.GetProperty("tracks").GetArrayLength());

        // Drag track 3 to the top: the whole list, renumbered.
        album = await PutAsync(client, id, revision++, ("n8-3", 1, 1), ("n8-1", 1, 2), ("N8-2", 1, 3));
        Assert.Equal(["n8-3 1.1", "n8-1 1.2", "n8-2 1.3"], Tracks(album));

        // The same list again is no change.
        using (var same = await SendAsync(client, HttpMethod.Put, TracksUri(id), revision, TracksJson(("n8-3", 1, 1), ("n8-1", 1, 2), ("n8-2", 1, 3))))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal(SongApi.Quoted(revision), same.Headers.ETag?.ToString());
        }

        // A typed number leaves a gap, and the order follows the numbers; Renumber closes the gap.
        album = await PutAsync(client, id, revision++, ("n8-3", 1, 9), ("n8-1", 1, 2), ("n8-2", 1, 3));
        Assert.Equal(["n8-1 1.2", "n8-2 1.3", "n8-3 1.9"], Tracks(album));
        album = await PutAsync(client, id, revision++, ("n8-1", 1, 1), ("n8-2", 1, 2), ("n8-3", 1, 3));
        Assert.Equal(["n8-1 1.1", "n8-2 1.2", "n8-3 1.3"], Tracks(album));

        // Move to a new disc: a disc gap in the list closes up.
        album = await PutAsync(client, id, revision++, ("n8-2", 1, 1), ("n8-3", 1, 2), ("n8-1", 5, 1));
        Assert.Equal(["n8-2 1.1", "n8-3 1.2", "n8-1 2.1"], Tracks(album));

        // A new Song goes to the end of the last disc.
        using (var added = await SendAsync(client, HttpMethod.Post, TracksUri(id), revision, """{"songId":"n8-4"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            revision++;
            Assert.Equal(["n8-2 1.1", "n8-3 1.2", "n8-1 2.1", "n8-4 2.2"], Tracks(await SetupApi.JsonAsync(added)));
        }

        // The Song lists the Album with its disc and track; the Songs list does too.
        var other = await CreateAsync(client, "anthology");
        using (var added = await SendAsync(client, HttpMethod.Post, TracksUri(other), 1, """{"songId":"n8-1"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal(
            [$"{other} anthology 1.1", $"{id} Pack EP 2.1"],
            song.GetProperty("albums").EnumerateArray().Select(static entry => $"{entry.GetProperty("id").GetString()} {entry.GetProperty("title").GetString()} {entry.GetProperty("disc").GetInt32()}.{entry.GetProperty("track").GetInt32()}"));
        var listed = await SongApi.ListAsync(client, "sort=title");
        Assert.Equal(2, listed.GetProperty("items").EnumerateArray().Single(static item => item.GetProperty("shortcode").GetString() == "n8-1").GetProperty("albums").GetArrayLength());

        // Removing renumbers the rest of the disc; removing the last track of a disc closes the discs up.
        using (var removed = await SendAsync(client, HttpMethod.Delete, TrackUri(id, "N8-1"), revision, json: null))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            revision++;
            Assert.Equal(["n8-2 1.1", "n8-3 1.2", "n8-4 2.1"], Tracks(await SetupApi.JsonAsync(removed)));
        }

        using (var removed = await SendAsync(client, HttpMethod.Delete, TrackUri(id, "n8-2"), revision, json: null))
        {
            revision++;
            Assert.Equal(["n8-3 1.1", "n8-4 2.1"], Tracks(await SetupApi.JsonAsync(removed)));
        }

        // Move n8-3 to disc 2: disc 1 empties and disc 2 becomes disc 1.
        album = await PutAsync(client, id, revision++, ("n8-4", 2, 1), ("n8-3", 2, 2));
        Assert.Equal(["n8-4 1.1", "n8-3 1.2"], Tracks(album));

        // Removing a Song that is not on the Album changes nothing.
        using (var notOn = await SendAsync(client, HttpMethod.Delete, TrackUri(id, "n8-1"), revision, json: null))
        {
            Assert.Equal(HttpStatusCode.OK, notOn.StatusCode);
            Assert.Equal(SongApi.Quoted(revision), notOn.Headers.ETag?.ToString());
        }

        Assert.Equal("1|1|n8-4,1|2|n8-3", TestDatabase.Scalar(factory.DataPath, $"SELECT group_concat(disc || '|' || track || '|n8-' || shortcode_number) FROM (SELECT disc, track, shortcode_number FROM album_songs JOIN songs ON songs.id = album_songs.song_id WHERE album_id = '{id.ToUpperInvariant()}' ORDER BY disc, track);"));

        // No Song's revision or updated time moved.
        Assert.Equal(songsBefore, SongRows(factory));
    }

    [Fact]
    public async Task ADuplicateSongATakenNumberAWrongListAndAStaleRevisionAreRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "First");
        await SongApi.CreateAsync(client, "Second");
        await SongApi.CreateAsync(client, "Third");
        var id = await CreateAsync(client, "Pack EP");
        foreach (var (reference, revision) in new[] { ("n8-1", 1), ("n8-2", 2) })
        {
            using var added = await SendAsync(client, HttpMethod.Post, TracksUri(id), revision, JsonSerializer.Serialize(new { songId = reference }));
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        // The same Song again, by ID or shortcode, is refused with the Album as it is.
        var songId = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")))).GetProperty("id").GetString();
        foreach (var reference in new[] { "N8-1", songId })
        {
            using var twice = await SendAsync(client, HttpMethod.Post, TracksUri(id), 3, JsonSerializer.Serialize(new { songId = reference }));
            var problem = await SetupApi.ProblemAsync(twice, HttpStatusCode.Conflict, "song_already_on_album");
            Assert.Equal(["n8-1 1.1", "n8-2 1.2"], Tracks(problem.GetProperty("current")));
        }

        // A Song that does not exist, a reference of another kind, and a wrong type are field errors.
        foreach (var json in new[] { """{"songId":"n8-99"}""", $$"""{"songId":"{{Guid.CreateVersion7()}}"}""", """{"songId":"n8-1-v1"}""", """{"songId":7}""", "{}" })
        {
            using var unknown = await SendAsync(client, HttpMethod.Post, TracksUri(id), 3, json);
            var problem = await SetupApi.ProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("songId", out _), json);
        }

        // Two tracks on one disc may not share a number: the refusal names the entry that holds it.
        using (var taken = await SendAsync(client, HttpMethod.Put, TracksUri(id), 3, TracksJson(("n8-1", 1, 1), ("n8-2", 1, 1))))
        {
            var problem = await SetupApi.ProblemAsync(taken, HttpStatusCode.Conflict, "track_number_taken");
            Assert.Equal("Track 1 on disc 1 is held by First (n8-1).", problem.GetProperty("title").GetString());
            var holder = problem.GetProperty("heldBy");
            Assert.Equal("n8-1", holder.GetProperty("shortcode").GetString());
            Assert.Equal(1, holder.GetProperty("disc").GetInt32());
            Assert.Equal(1, holder.GetProperty("track").GetInt32());
            Assert.Equal(["n8-1 1.1", "n8-2 1.2"], Tracks(problem.GetProperty("current")));
        }

        // The same numbers on different discs are fine; the holder is whichever had the number before.
        using (var taken = await SendAsync(client, HttpMethod.Put, TracksUri(id), 3, TracksJson(("n8-1", 1, 2), ("n8-2", 1, 2))))
        {
            var problem = await SetupApi.ProblemAsync(taken, HttpStatusCode.Conflict, "track_number_taken");
            Assert.Equal("n8-2", problem.GetProperty("heldBy").GetProperty("shortcode").GetString());
        }

        // The list must name exactly the Songs on the Album, each once.
        foreach (var json in new[]
        {
            TracksJson(),
            TracksJson(("n8-1", 1, 1)),
            TracksJson(("n8-1", 1, 1), ("n8-2", 1, 2), ("n8-3", 1, 3)),
            TracksJson(("n8-1", 1, 1), ("n8-1", 1, 2)),
            TracksJson(("n8-1", 1, 1), ("n8-99", 1, 2)),
        })
        {
            using var mismatch = await SendAsync(client, HttpMethod.Put, TracksUri(id), 3, json);
            var problem = await SetupApi.ProblemAsync(mismatch, HttpStatusCode.Conflict, "order_mismatch");
            Assert.Equal(["n8-1 1.1", "n8-2 1.2"], Tracks(problem.GetProperty("current")));
        }

        // Numbers from 1 to 999, whole, and every field there.
        foreach (var json in new[]
        {
            TracksJson(("n8-1", 0, 1), ("n8-2", 1, 2)),
            TracksJson(("n8-1", 1, 1000), ("n8-2", 1, 2)),
            TracksJson(("n8-1", 1000, 1), ("n8-2", 1, 2)),
            """{"tracks":[{"songId":"n8-1","disc":1,"track":1.5},{"songId":"n8-2","disc":1,"track":2}]}""",
            """{"tracks":[{"songId":"n8-1","disc":"1","track":1},{"songId":"n8-2","disc":1,"track":2}]}""",
            """{"tracks":[{"songId":"n8-1","track":1},{"songId":"n8-2","disc":1,"track":2}]}""",
            """{"tracks":["n8-1","n8-2"]}""",
            """{"tracks":"n8-1"}""",
            "{}",
        })
        {
            using var wrong = await SendAsync(client, HttpMethod.Put, TracksUri(id), 3, json);
            var problem = await SetupApi.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("tracks", out _), json);
        }

        // A stale revision is a conflict with the Album as it is; no revision is 428.
        using (var stale = await SendAsync(client, HttpMethod.Post, TracksUri(id), 1, """{"songId":"n8-3"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(3, problem.GetProperty("current").GetProperty("revision").GetInt32());
            Assert.Equal(2, problem.GetProperty("current").GetProperty("tracks").GetArrayLength());
        }

        using (var noRevision = await SendAsync(client, HttpMethod.Put, TracksUri(id), revision: null, TracksJson(("n8-1", 1, 1), ("n8-2", 1, 2))))
        {
            Assert.Equal((HttpStatusCode)428, noRevision.StatusCode);
        }

        // Removing a Song that does not exist, or from an Album that does not, is not found.
        foreach (var uri in new[] { TrackUri(id, "n8-99"), TrackUri(id, "n8-1-v1"), TrackUri(Guid.CreateVersion7().ToString(), "n8-1") })
        {
            using var gone = await SendAsync(client, HttpMethod.Delete, uri, 3, json: null);
            await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, "not_found");
        }

        using (var noAlbum = await SendAsync(client, HttpMethod.Post, TracksUri(Guid.CreateVersion7().ToString()), 1, """{"songId":"n8-3"}"""))
        {
            await SetupApi.ProblemAsync(noAlbum, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal("3", TestDatabase.Scalar(factory.DataPath, "SELECT revision FROM albums;"));
        Assert.Equal("1|1,1|2", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(disc || '|' || track) FROM (SELECT disc, track FROM album_songs ORDER BY disc, track);"));
    }

    [Fact]
    public async Task PastTrackNineHundredNinetyNineAddingIsRefusedUntilAnotherDiscIsStarted()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Last");
        await SongApi.CreateAsync(client, "One more");
        var id = await CreateAsync(client, "Long");
        using (var added = await SendAsync(client, HttpMethod.Post, TracksUri(id), 1, """{"songId":"n8-1"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        await PutAsync(client, id, 2, ("n8-1", 1, 999));

        using (var full = await SendAsync(client, HttpMethod.Post, TracksUri(id), 3, """{"songId":"n8-2"}"""))
        {
            var problem = await SetupApi.ProblemAsync(full, HttpStatusCode.Conflict, "disc_full");
            Assert.Contains("Move a track to a new disc", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
            Assert.Equal(["n8-1 1.999"], Tracks(problem.GetProperty("current")));
        }

        // Once the track is on a new disc of its own (here the only disc, renumbered), adding works again.
        await PutAsync(client, id, 3, ("n8-1", 1, 1));
        using (var added = await SendAsync(client, HttpMethod.Post, TracksUri(id), 4, """{"songId":"n8-2"}"""))
        {
            Assert.Equal(["n8-1 1.1", "n8-2 1.2"], Tracks(await SetupApi.JsonAsync(added)));
        }
    }

    [Fact]
    public async Task ReadingTracksNeedsCatalogReadAndManagingThemNeedsCollectionsWrite()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "First");
        var id = await CreateAsync(client, "Pack EP");

        // Each write, by a token with collections.write: add, renumber, remove.
        var writes = new (HttpMethod Method, Uri Uri, string Json)[]
        {
            (HttpMethod.Post, TracksUri(id), """{"songId":"n8-1"}"""),
            (HttpMethod.Put, TracksUri(id), TracksJson(("n8-1", 1, 4))),
            (HttpMethod.Delete, TrackUri(id, "n8-1"), "{}"),
        };
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CollectionsWrite);
        var revision = 1;
        foreach (var (method, uri, json) in writes)
        {
            using var write = await SendAsTokenAsync(client, method, uri, writer, json, SongApi.Quoted(revision));
            Assert.True(write.StatusCode == HttpStatusCode.OK, $"{method} {uri}: {await write.Content.ReadAsStringAsync()}");
            revision = (await SetupApi.JsonAsync(write)).GetProperty("revision").GetInt32();
        }

        using (var added = await SendAsync(client, HttpMethod.Post, TracksUri(id), revision, """{"songId":"n8-1"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            revision++;
        }

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, AlbumUri(id), reader))
        {
            Assert.Equal(["n8-1 1.1"], Tracks(await SetupApi.JsonAsync(read)));
        }

        // Every other scope is refused, naming the one needed, and nothing changes.
        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CollectionsWrite))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            foreach (var (method, uri, json) in writes)
            {
                using var refused = await SendAsTokenAsync(client, method, uri, token, json, SongApi.Quoted(revision));
                var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
                Assert.Equal(CredentialScopes.CollectionsWrite, problem.GetProperty("requiredScope").GetString());
            }
        }

        Assert.Equal(["n8-1 1.1"], Tracks(await ReadAsync(client, id)));
        Assert.Equal(revision.ToString(System.Globalization.CultureInfo.InvariantCulture), TestDatabase.Scalar(factory.DataPath, "SELECT revision FROM albums;"));
    }

    private static Uri AlbumUri(string id) => new($"/api/v1/albums/{id}", UriKind.Relative);

    private static Uri TracksUri(string id) => new($"/api/v1/albums/{id}/tracks", UriKind.Relative);

    private static Uri TrackUri(string id, string reference) => new($"/api/v1/albums/{id}/tracks/{reference}", UriKind.Relative);

    /// <summary>Every Song's shortcode number, revision, and updated time.</summary>
    private static List<string> SongRows(N8TracksApiFactory factory) =>
        TestDatabase.Rows(factory.DataPath, "SELECT shortcode_number || '|' || revision || '|' || updated_utc FROM songs ORDER BY shortcode_number;");

    /// <summary>Each track as <c>shortcode disc.track</c>, in the Album's order.</summary>
    private static List<string> Tracks(JsonElement album) =>
        [.. album.GetProperty("tracks").EnumerateArray().Select(static track => $"{track.GetProperty("shortcode").GetString()} {track.GetProperty("disc").GetInt32()}.{track.GetProperty("track").GetInt32()}")];

    private static string TracksJson(params (string SongId, int Disc, int Track)[] tracks) =>
        JsonSerializer.Serialize(new { tracks = tracks.Select(static track => new { songId = track.SongId, disc = track.Disc, track = track.Track }) });

    /// <summary>PUTs the whole list at <paramref name="revision"/>, which must succeed; the Album as it is then.</summary>
    private static async Task<JsonElement> PutAsync(HttpClient client, string id, int revision, params (string SongId, int Disc, int Track)[] tracks)
    {
        using var response = await SendAsync(client, HttpMethod.Put, TracksUri(id), revision, TracksJson(tracks));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(SongApi.Quoted(revision + 1), response.Headers.ETag?.ToString());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync(AlbumUri(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Albums);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Creates an Album with <paramref name="title"/>; its ID.</summary>
    private static async Task<string> CreateAsync(HttpClient client, string title)
    {
        using var created = await SendAsync(client, HttpMethod.Post, Albums, revision: null, JsonSerializer.Serialize(new { title }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await SetupApi.JsonAsync(created)).GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, int? revision, string? json)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (revision is { } value)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsTokenAsync(HttpClient client, HttpMethod method, Uri uri, string token, string json, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }
}
