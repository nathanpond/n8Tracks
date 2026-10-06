using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// A Song's credits: <c>PUT /api/v1/songs/{reference}/credits</c> (one primary Artist or none, and
/// featured Artists in order, under the Song's revision), the default Artist
/// (<c>GET</c>/<c>PUT /api/v1/settings/catalog</c>) applied when a Song is created, the Songs list's
/// <c>artist</c> filter, and Artists' Song counts.
/// </summary>
public sealed class SongCreditEndpointTests
{
    private static readonly Uri Catalog = new("/api/v1/settings/catalog", UriKind.Relative);

    [Fact]
    public async Task CreditsAreReplacedAsAWholeUnderTheSongsRevisionAndMoveItsUpdatedTime()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var n8 = await ArtistAsync(client, "n8");
        var guest = await ArtistAsync(client, "Guest");
        var choir = await ArtistAsync(client, "Choir");
        var song = await SongApi.CreateAsync(client, "Running in a Pack");
        var shortcode = song.GetProperty("shortcode").GetString()!;
        Assert.Equal(JsonValueKind.Null, song.GetProperty("credits").GetProperty("primary").ValueKind);
        Assert.Empty(song.GetProperty("credits").GetProperty("featured").EnumerateArray());

        // A primary Artist and two featured ones, in the order sent.
        clock.Advance(TimeSpan.FromMinutes(5));
        using (var set = await PutCreditsAsync(client, shortcode, 1, n8, [guest, choir]))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal("\"2\"", set.Headers.ETag?.ToString());
            Assert.Contains("no-store", set.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            var credited = await SetupApi.JsonAsync(set);
            Assert.Equal("n8 | Guest, Choir", Credits(credited));
            Assert.Equal(n8, credited.GetProperty("credits").GetProperty("primary").GetProperty("id").GetString());
            Assert.Equal(2, credited.GetProperty("revision").GetInt32());
            Assert.Equal("2026-10-01T09:05:00Z", credited.GetProperty("updatedAt").GetString());
        }

        // Read back by ID and in the list's rows.
        var read = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song.GetProperty("id").GetString()!)));
        Assert.Equal("n8 | Guest, Choir", Credits(read));
        Assert.Equal("n8 | Guest, Choir", Credits((await SongApi.ListAsync(client)).GetProperty("items")[0]));

        // Reordered, then one removed, then the primary cleared: each a new revision.
        Assert.Equal("n8 | Choir, Guest", Credits(await CreditAsync(client, shortcode, 2, n8, [choir, guest])));
        Assert.Equal("n8 | Choir", Credits(await CreditAsync(client, shortcode, 3, n8, [choir])));
        var cleared = await CreditAsync(client, shortcode, 4, null, [choir]);
        Assert.Equal("- | Choir", Credits(cleared));
        Assert.Equal(5, cleared.GetProperty("revision").GetInt32());

        // The same credits again change nothing: no new revision, no new time.
        clock.Advance(TimeSpan.FromMinutes(5));
        var same = await CreditAsync(client, shortcode, 5, null, [choir]);
        Assert.Equal(5, same.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:05:00Z", same.GetProperty("updatedAt").GetString());

        // "Make primary": the featured Artist becomes primary and the previous primary its first featured.
        var promoted = await CreditAsync(client, shortcode, 5, choir, []);
        Assert.Equal("Choir | -", Credits(promoted));
        Assert.Equal("Guest | Choir", Credits(await CreditAsync(client, shortcode, 6, guest, [choir])));

        // A credits write touches nothing else of the Song, and the Song's PATCH leaves them alone.
        Assert.Equal("Running in a Pack", promoted.GetProperty("title").GetString());
        var renamed = await SongApi.EditAsync(client, shortcode, 7, """{"title":"Renamed"}""");
        Assert.Equal("Guest | Choir", Credits(renamed));
        Assert.Equal(
            ["guest-primary-0", "choir-featured-0"],
            TestDatabase.Rows(factory.DataPath, "SELECT lower(a.name) || '-' || c.role || '-' || c.position FROM song_artist_credits c JOIN artists a ON a.id = c.artist_id ORDER BY c.role DESC, c.position;"));
    }

    [Fact]
    public async Task AnArtistIsCreditedOnceAndWrongCreditsAre422StoringNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var n8 = await ArtistAsync(client, "n8");
        var guest = await ArtistAsync(client, "Guest");
        var song = await SongApi.CreateAsync(client, "Song");
        var id = song.GetProperty("id").GetString()!;
        var unknown = Guid.CreateVersion7().ToString();
        var many = Enumerable.Range(0, 51).Select(static _ => Guid.CreateVersion7().ToString()).ToArray();

        var refusals = new (string Json, string Field)[]
        {
            ($$"""{"primaryArtistId":"{{n8}}","featuredArtistIds":["{{n8}}"]}""", "featuredArtistIds"),
            ($$"""{"primaryArtistId":null,"featuredArtistIds":["{{guest}}","{{guest}}"]}""", "featuredArtistIds"),
            ($$"""{"primaryArtistId":null,"featuredArtistIds":[{{string.Join(",", many.Select(static artist => $"\"{artist}\""))}}]}""", "featuredArtistIds"),
            ($$"""{"primaryArtistId":"{{unknown}}","featuredArtistIds":[]}""", "primaryArtistId"),
            ($$"""{"primaryArtistId":null,"featuredArtistIds":["{{unknown}}"]}""", "featuredArtistIds"),
            ("""{"primaryArtistId":"n8","featuredArtistIds":[]}""", "primaryArtistId"),
            ("""{"primaryArtistId":null,"featuredArtistIds":["n8"]}""", "featuredArtistIds"),
            ("""{"primaryArtistId":7,"featuredArtistIds":[]}""", "primaryArtistId"),
            ("""{"featuredArtistIds":[]}""", "primaryArtistId"),
            ("""{"primaryArtistId":null}""", "featuredArtistIds"),
            ("""{"primaryArtistId":null,"featuredArtistIds":null}""", "featuredArtistIds"),
            ("""{"primaryArtistId":null,"featuredArtistIds":[1]}""", "featuredArtistIds"),
        };
        foreach (var (json, field) in refusals)
        {
            using var response = await SendAsync(client, HttpMethod.Put, CreditsOf(id), SongApi.Quoted(1), json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"{json}: {problem}");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_artist_credits;"));
        Assert.Equal(1, (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(id)))).GetProperty("revision").GetInt32());

        // Complement: fifty featured Artists, none twice and not the primary, is a credit list.
        var fifty = new List<string>();
        for (var index = 0; index < 50; index++)
        {
            fifty.Add(await ArtistAsync(client, $"Featured {index}"));
        }

        var credited = await CreditAsync(client, id, 1, n8, [.. fifty]);
        Assert.Equal(50, credited.GetProperty("credits").GetProperty("featured").GetArrayLength());
        Assert.Equal("Featured 49", credited.GetProperty("credits").GetProperty("featured")[49].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AStaleOrMissingRevisionOrAnUnknownSongChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var n8 = await ArtistAsync(client, "n8");
        var song = await SongApi.CreateAsync(client, "Song");
        var id = song.GetProperty("id").GetString()!;
        await SongApi.EditAsync(client, id, 1, """{"title":"Renamed elsewhere"}""");

        using (var stale = await PutCreditsAsync(client, id, 1, n8, []))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal("Renamed elsewhere", problem.GetProperty("current").GetProperty("title").GetString());
            Assert.Equal(2, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var missing = await SendAsync(client, HttpMethod.Put, CreditsOf(id), ifMatch: null, $$"""{"primaryArtistId":"{{n8}}","featuredArtistIds":[]}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, "revision_required");
        }

        using (var unknownSong = await PutCreditsAsync(client, "n8-99", 1, n8, []))
        {
            await SetupApi.ProblemAsync(unknownSong, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_artist_credits;"));

        // Complement: at the current revision the credit is stored.
        Assert.Equal("n8 | -", Credits(await CreditAsync(client, id, 2, n8, [])));
    }

    [Fact]
    public async Task TheDefaultArtistCreditsNewSongsUnlessTheCreateNamesAnotherOrNone()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var n8 = await ArtistAsync(client, "n8");
        var other = await ArtistAsync(client, "Other");
        var before = await SongApi.CreateAsync(client, "Before the default");

        using (var initial = await client.GetAsync(Catalog))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            Assert.Equal("\"1\"", initial.Headers.ETag?.ToString());
            var settings = await SetupApi.JsonAsync(initial);
            Assert.Equal(1, settings.GetProperty("revision").GetInt32());
            Assert.Equal(JsonValueKind.Null, settings.GetProperty("defaultArtist").ValueKind);
        }

        var chosen = await SetDefaultAsync(client, 1, $"\"{n8}\"");
        Assert.Equal(2, chosen.GetProperty("revision").GetInt32());
        Assert.Equal("n8", chosen.GetProperty("defaultArtist").GetProperty("name").GetString());
        Assert.Equal(n8, chosen.GetProperty("defaultArtist").GetProperty("id").GetString());
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT json_extract(value, '$.revision') FROM settings WHERE key = 'catalog.defaultArtistId';"));

        // Omitted: the default. An explicit null: none. Another Artist: that one.
        var withDefault = await SongApi.CreateAsync(client, "With the default");
        Assert.Equal("n8 | -", Credits(withDefault));
        Assert.Equal("- | -", Credits(await CreateAsync(client, """{"title":"No one","primaryArtistId":null}""")));
        Assert.Equal("Other | -", Credits(await CreateAsync(client, $$"""{"title":"Another","primaryArtistId":"{{other}}"}""")));

        // A Song created before the default was set is unchanged.
        Assert.Equal("- | -", Credits(await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(before.GetProperty("id").GetString()!)))));

        // The same default again is no change.
        Assert.Equal(2, (await SetDefaultAsync(client, 2, $"\"{n8}\"")).GetProperty("revision").GetInt32());

        // Clearing the default leaves every existing credit alone, and new Songs get none.
        var cleared = await SetDefaultAsync(client, 2, "null");
        Assert.Equal(3, cleared.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("defaultArtist").ValueKind);
        Assert.Equal("- | -", Credits(await SongApi.CreateAsync(client, "After clearing")));
        Assert.Equal("n8 | -", Credits(await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(withDefault.GetProperty("shortcode").GetString()!)))));
    }

    [Fact]
    public async Task AnUnknownPrimaryArtistOnCreateIs422AndTakesNoShortcode()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var json in new[] { $$"""{"title":"Song","primaryArtistId":"{{Guid.CreateVersion7()}}"}""", """{"title":"Song","primaryArtistId":"n8"}""", """{"title":"Song","primaryArtistId":3}""" })
        {
            using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, SongApi.Songs, json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("primaryArtistId", out _), json);
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs;"));
        Assert.Equal("n8-1", (await SongApi.CreateAsync(client, "First")).GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task ADefaultArtistThatNoLongerExistsIsIgnoredAndShownAsNone()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var gone = await ArtistAsync(client, "Gone");
        await SetDefaultAsync(client, 1, $"\"{gone}\"");
        TestDatabase.Execute(factory.DataPath, "DELETE FROM artists WHERE name = 'Gone';");

        var settings = await SetupApi.JsonAsync(await client.GetAsync(Catalog));
        Assert.Equal(2, settings.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("defaultArtist").ValueKind);
        Assert.Equal("- | -", Credits(await SongApi.CreateAsync(client, "No default")));

        // Choosing it again is refused; clearing it is a change of the stored value.
        using (var refused = await SendAsync(client, HttpMethod.Put, Catalog, SongApi.Quoted(2), $$"""{"defaultArtistId":"{{gone}}"}"""))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("defaultArtistId", out _));
        }

        Assert.Equal(3, (await SetDefaultAsync(client, 2, "null")).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AWrongOrStaleDefaultChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var n8 = await ArtistAsync(client, "n8");

        foreach (var json in new[] { "{}", """{"defaultArtistId":7}""", """{"defaultArtistId":"n8"}""", $$"""{"defaultArtistId":"{{Guid.CreateVersion7()}}"}""" })
        {
            using var response = await SendAsync(client, HttpMethod.Put, Catalog, SongApi.Quoted(1), json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("defaultArtistId", out _), json);
        }

        using (var stale = await SendAsync(client, HttpMethod.Put, Catalog, SongApi.Quoted(2), $$"""{"defaultArtistId":"{{n8}}"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(1, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var missing = await SendAsync(client, HttpMethod.Put, Catalog, ifMatch: null, $$"""{"defaultArtistId":"{{n8}}"}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, "revision_required");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM settings WHERE key = 'catalog.defaultArtistId';"));
    }

    [Fact]
    public async Task TheListFiltersByAnyOfTheArtistsPrimaryOrFeaturedOrByNoCredits()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var n8 = await ArtistAsync(client, "n8");
        var guest = await ArtistAsync(client, "Guest");
        var nobody = await ArtistAsync(client, "Nobody");
        var primary = await SongApi.CreateAsync(client, "Primary");
        var featuring = await SongApi.CreateAsync(client, "Featuring");
        await SongApi.CreateAsync(client, "Uncredited");
        await CreditAsync(client, primary.GetProperty("id").GetString()!, 1, n8, []);
        await CreditAsync(client, featuring.GetProperty("id").GetString()!, 1, n8, [guest]);

        Assert.Equal(["n8-1", "n8-2"], await TitlesAsync(client, $"artist={n8}"));
        Assert.Equal(["n8-2"], await TitlesAsync(client, $"artist={guest}"));
        Assert.Equal(["n8-3"], await TitlesAsync(client, "artist=none"));
        Assert.Equal(["n8-2", "n8-3"], await TitlesAsync(client, $"artist={guest}&artist=none"));
        Assert.Empty(await TitlesAsync(client, $"artist={nobody}"));

        // Combined with other filters by AND.
        Assert.Equal(["n8-2"], await TitlesAsync(client, $"artist={guest}&tag=none&genre=none"));
        Assert.Equal(["n8-1"], await TitlesAsync(client, $"artist={n8}&artist={nobody}&pageSize=1&sort=title&direction=desc"));

        foreach (var wrong in new[] { "artist=n8", $"artist={Guid.CreateVersion7()}", "artist=" })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/songs?{wrong}", UriKind.Relative));
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
            Assert.Contains("artist", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnArtistCountsEachSongCreditingItOnce()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var n8 = await ArtistAsync(client, "n8");
        var guest = await ArtistAsync(client, "Guest");
        var first = await SongApi.CreateAsync(client, "First");
        var second = await SongApi.CreateAsync(client, "Second");
        await CreditAsync(client, first.GetProperty("id").GetString()!, 1, n8, [guest]);
        await CreditAsync(client, second.GetProperty("id").GetString()!, 1, guest, []);

        Assert.Equal(1, await SongCountAsync(client, n8));
        Assert.Equal(2, await SongCountAsync(client, guest));
        var listed = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/artists", UriKind.Relative)));
        Assert.Equal(["Guest 2", "n8 1"], listed.GetProperty("items").EnumerateArray().Select(static artist => $"{artist.GetProperty("name").GetString()} {artist.GetProperty("songCount").GetInt32()}"));
    }

    [Fact]
    public async Task SettingCreditsNeedsSongsWriteAndTheDefaultAppliesToSongsCreatedThroughTheApi()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var n8 = await ArtistAsync(client, "n8");
        await SetDefaultAsync(client, 1, $"\"{n8}\"");
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);

        // A Song created with a token gets the default too.
        using (var created = await BearerAsync(client, HttpMethod.Post, SongApi.Songs, writer, null, """{"title":"Through the API"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("n8 | -", Credits(await SetupApi.JsonAsync(created)));
        }

        var body = """{"primaryArtistId":null,"featuredArtistIds":[]}""";
        using (var set = await BearerAsync(client, HttpMethod.Put, CreditsOf("n8-1"), writer, SongApi.Quoted(1), body))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal("- | -", Credits(await SetupApi.JsonAsync(set)));
        }

        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.SongsWrite))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var refused = await BearerAsync(client, HttpMethod.Put, CreditsOf("n8-1"), token, SongApi.Quoted(2), $$"""{"primaryArtistId":"{{n8}}","featuredArtistIds":[]}""");
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_artist_credits;"));

        // The catalog settings are the session's alone.
        var all = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        using var settings = await BearerAsync(client, HttpMethod.Get, Catalog, all, null, null);
        await SetupApi.ProblemAsync(settings, HttpStatusCode.Forbidden, "session_required");
    }

    private static Uri CreditsOf(string reference) => new($"/api/v1/songs/{reference}/credits", UriKind.Relative);

    /// <summary>"primary | featured, featured", with "-" for none.</summary>
    private static string Credits(JsonElement song)
    {
        var credits = song.GetProperty("credits");
        var primary = credits.GetProperty("primary");
        var featured = credits.GetProperty("featured").EnumerateArray().Select(static artist => artist.GetProperty("name").GetString()).ToList();
        return $"{(primary.ValueKind == JsonValueKind.Null ? "-" : primary.GetProperty("name").GetString())} | {(featured.Count == 0 ? "-" : string.Join(", ", featured))}";
    }

    private static async Task<string> ArtistAsync(HttpClient client, string name)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/artists", UriKind.Relative), JsonSerializer.Serialize(new { name, confirmDuplicate = true }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> PutCreditsAsync(HttpClient client, string reference, int revision, string? primary, string[] featured) =>
        SendAsync(
            client,
            HttpMethod.Put,
            CreditsOf(reference),
            SongApi.Quoted(revision),
            JsonSerializer.Serialize(new { primaryArtistId = primary, featuredArtistIds = featured }));

    private static async Task<JsonElement> CreditAsync(HttpClient client, string reference, int revision, string? primary, string[] featured)
    {
        using var response = await PutCreditsAsync(client, reference, revision, primary, featured);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string json)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, SongApi.Songs, json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> SetDefaultAsync(HttpClient client, int revision, string idJson)
    {
        using var response = await SendAsync(client, HttpMethod.Put, Catalog, SongApi.Quoted(revision), $$"""{"defaultArtistId":{{idJson}}}""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var settings = await SetupApi.JsonAsync(response);
        Assert.Equal(SongApi.Quoted(settings.GetProperty("revision").GetInt32()), response.Headers.ETag?.ToString());
        return settings;
    }

    private static async Task<int> SongCountAsync(HttpClient client, string artistId) =>
        (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/artists/{artistId}", UriKind.Relative)))).GetProperty("songCount").GetInt32();

    private static async Task<List<string>> TitlesAsync(HttpClient client, string query) =>
        [.. SongApi.Shortcodes(await SongApi.ListAsync(client, query)).Order(StringComparer.Ordinal)];

    /// <summary>Sends <paramref name="json"/> with the anti-forgery header and, when given, <paramref name="ifMatch"/>.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string? ifMatch, string json)
    {
        using var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    /// <summary>Sends <paramref name="json"/> (when given) with a Bearer token and no anti-forgery header, as a non-browser client does.</summary>
    private static async Task<HttpResponseMessage> BearerAsync(HttpClient client, HttpMethod method, Uri uri, string token, string? ifMatch, string? json)
    {
        using var request = new HttpRequestMessage(method, uri);
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
