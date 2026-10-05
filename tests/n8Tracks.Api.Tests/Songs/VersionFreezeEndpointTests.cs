using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// A Version with a Generation attached, over the API: its lyrics and styles are refused with 409
/// <c>version_frozen</c> on every write path (an edit, autosave, a credential's edit, a restore),
/// after the revision checks, while its name, notes, and archived flag stay editable; the freeze is
/// permanent; and Generations have ordinals and shortcodes the resolver accepts.
/// </summary>
public sealed class VersionFreezeEndpointTests
{
    private const string Lyrics = "[Verse]\nOriginal line";
    private const string Styles = "dream pop";

    [Fact]
    public async Task AFrozenVersionsLyricsOrStylesChangeIsRefusedWith409VersionFrozenAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await FrozenVersionAsync(factory, client, "Frozen");
        var before = Stored(factory, one);

        foreach (var body in new[]
        {
            """{"lyrics":"Changed"}""",
            """{"styles":"Changed"}""",
            """{"lyrics":"Changed","styles":"Changed"}""",
            """{"lyrics":""}""",
            $$"""{"lyrics":{{JsonSerializer.Serialize(Lyrics + " ")}}}""",
        })
        {
            using var response = await SendAsync(client, HttpMethod.Patch, Version(one), "\"3\"", body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "version_frozen");
            Assert.Contains("Create a new Version from it", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
            Assert.Equal(one, problem.GetProperty("versionId").GetGuid());
            Assert.Equal("n8-1-v1", problem.GetProperty("versionShortcode").GetString());
            Assert.Equal(before, Stored(factory, one));
        }

        var read = await GetAsync(client, one);
        Assert.True(read.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(3, read.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AFrozenVersionsNameNotesAndArchivedFlagStayEditable()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await FrozenVersionAsync(factory, client, "Annotated");
        await SongApi.CreateAsync(client, "Second");

        var renamed = await EditAsync(client, one, 3, """{"name":"Keeper","notes":"The one with the hook."}""");
        Assert.Equal("Keeper", renamed.GetProperty("name").GetString());
        Assert.Equal("The one with the hook.", renamed.GetProperty("notes").GetString());
        Assert.Equal(4, renamed.GetProperty("revision").GetInt32());
        Assert.True(renamed.GetProperty("isFrozen").GetBoolean());

        var archived = await EditAsync(client, one, 4, """{"archived":true}""");
        Assert.True(archived.GetProperty("archived").GetBoolean());
        var active = await EditAsync(client, one, 5, """{"archived":false,"name":null}""");
        Assert.False(active.GetProperty("archived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, active.GetProperty("name").ValueKind);
        Assert.Equal(Lyrics + "|" + Styles, Stored(factory, one));
    }

    [Fact]
    public async Task UnchangedInputsWithMetadataAreAMetadataEditAndAChangedInputRefusesTheWholeEdit()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await FrozenVersionAsync(factory, client, "Mixed");

        // Unchanged after line-ending conversion: accepted as a metadata edit.
        var crlf = JsonSerializer.Serialize(Lyrics.Replace("\n", "\r\n", StringComparison.Ordinal));
        var named = await EditAsync(client, one, 3, $$"""{"name":"Same words","lyrics":{{crlf}},"styles":"{{Styles}}"}""");
        Assert.Equal("Same words", named.GetProperty("name").GetString());
        Assert.Equal(4, named.GetProperty("revision").GetInt32());

        // Unchanged inputs and nothing else: a no-op, no revision bump.
        var same = await EditAsync(client, one, 4, $$"""{"lyrics":{{JsonSerializer.Serialize(Lyrics)}},"styles":"{{Styles}}"}""");
        Assert.Equal(4, same.GetProperty("revision").GetInt32());

        // One changed input with a metadata change: refused whole, the name included.
        using (var mixed = await SendAsync(client, HttpMethod.Patch, Version(one), "\"4\"", """{"name":"Lost","notes":"Lost","archived":true,"styles":"other"}"""))
        {
            await SetupApi.ProblemAsync(mixed, HttpStatusCode.Conflict, "version_frozen");
        }

        var read = await GetAsync(client, one);
        Assert.Equal("Same words", read.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("notes").ValueKind);
        Assert.False(read.GetProperty("archived").GetBoolean());
        Assert.Equal(4, read.GetProperty("revision").GetInt32());
        Assert.Equal(Lyrics + "|" + Styles, Stored(factory, one));
    }

    [Fact]
    public async Task AStaleOrMissingRevisionIsReportedBeforeTheFreeze()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await FrozenVersionAsync(factory, client, "Stale");

        using (var stale = await SendAsync(client, HttpMethod.Patch, Version(one), "\"2\"", """{"lyrics":"Changed"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.True(problem.GetProperty("current").GetProperty("isFrozen").GetBoolean());
            Assert.Equal(3, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var missing = await SendAsync(client, HttpMethod.Patch, Version(one), null, """{"lyrics":"Changed"}"""))
        {
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        }

        Assert.Equal(Lyrics + "|" + Styles, Stored(factory, one));
    }

    [Fact]
    public async Task RestoringASnapshotOnAFrozenVersionIsRefusedAndNothingIsSnapshotted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Restored");
        await EditAsync(client, one, 1, $$"""{"lyrics":{{JsonSerializer.Serialize(Lyrics)}},"styles":"{{Styles}}"}""");
        var older = await SnapshotAsync(client, one, """{"lyrics":"Older text","styles":"older"}""");
        var same = await SnapshotAsync(client, one, $$"""{"lyrics":{{JsonSerializer.Serialize(Lyrics)}},"styles":"{{Styles}}"}""");
        await SongApi.AttachGenerationAsync(factory, one.ToString());
        var snapshots = SnapshotCount(factory, one);

        using (var refused = await SendAsync(client, HttpMethod.Post, Restore(one, older), "\"3\"", null))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "version_frozen");
            Assert.Equal("n8-1-v1", problem.GetProperty("versionShortcode").GetString());
        }

        using (var stale = await SendAsync(client, HttpMethod.Post, Restore(one, older), "\"2\"", null))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        Assert.Equal(snapshots, SnapshotCount(factory, one));
        Assert.Equal(Lyrics + "|" + Styles, Stored(factory, one));

        // Restoring the text it already holds changes nothing and is fine.
        using var unchanged = await SendAsync(client, HttpMethod.Post, Restore(one, same), "\"3\"", null);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(3, (await SetupApi.JsonAsync(unchanged)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task ACredentialsEditOfAFrozenVersionIsRefusedAndTakesNoSnapshot()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await FrozenVersionAsync(factory, client, "Tooled");
        using var tool = factory.CreateClient();
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.VersionsWrite, CredentialScopes.CatalogRead);

        using (var refused = await BearerAsync(tool, Version(one), token, "\"3\"", """{"lyrics":"Written by a tool"}"""))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "version_frozen");
        }

        Assert.Equal("0", SnapshotCount(factory, one));
        Assert.Equal(Lyrics + "|" + Styles, Stored(factory, one));

        using var renamed = await BearerAsync(tool, Version(one), token, "\"3\"", """{"name":"Tool name"}""");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    }

    [Fact]
    public async Task AVersionWithNoGenerationStaysMutableAndBranchingFromAFrozenOneGivesAMutableCopy()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await FrozenVersionAsync(factory, client, "Branch");

        using (var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative), """{"sourceVersionId":"n8-1-v1","number":"2"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.False((await SetupApi.JsonAsync(created)).GetProperty("isFrozen").GetBoolean());
        }

        var two = (await GetAsync(client, "n8-1-v2")).GetProperty("id").GetGuid();
        Assert.Equal(Lyrics + "|" + Styles, Stored(factory, two));
        for (var revision = 1; revision <= 3; revision++)
        {
            var edited = await EditAsync(client, two, revision, $$"""{"lyrics":"Edit {{revision}}"}""");
            Assert.False(edited.GetProperty("isFrozen").GetBoolean());
        }

        Assert.Equal("Edit 3|" + Styles, Stored(factory, two));
        Assert.Equal(Lyrics + "|" + Styles, Stored(factory, one));

        // The list shows which Versions are frozen.
        var list = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative)));
        Assert.Equal(
            new[] { "1:True", "2:False" },
            list.GetProperty("items").EnumerateArray().Select(static item => $"{item.GetProperty("number").GetString()}:{item.GetProperty("isFrozen").GetBoolean()}"));
    }

    [Fact]
    public async Task TheFreezeIsPermanentEvenWhenEveryGenerationIsGone()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await FrozenVersionAsync(factory, client, "Permanent");
        var second = await SongApi.AttachGenerationAsync(factory, "n8-1-v1");
        Assert.Equal(2, second.Generation.Ordinal);

        // As a later deletion would: every Generation removed.
        TestDatabase.Execute(factory.DataPath, "DELETE FROM generations;");

        using (var refused = await SendAsync(client, HttpMethod.Patch, Version(one), "\"4\"", """{"lyrics":"Changed"}"""))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "version_frozen");
        }

        Assert.True((await GetAsync(client, one)).GetProperty("isFrozen").GetBoolean());

        // The next ordinal is never a reused one.
        var third = await SongApi.AttachGenerationAsync(factory, "n8-1-v1");
        Assert.Equal(3, third.Generation.Ordinal);
        Assert.Equal("n8-1-v1-g3", third.Shortcode);
    }

    [Fact]
    public async Task TheDatabaseRefusesChangingAFrozenVersionsInputsOrUnfreezingItWhateverWritesIt()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await FrozenVersionAsync(factory, client, "Guarded");
        var id = one.ToString().ToUpperInvariant();

        foreach (var sql in new[]
        {
            $"UPDATE versions SET lyrics = 'x' WHERE id = '{id}';",
            $"UPDATE versions SET styles = 'x' WHERE id = '{id}';",
            $"UPDATE versions SET is_frozen = 0 WHERE id = '{id}';",
            $"UPDATE versions SET last_generation_ordinal = 0 WHERE id = '{id}';",
            "UPDATE generations SET ordinal = 7;",
        })
        {
            var refused = Assert.Throws<SqliteException>(() => TestDatabase.Execute(factory.DataPath, sql));
            Assert.Equal(19, refused.SqliteErrorCode);
        }

        // Metadata still changes underneath.
        TestDatabase.Execute(factory.DataPath, $"UPDATE versions SET name = 'Direct', notes = 'n', visibility = 'archived' WHERE id = '{id}';");
        Assert.Equal(Lyrics + "|" + Styles, Stored(factory, one));
    }

    [Fact]
    public async Task GenerationsHaveOrdinalsPerVersionAndShortcodesTheResolverAccepts()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Generated");
        var archived = SongApi.AddVersionDirectly(factory.DataPath, 1, "1.1", visibility: "archived");

        var first = await SongApi.AttachGenerationAsync(factory, "n8-1-v1");
        var second = await SongApi.AttachGenerationAsync(factory, "N8-1-V1");
        var other = await SongApi.AttachGenerationAsync(factory, archived.ToString());
        Assert.Equal(["n8-1-v1-g1", "n8-1-v1-g2", "n8-1-v1.1-g1"], new[] { first.Shortcode, second.Shortcode, other.Shortcode });
        Assert.Equal(TestClock.Start, first.Generation.CreatedUtc);
        Assert.Equal("3", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;"));

        foreach (var reference in new[] { "n8-1-v1-g2", "N8-1-V1-G2", second.Generation.Id.ToString() })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/resolve/{reference}", UriKind.Relative));
            var resolved = await SetupApi.JsonAsync(response);
            Assert.Equal("generation", resolved.GetProperty("entityType").GetString());
            Assert.Equal(second.Generation.Id, resolved.GetProperty("id").GetGuid());
            Assert.Equal("n8-1-v1-g2", resolved.GetProperty("shortcode").GetString());
            Assert.Equal("active", resolved.GetProperty("status").GetString());
            Assert.Equal("n8-1", resolved.GetProperty("song").GetProperty("shortcode").GetString());
            Assert.Equal("n8-1-v1", resolved.GetProperty("version").GetProperty("shortcode").GetString());
            Assert.Equal(second.Generation.VersionId, resolved.GetProperty("version").GetProperty("id").GetGuid());
        }

        foreach (var unknown in new[] { "n8-1-v1-g3", "n8-1-v2-g1", "n8-2-v1-g1", "n8-1-v1-g0" })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/resolve/{unknown}", UriKind.Relative));
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "reference_not_found");
        }

        // A Version keeps no "version" field in its answer, and a Generation shortcode is no Version.
        using (var version = await client.GetAsync(new Uri("/api/v1/resolve/n8-1-v1", UriKind.Relative)))
        {
            Assert.False((await SetupApi.JsonAsync(version)).TryGetProperty("version", out _));
        }

        using var notAVersion = await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1-g1", UriKind.Relative));
        await SetupApi.ProblemAsync(notAVersion, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task AttachingAGenerationRaisesTheRevisionAndMovesTheUpdatedTime()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await VersionOfNewSongAsync(client, "Timed");
        clock.Advance(TimeSpan.FromMinutes(10));

        await SongApi.AttachGenerationAsync(factory, one.ToString());

        var read = await GetAsync(client, one);
        Assert.Equal(2, read.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:10:00Z", read.GetProperty("updatedAt").GetString());
        Assert.True(read.GetProperty("isFrozen").GetBoolean());
        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal("2026-10-01T09:10:00Z", song.GetProperty("updatedAt").GetString());

        // An unknown Version or a reference of another kind attaches nothing.
        await using var scope = factory.Services.CreateAsyncScope();
        var generations = scope.ServiceProvider.GetRequiredService<GenerationService>();
        foreach (var reference in new[] { "n8-1-v9", "n8-1", "nonsense", null, Guid.CreateVersion7().ToString() })
        {
            Assert.IsType<GenerationAttachOutcome.VersionNotFound>(await generations.AttachAsync(reference, CancellationToken.None));
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;"));
    }

    /// <summary>A new Song's Version 1 with <see cref="Lyrics"/> and <see cref="Styles"/>, then a Generation: frozen at revision 3.</summary>
    private static async Task<Guid> FrozenVersionAsync(N8TracksApiFactory factory, HttpClient client, string title)
    {
        var one = await VersionOfNewSongAsync(client, title);
        await EditAsync(client, one, 1, $$"""{"lyrics":{{JsonSerializer.Serialize(Lyrics)}},"styles":"{{Styles}}"}""");
        var attached = await SongApi.AttachGenerationAsync(factory, one.ToString());
        Assert.Equal(1, attached.Generation.Ordinal);
        return one;
    }

    private static async Task<Guid> VersionOfNewSongAsync(HttpClient client, string title) =>
        (await SongApi.CreateAsync(client, title)).GetProperty("currentVersion").GetProperty("id").GetGuid();

    private static Uri Version(Guid id) => new($"/api/v1/versions/{id}", UriKind.Relative);

    private static Uri Restore(Guid versionId, Guid snapshotId) => new($"/api/v1/versions/{versionId}/snapshots/{snapshotId}/restore", UriKind.Relative);

    /// <summary>The Version's lyrics and styles exactly as stored, joined with <c>|</c>.</summary>
    private static string Stored(N8TracksApiFactory factory, Guid id) =>
        TestDatabase.Scalar(factory.DataPath, $"SELECT lyrics || '|' || styles FROM versions WHERE id = '{id.ToString().ToUpperInvariant()}';");

    private static string SnapshotCount(N8TracksApiFactory factory, Guid id) =>
        TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM editor_revisions WHERE version_id = '{id.ToString().ToUpperInvariant()}';");

    private static Task<JsonElement> GetAsync(HttpClient client, Guid id) => GetAsync(client, id.ToString());

    private static async Task<JsonElement> GetAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{reference}", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<Guid> SnapshotAsync(HttpClient client, Guid versionId, string json)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/versions/{versionId}/snapshots", UriKind.Relative), json);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Sends with the anti-forgery header, <paramref name="ifMatch"/> as written when given, and a JSON body when given.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string? ifMatch, string? json)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> BearerAsync(HttpClient client, Uri uri, string token, string ifMatch, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, uri)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> EditAsync(HttpClient client, Guid id, int revision, string json)
    {
        using var response = await SendAsync(client, HttpMethod.Patch, Version(id), SongApi.Quoted(revision), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }
}
