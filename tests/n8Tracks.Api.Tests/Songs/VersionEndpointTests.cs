using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// A Song's Versions as a tree: <c>GET /api/v1/songs/{reference}/versions</c> lists them,
/// <c>POST /api/v1/songs/{id}/versions</c> creates one from another, and
/// <c>PUT /api/v1/songs/{reference}/current-version</c> chooses the one being worked on.
/// </summary>
public sealed class VersionEndpointTests
{
    [Fact]
    public async Task CreatingFromAVersionCopiesItsInputsAssignsTheChosenNumberAndMakesItCurrent()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Branching");
        var songId = song.GetProperty("id").GetString()!;
        var one = song.GetProperty("currentVersion").GetProperty("id").GetString()!;
        TestDatabase.Execute(
            factory.DataPath,
            $"UPDATE versions SET lyrics = '[Verse]\nRun', styles = 'punk, fast', name = 'First', notes = 'A note', updated_utc = updated_utc WHERE id = '{one.ToUpperInvariant()}';");
        var sourceBefore = SourceRow(factory.DataPath, "1");
        clock.Advance(TimeSpan.FromMinutes(5));

        using var response = await CreateAsync(client, songId, $$"""{"sourceVersionId":"{{one}}","number":"1.1","name":"  Guitar experimentation  "}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var created = await SetupApi.JsonAsync(response);
        Assert.Equal("1.1", created.GetProperty("number").GetString());
        Assert.Equal("n8-1-v1.1", created.GetProperty("shortcode").GetString());
        Assert.Equal("Guitar experimentation", created.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("notes").ValueKind);
        Assert.False(created.GetProperty("archived").GetBoolean());
        Assert.True(created.GetProperty("current").GetBoolean());
        Assert.Equal(1, created.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:05:00Z", created.GetProperty("createdAt").GetString());

        // The inputs are copied; the name and notes are not.
        Assert.Equal(
            "[Verse]\nRun|punk, fast|active",
            TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || styles || '|' || visibility FROM versions WHERE number = '1.1';"));

        // The source is unchanged, revision and updated time included.
        Assert.Equal(sourceBefore, SourceRow(factory.DataPath, "1"));

        // The Song's current Version moved, its updated time with it; its revision did not.
        var after = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal("1.1", after.GetProperty("currentVersion").GetProperty("number").GetString());
        Assert.Equal(2, after.GetProperty("versionCount").GetInt32());
        Assert.Equal(1, after.GetProperty("revision").GetInt32());
        Assert.Equal("2026-10-01T09:05:00Z", after.GetProperty("updatedAt").GetString());

        // The list is flat, in tree order, and marks the current one.
        Assert.Equal(["1 First", "1.1 Guitar experimentation current"], await TreeAsync(client, "n8-1"));

        // A blank name is stored as none; the next option from 1 is now 2 (1.2 is the child).
        using var second = await CreateAsync(client, songId, $$"""{"sourceVersionId":"{{one}}","number":"2","name":"   "}""");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await SetupApi.JsonAsync(second)).GetProperty("name").ValueKind);
        Assert.Equal(["1 First", "1.1 Guitar experimentation", "2 current"], await TreeAsync(client, songId));
    }

    [Fact]
    public async Task ANumberThatIsNotAnOptionIsRefusedWithTheOptionsAndNothingIsStored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Choices");
        var songId = song.GetProperty("id").GetString()!;
        var one = song.GetProperty("currentVersion").GetProperty("id").GetString()!;

        foreach (var number in new[] { "5", "1.2", "1.1.1", "01", "v2", "not a number" })
        {
            using var response = await CreateAsync(client, songId, $$"""{"sourceVersionId":"{{one}}","number":"{{number}}"}""");
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, VersionsEndpoints.NotOfferedCode);
            Assert.Equal(["2 sibling proposed", "1.1 child"], Options(problem));
        }

        // The source's own number is used, but was never an option from it.
        using (var own = await CreateAsync(client, songId, $$"""{"sourceVersionId":"{{one}}","number":"1"}"""))
        {
            await SetupApi.ProblemAsync(own, HttpStatusCode.UnprocessableEntity, VersionsEndpoints.NotOfferedCode);
        }

        Assert.Equal(["1 current"], await TreeAsync(client, songId));
        Assert.Equal(["1"], TestDatabase.Rows(factory.DataPath, "SELECT number FROM used_version_numbers;"));
    }

    [Fact]
    public async Task ANumberTakenMeanwhileIsRefusedWith409AndFreshOptionsAndNothingIsStored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Race");
        var songId = song.GetProperty("id").GetString()!;
        var one = song.GetProperty("currentVersion").GetProperty("id").GetString()!;

        // The options were 2 and 1.1; another client takes 2 before this one sends it.
        SongApi.AddVersionDirectly(factory.DataPath, 1, "2");

        using (var response = await CreateAsync(client, songId, $$"""{"sourceVersionId":"{{one}}","number":"2","name":"Mine"}"""))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, VersionsEndpoints.TakenCode);
            Assert.Equal(["1.1 child proposed", "3 sibling"], Options(problem));
        }

        Assert.Equal(["1 current", "2"], await TreeAsync(client, songId));
        Assert.Empty(TestDatabase.Rows(factory.DataPath, "SELECT id FROM versions WHERE name = 'Mine';"));

        // A child taken meanwhile is refused the same way.
        SongApi.AddVersionDirectly(factory.DataPath, 1, "1.1");
        using (var child = await CreateAsync(client, songId, $$"""{"sourceVersionId":"{{one}}","number":"1.1"}"""))
        {
            var problem = await SetupApi.ProblemAsync(child, HttpStatusCode.Conflict, VersionsEndpoints.TakenCode);
            Assert.Equal(["1.2 child proposed", "3 sibling"], Options(problem));
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT v.number FROM songs s JOIN versions v ON v.id = s.current_version_id;"));
    }

    [Fact]
    public async Task AMissingOrWrongFieldIsRefusedAndNothingIsStored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Fields");
        var songId = song.GetProperty("id").GetString()!;
        var one = song.GetProperty("currentVersion").GetProperty("id").GetString()!;
        var other = await SongApi.CreateAsync(client, "Another Song");
        var othersOne = other.GetProperty("currentVersion").GetProperty("id").GetString()!;

        async Task<List<string>> RefusedFieldsAsync(string json)
        {
            using var response = await CreateAsync(client, songId, json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            return [.. problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name).Order(StringComparer.Ordinal)];
        }

        Assert.Equal(["number", "sourceVersionId"], await RefusedFieldsAsync("{}"));
        Assert.Equal(["sourceVersionId"], await RefusedFieldsAsync("""{"sourceVersionId":"n8-1","number":"2"}"""));
        Assert.Equal(["sourceVersionId"], await RefusedFieldsAsync("""{"sourceVersionId":"n8-2-v1","number":"2"}"""));
        Assert.Equal(["sourceVersionId"], await RefusedFieldsAsync("""{"sourceVersionId":"n8-1-v9","number":"2"}"""));
        Assert.Equal(["sourceVersionId"], await RefusedFieldsAsync($$"""{"sourceVersionId":"{{othersOne}}","number":"2"}"""));
        Assert.Equal(["sourceVersionId"], await RefusedFieldsAsync($$"""{"sourceVersionId":"{{Guid.CreateVersion7()}}","number":"2"}"""));
        Assert.Equal(["name"], await RefusedFieldsAsync($$"""{"sourceVersionId":"{{one}}","number":"2","name":"{{new string('a', VersionRules.NameMaximumLength + 1)}}"}"""));
        Assert.Equal(["name"], await RefusedFieldsAsync($$"""{"sourceVersionId":"{{one}}","number":"2","name":"two\nlines"}"""));

        // Complement: a name of exactly the limit is taken.
        using (var limit = await CreateAsync(client, songId, $$"""{"sourceVersionId":"{{one}}","number":"2","name":"{{new string('a', VersionRules.NameMaximumLength)}}"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, limit.StatusCode);
        }

        Assert.Equal(["1|1", "1|2", "2|1"], TestDatabase.Rows(factory.DataPath, "SELECT s.shortcode_number || '|' || v.number FROM versions v JOIN songs s ON s.id = v.song_id ORDER BY 1;"));

        using var unknownSong = await CreateAsync(client, Guid.CreateVersion7().ToString(), $$"""{"sourceVersionId":"{{one}}","number":"1.1"}""");
        await SetupApi.ProblemAsync(unknownSong, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task CreatingFromAFrozenVersionCanCarryNewLyricsAndStylesAndTheSourceStaysAsItWas()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Carried");
        var songId = song.GetProperty("id").GetString()!;
        TestDatabase.Execute(
            factory.DataPath,
            "UPDATE versions SET lyrics = 'Frozen words', styles = 'frozen style', name = 'Frozen', notes = 'Kept', updated_utc = updated_utc WHERE number = '1';");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1");
        var sourceBefore = SourceRow(factory.DataPath, "1");

        using var response = await CreateAsync(client, songId, """{"sourceVersionId":"n8-1-v1","number":"2","lyrics":"[Verse]\r\nCarried words","styles":"carried style"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await SetupApi.JsonAsync(response);
        Assert.True(created.GetProperty("current").GetBoolean());
        Assert.False(created.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(
            "[Verse]\nCarried words|carried style",
            TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || styles FROM versions WHERE number = '2';"));
        Assert.Equal(sourceBefore, SourceRow(factory.DataPath, "1"));

        // Only one of them: the other is copied. An empty string clears it; null copies it.
        using (var lyricsOnly = await CreateAsync(client, songId, """{"sourceVersionId":"n8-1-v1","number":"1.1","lyrics":"","styles":null}"""))
        {
            Assert.Equal(HttpStatusCode.Created, lyricsOnly.StatusCode);
        }

        Assert.Equal(
            "|frozen style",
            TestDatabase.Scalar(factory.DataPath, "SELECT lyrics || '|' || styles FROM versions WHERE number = '1.1';"));

        // The new Version is an ordinary, editable one.
        var detail = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v2", UriKind.Relative)));
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/versions/n8-1-v2", UriKind.Relative))
        {
            Content = new StringContent("""{"lyrics":"Edited"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(detail.GetProperty("revision").GetInt32()));
        using var edit = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
    }

    [Fact]
    public async Task CarriedLyricsOrStylesThatAreNotValidAreRefusedAndNothingIsStored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Carried refusals");
        var songId = song.GetProperty("id").GetString()!;

        async Task<List<string>> RefusedFieldsAsync(string extra)
        {
            using var response = await CreateAsync(client, songId, $$"""{"sourceVersionId":"n8-1-v1","number":"2",{{extra}}}""");
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            return [.. problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name).Order(StringComparer.Ordinal)];
        }

        Assert.Equal(["lyrics"], await RefusedFieldsAsync($$"""
            "lyrics":"{{new string('a', VersionRules.LyricsMaximumLength + 1)}}"
            """));
        Assert.Equal(["styles"], await RefusedFieldsAsync($$"""
            "styles":"{{new string('a', VersionRules.StylesMaximumLength + 1)}}"
            """));
        Assert.Equal(["lyrics", "styles"], await RefusedFieldsAsync("\"lyrics\":5,\"styles\":[\"x\"]"));
        Assert.Equal(["lyrics"], await RefusedFieldsAsync("\"lyrics\":\"a\\u0000b\""));
        Assert.Equal(["styles"], await RefusedFieldsAsync("\"styles\":\"\\ud83c\""));

        Assert.Equal(["1"], TestDatabase.Rows(factory.DataPath, "SELECT number FROM versions;"));

        // Complement: exactly at the limits is taken.
        using var limit = await CreateAsync(client, songId, $$"""{"sourceVersionId":"n8-1-v1","number":"2","lyrics":"{{new string('a', VersionRules.LyricsMaximumLength)}}","styles":"{{new string('b', VersionRules.StylesMaximumLength)}}"}""");
        Assert.Equal(HttpStatusCode.Created, limit.StatusCode);
    }

    [Fact]
    public async Task AnyVersionArchivedOrNotCanBeMadeCurrentWithoutARevision()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Switching");
        var one = song.GetProperty("currentVersion").GetProperty("id").GetString()!;
        var archived = SongApi.AddVersionDirectly(factory.DataPath, 1, "2", VersionRecord.Archived);
        clock.Advance(TimeSpan.FromHours(1));

        using (var response = await SetCurrentAsync(client, "n8-1", $$"""{"versionId":"{{archived}}"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await SetupApi.JsonAsync(response);
            Assert.Equal("2", body.GetProperty("currentVersion").GetProperty("number").GetString());
            Assert.Equal(1, body.GetProperty("revision").GetInt32());
            Assert.Equal("2026-10-01T10:00:00Z", body.GetProperty("updatedAt").GetString());
        }

        Assert.Equal(["1", "2 archived current"], await TreeAsync(client, "n8-1"));

        // By the Song's ID too, and back in one request.
        using (var back = await SetCurrentAsync(client, song.GetProperty("id").GetString()!, $$"""{"versionId":"{{one}}"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        }

        Assert.Equal(["1 current", "2 archived"], await TreeAsync(client, "n8-1"));

        // Archiving or choosing leaves the archived Version as it was.
        Assert.Equal("archived|1", TestDatabase.Scalar(factory.DataPath, "SELECT visibility || '|' || revision FROM versions WHERE number = '2';"));
    }

    [Fact]
    public async Task MakingCurrentAVersionThatIsNotTheSongsIsRefused()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Mine");
        var other = await SongApi.CreateAsync(client, "Theirs");
        var othersOne = other.GetProperty("currentVersion").GetProperty("id").GetString()!;

        foreach (var json in new[] { "{}", """{"versionId":"n8-2-v1"}""", $$"""{"versionId":"{{othersOne}}"}""", $$"""{"versionId":"{{Guid.CreateVersion7()}}"}""" })
        {
            using var response = await SetCurrentAsync(client, "n8-1", json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("versionId", out _));
        }

        using (var unknown = await SetCurrentAsync(client, "n8-99", $$"""{"versionId":"{{othersOne}}"}"""))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using var list = await client.GetAsync(Versions("n8-99"));
        await SetupApi.ProblemAsync(list, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        Assert.Equal(["1 current"], await TreeAsync(client, "n8-1"));
    }

    [Fact]
    public async Task CreatingAndChoosingNeedVersionsWriteAndListingNeedsCatalogRead()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(setUp, "Scoped");
        var songId = song.GetProperty("id").GetString()!;
        using var client = factory.CreateClient();

        var cases = new (HttpMethod Method, Uri Uri, string Scope)[]
        {
            (HttpMethod.Post, Versions(songId), CredentialScopes.VersionsWrite),
            (HttpMethod.Put, CurrentVersion("n8-1"), CredentialScopes.VersionsWrite),
            (HttpMethod.Get, Versions("n8-1"), CredentialScopes.CatalogRead),
        };
        foreach (var (method, uri, scope) in cases)
        {
            // With the scope, the request reaches the endpoint (the empty body is refused as invalid).
            var allowed = await CredentialApi.CreateTokenAsync(factory, scope);
            using (var response = await CredentialApi.SendAsync(client, method, uri, allowed))
            {
                Assert.True(
                    response.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity,
                    $"{method} {uri}: {response.StatusCode}");
            }

            var everythingElse = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(other => other != scope)]);
            using (var refused = await CredentialApi.SendAsync(client, method, uri, everythingElse))
            {
                var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
                Assert.Equal(scope, problem.GetProperty("requiredScope").GetString());
            }
        }

        // A token with versions.write creates a Version from a body of its own.
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.VersionsWrite);
        using var request = new HttpRequestMessage(HttpMethod.Post, Versions(songId))
        {
            Content = new StringContent(
                $$"""{"sourceVersionId":"{{song.GetProperty("currentVersion").GetProperty("id").GetString()}}","number":"2"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", writer);
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private static Uri Versions(string songReference) => new($"/api/v1/songs/{songReference}/versions", UriKind.Relative);

    private static Uri CurrentVersion(string songReference) => new($"/api/v1/songs/{songReference}/current-version", UriKind.Relative);

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string songId, string json) =>
        SongApi.SendJsonAsync(client, HttpMethod.Post, Versions(songId), json);

    private static Task<HttpResponseMessage> SetCurrentAsync(HttpClient client, string songReference, string json) =>
        SongApi.SendJsonAsync(client, HttpMethod.Put, CurrentVersion(songReference), json);

    /// <summary>A Version's stored columns, as one line, to compare before and after.</summary>
    private static string SourceRow(string dataPath, string number) =>
        TestDatabase.Scalar(dataPath, $"SELECT lyrics || '|' || styles || '|' || name || '|' || notes || '|' || visibility || '|' || updated_utc || '|' || revision FROM versions WHERE number = '{number}';");

    /// <summary>The Song's Versions, each as <c>number[ name][ archived][ current]</c>, asserting 200.</summary>
    private static async Task<List<string>> TreeAsync(HttpClient client, string songReference)
    {
        using var response = await client.GetAsync(Versions(songReference));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var body = await SetupApi.JsonAsync(response);

        return [.. body.GetProperty("items").EnumerateArray().Select(static version => string.Join(' ', new[]
        {
            version.GetProperty("number").GetString(),
            version.GetProperty("name").GetString(),
            version.GetProperty("archived").GetBoolean() ? "archived" : null,
            version.GetProperty("current").GetBoolean() ? "current" : null,
        }.OfType<string>()))];
    }

    /// <summary>A refusal's options, each as <c>number kind[ proposed]</c>.</summary>
    private static List<string> Options(JsonElement problem) =>
        [.. problem.GetProperty("options").EnumerateArray().Select(static option =>
            $"{option.GetProperty("number").GetString()} {option.GetProperty("kind").GetString()}{(option.GetProperty("proposed").GetBoolean() ? " proposed" : string.Empty)}")];
}
