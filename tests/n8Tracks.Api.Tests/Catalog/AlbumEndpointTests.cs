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
/// Albums: <c>GET /api/v1/albums</c> (a page by title, release date, or Album Artist) and
/// <c>GET /api/v1/albums/{id}</c> need <c>catalog.read</c>; <c>POST /api/v1/albums</c> (a title) and
/// <c>PATCH /api/v1/albums/{id}</c> (under the Album's revision) need <c>collections.write</c>. A
/// UPC/EAN another Album has is allowed, with a <c>duplicate_upc</c> warning.
/// </summary>
public sealed class AlbumEndpointTests
{
    private static readonly Uri Albums = new("/api/v1/albums", UriKind.Relative);

    [Fact]
    public async Task AnAlbumIsCreatedFromATitleAndReadBack()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var created = await SendAsync(client, HttpMethod.Post, Albums, revision: null, """{"title":"  Pack EP  "}""");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.ToString());
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var album = await SetupApi.JsonAsync(created);
        var id = album.GetProperty("id").GetString()!;
        Assert.Equal($"/api/v1/albums/{id}", created.Headers.Location?.OriginalString);
        Assert.Equal("Pack EP", album.GetProperty("title").GetString());
        foreach (var field in new[] { "description", "albumArtist", "releaseDate", "originalReleaseDate", "upc", "copyright", "publishing" })
        {
            Assert.Equal(JsonValueKind.Null, album.GetProperty(field).ValueKind);
        }

        Assert.Equal(0, album.GetProperty("links").GetArrayLength());
        Assert.Equal(0, album.GetProperty("warnings").GetArrayLength());
        Assert.Equal(0, album.GetProperty("songCount").GetInt32());
        Assert.Equal(1, album.GetProperty("revision").GetInt32());

        using var read = await client.GetAsync(AlbumUri(id));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("\"1\"", read.Headers.ETag?.ToString());
        Assert.Equal(album.GetRawText(), (await SetupApi.JsonAsync(read)).GetRawText());

        // Titles need not be unique.
        clock.Advance(TimeSpan.FromSeconds(1));
        using (var again = await SendAsync(client, HttpMethod.Post, Albums, revision: null, """{"title":"pack ep"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        }

        Assert.Equal("Pack EP|PACK EP|1,pack ep|PACK EP|1", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(title || '|' || title_key || '|' || revision) FROM (SELECT * FROM albums ORDER BY created_utc, id);"));

        using var missing = await client.GetAsync(AlbumUri(Guid.CreateVersion7().ToString()));
        await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task AnEditSetsTheAlbumArtistAndReleaseDetailsUnderTheAlbumsRevision()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, "Pack EP");
        var artist = await CreateArtistAsync(client, "n8");
        clock.Advance(TimeSpan.FromMinutes(5));

        using (var edited = await SendAsync(client, HttpMethod.Patch, AlbumUri(id), 1, $$"""
            {"albumArtistId":"{{artist}}","releaseDate":" 2026-03 ","originalReleaseDate":"1999","upc":"0 36000-29145 2",
             "description":"About the EP.\r\n","copyright":"© 2026 n8","publishing":"℗ 2026 n8\nAll rights reserved.",
             "links":[{"label":" Shop ","url":" https://example.com/pack "},{"url":"http://example.org"}]}
            """))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            Assert.Equal("\"2\"", edited.Headers.ETag?.ToString());
            var body = await SetupApi.JsonAsync(edited);
            Assert.Equal("Pack EP", body.GetProperty("title").GetString());
            Assert.Equal(artist, body.GetProperty("albumArtist").GetProperty("id").GetString());
            Assert.Equal("n8", body.GetProperty("albumArtist").GetProperty("name").GetString());
            Assert.Equal("2026-03", body.GetProperty("releaseDate").GetString());
            Assert.Equal("1999", body.GetProperty("originalReleaseDate").GetString());
            Assert.Equal("036000291452", body.GetProperty("upc").GetString());
            Assert.Equal("About the EP.", body.GetProperty("description").GetString());
            Assert.Equal("© 2026 n8", body.GetProperty("copyright").GetString());
            Assert.Equal("℗ 2026 n8\nAll rights reserved.", body.GetProperty("publishing").GetString());
            Assert.Equal(["Shop https://example.com/pack", "- http://example.org"], Links(body));
            Assert.NotEqual(body.GetProperty("createdAt").GetString(), body.GetProperty("updatedAt").GetString());
        }

        Assert.Equal("036000291452|0036000291452", TestDatabase.Scalar(factory.DataPath, "SELECT upc, upc_key FROM albums;"));

        // The Artist counts the Album it is Album Artist of.
        using (var read = await client.GetAsync(new Uri($"/api/v1/artists/{artist}", UriKind.Relative)))
        {
            Assert.Equal(1, (await SetupApi.JsonAsync(read)).GetProperty("albumCount").GetInt32());
        }

        // Sending what the Album has is no change and keeps the revision.
        using (var same = await SendAsync(client, HttpMethod.Patch, AlbumUri(id), 2, """{"title":" Pack EP ","upc":"036000291452","releaseDate":"2026-03"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal("\"2\"", same.Headers.ETag?.ToString());
        }

        // Null or blank clears an optional field; a field not sent is kept.
        using (var cleared = await SendAsync(client, HttpMethod.Patch, AlbumUri(id), 2, """{"albumArtistId":null,"upc":"  ","description":null,"links":null,"title":"Pack EP (Deluxe)"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            var body = await SetupApi.JsonAsync(cleared);
            Assert.Equal(3, body.GetProperty("revision").GetInt32());
            Assert.Equal("Pack EP (Deluxe)", body.GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("albumArtist").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("upc").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("description").ValueKind);
            Assert.Equal(0, body.GetProperty("links").GetArrayLength());
            Assert.Equal("2026-03", body.GetProperty("releaseDate").GetString());
            Assert.Equal("© 2026 n8", body.GetProperty("copyright").GetString());
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM album_links;"));
    }

    [Fact]
    public async Task WrongFieldsAStaleRevisionAndAMissingAlbumAreRefusedAndNothingIsStored()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        // Create: an over-long, blank, missing, or wrongly typed title.
        foreach (var json in new[] { $$"""{"title":"{{new string('a', 301)}}"}""", """{"title":"   "}""", "{}", """{"title":7}""" })
        {
            using var refused = await SendAsync(client, HttpMethod.Post, Albums, revision: null, json);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("title", out _), json);
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM albums;"));

        var id = await CreateAsync(client, "Pack EP");
        var cases = new (string Json, string Field, string? Error)[]
        {
            ("""{"upc":"036000291453"}""", "upc", "The check digit is wrong: check the code for a typing mistake."),
            ("""{"upc":"12345"}""", "upc", "Enter a UPC of 12 digits or an EAN of 13 digits."),
            ($$"""{"title":"{{new string('a', 301)}}"}""", "title", "Use at most 300 characters."),
            ("""{"title":null}""", "title", "Enter a title."),
            ("""{"releaseDate":"2025-02-29"}""", "releaseDate", "That day does not exist in that month."),
            ("""{"originalReleaseDate":"0999"}""", "originalReleaseDate", "Enter a year from 1000 to 9999."),
            ($$"""{"description":"{{new string('a', 10_001)}}"}""", "description", "Use at most 10,000 characters."),
            ($$"""{"copyright":"{{new string('a', 501)}}"}""", "copyright", "Use at most 500 characters."),
            ($$"""{"publishing":"{{new string('a', 501)}}"}""", "publishing", "Use at most 500 characters."),
            ("""{"links":[{"url":"ftp://example.com"}]}""", "links", "Link 1: Enter a web address starting with http:// or https://."),
            ("""{"links":"https://example.com"}""", "links", null),
            ("""{"albumArtistId":"not-an-id"}""", "albumArtistId", "Send an Artist ID or null."),
            ($$"""{"albumArtistId":"{{Guid.CreateVersion7()}}"}""", "albumArtistId", "There is no such Artist."),
            ("""{"upc":12}""", "upc", "Send text."),
        };
        foreach (var (json, field, error) in cases)
        {
            using var refused = await SendAsync(client, HttpMethod.Patch, AlbumUri(id), 1, json);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
            var errors = problem.GetProperty("errors").GetProperty(field);
            if (error is not null)
            {
                Assert.Equal(error, errors[0].GetString());
            }
        }

        // A stale revision is 409 with the Album as it is; no revision is 428; a wrong one 400.
        using (var stale = await SendAsync(client, HttpMethod.Patch, AlbumUri(id), 7, """{"upc":"036000291452"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(1, problem.GetProperty("current").GetProperty("revision").GetInt32());
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("current").GetProperty("upc").ValueKind);
        }

        using (var unconditional = await SendAsync(client, HttpMethod.Patch, AlbumUri(id), revision: null, """{"upc":"036000291452"}"""))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, unconditional.StatusCode);
        }

        using (var missing = await SendAsync(client, HttpMethod.Patch, AlbumUri(Guid.CreateVersion7().ToString()), 1, """{"upc":"036000291452"}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal("Pack EP||||1", TestDatabase.Scalar(factory.DataPath, "SELECT title, ifnull(upc, ''), ifnull(release_date, ''), ifnull(album_artist_id, ''), revision FROM albums;"));
    }

    [Fact]
    public async Task AUpcAnotherAlbumHasIsAllowedWithAWarning()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var first = await CreateAsync(client, "Pack EP");
        var second = await CreateAsync(client, "Another");
        var third = await CreateAsync(client, "Unrelated");

        using (var set = await SendAsync(client, HttpMethod.Patch, AlbumUri(first), 1, """{"upc":"036000291452"}"""))
        {
            Assert.Equal(0, (await SetupApi.JsonAsync(set)).GetProperty("warnings").GetArrayLength());
        }

        // The same code as a 13-digit EAN with a leading 0 counts as the same.
        using (var same = await SendAsync(client, HttpMethod.Patch, AlbumUri(second), 1, """{"upc":"0036000291452"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            var warning = Assert.Single((await SetupApi.JsonAsync(same)).GetProperty("warnings").EnumerateArray());
            Assert.Equal("duplicate_upc", warning.GetProperty("code").GetString());
            Assert.Equal("upc", warning.GetProperty("field").GetString());
            Assert.Equal("Another Album has this UPC/EAN.", warning.GetProperty("message").GetString());
            var other = Assert.Single(warning.GetProperty("albums").EnumerateArray());
            Assert.Equal(first, other.GetProperty("id").GetString());
            Assert.Equal("Pack EP", other.GetProperty("title").GetString());
        }

        // GET shows the warning too, on both Albums, and the list rows carry it.
        using (var read = await client.GetAsync(AlbumUri(first)))
        {
            var warning = Assert.Single((await SetupApi.JsonAsync(read)).GetProperty("warnings").EnumerateArray());
            Assert.Equal(second, Assert.Single(warning.GetProperty("albums").EnumerateArray()).GetProperty("id").GetString());
        }

        using (var list = await client.GetAsync(Albums))
        {
            var rows = (await SetupApi.JsonAsync(list)).GetProperty("items").EnumerateArray().ToDictionary(static row => row.GetProperty("id").GetString()!, static row => row.GetProperty("warnings").GetArrayLength());
            Assert.Equal(1, rows[first]);
            Assert.Equal(1, rows[second]);
            Assert.Equal(0, rows[third]);
        }

        // Complement: once the code differs, the warning goes.
        using (var changed = await SendAsync(client, HttpMethod.Patch, AlbumUri(second), 2, """{"upc":"4006381333931"}"""))
        {
            Assert.Equal(0, (await SetupApi.JsonAsync(changed)).GetProperty("warnings").GetArrayLength());
        }
    }

    [Fact]
    public async Task TheListIsByTitleReleaseDateOrArtistWithMissingValuesLastAndPaged()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var zed = await CreateArtistAsync(client, "Zed");
        var ann = await CreateArtistAsync(client, "ann");

        async Task<string> AlbumAsync(string title, string json)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var id = await CreateAsync(client, title);
            using var edited = await SendAsync(client, HttpMethod.Patch, AlbumUri(id), 1, json);
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            return id;
        }

        await AlbumAsync("beta", $$"""{"releaseDate":"2026-03","albumArtistId":"{{zed}}"}""");
        await AlbumAsync("Alpha", $$"""{"releaseDate":"2026-03-01","albumArtistId":"{{ann}}"}""");
        await AlbumAsync("gamma", """{"originalReleaseDate":"2025"}""");
        await AlbumAsync("alpha", "{}");
        await AlbumAsync("Delta", $$"""{"releaseDate":"2026","originalReleaseDate":"1990","albumArtistId":"{{ann}}"}""");

        Assert.Equal(["Alpha", "alpha", "beta", "Delta", "gamma"], await TitlesAsync(client, ""));
        Assert.Equal(["gamma", "Delta", "beta", "Alpha", "alpha"], await TitlesAsync(client, "?direction=desc"));

        // The shown date (release, else original) by its earliest day; undated last both ways.
        Assert.Equal(["gamma", "Delta", "beta", "Alpha", "alpha"], await TitlesAsync(client, "?sort=releaseDate"));
        Assert.Equal(["Alpha", "beta", "Delta", "gamma", "alpha"], await TitlesAsync(client, "?sort=releaseDate&direction=desc"));

        // The Album Artist's name ignoring case; no Album Artist last both ways.
        Assert.Equal(["Alpha", "Delta", "beta", "alpha", "gamma"], await TitlesAsync(client, "?sort=artist"));
        Assert.Equal(["beta", "Alpha", "Delta", "alpha", "gamma"], await TitlesAsync(client, "?sort=artist&direction=desc"));

        // One Artist's Albums.
        Assert.Equal(["Alpha", "Delta"], await TitlesAsync(client, $"?artist={ann}"));
        Assert.Empty(await TitlesAsync(client, $"?artist={Guid.CreateVersion7()}"));

        using (var paged = await client.GetAsync(new Uri("/api/v1/albums?page=2&pageSize=2", UriKind.Relative)))
        {
            var body = await SetupApi.JsonAsync(paged);
            Assert.Equal(5, body.GetProperty("total").GetInt32());
            Assert.Equal(2, body.GetProperty("page").GetInt32());
            Assert.Equal(2, body.GetProperty("pageSize").GetInt32());
            Assert.Equal(["beta", "Delta"], body.GetProperty("items").EnumerateArray().Select(static row => row.GetProperty("title").GetString()!).ToList());
        }

        using (var defaults = await client.GetAsync(Albums))
        {
            Assert.Equal(50, (await SetupApi.JsonAsync(defaults)).GetProperty("pageSize").GetInt32());
        }

        foreach (var query in new[] { "?sort=name", "?direction=up", "?page=0", "?pageSize=101", "?artist=n8", "?sort=title&sort=artist" })
        {
            using var refused = await client.GetAsync(new Uri("/api/v1/albums" + query, UriKind.Relative));
            await SetupApi.ProblemAsync(refused, HttpStatusCode.BadRequest, "invalid_request");
        }
    }

    [Fact]
    public async Task ReadingAlbumsNeedsCatalogReadAndWritingThemNeedsCollectionsWrite()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, "Pack EP");

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        foreach (var uri in new[] { Albums, AlbumUri(id) })
        {
            using var read = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, reader);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CollectionsWrite);
        using (var create = await SendAsTokenAsync(client, HttpMethod.Post, Albums, writer, """{"title":"By token"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        using (var edit = await SendAsTokenAsync(client, HttpMethod.Patch, AlbumUri(id), writer, """{"releaseDate":"2026"}""", ifMatch: "\"1\""))
        {
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        }

        // Every other scope is refused, naming the one needed, and nothing is stored.
        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CollectionsWrite))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var create = await SendAsTokenAsync(client, HttpMethod.Post, Albums, token, """{"title":"Refused"}""");
            var problem = await SetupApi.ProblemAsync(create, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CollectionsWrite, problem.GetProperty("requiredScope").GetString());
            using var edit = await SendAsTokenAsync(client, HttpMethod.Patch, AlbumUri(id), token, """{"title":"Refused"}""", ifMatch: "\"2\"");
            problem = await SetupApi.ProblemAsync(edit, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CollectionsWrite, problem.GetProperty("requiredScope").GetString());
        }

        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            foreach (var uri in new[] { Albums, AlbumUri(id) })
            {
                using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, token);
                var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
                Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
            }
        }

        Assert.Equal("By token,Pack EP", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(title) FROM (SELECT title FROM albums ORDER BY title);"));
        Assert.Equal("2026|2", TestDatabase.Scalar(factory.DataPath, "SELECT release_date, revision FROM albums WHERE title = 'Pack EP';"));

        // Signed out, nothing is served or stored.
        using var signedOut = factory.CreateClient();
        await SetupApi.ProblemAsync(await signedOut.GetAsync(Albums), HttpStatusCode.Unauthorized, "not_authenticated");
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, Albums) { Content = JsonContent.Create(new { title = "anonymous" }) };
        anonymous.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        await SetupApi.ProblemAsync(await signedOut.SendAsync(anonymous), HttpStatusCode.Unauthorized, "not_authenticated");
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM albums;"));
    }

    private static Uri AlbumUri(string id) => new($"/api/v1/albums/{id}", UriKind.Relative);

    private static async Task<List<string>> TitlesAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/albums" + query, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray().Select(static row => row.GetProperty("title").GetString()!)];
    }

    /// <summary>Creates an Album with <paramref name="title"/>; its ID.</summary>
    private static async Task<string> CreateAsync(HttpClient client, string title)
    {
        using var created = await SendAsync(client, HttpMethod.Post, Albums, revision: null, JsonSerializer.Serialize(new { title }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await SetupApi.JsonAsync(created)).GetProperty("id").GetString()!;
    }

    /// <summary>Creates an Artist named <paramref name="name"/>; its ID.</summary>
    private static async Task<string> CreateArtistAsync(HttpClient client, string name)
    {
        using var created = await SendAsync(client, HttpMethod.Post, new Uri("/api/v1/artists", UriKind.Relative), revision: null, JsonSerializer.Serialize(new { name }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await SetupApi.JsonAsync(created)).GetProperty("id").GetString()!;
    }

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

    /// <summary>Each link as "label url", with "-" for no label.</summary>
    private static List<string> Links(JsonElement album) =>
        [.. album.GetProperty("links").EnumerateArray().Select(static link => $"{(link.GetProperty("label").ValueKind == JsonValueKind.Null ? "-" : link.GetProperty("label").GetString())} {link.GetProperty("url").GetString()}")];
}
