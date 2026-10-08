using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// Genres: <c>GET</c> and <c>POST /api/v1/genres</c>, a Song's Genres and notes through the Song's
/// own <c>PATCH</c> (under its revision), and the Songs list's <c>genre</c> filter.
/// </summary>
public sealed class GenreEndpointTests
{
    private static readonly Uri Genres = new("/api/v1/genres", UriKind.Relative);

    [Fact]
    public async Task CreatingAGenreStoresItOnceWhateverTheLetterCaseOrSpacing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var created = await PostGenreAsync(client, new { name = "  Indie   Rock " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var genre = await SetupApi.JsonAsync(created);
        Assert.Equal("Indie Rock", genre.GetProperty("name").GetString());
        Assert.Equal(0, genre.GetProperty("songCount").GetInt32());
        var id = genre.GetProperty("id").GetString();
        Assert.EndsWith($"/api/v1/genres/{id}", created.Headers.Location?.ToString(), StringComparison.Ordinal);

        // Complement: the same name in another case (and spacing) is the same Genre, answered with 200.
        foreach (var name in new[] { "indie rock", "INDIE  ROCK", "Indie Rock" })
        {
            using var again = await PostGenreAsync(client, new { name });
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var existing = await SetupApi.JsonAsync(again);
            Assert.Equal(id, existing.GetProperty("id").GetString());
            Assert.Equal("Indie Rock", existing.GetProperty("name").GetString());
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM genres;"));
        Assert.Equal("Indie Rock|INDIE ROCK", TestDatabase.Scalar(factory.DataPath, "SELECT name, name_key FROM genres;"));
    }

    [Fact]
    public async Task AWrongNameIs422AndStoresNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var body in new object[] { new { }, new { name = "   " }, new { name = new string('a', 51) }, new { name = "Bell\u0007" } })
        {
            using var response = await PostGenreAsync(client, body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM genres;"));

        // Complement: fifty characters is a name.
        using var longest = await PostGenreAsync(client, new { name = new string('a', 50) });
        Assert.Equal(HttpStatusCode.Created, longest.StatusCode);
    }

    [Fact]
    public async Task GenresAreListedAlphabeticallyWithTheirSongCounts()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var folk = await CreateGenreAsync(client, "folk");
        var ambient = await CreateGenreAsync(client, "Ambient");
        var rock = await CreateGenreAsync(client, "Rock");
        var first = await SongApi.CreateAsync(client, "First");
        var second = await SongApi.CreateAsync(client, "Second");
        await AssignAsync(client, first, folk, rock);
        await AssignAsync(client, second, folk);

        var list = await SetupApi.JsonAsync(await client.GetAsync(Genres));

        Assert.Equal(
            ["Ambient 0", "folk 2", "Rock 1"],
            list.GetProperty("items").EnumerateArray().Select(static genre => $"{genre.GetProperty("name").GetString()} {genre.GetProperty("songCount").GetInt32()}"));
        Assert.Equal(ambient, list.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task AssigningGenresReplacesTheSongsListUnderItsRevisionAndMovesItsUpdatedTime()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Running in a Pack");
        var id = song.GetProperty("id").GetString()!;
        Assert.Empty(song.GetProperty("genres").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, song.GetProperty("notes").ValueKind);

        // Create on the spot, then assign: what the picker does.
        var rock = await CreateGenreAsync(client, "Indie Rock");
        var folk = await CreateGenreAsync(client, "Folk");
        clock.Advance(TimeSpan.FromMinutes(5));
        var assigned = await SongApi.EditAsync(client, id, 1, $$"""{"genreIds":["{{rock}}","{{folk}}","{{rock}}"]}""");

        // Alphabetical, each once, and the Song's revision and updated time moved.
        Assert.Equal(["Folk", "Indie Rock"], Names(assigned));
        Assert.Equal(folk, assigned.GetProperty("genres")[0].GetProperty("id").GetString());
        Assert.Equal(2, assigned.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:05:00Z", assigned.GetProperty("updatedAt").GetString());
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_genres;"));

        // Read back by shortcode, and in the list's rows.
        var read = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song.GetProperty("shortcode").GetString()!)));
        Assert.Equal(["Folk", "Indie Rock"], Names(read));
        var row = (await SongApi.ListAsync(client)).GetProperty("items")[0];
        Assert.Equal(["Folk", "Indie Rock"], Names(row));

        // The same Genres again change nothing: no new revision.
        clock.Advance(TimeSpan.FromMinutes(5));
        var same = await SongApi.EditAsync(client, id, 2, $$"""{"genreIds":["{{folk}}","{{rock}}"]}""");
        Assert.Equal(2, same.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:05:00Z", same.GetProperty("updatedAt").GetString());

        // Removing one, and then all, is a change too.
        var removed = await SongApi.EditAsync(client, id, 2, $$"""{"genreIds":["{{rock}}"]}""");
        Assert.Equal(["Indie Rock"], Names(removed));
        Assert.Equal(3, removed.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:10:00Z", removed.GetProperty("updatedAt").GetString());
        var none = await SongApi.EditAsync(client, id, 3, """{"genreIds":[]}""");
        Assert.Empty(none.GetProperty("genres").EnumerateArray());
        Assert.Equal(4, none.GetProperty("revision").GetInt32());

        // An edit without genreIds leaves them alone.
        await SongApi.EditAsync(client, id, 4, $$"""{"genreIds":["{{folk}}"]}""");
        var titled = await SongApi.EditAsync(client, id, 5, """{"title":"Renamed"}""");
        Assert.Equal(["Folk"], Names(titled));
    }

    [Fact]
    public async Task RemovingAGenreFromASongLeavesTheGenreAndItsOtherSongs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var folk = await CreateGenreAsync(client, "Folk");
        var first = await SongApi.CreateAsync(client, "First");
        var second = await SongApi.CreateAsync(client, "Second");
        await AssignAsync(client, first, folk);
        await AssignAsync(client, second, folk);
        var secondBefore = TestDatabase.Scalar(factory.DataPath, $"SELECT revision, updated_utc FROM songs WHERE shortcode_number = 2;");

        await SongApi.EditAsync(client, first.GetProperty("id").GetString()!, 2, """{"genreIds":[]}""");

        var genres = await SetupApi.JsonAsync(await client.GetAsync(Genres));
        var listed = Assert.Single(genres.GetProperty("items").EnumerateArray());
        Assert.Equal(folk, listed.GetProperty("id").GetString());
        Assert.Equal(1, listed.GetProperty("songCount").GetInt32());
        var other = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(second.GetProperty("id").GetString()!)));
        Assert.Equal(["Folk"], Names(other));
        Assert.Equal(secondBefore, TestDatabase.Scalar(factory.DataPath, $"SELECT revision, updated_utc FROM songs WHERE shortcode_number = 2;"));
    }

    [Fact]
    public async Task AGenreThatDoesNotExistIs422OnGenreIdsAndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var folk = await CreateGenreAsync(client, "Folk");
        var song = await SongApi.CreateAsync(client, "Song");
        var id = song.GetProperty("id").GetString()!;
        var before = TestDatabase.Scalar(factory.DataPath, "SELECT title, revision, updated_utc FROM songs;");

        var bodies = new[]
        {
            $$"""{"title":"Changed","genreIds":["{{folk}}","{{Guid.CreateVersion7()}}"]}""",
            """{"genreIds":["not an id"]}""",
            """{"genreIds":null}""",
            """{"genreIds":"Folk"}""",
            """{"genreIds":[1]}""",
        };
        foreach (var body in bodies)
        {
            using var response = await SongApi.PatchAsync(client, id, "\"1\"", body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("genreIds", out _), body);
        }

        Assert.Equal(before, TestDatabase.Scalar(factory.DataPath, "SELECT title, revision, updated_utc FROM songs;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_genres;"));
    }

    [Fact]
    public async Task AStaleRevisionIs409WithTheCurrentGenresAndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var folk = await CreateGenreAsync(client, "Folk");
        var rock = await CreateGenreAsync(client, "Rock");
        var song = await SongApi.CreateAsync(client, "Song");
        var id = song.GetProperty("id").GetString()!;
        await SongApi.EditAsync(client, id, 1, $$"""{"genreIds":["{{folk}}"],"notes":"Elsewhere"}""");

        using var response = await SongApi.PatchAsync(client, id, "\"1\"", $$"""{"genreIds":["{{rock}}"]}""");

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "revision_conflict");
        var current = problem.GetProperty("current");
        Assert.Equal(["Folk"], Names(current));
        Assert.Equal("Elsewhere", current.GetProperty("notes").GetString());
        Assert.Equal(2, current.GetProperty("revision").GetInt32());
        Assert.Equal(folk.ToUpperInvariant(), TestDatabase.Scalar(factory.DataPath, "SELECT genre_id FROM song_genres;"));
    }

    [Fact]
    public async Task NotesAreSavedWithTheSongsRevisionNormalisedAndLimited()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = (await SongApi.CreateAsync(client, "Song", "A concept")).GetProperty("id").GetString()!;

        var noted = await SongApi.EditAsync(client, id, 1, """{"notes":"  Bridge needs work.\r\nTry a key change. "}""");
        Assert.Equal("Bridge needs work.\nTry a key change.", noted.GetProperty("notes").GetString());
        Assert.Equal("A concept", noted.GetProperty("concept").GetString());
        Assert.Equal(2, noted.GetProperty("revision").GetInt32());

        var longest = await SongApi.EditAsync(client, id, 2, $$"""{"notes":"{{new string('n', 10_000)}}"}""");
        Assert.Equal(10_000, longest.GetProperty("notes").GetString()!.Length);

        using var tooLong = await SongApi.PatchAsync(client, id, "\"3\"", $$"""{"notes":"{{new string('n', 10_001)}}"}""");
        var problem = await SetupApi.ProblemAsync(tooLong, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Equal("Use at most 10,000 characters.", problem.GetProperty("errors").GetProperty("notes")[0].GetString());

        foreach (var clear in new[] { """{"notes":null}""", """{"notes":"   "}""" })
        {
            var revision = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(id)))).GetProperty("revision").GetInt32();
            var cleared = await SongApi.EditAsync(client, id, revision, clear);
            Assert.Equal(JsonValueKind.Null, cleared.GetProperty("notes").ValueKind);
        }

        Assert.Equal(string.Empty, TestDatabase.Scalar(factory.DataPath, "SELECT coalesce(notes, '') FROM songs;"));
    }

    [Fact]
    public async Task ReadingGenresNeedsCatalogReadAndCreatingOrAssigningThemNeedsSongsWrite()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = SongApi.Song((await SongApi.CreateAsync(client, "Song")).GetProperty("shortcode").GetString()!);

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, Genres, reader))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using (var create = await SendAsTokenAsync(client, HttpMethod.Post, Genres, writer, """{"name":"Folk"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        var folk = (await SetupApi.JsonAsync(await client.GetAsync(Genres))).GetProperty("items")[0].GetProperty("id").GetString();
        using (var assign = await SendAsTokenAsync(client, HttpMethod.Patch, song, writer, $$"""{"genreIds":["{{folk}}"]}""", ifMatch: "\"1\""))
        {
            Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        }

        // Every other scope is refused, naming the one needed, and nothing is stored.
        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.SongsWrite))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var create = await SendAsTokenAsync(client, HttpMethod.Post, Genres, token, """{"name":"Refused"}""");
            var problem = await SetupApi.ProblemAsync(create, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
            using var assign = await SendAsTokenAsync(client, HttpMethod.Patch, song, token, """{"genreIds":[]}""", ifMatch: "\"2\"");
            problem = await SetupApi.ProblemAsync(assign, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
        }

        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Genres, token);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        Assert.Equal("Folk", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name) FROM genres;"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_genres;"));

        // Signed out, nothing is served.
        using var signedOut = factory.CreateClient();
        await SetupApi.ProblemAsync(await signedOut.GetAsync(Genres), HttpStatusCode.Unauthorized, "not_authenticated");
    }

    [Fact]
    public async Task TheSongsListIsFilteredByAnyOfSeveralGenresOrByNone()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var folk = await CreateGenreAsync(client, "Folk");
        var rock = await CreateGenreAsync(client, "Rock");
        var jazz = await CreateGenreAsync(client, "Jazz");
        var first = await SongApi.CreateAsync(client, "A folk song");
        var second = await SongApi.CreateAsync(client, "B rock song");
        var third = await SongApi.CreateAsync(client, "C folk rock song");
        await SongApi.CreateAsync(client, "D no genre");
        await AssignAsync(client, first, folk);
        await AssignAsync(client, second, rock);
        await AssignAsync(client, third, folk, rock);

        async Task<List<string>> TitlesAsync(string query) =>
            [.. (await SongApi.ListAsync(client, "sort=title&" + query)).GetProperty("items").EnumerateArray().Select(static song => song.GetProperty("title").GetString()!)];

        Assert.Equal(["A folk song", "C folk rock song"], await TitlesAsync($"genre={folk}"));
        Assert.Equal(["A folk song", "B rock song", "C folk rock song"], await TitlesAsync($"genre={folk}&genre={rock}"));
        Assert.Empty(await TitlesAsync($"genre={jazz}"));
        Assert.Equal(["D no genre"], await TitlesAsync("genre=none"));
        Assert.Equal(["B rock song", "C folk rock song", "D no genre"], await TitlesAsync($"genre=none&genre={rock}"));
        Assert.Equal(4, (await SongApi.ListAsync(client)).GetProperty("total").GetInt32());

        // Combined with the state filter by AND.
        await SongApi.EditAsync(client, third.GetProperty("id").GetString()!, 2, $$"""{"stateId":"{{DefaultWorkflowStates.Writing.Id}}"}""");
        Assert.Equal(["C folk rock song"], await TitlesAsync($"genre={folk}&state={DefaultWorkflowStates.Writing.Id}"));
        var filtered = await SongApi.ListAsync(client, $"genre={folk}&state={DefaultWorkflowStates.Writing.Id}");
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());

        // A Genre that does not exist matches nothing (#225); a malformed one is 400.
        Assert.Empty(await TitlesAsync($"genre={Guid.CreateVersion7()}"));
        foreach (var wrong in new[] { "folk", "NONE", string.Empty })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/songs?genre={wrong}", UriKind.Relative));
            await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }
    }

    /// <summary>Creates a Genre (or finds it) and returns its ID.</summary>
    private static async Task<string> CreateGenreAsync(HttpClient client, string name)
    {
        using var response = await PostGenreAsync(client, new { name });
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    /// <summary>Gives a Song (as created, at its current revision) exactly <paramref name="genres"/>.</summary>
    private static async Task AssignAsync(HttpClient client, JsonElement song, params string[] genres)
    {
        var id = song.GetProperty("id").GetString()!;
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(id)))).GetProperty("revision").GetInt32();
        await SongApi.EditAsync(client, id, revision, JsonSerializer.Serialize(new { genreIds = genres }));
    }

    private static async Task<HttpResponseMessage> PostGenreAsync(HttpClient client, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Genres) { Content = JsonContent.Create(body) };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsTokenAsync(HttpClient client, HttpMethod method, Uri uri, string token, string json, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    private static List<string> Names(JsonElement song) =>
        [.. song.GetProperty("genres").EnumerateArray().Select(static genre => genre.GetProperty("name").GetString()!)];
}
