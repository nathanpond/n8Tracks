using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// Creating, reading, and listing Songs through the API: a Song needs only a title, and is born with
/// a shortcode that is never reused, a workflow state, and an empty Version <c>1</c> as its current
/// Version, all in one transaction.
/// </summary>
public sealed class SongEndpointTests
{
    [Fact]
    public async Task CreatingASongCreatesVersionOneAsItsCurrentVersionWithShortcodes()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var created = await SongApi.PostAsync(client, new { title = "  Running in a Pack ", concept = "Fast-paced song about running in a pack." });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.Tag);
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var song = await SetupApi.JsonAsync(created);
        var id = song.GetProperty("id").GetString()!;
        Assert.Equal(7, Guid.Parse(id).Version);
        Assert.EndsWith("/api/v1/songs/" + id, created.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal("n8-1", song.GetProperty("shortcode").GetString());
        Assert.Equal("Running in a Pack", song.GetProperty("title").GetString());
        Assert.Equal("Fast-paced song about running in a pack.", song.GetProperty("concept").GetString());
        Assert.Equal(DefaultWorkflowStates.Idea.Id, song.GetProperty("state").GetProperty("id").GetGuid());
        Assert.Equal("Idea", song.GetProperty("state").GetProperty("name").GetString());
        Assert.Equal(StateColours.Yellow, song.GetProperty("state").GetProperty("colour").GetString());
        var version = song.GetProperty("currentVersion");
        Assert.Equal(7, version.GetProperty("id").GetGuid().Version);
        Assert.Equal("1", version.GetProperty("number").GetString());
        Assert.Equal("n8-1-v1", version.GetProperty("shortcode").GetString());
        Assert.Equal(1, song.GetProperty("versionCount").GetInt32());
        Assert.Equal("2026-10-01T09:00:00Z", song.GetProperty("createdAt").GetString());
        Assert.Equal("2026-10-01T09:00:00Z", song.GetProperty("updatedAt").GetString());
        Assert.Equal(1, song.GetProperty("revision").GetInt32());

        // In the database: the Song, its one Version (mutable and empty), the pointer, and the sequence.
        var versionId = version.GetProperty("id").GetGuid();
        Assert.Equal(
            $"1|{versionId.ToString().ToUpperInvariant()}|1",
            TestDatabase.Scalar(factory.DataPath, "SELECT shortcode_number, current_version_id, revision FROM songs;"));
        Assert.Equal(
            $"{id.ToUpperInvariant()}|1|null|null|active|||1",
            TestDatabase.Scalar(factory.DataPath, "SELECT song_id, number, coalesce(name, 'null'), coalesce(notes, 'null'), visibility, lyrics, styles, revision FROM versions;"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT last_value FROM shortcode_sequence;"));
    }

    [Fact]
    public async Task ABlankOrOverLongTitleOrConceptIsRefusedAndNothingIsCreated()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var body in new object[]
        {
            new { },
            new { title = "   " },
            new { title = (string?)null, concept = "A concept" },
            new { title = new string('x', 301) },
            new { title = "Two\nlines" },
        })
        {
            using var refused = await SongApi.PostAsync(client, body);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal(["title"], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
        }

        using (var both = await SongApi.PostAsync(client, new { title = "", concept = new string('x', 2001) }))
        {
            var problem = await SetupApi.ProblemAsync(both, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            var errors = problem.GetProperty("errors");
            Assert.Equal(["concept", "title"], errors.EnumerateObject().Select(static error => error.Name).Order(StringComparer.Ordinal));
            Assert.Equal("Enter a title.", errors.GetProperty("title")[0].GetString());
            Assert.Equal("Use at most 2,000 characters.", errors.GetProperty("concept")[0].GetString());
        }

        using (var control = await SongApi.PostAsync(client, new { title = "Fine", concept = "tab\there" }))
        {
            var problem = await SetupApi.ProblemAsync(control, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal(["concept"], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
        }

        // Nothing was stored and the sequence did not move.
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM versions;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT last_value FROM shortcode_sequence;"));

        // Complement: the limits themselves are accepted, and the first Song is still n8-1.
        var song = await SongApi.CreateAsync(client, new string('x', 300), "   \n  ");
        Assert.Equal("n8-1", song.GetProperty("shortcode").GetString());
        Assert.Equal(JsonValueKind.Null, song.GetProperty("concept").ValueKind);
        var withConcept = await SongApi.CreateAsync(client, "Concept at the limit", " " + new string('y', 2000) + "\r\n");
        Assert.Equal(new string('y', 2000), withConcept.GetProperty("concept").GetString());
    }

    [Fact]
    public async Task AFailureInsideTheCreationLeavesNothingBehindAndTheShortcodeUnused()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        TestDatabase.Execute(factory.DataPath, "CREATE TRIGGER test_refuse_versions BEFORE INSERT ON versions BEGIN SELECT RAISE(ABORT, 'refused by the test'); END;");

        using (var failed = await SongApi.PostAsync(client, new { title = "Doomed" }))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT last_value FROM shortcode_sequence;"));

        // Complement: once versions can be written again, the first Song gets n8-1.
        TestDatabase.Execute(factory.DataPath, "DROP TRIGGER test_refuse_versions;");
        Assert.Equal("n8-1", (await SongApi.CreateAsync(client, "Survivor")).GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task DuplicateTitlesAreAllowedAndShortcodesAreNeverReused()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        for (var count = 0; count < 3; count++)
        {
            await SongApi.CreateAsync(client, "Running in a Pack");
        }

        Assert.Equal(["n8-3", "n8-2", "n8-1"], SongApi.Shortcodes(await SongApi.ListAsync(client)));

        // Removing the middle Song, and then the last, gives neither number back.
        SongApi.RemoveDirectly(factory.DataPath, 2);
        Assert.Equal("n8-4", (await SongApi.CreateAsync(client, "Running in a Pack")).GetProperty("shortcode").GetString());
        SongApi.RemoveDirectly(factory.DataPath, 4);
        Assert.Equal("n8-5", (await SongApi.CreateAsync(client, "Running in a Pack")).GetProperty("shortcode").GetString());

        using var gone = await client.GetAsync(SongApi.Song("n8-2"));
        await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task ASongIsReadByItsIdOrShortcodeInAnyCase()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var created = await SongApi.CreateAsync(client, "Found");
        var id = created.GetProperty("id").GetString()!;

        foreach (var reference in new[] { id, id.ToUpperInvariant(), "n8-1", "N8-1" })
        {
            using var response = await client.GetAsync(SongApi.Song(reference));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
            var song = await SetupApi.JsonAsync(response);
            Assert.Equal(id, song.GetProperty("id").GetString());
            Assert.Equal("n8-1-v1", song.GetProperty("currentVersion").GetProperty("shortcode").GetString());
        }

        foreach (var reference in new[] { "n8-2", "n8-01", "n8-1-v1", "Found", Guid.CreateVersion7().ToString(), id.Replace("-", string.Empty, StringComparison.Ordinal) })
        {
            using var response = await client.GetAsync(SongApi.Song(reference));
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }
    }

    [Fact]
    public async Task TheListSortsByUpdatedOrTitleFiltersByStateAndPages()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        foreach (var title in new[] { "banana", "Apple", "cherry", "apple" })
        {
            await SongApi.CreateAsync(client, title);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        // n8-1 banana, n8-2 Apple, n8-3 cherry, n8-4 apple. By default: last updated, newest first.
        var all = await SongApi.ListAsync(client);
        Assert.Equal(["n8-4", "n8-3", "n8-2", "n8-1"], SongApi.Shortcodes(all));
        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(50, all.GetProperty("pageSize").GetInt32());
        Assert.Equal(4, all.GetProperty("total").GetInt32());
        var item = all.GetProperty("items")[3];
        Assert.Equal("banana", item.GetProperty("title").GetString());
        Assert.Equal("Idea", item.GetProperty("state").GetProperty("name").GetString());
        Assert.Equal(1, item.GetProperty("versionCount").GetInt32());

        Assert.Equal(["n8-1", "n8-2", "n8-3", "n8-4"], SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=updated&direction=asc")));

        // Titles ignore case, A to Z by default, the shortcode breaking ties in the same direction.
        Assert.Equal(["n8-2", "n8-4", "n8-1", "n8-3"], SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=title")));
        Assert.Equal(["n8-3", "n8-1", "n8-4", "n8-2"], SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=title&direction=desc")));

        // Filter: any number of states; a Song in another state is left out.
        TestDatabase.Execute(factory.DataPath, $"UPDATE songs SET workflow_state_id = '{Upper(DefaultWorkflowStates.Archived.Id)}' WHERE shortcode_number = 1;");
        TestDatabase.Execute(factory.DataPath, $"UPDATE songs SET workflow_state_id = '{Upper(DefaultWorkflowStates.Writing.Id)}' WHERE shortcode_number = 3;");
        var archived = await SongApi.ListAsync(client, $"state={DefaultWorkflowStates.Archived.Id}");
        Assert.Equal(["n8-1"], SongApi.Shortcodes(archived));
        Assert.Equal(1, archived.GetProperty("total").GetInt32());
        Assert.Equal("Archived", archived.GetProperty("items")[0].GetProperty("state").GetProperty("name").GetString());
        Assert.Equal(
            ["n8-3", "n8-1"],
            SongApi.Shortcodes(await SongApi.ListAsync(client, $"state={DefaultWorkflowStates.Archived.Id}&state={DefaultWorkflowStates.Writing.Id}&state={DefaultWorkflowStates.Writing.Id}")));
        Assert.Empty(SongApi.Shortcodes(await SongApi.ListAsync(client, $"state={DefaultWorkflowStates.Final.Id}")));

        // Paging: the total is of every match; a page past the end is empty.
        var second = await SongApi.ListAsync(client, "sort=title&pageSize=3&page=2");
        Assert.Equal(["n8-3"], SongApi.Shortcodes(second));
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(3, second.GetProperty("pageSize").GetInt32());
        Assert.Equal(4, second.GetProperty("total").GetInt32());
        var past = await SongApi.ListAsync(client, "page=3&pageSize=100");
        Assert.Empty(SongApi.Shortcodes(past));
        Assert.Equal(4, past.GetProperty("total").GetInt32());
        Assert.Empty(SongApi.Shortcodes(await SongApi.ListAsync(client, "page=2147483647&pageSize=100")));
        Assert.Equal(["n8-4"], SongApi.Shortcodes(await SongApi.ListAsync(client, "pageSize=1")));
    }

    [Theory]
    [InlineData("sort=created")]
    [InlineData("sort=Title")]
    [InlineData("sort=")]
    [InlineData("direction=up")]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=one")]
    [InlineData("page=1.5")]
    [InlineData("page=2147483648")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=+5")]
    [InlineData("state=idea")]
    [InlineData("state=01a10a6e-dc80-7000-8000-000000000099")]
    [InlineData("state=")]
    [InlineData("sort=title&sort=updated")]
    [InlineData("page=1&page=2")]
    public async Task AnUnknownOrOutOfRangeListParameterIs400(string query)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(new Uri($"/api/v1/songs?{query}", UriKind.Relative));

        await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, ApiProblem.InvalidRequestCode);
    }

    [Fact]
    public async Task ChangingAVersionMovesItsSongsUpdatedTimeButNotItsRevision()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Older");
        clock.Advance(TimeSpan.FromMinutes(1));
        await SongApi.CreateAsync(client, "Newer");
        Assert.Equal(["n8-2", "n8-1"], SongApi.Shortcodes(await SongApi.ListAsync(client)));

        // A Version write sets the Version's updated time; the Song's follows it.
        TestDatabase.Execute(factory.DataPath, "UPDATE versions SET lyrics = '[Verse]', updated_utc = '2026-10-02T10:00:00.000Z', revision = revision + 1 WHERE song_id = (SELECT id FROM songs WHERE shortcode_number = 1);");

        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal("2026-10-02T10:00:00Z", song.GetProperty("updatedAt").GetString());
        Assert.Equal("2026-10-01T09:00:00Z", song.GetProperty("createdAt").GetString());
        Assert.Equal(1, song.GetProperty("revision").GetInt32());
        Assert.Equal(["n8-1", "n8-2"], SongApi.Shortcodes(await SongApi.ListAsync(client)));

        // A new Version moves it too, and counts.
        TestDatabase.Execute(
            factory.DataPath,
            $"""
            INSERT INTO versions (id, song_id, number, number_sort_key, visibility, lyrics, styles, created_utc, updated_utc, revision)
            SELECT '{Upper(Guid.CreateVersion7())}', id, '2', '0000000002', 'active', '', '', '2026-10-03T10:00:00.000Z', '2026-10-03T10:00:00.000Z', 1
            FROM songs WHERE shortcode_number = 2;
            """);
        var second = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-2")));
        Assert.Equal("2026-10-03T10:00:00Z", second.GetProperty("updatedAt").GetString());
        Assert.Equal(2, second.GetProperty("versionCount").GetInt32());
        Assert.Equal("1", second.GetProperty("currentVersion").GetProperty("number").GetString());
        Assert.Equal(1, second.GetProperty("revision").GetInt32());

        // Complement: a Version write never moves the Song's time backwards.
        TestDatabase.Execute(factory.DataPath, "UPDATE versions SET updated_utc = '2026-01-01T00:00:00.000Z' WHERE song_id = (SELECT id FROM songs WHERE shortcode_number = 1);");
        var unchanged = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal("2026-10-02T10:00:00Z", unchanged.GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task ANewSongStartsInTheFirstVisibleState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        TestDatabase.Execute(factory.DataPath, $"UPDATE workflow_states SET hidden = 1 WHERE id = '{Upper(DefaultWorkflowStates.Idea.Id)}';");

        var song = await SongApi.CreateAsync(client, "Skips Idea");

        Assert.Equal("Writing", song.GetProperty("state").GetProperty("name").GetString());

        // A Song in a hidden state is still listed.
        TestDatabase.Execute(factory.DataPath, $"UPDATE songs SET workflow_state_id = '{Upper(DefaultWorkflowStates.Idea.Id)}';");
        Assert.Equal(["n8-1"], SongApi.Shortcodes(await SongApi.ListAsync(client)));
    }

    [Fact]
    public async Task TheWorkflowStatesAreListedInOrderWithTheirColours()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(SongApi.WorkflowStates);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["Idea|yellow|1|False", "Writing|blue|2|False", "Generating|violet|3|False", "Refining|orange|4|False", "Final|green|5|False", "Released|teal|6|False", "Archived|gray|7|False"],
            items.Select(static state => $"{state.GetProperty("name").GetString()}|{state.GetProperty("colour").GetString()}|{state.GetProperty("order").GetInt32()}|{state.GetProperty("hidden").GetBoolean()}"));
        Assert.Equal(DefaultWorkflowStates.All.Select(static state => state.Id), items.Select(static state => state.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task EachEndpointNeedsItsScope()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(setUp, "Scoped");
        using var client = factory.CreateClient();

        var reads = new[] { SongApi.Songs, SongApi.Song("n8-1"), SongApi.WorkflowStates };
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        foreach (var uri in reads)
        {
            using var response = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, reader);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{uri}: {response.StatusCode}");
        }

        var everythingButRead = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);
        foreach (var uri in reads)
        {
            using var response = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, everythingButRead);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        var everythingButWrite = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.SongsWrite)]);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Post, SongApi.Songs, everythingButWrite))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
        }

        // Complement: songs.write alone reaches the create endpoint (the empty body is then refused for its title).
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using (var reached = await CredentialApi.SendAsync(client, HttpMethod.Post, SongApi.Songs, writer))
        {
            await SetupApi.ProblemAsync(reached, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        }

        // No session and no token: 401.
        using var anonymous = await client.GetAsync(SongApi.Songs);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();
}
