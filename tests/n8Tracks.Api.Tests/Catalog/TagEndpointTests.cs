using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// Tags: <c>GET</c> and <c>POST /api/v1/tags</c> (with the colour chosen on creation), a Song's
/// Tags through the Song's own <c>PATCH</c> (under its revision), and the Songs list's <c>tag</c>
/// filter.
/// </summary>
public sealed class TagEndpointTests
{
    private static readonly Uri Tags = new("/api/v1/tags", UriKind.Relative);

    [Fact]
    public async Task CreatingATagStoresItOnceInTheNextColourWhateverTheLetterCaseOrSpacing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var created = await PostTagAsync(client, new { name = "  late   summer " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var tag = await SetupApi.JsonAsync(created);
        Assert.Equal("late summer", tag.GetProperty("name").GetString());
        Assert.Equal("gray", tag.GetProperty("colour").GetString());
        Assert.Equal(0, tag.GetProperty("songCount").GetInt32());
        Assert.Equal(1, tag.GetProperty("revision").GetInt32());
        var id = tag.GetProperty("id").GetString();
        Assert.EndsWith($"/api/v1/tags/{id}", created.Headers.Location?.ToString(), StringComparison.Ordinal);

        // Complement: the same name in another case (and spacing) is the same Tag, answered with
        // 200, in its own colour; no colour is used up.
        foreach (var name in new[] { "Late Summer", "LATE  SUMMER", "late summer" })
        {
            using var again = await PostTagAsync(client, new { name });
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var existing = await SetupApi.JsonAsync(again);
            Assert.Equal(id, existing.GetProperty("id").GetString());
            Assert.Equal("late summer", existing.GetProperty("name").GetString());
            Assert.Equal("gray", existing.GetProperty("colour").GetString());
        }

        Assert.Equal("late summer|LATE SUMMER|gray|1", TestDatabase.Scalar(factory.DataPath, "SELECT name, name_key, colour, revision FROM tags;"));
        Assert.Equal("red", await ColourOfNewTagAsync(client, "running"));
    }

    [Fact]
    public async Task NewTagsTakeTheFirstUnusedColourThenTheLeastUsed()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // Twelve Tags take the twelve colours in palette order.
        var colours = new List<string>();
        for (var index = 1; index <= 12; index++)
        {
            colours.Add(await ColourOfNewTagAsync(client, $"tag {index}"));
        }

        Assert.Equal(TagRules.Palette, colours);

        // Every colour in use once: the earliest of the least used.
        Assert.Equal("gray", await ColourOfNewTagAsync(client, "tag 13"));
        Assert.Equal("red", await ColourOfNewTagAsync(client, "tag 14"));

        // A colour no Tag has any more is taken first, wherever it is in the palette.
        TestDatabase.Execute(factory.DataPath, "UPDATE tags SET colour = 'gray' WHERE name = 'tag 7';");
        Assert.Equal("blue", await ColourOfNewTagAsync(client, "tag 15"));

        // Counts now: gray 3, red 2, every other 1: pink is the earliest least used.
        Assert.Equal("pink", await ColourOfNewTagAsync(client, "tag 16"));

        // Least used counts Tags, not the Songs that have them.
        var song = await SongApi.CreateAsync(client, "Song");
        var grape = (await SetupApi.JsonAsync(await client.GetAsync(Tags))).GetProperty("items").EnumerateArray()
            .Single(static tag => tag.GetProperty("name").GetString() == "tag 4").GetProperty("id").GetString()!;
        await AssignAsync(client, song, grape);
        Assert.Equal("grape", await ColourOfNewTagAsync(client, "tag 17"));
    }

    [Fact]
    public async Task AWrongNameIs422AndStoresNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var body in new object[] { new { }, new { name = "   " }, new { name = new string('a', 51) }, new { name = "Bell\u0007" } })
        {
            using var response = await PostTagAsync(client, body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM tags;"));

        // Complement: fifty characters is a name.
        using var longest = await PostTagAsync(client, new { name = new string('a', 50) });
        Assert.Equal(HttpStatusCode.Created, longest.StatusCode);
    }

    [Fact]
    public async Task TagsAreListedAlphabeticallyIgnoringCaseWithColoursAndSongCounts()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var summer = await CreateTagAsync(client, "summer");
        var ambient = await CreateTagAsync(client, "Ambient");
        var running = await CreateTagAsync(client, "running");
        var first = await SongApi.CreateAsync(client, "First");
        var second = await SongApi.CreateAsync(client, "Second");
        await AssignAsync(client, first, summer, running);
        await AssignAsync(client, second, summer);

        var list = await SetupApi.JsonAsync(await client.GetAsync(Tags));

        Assert.Equal(
            ["Ambient red 0", "running pink 1", "summer gray 2"],
            list.GetProperty("items").EnumerateArray().Select(static tag => $"{tag.GetProperty("name").GetString()} {tag.GetProperty("colour").GetString()} {tag.GetProperty("songCount").GetInt32()}"));
        Assert.Equal(ambient, list.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task AssigningTagsReplacesTheSongsListUnderItsRevisionAndMovesItsUpdatedTime()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Running in a Pack");
        var id = song.GetProperty("id").GetString()!;
        Assert.Empty(song.GetProperty("tags").EnumerateArray());

        // Create on the spot, then assign: what the picker does.
        var summer = await CreateTagAsync(client, "summer");
        var running = await CreateTagAsync(client, "Running");
        clock.Advance(TimeSpan.FromMinutes(5));
        var assigned = await SongApi.EditAsync(client, id, 1, $$"""{"tagIds":["{{summer}}","{{running}}","{{summer}}"]}""");

        // Alphabetical ignoring case, each once with its colour, and the revision and time moved.
        Assert.Equal(["Running red", "summer gray"], Labels(assigned));
        Assert.Equal(running, assigned.GetProperty("tags")[0].GetProperty("id").GetString());
        Assert.Equal(2, assigned.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:05:00Z", assigned.GetProperty("updatedAt").GetString());
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_tags;"));

        // Read back by shortcode, and in the list's rows (every Tag; the client shortens).
        var read = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song.GetProperty("shortcode").GetString()!)));
        Assert.Equal(["Running red", "summer gray"], Labels(read));
        var row = (await SongApi.ListAsync(client)).GetProperty("items")[0];
        Assert.Equal(["Running red", "summer gray"], Labels(row));

        // The same Tags again change nothing: no new revision.
        clock.Advance(TimeSpan.FromMinutes(5));
        var same = await SongApi.EditAsync(client, id, 2, $$"""{"tagIds":["{{running}}","{{summer}}"]}""");
        Assert.Equal(2, same.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:05:00Z", same.GetProperty("updatedAt").GetString());

        // Removing one, and then all, is a change too.
        var removed = await SongApi.EditAsync(client, id, 2, $$"""{"tagIds":["{{summer}}"]}""");
        Assert.Equal(["summer gray"], Labels(removed));
        Assert.Equal(3, removed.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:10:00Z", removed.GetProperty("updatedAt").GetString());
        var none = await SongApi.EditAsync(client, id, 3, """{"tagIds":[]}""");
        Assert.Empty(none.GetProperty("tags").EnumerateArray());
        Assert.Equal(4, none.GetProperty("revision").GetInt32());

        // An edit without tagIds leaves them alone, and Genres and Tags are separate lists.
        await SongApi.EditAsync(client, id, 4, $$"""{"tagIds":["{{running}}"]}""");
        var titled = await SongApi.EditAsync(client, id, 5, """{"title":"Renamed","genreIds":[]}""");
        Assert.Equal(["Running red"], Labels(titled));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM genres;"));
    }

    [Fact]
    public async Task ManyTagsAreAllKeptAndListedAlphabetically()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Song");
        string[] names = ["zeta", "Alpha", "beta", "Gamma", "delta", "epsilon"];
        var ids = new List<string>();
        foreach (var name in names)
        {
            ids.Add(await CreateTagAsync(client, name));
        }

        await AssignAsync(client, song, [.. ids]);

        var read = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song.GetProperty("id").GetString()!)));
        Assert.Equal(
            ["Alpha", "beta", "delta", "epsilon", "Gamma", "zeta"],
            read.GetProperty("tags").EnumerateArray().Select(static tag => tag.GetProperty("name").GetString()!));
    }

    [Fact]
    public async Task RemovingATagFromASongLeavesTheTagAndItsOtherSongs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var summer = await CreateTagAsync(client, "summer");
        var first = await SongApi.CreateAsync(client, "First");
        var second = await SongApi.CreateAsync(client, "Second");
        await AssignAsync(client, first, summer);
        await AssignAsync(client, second, summer);
        var secondBefore = TestDatabase.Scalar(factory.DataPath, "SELECT revision, updated_utc FROM songs WHERE shortcode_number = 2;");

        await SongApi.EditAsync(client, first.GetProperty("id").GetString()!, 2, """{"tagIds":[]}""");

        var tags = await SetupApi.JsonAsync(await client.GetAsync(Tags));
        var listed = Assert.Single(tags.GetProperty("items").EnumerateArray());
        Assert.Equal(summer, listed.GetProperty("id").GetString());
        Assert.Equal(1, listed.GetProperty("songCount").GetInt32());
        var other = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(second.GetProperty("id").GetString()!)));
        Assert.Equal(["summer gray"], Labels(other));
        Assert.Equal(secondBefore, TestDatabase.Scalar(factory.DataPath, "SELECT revision, updated_utc FROM songs WHERE shortcode_number = 2;"));
    }

    [Fact]
    public async Task ATagThatDoesNotExistIs422OnTagIdsAndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var summer = await CreateTagAsync(client, "summer");
        var song = await SongApi.CreateAsync(client, "Song");
        var id = song.GetProperty("id").GetString()!;
        var before = TestDatabase.Scalar(factory.DataPath, "SELECT title, revision, updated_utc FROM songs;");

        var bodies = new[]
        {
            $$"""{"title":"Changed","tagIds":["{{summer}}","{{Guid.CreateVersion7()}}"]}""",
            """{"tagIds":["not an id"]}""",
            """{"tagIds":null}""",
            """{"tagIds":"summer"}""",
            """{"tagIds":[1]}""",
        };
        foreach (var body in bodies)
        {
            using var response = await SongApi.PatchAsync(client, id, "\"1\"", body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("tagIds", out _), body);
        }

        Assert.Equal(before, TestDatabase.Scalar(factory.DataPath, "SELECT title, revision, updated_utc FROM songs;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_tags;"));
    }

    [Fact]
    public async Task AStaleRevisionIs409WithTheCurrentTagsAndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var summer = await CreateTagAsync(client, "summer");
        var running = await CreateTagAsync(client, "running");
        var song = await SongApi.CreateAsync(client, "Song");
        var id = song.GetProperty("id").GetString()!;
        await SongApi.EditAsync(client, id, 1, $$"""{"tagIds":["{{summer}}"]}""");

        using var response = await SongApi.PatchAsync(client, id, "\"1\"", $$"""{"tagIds":["{{running}}"]}""");

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "revision_conflict");
        var current = problem.GetProperty("current");
        Assert.Equal(["summer gray"], Labels(current));
        Assert.Equal(2, current.GetProperty("revision").GetInt32());
        Assert.Equal(summer.ToUpperInvariant(), TestDatabase.Scalar(factory.DataPath, "SELECT tag_id FROM song_tags;"));
    }

    [Fact]
    public async Task ReadingTagsNeedsCatalogReadAndCreatingOrAssigningThemNeedsSongsWrite()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = SongApi.Song((await SongApi.CreateAsync(client, "Song")).GetProperty("shortcode").GetString()!);

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, Tags, reader))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using (var create = await SendAsTokenAsync(client, HttpMethod.Post, Tags, writer, """{"name":"summer"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        var summer = (await SetupApi.JsonAsync(await client.GetAsync(Tags))).GetProperty("items")[0].GetProperty("id").GetString();
        using (var assign = await SendAsTokenAsync(client, HttpMethod.Patch, song, writer, $$"""{"tagIds":["{{summer}}"]}""", ifMatch: "\"1\""))
        {
            Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        }

        // Every other scope is refused, naming the one needed, and nothing is stored.
        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.SongsWrite))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var create = await SendAsTokenAsync(client, HttpMethod.Post, Tags, token, """{"name":"Refused"}""");
            var problem = await SetupApi.ProblemAsync(create, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
            using var assign = await SendAsTokenAsync(client, HttpMethod.Patch, song, token, """{"tagIds":[]}""", ifMatch: "\"2\"");
            problem = await SetupApi.ProblemAsync(assign, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
        }

        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Tags, token);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        Assert.Equal("summer", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name) FROM tags;"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_tags;"));

        // Signed out, nothing is served or stored.
        using var signedOut = factory.CreateClient();
        await SetupApi.ProblemAsync(await signedOut.GetAsync(Tags), HttpStatusCode.Unauthorized, "not_authenticated");
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, Tags) { Content = JsonContent.Create(new { name = "anonymous" }) };
        anonymous.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        await SetupApi.ProblemAsync(await signedOut.SendAsync(anonymous), HttpStatusCode.Unauthorized, "not_authenticated");
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM tags;"));
    }

    [Fact]
    public async Task TheSongsListIsFilteredByAnyOfSeveralTagsOrByNone()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var running = await CreateTagAsync(client, "running");
        var summer = await CreateTagAsync(client, "summer");
        var unused = await CreateTagAsync(client, "unused");
        using var genre = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/genres", UriKind.Relative)) { Content = JsonContent.Create(new { name = "Folk" }) };
        genre.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        var folk = (await SetupApi.JsonAsync(await client.SendAsync(genre))).GetProperty("id").GetString()!;
        var first = await SongApi.CreateAsync(client, "A running song");
        var second = await SongApi.CreateAsync(client, "B summer song");
        var third = await SongApi.CreateAsync(client, "C running summer song");
        await SongApi.CreateAsync(client, "D no tag");
        await AssignAsync(client, first, running);
        await AssignAsync(client, second, summer);
        await AssignAsync(client, third, running, summer);

        async Task<List<string>> TitlesAsync(string query) =>
            [.. (await SongApi.ListAsync(client, "sort=title&" + query)).GetProperty("items").EnumerateArray().Select(static song => song.GetProperty("title").GetString()!)];

        Assert.Equal(["A running song", "C running summer song"], await TitlesAsync($"tag={running}"));
        Assert.Equal(["A running song", "B summer song", "C running summer song"], await TitlesAsync($"tag={running}&tag={summer}&tagMode=any"));
        Assert.Equal(["C running summer song"], await TitlesAsync($"tag={running}&tag={summer}"));
        Assert.Empty(await TitlesAsync($"tag={unused}"));
        Assert.Equal(["D no tag"], await TitlesAsync("tag=none"));
        Assert.Equal(["B summer song", "C running summer song", "D no tag"], await TitlesAsync($"tag=none&tag={summer}&tagMode=any"));
        Assert.Equal(4, (await SongApi.ListAsync(client)).GetProperty("total").GetInt32());

        // Combined with the state and Genre filters by AND.
        await SongApi.EditAsync(client, third.GetProperty("id").GetString()!, 2, $$"""{"stateId":"{{DefaultWorkflowStates.Writing.Id}}"}""");
        Assert.Equal(["C running summer song"], await TitlesAsync($"tag={running}&state={DefaultWorkflowStates.Writing.Id}"));
        var filtered = await SongApi.ListAsync(client, $"tag={running}&state={DefaultWorkflowStates.Writing.Id}");
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        await SongApi.EditAsync(client, first.GetProperty("id").GetString()!, 2, $$"""{"genreIds":["{{folk}}"]}""");
        Assert.Equal(["A running song"], await TitlesAsync($"tag={running}&genre={folk}"));

        // A Tag that does not exist (or a Genre's ID) matches nothing (#225); a malformed one is 400.
        Assert.Empty(await TitlesAsync($"tag={Guid.CreateVersion7()}"));
        Assert.Empty(await TitlesAsync($"tag={folk}"));
        foreach (var wrong in new[] { "running", "NONE", string.Empty })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/songs?tag={wrong}", UriKind.Relative));
            await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }
    }

    [Fact]
    public async Task ATagsColourIsAlwaysAPaletteColour()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CreateTagAsync(client, "summer");

        // The database refuses a colour outside the palette, even from raw SQL.
        var refused = Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() =>
            TestDatabase.Execute(factory.DataPath, "UPDATE tags SET colour = 'purple';"));
        Assert.Contains("ck_tags_colour", refused.Message, StringComparison.Ordinal);
        Assert.Equal("gray", TestDatabase.Scalar(factory.DataPath, "SELECT colour FROM tags;"));
    }

    /// <summary>Creates a Tag (or finds it) and returns its ID.</summary>
    private static async Task<string> CreateTagAsync(HttpClient client, string name)
    {
        using var response = await PostTagAsync(client, new { name });
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    /// <summary>Creates a new Tag and returns the colour it was given.</summary>
    private static async Task<string> ColourOfNewTagAsync(HttpClient client, string name)
    {
        using var response = await PostTagAsync(client, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("colour").GetString()!;
    }

    /// <summary>Gives a Song (as created, at its current revision) exactly <paramref name="tags"/>.</summary>
    private static async Task AssignAsync(HttpClient client, JsonElement song, params string[] tags)
    {
        var id = song.GetProperty("id").GetString()!;
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(id)))).GetProperty("revision").GetInt32();
        await SongApi.EditAsync(client, id, revision, JsonSerializer.Serialize(new { tagIds = tags }));
    }

    private static async Task<HttpResponseMessage> PostTagAsync(HttpClient client, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Tags) { Content = JsonContent.Create(body) };
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

    /// <summary>Each of a Song's Tags as "name colour", in the order the Song lists them.</summary>
    private static List<string> Labels(JsonElement song) =>
        [.. song.GetProperty("tags").EnumerateArray().Select(static tag => $"{tag.GetProperty("name").GetString()} {tag.GetProperty("colour").GetString()}")];
}
