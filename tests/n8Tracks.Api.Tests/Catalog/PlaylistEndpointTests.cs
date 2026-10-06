using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// Playlists: <c>GET /api/v1/playlists</c> (a page by title) and <c>GET /api/v1/playlists/{id}</c>
/// (with its Songs in order) need <c>catalog.read</c>; creating, editing, and adding, removing, and
/// reordering Songs need <c>collections.write</c>, each under the Playlist's revision. A Song is on a
/// Playlist at most once, and changing a Playlist never changes its Songs.
/// </summary>
public sealed class PlaylistEndpointTests
{
    private static readonly Uri Playlists = new("/api/v1/playlists", UriKind.Relative);

    [Fact]
    public async Task APlaylistIsCreatedFromATitleEditedAndListedByTitle()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var created = await SendAsync(client, HttpMethod.Post, Playlists, revision: null, """{"title":"  Road\ntrip  "}""");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.ToString());
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var playlist = await SetupApi.JsonAsync(created);
        var id = playlist.GetProperty("id").GetString()!;
        Assert.Equal($"/api/v1/playlists/{id}", created.Headers.Location?.OriginalString);
        Assert.Equal("Road trip", playlist.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, playlist.GetProperty("description").ValueKind);
        Assert.Equal(0, playlist.GetProperty("songCount").GetInt32());
        Assert.Equal(0, playlist.GetProperty("songs").GetArrayLength());
        Assert.Equal(1, playlist.GetProperty("revision").GetInt32());

        using (var read = await client.GetAsync(PlaylistUri(id)))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("\"1\"", read.Headers.ETag?.ToString());
            Assert.Equal(playlist.GetRawText(), (await SetupApi.JsonAsync(read)).GetRawText());
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        using (var edited = await SendAsync(client, HttpMethod.Patch, PlaylistUri(id), 1, """{"title":"Road trip\r\n2026","description":" For the drive.\r\nLoud. "}"""))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            Assert.Equal("\"2\"", edited.Headers.ETag?.ToString());
            var body = await SetupApi.JsonAsync(edited);
            Assert.Equal("Road trip 2026", body.GetProperty("title").GetString());
            Assert.Equal("For the drive.\nLoud.", body.GetProperty("description").GetString());
            Assert.NotEqual(body.GetProperty("createdAt").GetString(), body.GetProperty("updatedAt").GetString());
        }

        // Sending what the Playlist has is no change; null clears the description.
        using (var same = await SendAsync(client, HttpMethod.Patch, PlaylistUri(id), 2, """{"title":" Road trip 2026 "}"""))
        {
            Assert.Equal("\"2\"", same.Headers.ETag?.ToString());
        }

        using (var cleared = await SendAsync(client, HttpMethod.Patch, PlaylistUri(id), 2, """{"description":null}"""))
        {
            Assert.Equal(JsonValueKind.Null, (await SetupApi.JsonAsync(cleared)).GetProperty("description").ValueKind);
        }

        // Titles need not be unique; the list is by title ignoring case, then the earlier created, and paged.
        foreach (var title in new[] { "road trip 2026", "Anthems", "zebra" })
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await CreateAsync(client, title);
        }

        Assert.Equal(["Anthems", "Road trip 2026", "road trip 2026", "zebra"], await TitlesAsync(client, ""));
        using (var page = await client.GetAsync(new Uri("/api/v1/playlists?page=2&pageSize=3", UriKind.Relative)))
        {
            var body = await SetupApi.JsonAsync(page);
            Assert.Equal(4, body.GetProperty("total").GetInt32());
            Assert.Equal("zebra", Assert.Single(body.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
            Assert.False(Assert.Single(body.GetProperty("items").EnumerateArray()).TryGetProperty("songs", out _));
        }

        foreach (var query in new[] { "?page=0", "?pageSize=101", "?page=x", "?page=1&page=2" })
        {
            using var refused = await client.GetAsync(new Uri("/api/v1/playlists" + query, UriKind.Relative));
            await SetupApi.ProblemAsync(refused, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var missing = await client.GetAsync(PlaylistUri(Guid.CreateVersion7().ToString()));
        await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task SongsAreAddedAtTheEndReorderedAndRemovedWithoutChangingTheSongs()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var first = await SongApi.CreateAsync(client, "First");
        await SongApi.CreateAsync(client, "Second");
        await SongApi.CreateAsync(client, "Third");
        var id = await CreateAsync(client, "Road trip");
        var songsBefore = SongRows(factory);
        clock.Advance(TimeSpan.FromMinutes(5));

        // Added by ID or shortcode, each at the end, each raising the revision.
        var revision = 1;
        foreach (var reference in new[] { first.GetProperty("id").GetString()!, "N8-2", "n8-3" })
        {
            using var added = await SendAsync(client, HttpMethod.Post, SongsUri(id), revision, JsonSerializer.Serialize(new { songId = reference }));
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            revision++;
            Assert.Equal(SongApi.Quoted(revision), added.Headers.ETag?.ToString());
        }

        var playlist = await ReadAsync(client, id);
        Assert.Equal(["n8-1", "n8-2", "n8-3"], Shortcodes(playlist));
        Assert.Equal(3, playlist.GetProperty("songCount").GetInt32());
        var entry = playlist.GetProperty("songs")[0];
        Assert.Equal("First", entry.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("primaryArtist").ValueKind);
        Assert.Equal(first.GetProperty("state").GetProperty("id").GetString(), entry.GetProperty("state").GetProperty("id").GetString());
        Assert.False(entry.GetProperty("hasSelectedGeneration").GetBoolean());
        Assert.Equal("0|1|2", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(position, '|') FROM (SELECT position FROM playlist_songs ORDER BY position);"));

        // Drag the last to the top: the whole list, by ID or shortcode.
        using (var reordered = await SendAsync(client, HttpMethod.Put, SongsUri(id), revision, JsonSerializer.Serialize(new { songIds = new[] { "n8-3", first.GetProperty("id").GetString(), "N8-2" } })))
        {
            Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
            revision++;
            Assert.Equal(["n8-3", "n8-1", "n8-2"], Shortcodes(await SetupApi.JsonAsync(reordered)));
        }

        // The same order again is no change.
        using (var same = await SendAsync(client, HttpMethod.Put, SongsUri(id), revision, """{"songIds":["n8-3","n8-1","n8-2"]}"""))
        {
            Assert.Equal(SongApi.Quoted(revision), same.Headers.ETag?.ToString());
        }

        Assert.Equal(["n8-3", "n8-1", "n8-2"], Shortcodes(await ReadAsync(client, id)));

        // Removing keeps the others' order; removing a Song not on it changes nothing.
        using (var removed = await SendAsync(client, HttpMethod.Delete, new Uri($"/api/v1/playlists/{id}/songs/N8-1", UriKind.Relative), revision, json: null))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            revision++;
            Assert.Equal(["n8-3", "n8-2"], Shortcodes(await SetupApi.JsonAsync(removed)));
        }

        using (var notOn = await SendAsync(client, HttpMethod.Delete, new Uri($"/api/v1/playlists/{id}/songs/{first.GetProperty("id").GetString()}", UriKind.Relative), revision, json: null))
        {
            Assert.Equal(HttpStatusCode.OK, notOn.StatusCode);
            Assert.Equal(SongApi.Quoted(revision), notOn.Headers.ETag?.ToString());
        }

        Assert.Equal("0|1", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(position, '|') FROM (SELECT position FROM playlist_songs ORDER BY position);"));

        // A Song can be on any number of Playlists, and lists them by title.
        var other = await CreateAsync(client, "anthems");
        using (var added = await SendAsync(client, HttpMethod.Post, SongsUri(other), 1, """{"songId":"n8-2"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-2")));
        Assert.Equal(
            [$"{other} anthems", $"{id} Road trip"],
            song.GetProperty("playlists").EnumerateArray().Select(static playlist => $"{playlist.GetProperty("id").GetString()} {playlist.GetProperty("title").GetString()}"));
        Assert.Equal(0, (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")))).GetProperty("playlists").GetArrayLength());
        var listed = await SongApi.ListAsync(client, "sort=title");
        Assert.Equal(2, listed.GetProperty("items").EnumerateArray().Single(static item => item.GetProperty("shortcode").GetString() == "n8-2").GetProperty("playlists").GetArrayLength());

        // No Song's revision or updated time moved.
        Assert.Equal(songsBefore, SongRows(factory));
    }

    [Fact]
    public async Task ADuplicateAnUnknownSongAStaleRevisionAndAWrongOrderAreRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "First");
        await SongApi.CreateAsync(client, "Second");
        var id = await CreateAsync(client, "Road trip");
        using (var added = await SendAsync(client, HttpMethod.Post, SongsUri(id), 1, """{"songId":"n8-1"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        // The same Song again, by ID or shortcode, is refused with the Playlist as it is.
        var songId = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")))).GetProperty("id").GetString();
        foreach (var reference in new[] { "N8-1", songId })
        {
            using var twice = await SendAsync(client, HttpMethod.Post, SongsUri(id), 2, JsonSerializer.Serialize(new { songId = reference }));
            var problem = await SetupApi.ProblemAsync(twice, HttpStatusCode.Conflict, "song_already_on_playlist");
            Assert.Equal(["n8-1"], Shortcodes(problem.GetProperty("current")));
        }

        // A Song that does not exist, a reference of another kind, and a wrong type are field errors.
        foreach (var json in new[] { """{"songId":"n8-99"}""", $$"""{"songId":"{{Guid.CreateVersion7()}}"}""", """{"songId":"n8-1-v1"}""", """{"songId":"nonsense"}""", """{"songId":7}""", "{}" })
        {
            using var unknown = await SendAsync(client, HttpMethod.Post, SongsUri(id), 2, json);
            var problem = await SetupApi.ProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("songId", out _), json);
        }

        // A stale revision is a conflict with the Playlist as it is; no revision is 428.
        using (var stale = await SendAsync(client, HttpMethod.Post, SongsUri(id), 1, """{"songId":"n8-2"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(2, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var noRevision = await SendAsync(client, HttpMethod.Put, SongsUri(id), revision: null, """{"songIds":["n8-1"]}"""))
        {
            Assert.Equal((HttpStatusCode)428, noRevision.StatusCode);
        }

        // A new order must name exactly the Songs on the Playlist, each once.
        foreach (var json in new[] { """{"songIds":[]}""", """{"songIds":["n8-1","n8-2"]}""", """{"songIds":["n8-1","n8-1"]}""", """{"songIds":["n8-2"]}""", """{"songIds":["n8-99"]}""" })
        {
            using var mismatch = await SendAsync(client, HttpMethod.Put, SongsUri(id), 2, json);
            var problem = await SetupApi.ProblemAsync(mismatch, HttpStatusCode.Conflict, "order_mismatch");
            Assert.Equal(["n8-1"], Shortcodes(problem.GetProperty("current")));
        }

        foreach (var json in new[] { """{"songIds":"n8-1"}""", """{"songIds":[1]}""", "{}" })
        {
            using var wrong = await SendAsync(client, HttpMethod.Put, SongsUri(id), 2, json);
            var problem = await SetupApi.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("songIds", out _), json);
        }

        // Removing a Song that does not exist, or from a Playlist that does not, is not found.
        foreach (var path in new[] { $"/api/v1/playlists/{id}/songs/n8-99", $"/api/v1/playlists/{id}/songs/n8-1-v1", $"/api/v1/playlists/{Guid.CreateVersion7()}/songs/n8-1" })
        {
            using var gone = await SendAsync(client, HttpMethod.Delete, new Uri(path, UriKind.Relative), 2, json: null);
            await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, "not_found");
        }

        // Wrong titles and descriptions.
        foreach (var json in new[] { $$"""{"title":"{{new string('a', 301)}}"}""", """{"title":"   "}""", "{}", """{"title":7}""", """{"title":null}""" })
        {
            using var refused = await SendAsync(client, HttpMethod.Post, Playlists, revision: null, json);
            await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        foreach (var json in new[] { """{"title":null}""", """{"title":"\t"}""", $$"""{"description":"{{new string('a', 10_001)}}"}""", """{"description":7}""" })
        {
            using var refused = await SendAsync(client, HttpMethod.Patch, PlaylistUri(id), 2, json);
            await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        using (var stale = await SendAsync(client, HttpMethod.Patch, PlaylistUri(id), 1, """{"title":"Renamed"}"""))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        Assert.Equal("1|Road trip|2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) || '|' || min(title) || '|' || min(revision) FROM playlists;"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM playlist_songs;"));
    }

    [Fact]
    public async Task APlaylistHoldsAtMostAThousandSongs()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Seed");
        await SongApi.CreateAsync(client, "One too many");
        var id = await CreateAsync(client, "Everything");

        // 999 more Songs, copied from the first straight in the database, then all 1,000 on the Playlist.
        TestDatabase.Execute(
            factory.DataPath,
            $"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 999)
            INSERT INTO songs (id, shortcode_number, title, title_sort_key, workflow_state_id, current_version_id, created_utc, updated_utc, revision)
            SELECT printf('00000000-0000-7000-8000-%012d', i), 1000 + i, 'Copy ' || i, 'copy ' || i, workflow_state_id, current_version_id, created_utc, updated_utc, 1
            FROM songs, n WHERE shortcode_number = 1;
            INSERT INTO playlist_songs (playlist_id, song_id, position)
            SELECT '{id.ToUpperInvariant()}', id, row_number() OVER (ORDER BY shortcode_number) - 1 FROM songs WHERE shortcode_number <> 2;
            """);
        Assert.Equal(1000, (await ReadAsync(client, id)).GetProperty("songCount").GetInt32());

        using var full = await SendAsync(client, HttpMethod.Post, SongsUri(id), 1, """{"songId":"n8-2"}""");
        var problem = await SetupApi.ProblemAsync(full, HttpStatusCode.Conflict, "playlist_full");
        Assert.Equal(1000, problem.GetProperty("current").GetProperty("songs").GetArrayLength());
        Assert.Equal("1000", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM playlist_songs;"));
    }

    [Fact]
    public async Task ReadingPlaylistsNeedsCatalogReadAndManagingThemNeedsCollectionsWrite()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "First");
        var id = await CreateAsync(client, "Road trip");

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        foreach (var uri in new[] { Playlists, PlaylistUri(id) })
        {
            using var read = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, reader);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        // Each write, by a token with collections.write: create, edit, add, reorder, remove.
        var writes = new (HttpMethod Method, Uri Uri, string Json)[]
        {
            (HttpMethod.Patch, PlaylistUri(id), """{"title":"By token"}"""),
            (HttpMethod.Post, SongsUri(id), """{"songId":"n8-1"}"""),
            (HttpMethod.Put, SongsUri(id), """{"songIds":["n8-1"]}"""),
            (HttpMethod.Delete, new Uri($"/api/v1/playlists/{id}/songs/n8-1", UriKind.Relative), "{}"),
        };
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CollectionsWrite);
        using (var create = await SendAsTokenAsync(client, HttpMethod.Post, Playlists, writer, """{"title":"Created by token"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        var revision = 1;
        foreach (var (method, uri, json) in writes)
        {
            using var write = await SendAsTokenAsync(client, method, uri, writer, json, SongApi.Quoted(revision));
            Assert.True(write.StatusCode == HttpStatusCode.OK, $"{method} {uri}: {await write.Content.ReadAsStringAsync()}");
            revision = (await SetupApi.JsonAsync(write)).GetProperty("revision").GetInt32();
        }

        // Every other scope is refused, naming the one needed, and nothing changes.
        var before = TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(title || '|' || revision) FROM (SELECT * FROM playlists ORDER BY title);");
        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CollectionsWrite))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            foreach (var (method, uri, json) in writes.Prepend((HttpMethod.Post, Playlists, """{"title":"Refused"}""")))
            {
                using var refused = await SendAsTokenAsync(client, method, uri, token, json, SongApi.Quoted(revision));
                var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
                Assert.Equal(CredentialScopes.CollectionsWrite, problem.GetProperty("requiredScope").GetString());
            }
        }

        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            foreach (var uri in new[] { Playlists, PlaylistUri(id) })
            {
                using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, token);
                var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
                Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
            }
        }

        Assert.Equal(before, TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(title || '|' || revision) FROM (SELECT * FROM playlists ORDER BY title);"));
        Assert.Equal("By token|4,Created by token|1", before);

        // Signed out, nothing is served or stored.
        using var signedOut = factory.CreateClient();
        await SetupApi.ProblemAsync(await signedOut.GetAsync(Playlists), HttpStatusCode.Unauthorized, "not_authenticated");
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, Playlists) { Content = JsonContent.Create(new { title = "anonymous" }) };
        anonymous.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        await SetupApi.ProblemAsync(await signedOut.SendAsync(anonymous), HttpStatusCode.Unauthorized, "not_authenticated");
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM playlists;"));
    }

    [Fact]
    public async Task TheSongsListSearchesByTitleOrShortcodeTenAtATime()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        foreach (var title in new[] { "Night Drive", "Midnight", "Daylight", "NIGHTS" })
        {
            await SongApi.CreateAsync(client, title);
        }

        for (var index = 5; index <= 12; index++)
        {
            await SongApi.CreateAsync(client, $"Filler {index}");
        }

        // A title contains the text, ignoring case.
        Assert.Equal(["n8-2", "n8-1", "n8-4"], SongApi.Shortcodes(await SongApi.ListAsync(client, "q=NIGHT&sort=title")));
        Assert.Equal(["n8-3"], SongApi.Shortcodes(await SongApi.ListAsync(client, "q=%20daylight%20")));

        // A shortcode starts with the text, ignoring case: n8-1 and n8-10 to n8-12.
        Assert.Equal(["n8-1", "n8-10", "n8-11", "n8-12"], SongApi.Shortcodes(await SongApi.ListAsync(client, "q=N8-1&sort=title&direction=asc")).Order(StringComparer.Ordinal));
        Assert.Equal(["n8-12"], SongApi.Shortcodes(await SongApi.ListAsync(client, "q=n8-12")));
        Assert.Empty(SongApi.Shortcodes(await SongApi.ListAsync(client, "q=n8-99")));

        // Ten at a time by default, with the total; combined with the other parameters by AND.
        var all = await SongApi.ListAsync(client, "q=n8-");
        Assert.Equal(10, all.GetProperty("items").GetArrayLength());
        Assert.Equal(10, all.GetProperty("pageSize").GetInt32());
        Assert.Equal(12, all.GetProperty("total").GetInt32());
        var state = (await SongApi.ListAsync(client)).GetProperty("items")[0].GetProperty("state").GetProperty("id").GetString();
        Assert.Equal(3, (await SongApi.ListAsync(client, $"q=night&state={state}")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await SongApi.ListAsync(client, "q=night&tag=none&genre=none&artist=none&page=2")).GetProperty("items").GetArrayLength());

        // Nothing for an empty query.
        foreach (var query in new[] { "q=", "q=%20%20" })
        {
            var empty = await SongApi.ListAsync(client, query);
            Assert.Equal(0, empty.GetProperty("total").GetInt32());
            Assert.Equal(0, empty.GetProperty("items").GetArrayLength());
        }

        using var repeated = await client.GetAsync(new Uri("/api/v1/songs?q=a&q=b", UriKind.Relative));
        await SetupApi.ProblemAsync(repeated, HttpStatusCode.BadRequest, "invalid_request");
    }

    private static Uri PlaylistUri(string id) => new($"/api/v1/playlists/{id}", UriKind.Relative);

    private static Uri SongsUri(string id) => new($"/api/v1/playlists/{id}/songs", UriKind.Relative);

    /// <summary>Every Song's shortcode number, revision, and updated time.</summary>
    private static List<string> SongRows(N8TracksApiFactory factory) =>
        TestDatabase.Rows(factory.DataPath, "SELECT shortcode_number || '|' || revision || '|' || updated_utc FROM songs ORDER BY shortcode_number;");

    private static List<string> Shortcodes(JsonElement playlist) =>
        [.. playlist.GetProperty("songs").EnumerateArray().Select(static song => song.GetProperty("shortcode").GetString()!)];

    private static async Task<JsonElement> ReadAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync(PlaylistUri(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<List<string>> TitlesAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/playlists" + query, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray().Select(static row => row.GetProperty("title").GetString()!)];
    }

    /// <summary>Creates a Playlist with <paramref name="title"/>; its ID.</summary>
    private static async Task<string> CreateAsync(HttpClient client, string title)
    {
        using var created = await SendAsync(client, HttpMethod.Post, Playlists, revision: null, JsonSerializer.Serialize(new { title }));
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
