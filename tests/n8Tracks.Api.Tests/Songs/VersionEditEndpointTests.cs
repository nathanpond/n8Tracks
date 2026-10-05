using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// <c>PATCH /api/v1/versions/{id}</c>: a Version's name, notes, and archived flag, each edited on
/// the revision it was read at. Archiving is visibility only: numbers, descendants, lyrics, styles,
/// and the current Version never change.
/// </summary>
public sealed class VersionEditEndpointTests
{
    [Fact]
    public async Task ANameAndNotesEditAtTheCurrentRevisionIsStoredTrimmedAndRaisesTheRevision()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Annotated");
        // Added directly with an updated time of 2026-10-03; the edit comes after it.
        var id = SongApi.AddVersionDirectly(factory.DataPath, 1, "1.1", lyrics: "[Verse]\nRun", styles: "punk");
        clock.Advance(TimeSpan.FromDays(3));

        using (var response = await PatchAsync(client, id, "\"1\"", """{"name":"  Guitar experimentation ","notes":"  Try a capo.\r\nAnd slower.  "}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
            var version = await SetupApi.JsonAsync(response);
            Assert.Equal(id, version.GetProperty("id").GetGuid());
            Assert.Equal("1.1", version.GetProperty("number").GetString());
            Assert.Equal("n8-1-v1.1", version.GetProperty("shortcode").GetString());
            Assert.Equal("Guitar experimentation", version.GetProperty("name").GetString());
            Assert.Equal("Try a capo.\nAnd slower.", version.GetProperty("notes").GetString());
            Assert.False(version.GetProperty("archived").GetBoolean());
            Assert.False(version.GetProperty("current").GetBoolean());
            Assert.Equal(2, version.GetProperty("revision").GetInt32());
            Assert.Equal("2026-10-04T09:00:00Z", version.GetProperty("updatedAt").GetString());
        }

        // Only the fields sent change; a blank name and blank notes are stored as none.
        var renamed = await EditAsync(client, id, 2, """{"name":"Capo"}""");
        Assert.Equal("Capo", renamed.GetProperty("name").GetString());
        Assert.Equal("Try a capo.\nAnd slower.", renamed.GetProperty("notes").GetString());
        var cleared = await EditAsync(client, id, 3, """{"name":"   ","notes":null}""");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("notes").ValueKind);
        Assert.Equal(4, cleared.GetProperty("revision").GetInt32());

        // The inputs are untouched; the Song's updated time moved with its Version, its revision did not.
        Assert.Equal("[Verse]\nRun|punk|1.1", TestDatabase.Scalar(factory.DataPath, $"SELECT lyrics || '|' || styles || '|' || number FROM versions WHERE id = '{Upper(id)}';"));
        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal("2026-10-04T09:00:00Z", song.GetProperty("updatedAt").GetString());
        Assert.Equal(1, song.GetProperty("revision").GetInt32());
        Assert.Equal("1", song.GetProperty("currentVersion").GetProperty("number").GetString());
    }

    [Fact]
    public async Task AStaleRevisionIs409WithTheCurrentVersionAndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(client, "Shared")).GetProperty("currentVersion"));
        await EditAsync(client, one, 1, """{"name":"Changed elsewhere","notes":"Their note"}""");
        var before = Row(factory.DataPath, one);

        foreach (var stale in new[] { "\"1\"", "\"3\"" })
        {
            foreach (var json in new[] { """{"notes":"My note"}""", """{"archived":true}""" })
            {
                using var response = await PatchAsync(client, one, stale, json);

                var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, Revisions.ConflictCode);
                var current = problem.GetProperty("current");
                Assert.Equal(one, current.GetProperty("id").GetGuid());
                Assert.Equal("Changed elsewhere", current.GetProperty("name").GetString());
                Assert.Equal("Their note", current.GetProperty("notes").GetString());
                Assert.False(current.GetProperty("archived").GetBoolean());
                Assert.True(current.GetProperty("current").GetBoolean());
                Assert.Equal(2, current.GetProperty("revision").GetInt32());
                Assert.Equal(before, Row(factory.DataPath, one));
            }
        }

        // Complement: the same edit on the current revision is stored.
        var applied = await EditAsync(client, one, 2, """{"notes":"My note"}""");
        Assert.Equal("My note", applied.GetProperty("notes").GetString());
        Assert.Equal("Changed elsewhere", applied.GetProperty("name").GetString());
        Assert.Equal(3, applied.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AnEditWithoutARevisionIs428AndAMalformedOneIs400()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(client, "Guarded")).GetProperty("currentVersion"));

        using (var missing = await PatchAsync(client, one, ifMatch: null, """{"archived":true}"""))
        {
            await SetupApi.ProblemAsync(missing, (HttpStatusCode)428, Revisions.RequiredCode);
        }

        foreach (var malformed in new[] { "1", "\"\"", "\"0\"", "\"one\"", "W/\"1\"", "*" })
        {
            using var response = await PatchAsync(client, one, malformed, """{"name":"Unguarded"}""");
            await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, Revisions.InvalidCode);
        }

        Assert.Equal("|active|1", TestDatabase.Scalar(factory.DataPath, "SELECT ifnull(name, '') || '|' || visibility || '|' || revision FROM versions;"));
    }

    [Fact]
    public async Task AWrongFieldIs422AndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(client, "Valid")).GetProperty("currentVersion"));
        await EditAsync(client, one, 1, """{"name":"Kept","notes":"Kept too"}""");

        foreach (var (json, field) in new[]
        {
            ($$"""{"name":"{{new string('x', VersionRules.NameMaximumLength + 1)}}"}""", "name"),
            ("""{"name":"Two\nlines"}""", "name"),
            ("""{"name":5}""", "name"),
            ($$"""{"notes":"{{new string('x', VersionRules.NotesMaximumLength + 1)}}"}""", "notes"),
            ("""{"notes":"tab\there"}""", "notes"),
            ("""{"notes":["a"]}""", "notes"),
            ("""{"notes":"\ud83c"}""", "notes"),
            ("""{"name":"x\udfb8"}""", "name"),
            ("""{"archived":"yes"}""", "archived"),
            ("""{"archived":null}""", "archived"),
            ("""{"archived":1}""", "archived"),
        })
        {
            using var response = await PatchAsync(client, one, "\"2\"", json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
        }

        // One wrong field refuses the whole edit.
        using (var mixed = await PatchAsync(client, one, "\"2\"", $$"""{"name":"Fine","notes":"{{new string('x', VersionRules.NotesMaximumLength + 1)}}"}"""))
        {
            var problem = await SetupApi.ProblemAsync(mixed, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal("Use at most 10,000 characters.", problem.GetProperty("errors").GetProperty("notes")[0].GetString());
        }

        Assert.Equal("Kept|Kept too|active|2", TestDatabase.Scalar(factory.DataPath, "SELECT name || '|' || notes || '|' || visibility || '|' || revision FROM versions;"));

        // Complement: the limits themselves are taken.
        var limits = await EditAsync(
            client,
            one,
            2,
            $$"""{"name":"{{new string('x', VersionRules.NameMaximumLength)}}","notes":"{{new string('y', VersionRules.NotesMaximumLength)}}"}""");
        Assert.Equal(3, limits.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task ArchivingAndUnarchivingLeaveNumbersDescendantsInputsAndTheCurrentVersionAlone()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Tidy");
        var two = SongApi.AddVersionDirectly(factory.DataPath, 1, "2", lyrics: "[Chorus]\nGo", styles: "slow");
        SongApi.AddVersionDirectly(factory.DataPath, 1, "2.1");
        SongApi.AddVersionDirectly(factory.DataPath, 1, "2.1.1");
        var before = AllRows(factory.DataPath);
        var used = TestDatabase.Rows(factory.DataPath, "SELECT number FROM used_version_numbers ORDER BY number;");

        var archived = await EditAsync(client, two, 1, """{"archived":true}""");
        Assert.True(archived.GetProperty("archived").GetBoolean());
        Assert.Equal(2, archived.GetProperty("revision").GetInt32());

        // Only 2 is archived; its descendants are still there, active, numbered as before.
        Assert.Equal(["1 current", "2 archived", "2.1", "2.1.1"], await TreeAsync(client));
        Assert.Equal("[Chorus]\nGo|slow", TestDatabase.Scalar(factory.DataPath, $"SELECT lyrics || '|' || styles FROM versions WHERE id = '{Upper(two)}';"));
        Assert.Equal(used, TestDatabase.Rows(factory.DataPath, "SELECT number FROM used_version_numbers ORDER BY number;"));

        var unarchived = await EditAsync(client, two, 2, """{"archived":false}""");
        Assert.False(unarchived.GetProperty("archived").GetBoolean());
        Assert.Equal(["1 current", "2", "2.1", "2.1.1"], await TreeAsync(client));

        // Everything but 2's revision and updated time is as it was.
        Assert.Equal(
            before.Where(row => !row.StartsWith("2|", StringComparison.Ordinal)),
            AllRows(factory.DataPath).Where(row => !row.StartsWith("2|", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ArchivingTheCurrentVersionKeepsItCurrentAndItsInputs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Still working");
        var one = Id(song.GetProperty("currentVersion"));
        TestDatabase.Execute(factory.DataPath, $"UPDATE versions SET lyrics = 'Keep me', styles = 'lofi' WHERE id = '{Upper(one)}';");

        var archived = await EditAsync(client, one, 1, """{"archived":true}""");

        Assert.True(archived.GetProperty("archived").GetBoolean());
        Assert.True(archived.GetProperty("current").GetBoolean());
        Assert.Equal(["1 archived current"], await TreeAsync(client));
        Assert.Equal("Keep me|lofi", TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || styles FROM versions;"));
        var after = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal(one, after.GetProperty("currentVersion").GetProperty("id").GetGuid());
        Assert.Equal(1, after.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AnEditThatChangesNothingIsNotWritten()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(client, "Same")).GetProperty("currentVersion"));
        await EditAsync(client, one, 1, """{"name":"Same","notes":"Same note"}""");
        var before = Row(factory.DataPath, one);
        clock.Advance(TimeSpan.FromMinutes(1));

        foreach (var json in new[] { "{}", """{"name":"  Same  "}""", """{"notes":"Same note\r\n","archived":false}""" })
        {
            using var response = await PatchAsync(client, one, "\"2\"", json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
            Assert.Equal(2, (await SetupApi.JsonAsync(response)).GetProperty("revision").GetInt32());
        }

        Assert.Equal(before, Row(factory.DataPath, one));
    }

    [Fact]
    public async Task EditingAMissingVersionIs404()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Present");

        using (var gone = await PatchAsync(client, Guid.CreateVersion7(), "\"1\"", """{"name":"Ghost"}"""))
        {
            await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // Only an ID names a Version to edit: a shortcode falls through to the API's 404.
        using var byShortcode = await SongApi.SendJsonAsync(client, HttpMethod.Patch, new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative), """{"name":"Ghost"}""");
        await SetupApi.ProblemAsync(byShortcode, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        Assert.Equal("", TestDatabase.Scalar(factory.DataPath, "SELECT ifnull(name, '') FROM versions;"));
    }

    [Fact]
    public async Task EditingNeedsVersionsWrite()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(setUp, "Scoped")).GetProperty("currentVersion"));
        using var client = factory.CreateClient();
        var uri = Version(one);

        var everythingButWrite = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.VersionsWrite)]);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Patch, uri, everythingButWrite))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(CredentialScopes.VersionsWrite, problem.GetProperty("requiredScope").GetString());
        }

        // Complement: versions.write alone reaches the endpoint (which then asks for the revision).
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.VersionsWrite);
        using (var reached = await CredentialApi.SendAsync(client, HttpMethod.Patch, uri, writer))
        {
            await SetupApi.ProblemAsync(reached, (HttpStatusCode)428, Revisions.RequiredCode);
        }

        // No session and no token: 401.
        using var anonymous = await PatchAsync(client, one, "\"1\"", """{"archived":true}""");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("active|1", TestDatabase.Scalar(factory.DataPath, "SELECT visibility || '|' || revision FROM versions;"));
    }

    private static Uri Version(Guid id) => new($"/api/v1/versions/{id}", UriKind.Relative);

    private static Guid Id(JsonElement version) => version.GetProperty("id").GetGuid();

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    /// <summary>PATCHes <paramref name="json"/> as it is, with <paramref name="ifMatch"/> exactly as written when given.</summary>
    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, string? ifMatch, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, Version(id))
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

    /// <summary>Edits a Version at <paramref name="revision"/> and returns it, asserting 200.</summary>
    private static async Task<JsonElement> EditAsync(HttpClient client, Guid id, int revision, string json)
    {
        using var response = await PatchAsync(client, id, SongApi.Quoted(revision), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>A Version's stored columns, as one line, to compare before and after.</summary>
    private static string Row(string dataPath, Guid id) =>
        TestDatabase.Scalar(
            dataPath,
            $"SELECT number || '|' || ifnull(name, '') || '|' || ifnull(notes, '') || '|' || visibility || '|' || lyrics || '|' || styles || '|' || updated_utc || '|' || revision FROM versions WHERE id = '{Upper(id)}';");

    /// <summary>Every Version's stored columns, one line each, starting with its number, in number order.</summary>
    private static List<string> AllRows(string dataPath) =>
        TestDatabase.Rows(
            dataPath,
            "SELECT number || '|' || song_id || '|' || ifnull(name, '') || '|' || ifnull(notes, '') || '|' || visibility || '|' || lyrics || '|' || styles || '|' || updated_utc || '|' || revision FROM versions ORDER BY number_sort_key;");

    /// <summary>The Song n8-1's Versions, each as <c>number[ archived][ current]</c>.</summary>
    private static async Task<List<string>> TreeAsync(HttpClient client)
    {
        var body = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative)));
        return [.. body.GetProperty("items").EnumerateArray().Select(static version => string.Join(' ', new[]
        {
            version.GetProperty("number").GetString(),
            version.GetProperty("archived").GetBoolean() ? "archived" : null,
            version.GetProperty("current").GetBoolean() ? "current" : null,
        }.OfType<string>()))];
    }
}
