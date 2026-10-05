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
/// A Version's lyrics and styles: read with <c>GET /api/v1/versions/{id}</c> and written with
/// <c>PATCH /api/v1/versions/{id}</c> on the revision they were read at. What is stored and returned
/// is exactly what was sent, apart from line endings, which become <c>\n</c>.
/// </summary>
public sealed class VersionInputsEndpointTests
{
    // Leading and trailing spaces, tabs, a blank line, CRLF and CR line endings, emoji, right-to-left
    // script, a combining accent, an unknown tag, and a final blank line.
    private const string Sent = "  [Verse]  \r\nRun\t\r(ooh, yeah)   \r\n\r\nשלום é 🎸\n[Whisper softly, building]\r\n\r\n";
    private const string Stored = "  [Verse]  \nRun\t\n(ooh, yeah)   \n\nשלום é 🎸\n[Whisper softly, building]\n\n";

    [Fact]
    public async Task LyricsAndStylesComeBackAsSentWithOnlyLineEndingsConverted()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(client, "Written")).GetProperty("currentVersion"));
        clock.Advance(TimeSpan.FromMinutes(5));

        var body = JsonSerializer.Serialize(new { lyrics = Sent, styles = " lofi,\r\n\tdreamy \r" });
        using (var response = await PatchAsync(client, one, "\"1\"", body))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
            var version = await SetupApi.JsonAsync(response);
            Assert.Equal(Stored, version.GetProperty("lyrics").GetString());
            Assert.Equal(" lofi,\n\tdreamy \n", version.GetProperty("styles").GetString());
            Assert.Equal(2, version.GetProperty("revision").GetInt32());
            Assert.Equal("1", version.GetProperty("number").GetString());
        }

        // Read back, it is identical; in the database it is not trimmed either.
        var read = await GetAsync(client, one);
        Assert.Equal(Stored, read.GetProperty("lyrics").GetString());
        Assert.Equal(" lofi,\n\tdreamy \n", read.GetProperty("styles").GetString());
        Assert.Equal(Stored, TestDatabase.Scalar(factory.DataPath, "SELECT lyrics FROM versions;"));
        Assert.NotEqual(Stored.Trim(), TestDatabase.Scalar(factory.DataPath, "SELECT lyrics FROM versions;"));

        // The Song's updated time moved with its Version; its revision did not.
        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal("2026-10-01T09:05:00Z", song.GetProperty("updatedAt").GetString());
        Assert.Equal(1, song.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task NeverSetAndClearedInputsAreEmptyStringsAndNullIsRefused()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(client, "Blank")).GetProperty("currentVersion"));

        var fresh = await GetAsync(client, one);
        Assert.Equal("", fresh.GetProperty("lyrics").GetString());
        Assert.Equal("", fresh.GetProperty("styles").GetString());
        Assert.Equal(1, fresh.GetProperty("revision").GetInt32());

        await EditAsync(client, one, 1, """{"lyrics":"[Intro]","styles":"punk"}""");
        var cleared = await EditAsync(client, one, 2, """{"lyrics":"","styles":""}""");
        Assert.Equal("", cleared.GetProperty("lyrics").GetString());
        Assert.Equal("", cleared.GetProperty("styles").GetString());
        Assert.Equal(3, cleared.GetProperty("revision").GetInt32());

        foreach (var field in new[] { "lyrics", "styles" })
        {
            using var response = await PatchAsync(client, one, "\"3\"", $$"""{"{{field}}":null}""");
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
            Assert.Equal($"Send text: an empty string clears the {field}.", problem.GetProperty("errors").GetProperty(field)[0].GetString());
        }

        Assert.Equal("||3", TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || styles || '|' || revision FROM versions;"));
    }

    [Fact]
    public async Task InputsOverTheLimitOrWithForbiddenCharactersAre422AndChangeNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(client, "Bounded")).GetProperty("currentVersion"));
        await EditAsync(client, one, 1, """{"lyrics":"Kept","styles":"Kept too"}""");

        foreach (var (json, field) in new[]
        {
            ($$"""{"lyrics":"{{new string('x', VersionRules.LyricsMaximumLength + 1)}}"}""", "lyrics"),
            ($$"""{"styles":"{{new string('x', VersionRules.StylesMaximumLength + 1)}}"}""", "styles"),
            ("""{"lyrics":"a\u0000b"}""", "lyrics"),
            ("""{"styles":"\ud83c"}""", "styles"),
            ("""{"lyrics":5}""", "lyrics"),
            ("""{"styles":["punk"]}""", "styles"),
        })
        {
            using var response = await PatchAsync(client, one, "\"2\"", json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
        }

        // 5,001 characters of lyrics is a field error, and one wrong field refuses the whole edit.
        using (var mixed = await PatchAsync(client, one, "\"2\"", $$"""{"styles":"Fine","lyrics":"{{new string('x', VersionRules.LyricsMaximumLength + 1)}}"}"""))
        {
            var problem = await SetupApi.ProblemAsync(mixed, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal("Use at most 5,000 characters.", problem.GetProperty("errors").GetProperty("lyrics")[0].GetString());
        }

        Assert.Equal("Kept|Kept too|2", TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || styles || '|' || revision FROM versions;"));

        // Complement: the limits themselves are taken, counted after CRLF becomes LF.
        var crlf = string.Concat(Enumerable.Repeat("x\\r\\n", VersionRules.LyricsMaximumLength / 2));
        var limits = await EditAsync(
            client,
            one,
            2,
            $$"""{"lyrics":"{{crlf}}","styles":"{{new string('y', VersionRules.StylesMaximumLength)}}"}""");
        Assert.Equal(VersionRules.LyricsMaximumLength, limits.GetProperty("lyrics").GetString()!.Length);
        Assert.Equal(3, limits.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AStaleInputsEditIs409WithTheCurrentLyricsAndStyles()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(client, "Shared")).GetProperty("currentVersion"));
        await EditAsync(client, one, 1, """{"lyrics":"Theirs\n","styles":"their style"}""");

        using (var stale = await PatchAsync(client, one, "\"1\"", """{"lyrics":"Mine"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
            var current = problem.GetProperty("current");
            Assert.Equal("Theirs\n", current.GetProperty("lyrics").GetString());
            Assert.Equal("their style", current.GetProperty("styles").GetString());
            Assert.Equal(2, current.GetProperty("revision").GetInt32());
        }

        using (var missing = await PatchAsync(client, one, ifMatch: null, """{"lyrics":"Mine"}"""))
        {
            await SetupApi.ProblemAsync(missing, (HttpStatusCode)428, Revisions.RequiredCode);
        }

        Assert.Equal("Theirs\n|2", TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || revision FROM versions;"));

        // Complement: on the current revision the same edit is stored.
        var mine = await EditAsync(client, one, 2, """{"lyrics":"Mine"}""");
        Assert.Equal("Mine", mine.GetProperty("lyrics").GetString());
        Assert.Equal("their style", mine.GetProperty("styles").GetString());
    }

    [Fact]
    public async Task OneEditOfLyricsStylesAndANameRaisesTheRevisionOnceAndAnUnchangedOneNotAtAll()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(client, "Together")).GetProperty("currentVersion"));

        var edited = await EditAsync(client, one, 1, """{"name":"Take","lyrics":"[Chorus]\r\nGo","styles":"slow"}""");
        Assert.Equal("Take", edited.GetProperty("name").GetString());
        Assert.Equal("[Chorus]\nGo", edited.GetProperty("lyrics").GetString());
        Assert.Equal(2, edited.GetProperty("revision").GetInt32());
        var before = TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || styles || '|' || updated_utc || '|' || revision FROM versions;");
        clock.Advance(TimeSpan.FromMinutes(1));

        // The same text again (line endings aside) changes nothing.
        foreach (var json in new[] { """{"lyrics":"[Chorus]\nGo"}""", """{"lyrics":"[Chorus]\rGo","styles":"slow","name":"Take"}""" })
        {
            using var response = await PatchAsync(client, one, "\"2\"", json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        }

        Assert.Equal(before, TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || styles || '|' || updated_utc || '|' || revision FROM versions;"));

        // Annotations-only edits still leave the inputs alone.
        var renamed = await EditAsync(client, one, 2, """{"name":"Renamed","archived":true}""");
        Assert.Equal("[Chorus]\nGo", renamed.GetProperty("lyrics").GetString());
        Assert.Equal("slow", renamed.GetProperty("styles").GetString());
    }

    [Fact]
    public async Task ReadingAVersionNeedsCatalogReadAndAMissingOneIs404()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(setUp, "Readable")).GetProperty("currentVersion"));
        using var client = factory.CreateClient();

        var everythingButRead = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Version(one), everythingButRead))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, Version(one), reader))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("\"1\"", read.Headers.ETag?.Tag);
            Assert.Contains("no-store", read.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Equal("", (await SetupApi.JsonAsync(read)).GetProperty("lyrics").GetString());
        }

        using (var gone = await setUp.GetAsync(Version(Guid.CreateVersion7())))
        {
            await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using var anonymous = await client.GetAsync(Version(one));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task WritingInputsNeedsVersionsWrite()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        var one = Id((await SongApi.CreateAsync(setUp, "Scoped")).GetProperty("currentVersion"));
        using var client = factory.CreateClient();

        var everythingButWrite = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.VersionsWrite)]);
        using (var refused = await BearerPatchAsync(client, one, everythingButWrite, """{"lyrics":"Not mine"}"""))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(CredentialScopes.VersionsWrite, problem.GetProperty("requiredScope").GetString());
        }

        Assert.Equal("|1", TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || revision FROM versions;"));

        // Complement: versions.write alone stores them.
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.VersionsWrite);
        using (var written = await BearerPatchAsync(client, one, writer, """{"lyrics":"Mine"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        }

        Assert.Equal("Mine|2", TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || revision FROM versions;"));
    }

    private static Uri Version(Guid id) => new($"/api/v1/versions/{id}", UriKind.Relative);

    private static Guid Id(JsonElement version) => version.GetProperty("id").GetGuid();

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(Version(id));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

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

    /// <summary>PATCHes <paramref name="json"/> at revision 1 with a bearer token and no anti-forgery header.</summary>
    private static async Task<HttpResponseMessage> BearerPatchAsync(HttpClient client, Guid id, string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, Version(id))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", "\"1\""));
        return await client.SendAsync(request);
    }

    /// <summary>Edits a Version at <paramref name="revision"/> and returns it, asserting 200.</summary>
    private static async Task<JsonElement> EditAsync(HttpClient client, Guid id, int revision, string json)
    {
        using var response = await PatchAsync(client, id, SongApi.Quoted(revision), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }
}
