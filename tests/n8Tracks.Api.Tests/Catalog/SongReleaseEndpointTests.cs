using System.Net;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// A Song's release details: the <c>release</c> object of <c>PATCH /api/v1/songs/{reference}</c>
/// (each member optional, null clearing it, links as a whole list, under the Song's revision), the
/// <c>release</c> and <c>warnings</c> of every Song answer (a <c>duplicate_isrc</c> warning naming
/// the other Songs), and the language list (<c>GET /api/v1/languages</c>).
/// </summary>
public sealed class SongReleaseEndpointTests
{
    private static readonly Uri LanguagesUri = new("/api/v1/languages", UriKind.Relative);

    [Fact]
    public async Task EveryReleaseFieldRoundTripsNormalisedAndMovesTheSongsRevisionAndTime()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Running in a Pack");
        var shortcode = song.GetProperty("shortcode").GetString()!;

        // A new Song has a release object with every member null and no links, and no warnings.
        Assert.Equal(
            """{"releaseDate":null,"originalReleaseDate":null,"explicit":null,"copyright":null,"publishing":null,"isrc":null,"language":null,"links":[]}""",
            song.GetProperty("release").GetRawText());
        Assert.Empty(song.GetProperty("warnings").EnumerateArray());

        clock.Advance(TimeSpan.FromMinutes(5));
        var edited = await SongApi.EditAsync(client, shortcode, 1, """
            {"release":{"releaseDate":" 2026-03 ","originalReleaseDate":"1999","explicit":"explicit",
             "copyright":"  ℗ 2026 n8\r\n© 2026 n8 ","publishing":"n8 Songs","isrc":"us-s1z-99-00001","language":" EN ",
             "links":[{"label":" Spotify ","url":" https://open.spotify.com/track/1 "},{"url":"http://example.com/n8"},{"label":null,"url":"https://bandcamp.example/t"}]}}
            """);
        var release = edited.GetProperty("release");
        Assert.Equal("2026-03", release.GetProperty("releaseDate").GetString());
        Assert.Equal("1999", release.GetProperty("originalReleaseDate").GetString());
        Assert.Equal("explicit", release.GetProperty("explicit").GetString());
        Assert.Equal("℗ 2026 n8\n© 2026 n8", release.GetProperty("copyright").GetString());
        Assert.Equal("n8 Songs", release.GetProperty("publishing").GetString());
        Assert.Equal("USS1Z9900001", release.GetProperty("isrc").GetString());
        Assert.Equal("en", release.GetProperty("language").GetString());
        Assert.Equal(
            """[{"label":"Spotify","url":"https://open.spotify.com/track/1"},{"label":null,"url":"http://example.com/n8"},{"label":null,"url":"https://bandcamp.example/t"}]""",
            release.GetProperty("links").GetRawText());
        Assert.Equal(2, edited.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:05:00Z", edited.GetProperty("updatedAt").GetString());
        Assert.Equal("Running in a Pack", edited.GetProperty("title").GetString());

        // Read back by ID, by shortcode, and in the list's rows; stored as answered.
        var read = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song.GetProperty("id").GetString()!)));
        Assert.Equal(release.GetRawText(), read.GetProperty("release").GetRawText());
        Assert.Equal(release.GetRawText(), (await SongApi.ListAsync(client)).GetProperty("items")[0].GetProperty("release").GetRawText());
        Assert.Equal(
            "2026-03|1999|explicit|USS1Z9900001|en",
            TestDatabase.Scalar(factory.DataPath, "SELECT release_date || '|' || original_release_date || '|' || explicit_content || '|' || isrc || '|' || language FROM songs;"));
        Assert.Equal(
            ["0|Spotify|https://open.spotify.com/track/1", "1||http://example.com/n8", "2||https://bandcamp.example/t"],
            TestDatabase.Rows(factory.DataPath, "SELECT position || '|' || ifnull(label, '') || '|' || url FROM song_links ORDER BY position;"));

        // Omitted members are unchanged; the same values again change nothing (no new revision or time).
        clock.Advance(TimeSpan.FromMinutes(5));
        var same = await SongApi.EditAsync(client, shortcode, 2, """{"release":{"isrc":"ISRC US S1Z 99 00001","explicit":"explicit"},"title":"Running in a Pack"}""");
        Assert.Equal(2, same.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:05:00Z", same.GetProperty("updatedAt").GetString());
        Assert.Equal(release.GetRawText(), same.GetProperty("release").GetRawText());

        // A release object with nothing in it, and an edit without one, leave the release alone.
        Assert.Equal(release.GetRawText(), (await SongApi.EditAsync(client, shortcode, 2, """{"release":{}}""")).GetProperty("release").GetRawText());
        var renamed = await SongApi.EditAsync(client, shortcode, 2, """{"title":"Renamed"}""");
        Assert.Equal(release.GetRawText(), renamed.GetProperty("release").GetRawText());

        // One member changed, the rest kept; links reordered as sent.
        var clean = await SongApi.EditAsync(client, shortcode, 3, """{"release":{"explicit":"clean","links":[{"url":"http://example.com/n8"},{"label":"Spotify","url":"https://open.spotify.com/track/1"}]}}""");
        Assert.Equal("clean", clean.GetProperty("release").GetProperty("explicit").GetString());
        Assert.Equal("USS1Z9900001", clean.GetProperty("release").GetProperty("isrc").GetString());
        Assert.Equal("""[{"label":null,"url":"http://example.com/n8"},{"label":"Spotify","url":"https://open.spotify.com/track/1"}]""", clean.GetProperty("release").GetProperty("links").GetRawText());
        Assert.Equal(4, clean.GetProperty("revision").GetInt32());

        // Clearing: null for each member (blank text too) stores none, and null links store none.
        var cleared = await SongApi.EditAsync(client, shortcode, 4, """
            {"release":{"releaseDate":null,"originalReleaseDate":"  ","explicit":null,"copyright":" \n ","publishing":null,"isrc":" - ","language":"","links":null}}
            """);
        Assert.Equal(
            """{"releaseDate":null,"originalReleaseDate":null,"explicit":null,"copyright":null,"publishing":null,"isrc":null,"language":null,"links":[]}""",
            cleared.GetProperty("release").GetRawText());
        Assert.Equal(5, cleared.GetProperty("revision").GetInt32());
        Assert.Equal("||||", TestDatabase.Scalar(factory.DataPath, "SELECT ifnull(release_date, '') || '|' || ifnull(original_release_date, '') || '|' || ifnull(explicit_content, '') || '|' || ifnull(isrc, '') || '|' || ifnull(language, '') FROM songs;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_links;"));
    }

    [Fact]
    public async Task WrongReleaseFieldsAreRefusedByFieldAndChangeNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Running in a Pack");
        var shortcode = song.GetProperty("shortcode").GetString()!;
        await SongApi.EditAsync(client, shortcode, 1, """{"release":{"isrc":"USS1Z9900001","language":"en","links":[{"url":"https://a.example"}]}}""");

        foreach (var (json, field, message) in new[]
        {
            ("""{"release":{"isrc":"US-S1Z-99-0000"}}""", "release.isrc", "An ISRC has 12 characters once spaces and hyphens are left out; this has 11."),
            ("""{"release":{"isrc":"1SS1Z9900001"}}""", "release.isrc", "Enter an ISRC as two letters, three letters or digits, and seven digits (such as US-S1Z-99-00001)."),
            ("""{"release":{"language":"xx"}}""", "release.language", "Choose a language from the list."),
            ("""{"release":{"language":"english"}}""", "release.language", "Choose a language from the list."),
            ("""{"release":{"explicit":"yes"}}""", "release.explicit", "Choose explicit, clean, or null for not set."),
            ("""{"release":{"releaseDate":"2026-02-30"}}""", "release.releaseDate", "That day does not exist in that month."),
            ("""{"release":{"originalReleaseDate":"0999"}}""", "release.originalReleaseDate", "Enter a year from 1000 to 9999."),
            ("""{"release":{"copyright":"{{long}}"}}""".Replace("{{long}}", new string('a', 501), StringComparison.Ordinal), "release.copyright", "Use at most 500 characters."),
            ("""{"release":{"publishing":"{{long}}"}}""".Replace("{{long}}", new string('a', 501), StringComparison.Ordinal), "release.publishing", "Use at most 500 characters."),
            ("""{"release":{"links":[{"label":"FTP","url":"ftp://example.com/a"}]}}""", "release.links", "Link 1: Enter a web address starting with http:// or https://."),
            ("""{"release":{"links":[{"url":"javascript:alert(1)"}]}}""", "release.links", "Link 1: Enter a web address starting with http:// or https://."),
            ("""{"release":{"links":[{"label":"No URL"}]}}""", "release.links", "Send a list of links, each with a url and an optional label."),
            ("""{"release":{"links":"https://a.example"}}""", "release.links", "Send a list of links, each with a url and an optional label."),
            ("""{"release":{"isrc":12}}""", "release.isrc", "Send text or null."),
            ("""{"release":{"explicit":true}}""", "release.explicit", "Send text or null."),
            ("""{"release":null}""", "release", "Send an object of release details, or leave it out."),
            ("""{"release":"2026"}""", "release", "Send an object of release details, or leave it out."),
        })
        {
            using var response = await SongApi.PatchAsync(client, shortcode, SongApi.Quoted(2), json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal([message], problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(static error => error.GetString()));
        }

        // A wrong member refuses the whole edit, the rest of the release and the title included.
        using (var mixed = await SongApi.PatchAsync(client, shortcode, SongApi.Quoted(2), """{"title":"Renamed","release":{"releaseDate":"2026","isrc":"short"}}"""))
        {
            var problem = await SetupApi.ProblemAsync(mixed, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal(["release.isrc"], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
        }

        // A stale revision is a conflict with the current Song; nothing changed.
        using (var stale = await SongApi.PatchAsync(client, shortcode, SongApi.Quoted(1), """{"release":{"isrc":"GBAAA2612345"}}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal("USS1Z9900001", problem.GetProperty("current").GetProperty("release").GetProperty("isrc").GetString());
        }

        Assert.Equal("Running in a Pack|USS1Z9900001|en||2", TestDatabase.Scalar(factory.DataPath, "SELECT title || '|' || isrc || '|' || language || '|' || ifnull(release_date, '') || '|' || revision FROM songs;"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_links;"));
    }

    [Fact]
    public async Task AnIsrcAnotherSongHasIsAllowedWithAWarningNamingTheOtherSongs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var first = await SongApi.CreateAsync(client, "Running in a Pack");
        var second = await SongApi.CreateAsync(client, "anthem");
        var third = await SongApi.CreateAsync(client, "Breathe");
        var (a, b, c) = (Shortcode(first), Shortcode(second), Shortcode(third));

        var only = await SongApi.EditAsync(client, a, 1, """{"release":{"isrc":"US-S1Z-99-00001"}}""");
        Assert.Empty(only.GetProperty("warnings").EnumerateArray());

        // The second Song with it is allowed, and told about the first.
        var shared = await SongApi.EditAsync(client, b, 1, """{"release":{"isrc":"uss1z9900001"}}""");
        Assert.Equal("USS1Z9900001", shared.GetProperty("release").GetProperty("isrc").GetString());
        var warning = Assert.Single(shared.GetProperty("warnings").EnumerateArray());
        Assert.Equal("duplicate_isrc", warning.GetProperty("code").GetString());
        Assert.Equal("release.isrc", warning.GetProperty("field").GetString());
        Assert.Equal("Another Song has this ISRC.", warning.GetProperty("message").GetString());
        Assert.Equal(
            $$"""[{"id":"{{first.GetProperty("id").GetString()}}","shortcode":"{{a}}","title":"Running in a Pack"}]""",
            warning.GetProperty("songs").GetRawText());

        // A third: each names the other two, by title ignoring case; GET answers it too.
        await SongApi.EditAsync(client, c, 1, """{"release":{"isrc":"USS1Z9900001"}}""");
        var read = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(a)));
        var both = Assert.Single(read.GetProperty("warnings").EnumerateArray());
        Assert.Equal("Other Songs have this ISRC.", both.GetProperty("message").GetString());
        Assert.Equal(["anthem", "Breathe"], both.GetProperty("songs").EnumerateArray().Select(static other => other.GetProperty("title").GetString()));

        // The warning lasts only while it applies.
        await SongApi.EditAsync(client, b, 2, """{"release":{"isrc":null}}""");
        await SongApi.EditAsync(client, c, 2, """{"release":{"isrc":"GBAAA2612345"}}""");
        var alone = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(a)));
        Assert.Empty(alone.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task TheLanguageListIsEveryCodeTheEditAcceptsByName()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(LanguagesUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var items = (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(184, items.Count);
        Assert.Equal("""{"code":"ab","name":"Abkhazian"}""", items[0].GetRawText());
        Assert.Contains(items, static item => item.GetRawText() == """{"code":"zxx","name":"No linguistic content"}""");

        // Every code listed is accepted by the edit.
        var song = Shortcode(await SongApi.CreateAsync(client, "Polyglot"));
        var revision = 1;
        foreach (var code in items.Select(static item => item.GetProperty("code").GetString()))
        {
            var edited = await SongApi.EditAsync(client, song, revision, $$$"""{"release":{"language":"{{{code}}}"}}""");
            Assert.Equal(code, edited.GetProperty("release").GetProperty("language").GetString());
            revision = edited.GetProperty("revision").GetInt32();
        }
    }

    [Fact]
    public async Task EditingTheReleaseNeedsSongsWriteAndReadingItAndTheLanguagesNeedsCatalogRead()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = Shortcode(await SongApi.CreateAsync(client, "Running in a Pack"));
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);

        using (var languages = await CredentialApi.SendAsync(client, HttpMethod.Get, LanguagesUri, reader))
        {
            Assert.Equal(184, (await SetupApi.JsonAsync(languages)).GetProperty("items").GetArrayLength());
        }

        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, LanguagesUri, others))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        using (var refused = await BearerPatchAsync(client, song, reader, """{"release":{"isrc":"USS1Z9900001"}}"""))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
        }

        using (var edited = await BearerPatchAsync(client, song, writer, """{"release":{"isrc":"USS1Z9900001","explicit":"clean"}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        using var read = await CredentialApi.SendAsync(client, HttpMethod.Get, SongApi.Song(song), reader);
        var release = (await SetupApi.JsonAsync(read)).GetProperty("release");
        Assert.Equal("USS1Z9900001|clean", $"{release.GetProperty("isrc").GetString()}|{release.GetProperty("explicit").GetString()}");
    }

    private static string Shortcode(JsonElement song) => song.GetProperty("shortcode").GetString()!;

    private static async Task<HttpResponseMessage> BearerPatchAsync(HttpClient client, string song, string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, SongApi.Song(song))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(1)));
        return await client.SendAsync(request);
    }
}
