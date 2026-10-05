using System.Globalization;
using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// A Version's editing history: snapshots of its lyrics and styles taken with
/// <c>POST /api/v1/versions/{id}/snapshots</c> (deduplicated against the newest, pruned to 50),
/// listed newest first, read one by one, and restored on the Version's revision after the current
/// text is snapshotted. A credential's edit of the text is snapshotted first; a session's is not.
/// </summary>
public sealed class EditorRevisionEndpointTests
{
    [Fact]
    public async Task SnapshotsAreStoredListedNewestFirstAndReadBackWithTheirText()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Remembered");

        JsonElement first;
        using (var response = await SnapshotAsync(client, one, """{"lyrics":"[Verse]\r\nFirst","styles":"lofi"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            first = await SetupApi.JsonAsync(response);
        }

        Assert.Equal("[Verse]\nFirst", first.GetProperty("lyrics").GetString());
        Assert.Equal("lofi", first.GetProperty("styles").GetString());
        Assert.Equal(one, first.GetProperty("versionId").GetGuid());
        Assert.Equal("2026-10-01T09:00:00Z", first.GetProperty("createdAt").GetString());

        clock.Advance(TimeSpan.FromMinutes(2));
        var second = await SnapshotCreatedAsync(client, one, """{"lyrics":"[Verse]\nSecond","styles":""}""");

        var list = await ListAsync(client, one);
        Assert.Equal([Id(second), Id(first)], list.Select(Id));
        Assert.Equal(["2026-10-01T09:02:00Z", "2026-10-01T09:00:00Z"], list.Select(static item => item.GetProperty("createdAt").GetString()));
        Assert.All(list, static item => Assert.False(item.TryGetProperty("lyrics", out _)));

        using (var read = await client.GetAsync(Snapshot(one, Id(first))))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var body = await SetupApi.JsonAsync(read);
            Assert.Equal("[Verse]\nFirst", body.GetProperty("lyrics").GetString());
            Assert.Equal("lofi", body.GetProperty("styles").GetString());
        }

        // Taking snapshots changes neither the Version nor its revision.
        Assert.Equal("|1", TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || revision FROM versions;"));
    }

    [Fact]
    public async Task ASnapshotIdenticalToTheNewestIsNotStoredAndAnswers200WithIt()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Repeated");

        var first = await SnapshotCreatedAsync(client, one, """{"lyrics":"Same\n","styles":"pop"}""");
        clock.Advance(TimeSpan.FromMinutes(1));

        // The same text again, line endings aside, is the existing snapshot.
        using (var again = await SnapshotAsync(client, one, """{"lyrics":"Same\r\n","styles":"pop"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(Id(first), Id(await SetupApi.JsonAsync(again)));
        }

        Assert.Equal("1", Count(factory, one));

        // Complement: only the newest is compared. A change of styles alone is new, and going back
        // to the first text after it is new again.
        await SnapshotCreatedAsync(client, one, """{"lyrics":"Same\n","styles":"rock"}""");
        clock.Advance(TimeSpan.FromMinutes(1));
        await SnapshotCreatedAsync(client, one, """{"lyrics":"Same\n","styles":"pop"}""");
        Assert.Equal("3", Count(factory, one));
    }

    [Fact]
    public async Task CapturedAtDatesTheSnapshotButNeverLaterThanNow()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Offline");
        clock.Advance(TimeSpan.FromHours(1));

        var earlier = await SnapshotCreatedAsync(client, one, """{"lyrics":"Typed offline","styles":"","capturedAt":"2026-10-01T09:30:15.250Z"}""");
        Assert.Equal("2026-10-01T09:30:15.25Z", earlier.GetProperty("createdAt").GetString());

        var future = await SnapshotCreatedAsync(client, one, """{"lyrics":"From a fast clock","styles":"","capturedAt":"2026-10-02T09:00:00+02:00"}""");
        Assert.Equal("2026-10-01T10:00:00Z", future.GetProperty("createdAt").GetString());

        // Newest first is by capture time.
        var later = await SnapshotCreatedAsync(client, one, """{"lyrics":"Typed offline, later","styles":"","capturedAt":"2026-10-01T09:45:00Z"}""");
        Assert.Equal([Id(future), Id(later), Id(earlier)], (await ListAsync(client, one)).Select(Id));
    }

    [Fact]
    public async Task AWrongSnapshotIs422AndOneOfNoVersionIs404AndNothingIsStored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Checked");

        foreach (var (json, field) in new[]
        {
            ("""{"styles":"pop"}""", "lyrics"),
            ("""{"lyrics":"Words","styles":null}""", "styles"),
            ("""{"lyrics":5,"styles":"pop"}""", "lyrics"),
            ($$"""{"lyrics":"{{new string('x', 5_001)}}","styles":""}""", "lyrics"),
            ($$"""{"lyrics":"","styles":"{{new string('x', 1_001)}}"}""", "styles"),
            ("""{"lyrics":"a\u0000b","styles":""}""", "lyrics"),
            ("""{"lyrics":"","styles":"","capturedAt":"yesterday"}""", "capturedAt"),
            ("""{"lyrics":"","styles":"","capturedAt":"2026-10-01"}""", "capturedAt"),
            ("""{"lyrics":"","styles":"","capturedAt":7}""", "capturedAt"),
        })
        {
            using var response = await SnapshotAsync(client, one, json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
        }

        Assert.Equal("0", Count(factory, one));

        var nowhere = Guid.CreateVersion7();
        using (var missing = await SnapshotAsync(client, nowhere, """{"lyrics":"","styles":""}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using (var list = await client.GetAsync(Snapshots(nowhere)))
        {
            await SetupApi.ProblemAsync(list, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT COUNT(*) FROM editor_revisions;"));
    }

    [Fact]
    public async Task EachVersionKeepsItsFiftyNewestAndAddingOneRemovesExactlyTheOldest()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Pruned");
        var other = SongApi.AddVersionDirectly(factory.DataPath, 1, "2");

        // 50 each, one minute apart, all before the clock's start.
        AddSnapshotsDirectly(factory.DataPath, one, EditorRevisionService.MaximumKept, "mine");
        AddSnapshotsDirectly(factory.DataPath, other, EditorRevisionService.MaximumKept, "theirs");
        var before = (await ListAsync(client, one)).Select(Id).ToList();
        Assert.Equal(50, before.Count);

        var added = await SnapshotCreatedAsync(client, one, """{"lyrics":"Fifty-first","styles":""}""");
        var after = (await ListAsync(client, one)).Select(Id).ToList();
        Assert.Equal([Id(added), .. before[..^1]], after);
        Assert.DoesNotContain(before[^1], after);

        for (var more = 0; more < 3; more++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await SnapshotCreatedAsync(client, one, $$"""{"lyrics":"More {{more}}","styles":""}""");
            Assert.Equal("50", Count(factory, one));
        }

        // Another Version's history is never pruned with this one's.
        Assert.Equal("50", Count(factory, other));
        Assert.Equal(
            "theirs 0",
            TestDatabase.Scalar(factory.DataPath, $"SELECT lyrics FROM editor_revisions WHERE version_id = '{Upper(other)}' ORDER BY created_utc LIMIT 1;"));
    }

    [Fact]
    public async Task ASnapshotOfAnotherVersionIsNeverListedReadOrRestored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Mine");
        var other = SongApi.AddVersionDirectly(factory.DataPath, 1, "2", lyrics: "Theirs");
        var theirs = await SnapshotCreatedAsync(client, other, """{"lyrics":"Their draft","styles":""}""");

        Assert.Empty(await ListAsync(client, one));

        using (var read = await client.GetAsync(Snapshot(one, Id(theirs))))
        {
            await SetupApi.ProblemAsync(read, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using (var restore = await RestoreAsync(client, one, Id(theirs), "\"1\""))
        {
            await SetupApi.ProblemAsync(restore, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal("|1", TestDatabase.Scalar(factory.DataPath, $"SELECT lyrics || '|' || revision FROM versions WHERE id = '{Upper(one)}';"));

        // Complement: through its own Version it is read.
        using var own = await client.GetAsync(Snapshot(other, Id(theirs)));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task RestoringSnapshotsTheCurrentTextFirstAndReplacesOnlyTheLyricsAndStyles()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Restored");
        await EditAsync(client, one, 1, """{"name":"Take","notes":"Kept","lyrics":"[Verse]\nEarly","styles":"folk"}""");
        var early = await SnapshotCreatedAsync(client, one, """{"lyrics":"[Verse]\nEarly","styles":"folk"}""");
        clock.Advance(TimeSpan.FromMinutes(1));
        await EditAsync(client, one, 2, """{"lyrics":"[Verse]\nRewritten","styles":"metal"}""");
        clock.Advance(TimeSpan.FromMinutes(1));

        JsonElement restored;
        using (var response = await RestoreAsync(client, one, Id(early), "\"3\""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"4\"", response.Headers.ETag?.Tag);
            restored = await SetupApi.JsonAsync(response);
        }

        Assert.Equal("[Verse]\nEarly", restored.GetProperty("lyrics").GetString());
        Assert.Equal("folk", restored.GetProperty("styles").GetString());
        Assert.Equal("Take", restored.GetProperty("name").GetString());
        Assert.Equal("Kept", restored.GetProperty("notes").GetString());
        Assert.Equal(4, restored.GetProperty("revision").GetInt32());

        // The replaced text is now the newest snapshot, so the restore can be undone.
        var list = await ListAsync(client, one);
        Assert.Equal(2, list.Count);
        Assert.Equal(Id(early), Id(list[1]));
        var undo = Id(list[0]);
        using (var replaced = await client.GetAsync(Snapshot(one, undo)))
        {
            var body = await SetupApi.JsonAsync(replaced);
            Assert.Equal("[Verse]\nRewritten", body.GetProperty("lyrics").GetString());
            Assert.Equal("metal", body.GetProperty("styles").GetString());
            Assert.Equal("2026-10-01T09:02:00Z", body.GetProperty("createdAt").GetString());
        }

        using (var back = await RestoreAsync(client, one, undo, "\"4\""))
        {
            Assert.Equal("[Verse]\nRewritten", (await SetupApi.JsonAsync(back)).GetProperty("lyrics").GetString());
        }

        // Restoring the text the Version already holds changes nothing.
        using (var same = await RestoreAsync(client, one, undo, "\"5\""))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal("\"5\"", same.Headers.ETag?.Tag);
        }
    }

    [Fact]
    public async Task RestoringOnAStaleRevisionIs409AndChangesNothingAndARevisionIsRequired()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Contested");
        var old = await SnapshotCreatedAsync(client, one, """{"lyrics":"Old","styles":""}""");
        await EditAsync(client, one, 1, """{"lyrics":"Changed elsewhere"}""");

        using (var stale = await RestoreAsync(client, one, Id(old), "\"1\""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
            Assert.Equal("Changed elsewhere", problem.GetProperty("current").GetProperty("lyrics").GetString());
            Assert.Equal(2, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var missing = await RestoreAsync(client, one, Id(old), ifMatch: null))
        {
            await SetupApi.ProblemAsync(missing, (HttpStatusCode)428, Revisions.RequiredCode);
        }

        using (var noSuchSnapshot = await RestoreAsync(client, one, Guid.CreateVersion7(), "\"2\""))
        {
            await SetupApi.ProblemAsync(noSuchSnapshot, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using (var noSuchVersion = await RestoreAsync(client, Guid.CreateVersion7(), Id(old), "\"2\""))
        {
            await SetupApi.ProblemAsync(noSuchVersion, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal("Changed elsewhere|2", TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || revision FROM versions;"));
        Assert.Equal("1", Count(factory, one));

        // Complement: on the current revision it is restored.
        using var current = await RestoreAsync(client, one, Id(old), "\"2\"");
        Assert.Equal("Old", (await SetupApi.JsonAsync(current)).GetProperty("lyrics").GetString());
    }

    [Fact]
    public async Task ACredentialsEditOfTheTextIsSnapshottedFirstAndASessionsIsNot()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var session = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(session, "Tooled");

        // The session's own edit: no snapshot (the editor's idle and leave triggers cover it).
        await EditAsync(session, one, 1, """{"lyrics":"Written in the editor","styles":"pop"}""");
        Assert.Equal("0", Count(factory, one));
        clock.Advance(TimeSpan.FromMinutes(1));

        using var tool = factory.CreateClient();
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.VersionsWrite, CredentialScopes.CatalogRead);
        using (var edited = await BearerAsync(tool, HttpMethod.Patch, Version(one), token, "\"2\"", """{"lyrics":"Written by a tool"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        var list = await ListAsync(session, one);
        var kept = Assert.Single(list);
        Assert.Equal("2026-10-01T09:01:00Z", kept.GetProperty("createdAt").GetString());
        using (var read = await BearerAsync(tool, HttpMethod.Get, Snapshot(one, Id(kept)), token))
        {
            var body = await SetupApi.JsonAsync(read);
            Assert.Equal("Written in the editor", body.GetProperty("lyrics").GetString());
            Assert.Equal("pop", body.GetProperty("styles").GetString());
        }

        // A tool's edit that leaves the text alone (a name; the same lyrics) takes no snapshot, and
        // neither does a refused one.
        using (var named = await BearerAsync(tool, HttpMethod.Patch, Version(one), token, "\"3\"", """{"name":"Tool take","lyrics":"Written by a tool"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        }

        using (var stale = await BearerAsync(tool, HttpMethod.Patch, Version(one), token, "\"1\"", """{"lyrics":"Stale"}"""))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }

        Assert.Equal("1", Count(factory, one));

        // A second tool edit snapshots the first tool's text.
        using (var again = await BearerAsync(tool, HttpMethod.Patch, Version(one), token, "\"4\"", """{"styles":"rock"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        Assert.Equal("2", Count(factory, one));
        Assert.Equal(
            "Written by a tool|pop",
            TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || styles FROM editor_revisions ORDER BY created_utc DESC, sequence DESC LIMIT 1;"));
    }

    [Fact]
    public async Task SnapshotsAndRestoreNeedVersionsWriteAndReadingThemNeedsCatalogRead()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(setUp, "Scoped");
        var snapshot = Id(await SnapshotCreatedAsync(setUp, one, """{"lyrics":"Kept","styles":""}"""));
        using var client = factory.CreateClient();

        foreach (var (method, uri, scope) in new[]
        {
            (HttpMethod.Post, Snapshots(one), CredentialScopes.VersionsWrite),
            (HttpMethod.Post, Restore(one, snapshot), CredentialScopes.VersionsWrite),
            (HttpMethod.Get, Snapshots(one), CredentialScopes.CatalogRead),
            (HttpMethod.Get, Snapshot(one, snapshot), CredentialScopes.CatalogRead),
        })
        {
            var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(other => other != scope)]);
            using (var refused = await BearerAsync(client, method, uri, others, "\"1\"", """{"lyrics":"Tool","styles":""}"""))
            {
                var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
                Assert.Equal(scope, problem.GetProperty("requiredScope").GetString());
            }

            var holder = await CredentialApi.CreateTokenAsync(factory, scope);
            using var allowed = await BearerAsync(client, method, uri, holder, "\"1\"", """{"lyrics":"Tool","styles":""}""");
            Assert.True(allowed.IsSuccessStatusCode, $"{method} {uri}: {(int)allowed.StatusCode}");
        }

        using var anonymous = await client.GetAsync(Snapshots(one));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static Uri Version(Guid id) => new($"/api/v1/versions/{id}", UriKind.Relative);

    private static Uri Snapshots(Guid versionId) => new($"/api/v1/versions/{versionId}/snapshots", UriKind.Relative);

    private static Uri Snapshot(Guid versionId, Guid snapshotId) => new($"/api/v1/versions/{versionId}/snapshots/{snapshotId}", UriKind.Relative);

    private static Uri Restore(Guid versionId, Guid snapshotId) => new($"/api/v1/versions/{versionId}/snapshots/{snapshotId}/restore", UriKind.Relative);

    private static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    private static string Count(N8TracksApiFactory factory, Guid versionId) =>
        TestDatabase.Scalar(factory.DataPath, $"SELECT COUNT(*) FROM editor_revisions WHERE version_id = '{Upper(versionId)}';");

    private static async Task<Guid> VersionOfNewSongAsync(HttpClient client, string title) =>
        (await SongApi.CreateAsync(client, title)).GetProperty("currentVersion").GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> SnapshotAsync(HttpClient client, Guid versionId, string json) =>
        SongApi.SendJsonAsync(client, HttpMethod.Post, Snapshots(versionId), json);

    /// <summary>Takes a snapshot and returns it, asserting 201.</summary>
    private static async Task<JsonElement> SnapshotCreatedAsync(HttpClient client, Guid versionId, string json)
    {
        using var response = await SnapshotAsync(client, versionId, json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, Guid versionId)
    {
        using var response = await client.GetAsync(Snapshots(versionId));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    /// <summary>POSTs a restore with the anti-forgery header and, when given, <paramref name="ifMatch"/> as written.</summary>
    private static async Task<HttpResponseMessage> RestoreAsync(HttpClient client, Guid versionId, Guid snapshotId, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Restore(versionId, snapshotId));
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    /// <summary>Edits a Version at <paramref name="revision"/> through the session, asserting 200.</summary>
    private static async Task EditAsync(HttpClient client, Guid id, int revision, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, Version(id))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Sends a request with a bearer token and no anti-forgery header: with <paramref name="json"/>
    /// and <paramref name="ifMatch"/> on a write, with neither on a read.
    /// </summary>
    private static async Task<HttpResponseMessage> BearerAsync(HttpClient client, HttpMethod method, Uri uri, string token, string? ifMatch = null, string? json = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent(json ?? "{}", System.Text.Encoding.UTF8, "application/json");
            if (ifMatch is not null)
            {
                Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
            }
        }

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Adds <paramref name="count"/> snapshots of a Version straight in the database, one minute apart
    /// and ending a minute before the test clock starts; the oldest holds "<paramref name="text"/> 0".
    /// </summary>
    private static void AddSnapshotsDirectly(string dataPath, Guid versionId, int count, string text)
    {
        var sequence = long.Parse(TestDatabase.Scalar(dataPath, "SELECT COALESCE(MAX(sequence), 0) FROM editor_revisions;"), CultureInfo.InvariantCulture);
        var values = Enumerable.Range(0, count).Select(index =>
        {
            var created = TestClock.Start.AddMinutes(index - count).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            return $"('{Upper(Guid.CreateVersion7())}', '{Upper(versionId)}', {sequence + index + 1}, '{text} {index}', '', '{created}')";
        });
        TestDatabase.Execute(dataPath, $"INSERT INTO editor_revisions (id, version_id, sequence, lyrics, styles, created_utc) VALUES {string.Join(", ", values)};");
    }
}
