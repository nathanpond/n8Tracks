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
/// Editing a Song's title, concept, and workflow state through <c>PATCH /api/v1/songs/{id}</c>: a
/// partial merge against the revision the caller read, so a stale edit is refused with the Song as
/// it is now and never overwrites a newer one.
/// </summary>
public sealed class SongEditTests
{
    private static readonly string Writing = DefaultWorkflowStates.Writing.Id.ToString();

    [Fact]
    public async Task AnEditAtTheCurrentRevisionIsStoredAndRaisesTheRevision()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = (await SongApi.CreateAsync(client, "Old title", "Old concept")).GetProperty("id").GetString()!;
        clock.Advance(TimeSpan.FromMinutes(5));

        using var response = await SongApi.PatchAsync(client, id, "\"1\"", $$"""{"title":"  New title ","stateId":"{{Writing}}"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var song = await SetupApi.JsonAsync(response);
        Assert.Equal(2, song.GetProperty("revision").GetInt32());
        Assert.Equal("New title", song.GetProperty("title").GetString());
        Assert.Equal("Old concept", song.GetProperty("concept").GetString());
        Assert.Equal("Writing", song.GetProperty("state").GetProperty("name").GetString());
        Assert.Equal(StateColours.Blue, song.GetProperty("state").GetProperty("colour").GetString());
        Assert.Equal("2026-10-01T09:05:00Z", song.GetProperty("updatedAt").GetString());
        Assert.Equal("2026-10-01T09:00:00Z", song.GetProperty("createdAt").GetString());

        // Stored, sort key included, and readable by the next request.
        Assert.Equal("New title|new title|2", TestDatabase.Scalar(factory.DataPath, "SELECT title, title_sort_key, revision FROM songs;"));
        var read = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(id)));
        Assert.Equal(2, read.GetProperty("revision").GetInt32());

        // The next edit is based on revision 2: the concept is trimmed, and blank or null clears it.
        var trimmed = await SongApi.EditAsync(client, id, 2, """{"concept":"  Line one\r\nLine two  "}""");
        Assert.Equal("Line one\nLine two", trimmed.GetProperty("concept").GetString());
        Assert.Equal(3, trimmed.GetProperty("revision").GetInt32());
        var blank = await SongApi.EditAsync(client, id, 3, """{"concept":"   "}""");
        Assert.Equal(JsonValueKind.Null, blank.GetProperty("concept").ValueKind);
        await SongApi.EditAsync(client, id, 4, """{"concept":"Back again"}""");
        var cleared = await SongApi.EditAsync(client, id, 5, """{"concept":null}""");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("concept").ValueKind);
        Assert.Equal(6, cleared.GetProperty("revision").GetInt32());
        Assert.Equal("New title", cleared.GetProperty("title").GetString());
        Assert.Equal("Writing", cleared.GetProperty("state").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AStaleRevisionIs409WithTheCurrentSongAndChangesNothing()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = (await SongApi.CreateAsync(client, "Shared", "First concept")).GetProperty("id").GetString()!;
        clock.Advance(TimeSpan.FromMinutes(1));
        await SongApi.EditAsync(client, id, 1, $$"""{"title":"Changed elsewhere","stateId":"{{Writing}}"}""");
        var before = TestDatabase.Scalar(factory.DataPath, "SELECT title, concept, workflow_state_id, updated_utc, revision FROM songs;");

        // Behind (1) and ahead (3) of the current revision are both stale.
        foreach (var stale in new[] { "\"1\"", "\"3\"" })
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            using var response = await SongApi.PatchAsync(client, id, stale, """{"concept":"My concept"}""");

            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, Revisions.ConflictCode);
            var current = problem.GetProperty("current");
            Assert.Equal(id, current.GetProperty("id").GetString());
            Assert.Equal("Changed elsewhere", current.GetProperty("title").GetString());
            Assert.Equal("First concept", current.GetProperty("concept").GetString());
            Assert.Equal(DefaultWorkflowStates.Writing.Id, current.GetProperty("state").GetProperty("id").GetGuid());
            Assert.Equal(2, current.GetProperty("revision").GetInt32());
            Assert.Equal(before, TestDatabase.Scalar(factory.DataPath, "SELECT title, concept, workflow_state_id, updated_utc, revision FROM songs;"));
        }

        // Complement: the same edit on the current revision is stored.
        var applied = await SongApi.EditAsync(client, id, 2, """{"concept":"My concept"}""");
        Assert.Equal("My concept", applied.GetProperty("concept").GetString());
        Assert.Equal("Changed elsewhere", applied.GetProperty("title").GetString());
        Assert.Equal(3, applied.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AnEditWithoutARevisionIs428AndAMalformedOneIs400()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = (await SongApi.CreateAsync(client, "Guarded")).GetProperty("id").GetString()!;

        using (var missing = await SongApi.PatchAsync(client, id, ifMatch: null, """{"title":"Unguarded"}"""))
        {
            await SetupApi.ProblemAsync(missing, (HttpStatusCode)428, Revisions.RequiredCode);
        }

        foreach (var malformed in new[] { "1", "\"\"", "\"0\"", "\"01\"", "\"-1\"", "\"one\"", "W/\"1\"", "*", "\"1\", \"2\"" })
        {
            using var response = await SongApi.PatchAsync(client, id, malformed, """{"title":"Unguarded"}""");
            await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, Revisions.InvalidCode);
        }

        Assert.Equal("Guarded|1", TestDatabase.Scalar(factory.DataPath, "SELECT title, revision FROM songs;"));
    }

    [Fact]
    public async Task ASongMovesFromAnyStateToAnyOtherHiddenOnesIncluded()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = (await SongApi.CreateAsync(client, "Wanderer")).GetProperty("id").GetString()!;
        TestDatabase.Execute(factory.DataPath, $"UPDATE workflow_states SET hidden = 1 WHERE id = '{DefaultWorkflowStates.Final.Id.ToString().ToUpperInvariant()}';");

        // Forwards, backwards, and into the hidden state: every move is allowed.
        var revision = 1;
        foreach (var state in new[] { DefaultWorkflowStates.Archived, DefaultWorkflowStates.Idea, DefaultWorkflowStates.Final, DefaultWorkflowStates.Writing })
        {
            var song = await SongApi.EditAsync(client, id, revision, $$"""{"stateId":"{{state.Id.ToString().ToUpperInvariant()}}"}""");
            revision++;
            Assert.Equal(state.Name, song.GetProperty("state").GetProperty("name").GetString());
            Assert.Equal(revision, song.GetProperty("revision").GetInt32());
        }
    }

    [Fact]
    public async Task AWrongFieldIs422AndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = (await SongApi.CreateAsync(client, "Valid", "Kept")).GetProperty("id").GetString()!;

        foreach (var (json, field) in new[]
        {
            ("""{"title":""}""", "title"),
            ("""{"title":"   "}""", "title"),
            ("""{"title":null}""", "title"),
            ($$"""{"title":"{{new string('x', 301)}}"}""", "title"),
            ("""{"title":"Two\nlines"}""", "title"),
            ("""{"title":5}""", "title"),
            ($$"""{"concept":"{{new string('x', 2001)}}"}""", "concept"),
            ("""{"concept":"tab\there"}""", "concept"),
            ("""{"concept":["a"]}""", "concept"),
            ("""{"stateId":"writing"}""", "stateId"),
            ("""{"stateId":null}""", "stateId"),
            ("""{"stateId":"01a10a6e-dc80-7000-8000-000000000099"}""", "stateId"),
            ("""{"stateId":{}}""", "stateId"),
        })
        {
            using var response = await SongApi.PatchAsync(client, id, "\"1\"", json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
        }

        using (var empty = await SongApi.PatchAsync(client, id, "\"1\"", """{"title":"","concept":"Fine"}"""))
        {
            var problem = await SetupApi.ProblemAsync(empty, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal("Enter a title.", problem.GetProperty("errors").GetProperty("title")[0].GetString());
        }

        Assert.Equal("Valid|Kept|1", TestDatabase.Scalar(factory.DataPath, "SELECT title, concept, revision FROM songs;"));

        // Complement: the limits themselves are accepted.
        var limits = await SongApi.EditAsync(client, id, 1, $$"""{"title":"{{new string('x', 300)}}","concept":"{{new string('y', 2000)}}"}""");
        Assert.Equal(2, limits.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AnEditThatChangesNothingIsNotWritten()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = (await SongApi.CreateAsync(client, "Same", "Same concept")).GetProperty("id").GetString()!;
        clock.Advance(TimeSpan.FromMinutes(1));

        foreach (var json in new[] { "{}", """{"title":"  Same  "}""", $$"""{"concept":"Same concept\n","stateId":"{{DefaultWorkflowStates.Idea.Id}}"}""" })
        {
            using var response = await SongApi.PatchAsync(client, id, "\"1\"", json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
            var song = await SetupApi.JsonAsync(response);
            Assert.Equal(1, song.GetProperty("revision").GetInt32());
            Assert.Equal("2026-10-01T09:00:00Z", song.GetProperty("updatedAt").GetString());
        }

        Assert.Equal("1|2026-10-01T09:00:00.000Z", TestDatabase.Scalar(factory.DataPath, "SELECT revision, updated_utc FROM songs;"));
    }

    [Fact]
    public async Task EditingAMissingSongIs404()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Removed");
        var id = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM songs;").ToLowerInvariant();
        SongApi.RemoveDirectly(factory.DataPath, 1);

        using (var gone = await SongApi.PatchAsync(client, id, "\"1\"", """{"title":"Ghost"}"""))
        {
            await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // Only an ID names a Song to edit: a shortcode falls through to the API's 404.
        using var byShortcode = await SongApi.PatchAsync(client, "n8-1", "\"1\"", """{"title":"Ghost"}""");
        await SetupApi.ProblemAsync(byShortcode, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task EditingNeedsSongsWrite()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        var id = (await SongApi.CreateAsync(setUp, "Scoped")).GetProperty("id").GetString()!;
        using var client = factory.CreateClient();
        var uri = SongApi.Song(id);

        var everythingButWrite = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.SongsWrite)]);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Patch, uri, everythingButWrite))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
        }

        // Complement: songs.write alone reaches the endpoint (which then asks for the revision).
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using (var reached = await CredentialApi.SendAsync(client, HttpMethod.Patch, uri, writer))
        {
            await SetupApi.ProblemAsync(reached, (HttpStatusCode)428, Revisions.RequiredCode);
        }

        // No session and no token: 401.
        using var anonymous = await SongApi.PatchAsync(client, id, "\"1\"", "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Scoped|1", TestDatabase.Scalar(factory.DataPath, "SELECT title, revision FROM songs;"));
    }
}
