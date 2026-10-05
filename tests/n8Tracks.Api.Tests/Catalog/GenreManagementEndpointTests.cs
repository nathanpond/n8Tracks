using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// Managing the Genre list (Settings → Genres): <c>PATCH /api/v1/genres/{id}</c> (rename),
/// <c>POST /api/v1/genres/{id}/merge</c>, and <c>DELETE /api/v1/genres/{id}</c>, each session-only
/// under the Genre's own revision. A merge or delete changes each affected Song's Genres and moves
/// that Song's last-updated time and revision on; a rename moves no Song.
/// </summary>
public sealed class GenreManagementEndpointTests
{
    private static readonly Uri Genres = new("/api/v1/genres", UriKind.Relative);

    [Fact]
    public async Task RenamingAGenreChangesItEverywhereAndMovesNoSong()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var genre = await CreateGenreAsync(client, "Indy Rock");
        var song = await SongApi.CreateAsync(client, "Pack");
        var assigned = await AssignAsync(client, song, genre);
        var listed = await GenreAsync(client, genre);
        Assert.Equal(1, listed.GetProperty("revision").GetInt32());
        clock.Advance(TimeSpan.FromMinutes(5));

        using var renamed = await SendAsync(client, HttpMethod.Patch, GenreUri(genre), 1, """{"name":"  Indie   Rock "}""");

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("\"2\"", renamed.Headers.ETag?.ToString());
        Assert.Contains("no-store", renamed.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var answer = await SetupApi.JsonAsync(renamed);
        Assert.Equal("Indie Rock", answer.GetProperty("name").GetString());
        Assert.Equal(1, answer.GetProperty("songCount").GetInt32());
        Assert.Equal(2, answer.GetProperty("revision").GetInt32());

        // The Song shows the new name, and its revision and updated time are where they were.
        var read = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song.GetProperty("id").GetString()!)));
        Assert.Equal(["Indie Rock"], Names(read));
        Assert.Equal(assigned.GetProperty("revision").GetInt32(), read.GetProperty("revision").GetInt32());
        Assert.Equal(assigned.GetProperty("updatedAt").GetString(), read.GetProperty("updatedAt").GetString());
        Assert.Equal("Indie Rock|INDIE ROCK|2", TestDatabase.Scalar(factory.DataPath, "SELECT name, name_key, revision FROM genres;"));

        // Its own name in another letter case is a rename; the same name again is no change.
        using (var cased = await SendAsync(client, HttpMethod.Patch, GenreUri(genre), 2, """{"name":"indie rock"}"""))
        {
            Assert.Equal(3, (await SetupApi.JsonAsync(cased)).GetProperty("revision").GetInt32());
        }

        using (var same = await SendAsync(client, HttpMethod.Patch, GenreUri(genre), 3, """{"name":"indie rock"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal(3, (await SetupApi.JsonAsync(same)).GetProperty("revision").GetInt32());
        }
    }

    [Fact]
    public async Task RenamingToAnotherGenresNameIs409WithItsIdForTheMergeOffer()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var proper = await CreateGenreAsync(client, "Indie Rock");
        var dashed = await CreateGenreAsync(client, "indie-rock");
        await AssignAsync(client, await SongApi.CreateAsync(client, "Song"), proper);

        using var response = await SendAsync(client, HttpMethod.Patch, GenreUri(dashed), 1, """{"name":"INDIE ROCK"}""");

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "genre_name_taken");
        Assert.Equal(proper, problem.GetProperty("genreId").GetString());
        Assert.Equal("Indie Rock", problem.GetProperty("genre").GetProperty("name").GetString());
        Assert.Equal(1, problem.GetProperty("genre").GetProperty("songCount").GetInt32());
        Assert.Equal("Indie Rock,indie-rock", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name) FROM (SELECT name FROM genres ORDER BY name_key);"));
    }

    [Fact]
    public async Task ARenameIsRefusedWhenStaleMissingWrongOrWithoutARevision()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var genre = await CreateGenreAsync(client, "Folk");

        using (var stale = await SendAsync(client, HttpMethod.Patch, GenreUri(genre), 4, """{"name":"Folk Rock"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal("Folk", problem.GetProperty("current").GetProperty("name").GetString());
            Assert.Equal(1, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var missing = await SendAsync(client, HttpMethod.Patch, GenreUri(Guid.CreateVersion7().ToString()), 1, """{"name":"Folk Rock"}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }

        foreach (var body in new[] { "{}", """{"name":"  "}""", """{"name":42}""", $$"""{"name":"{{new string('a', 51)}}"}""" })
        {
            using var wrong = await SendAsync(client, HttpMethod.Patch, GenreUri(genre), 1, body);
            var problem = await SetupApi.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _), body);
        }

        using (var unrevisioned = await SendAsync(client, HttpMethod.Patch, GenreUri(genre), revision: null, """{"name":"Folk Rock"}"""))
        {
            await SetupApi.ProblemAsync(unrevisioned, (HttpStatusCode)428, "revision_required");
        }

        Assert.Equal("Folk|1", TestDatabase.Scalar(factory.DataPath, "SELECT name, revision FROM genres;"));
    }

    [Fact]
    public async Task MergingMovesSongsToTheTargetOnceAndRemovesTheMergedGenres()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var target = await CreateGenreAsync(client, "Indie Rock");
        var dashed = await CreateGenreAsync(client, "indie-rock");
        var spaced = await CreateGenreAsync(client, "Indie  Rock!");
        var folk = await CreateGenreAsync(client, "Folk");
        var both = await AssignAsync(client, await SongApi.CreateAsync(client, "Both"), target, dashed);
        var onlyDashed = await AssignAsync(client, await SongApi.CreateAsync(client, "Only dashed"), dashed, folk);
        var onlySpaced = await AssignAsync(client, await SongApi.CreateAsync(client, "Only spaced"), spaced);
        var onlyTarget = await AssignAsync(client, await SongApi.CreateAsync(client, "Only target"), target);
        var untouched = await AssignAsync(client, await SongApi.CreateAsync(client, "Untouched"), folk);
        clock.Advance(TimeSpan.FromMinutes(10));

        using var merged = await SendAsync(client, HttpMethod.Post, MergeUri(target), 1, $$"""{"sourceIds":["{{dashed}}","{{spaced}}","{{dashed}}"]}""");

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal("\"2\"", merged.Headers.ETag?.ToString());
        var answer = await SetupApi.JsonAsync(merged);
        Assert.Equal(target, answer.GetProperty("id").GetString());
        Assert.Equal(4, answer.GetProperty("songCount").GetInt32());
        Assert.Equal(2, answer.GetProperty("revision").GetInt32());

        // The merged Genres are gone; a Song that had both has the target once.
        Assert.Equal("Folk,Indie Rock", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name) FROM (SELECT name FROM genres ORDER BY name_key);"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM song_genres WHERE song_id = '{both.GetProperty("id").GetString()!.ToUpperInvariant()}';"));

        // Each Song whose Genres changed moved on; the others did not.
        await AssertMovedAsync(client, both, ["Indie Rock"], "2026-10-01T09:10:00Z");
        await AssertMovedAsync(client, onlyDashed, ["Folk", "Indie Rock"], "2026-10-01T09:10:00Z");
        await AssertMovedAsync(client, onlySpaced, ["Indie Rock"], "2026-10-01T09:10:00Z");
        await AssertUnchangedAsync(client, onlyTarget, ["Indie Rock"]);
        await AssertUnchangedAsync(client, untouched, ["Folk"]);

        // A client holding a moved Song's old revision gets the ordinary conflict.
        using var stale = await SongApi.PatchAsync(client, both.GetProperty("id").GetString()!, SongApi.Quoted(both.GetProperty("revision").GetInt32()), """{"title":"Stale"}""");
        await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
    }

    [Fact]
    public async Task AMergeIntoItselfAMissingGenreOrWithOneBadSourceIs422AndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var target = await CreateGenreAsync(client, "Indie Rock");
        var source = await CreateGenreAsync(client, "indie-rock");
        var song = await AssignAsync(client, await SongApi.CreateAsync(client, "Song"), source);
        var missing = Guid.CreateVersion7().ToString();

        foreach (var (uri, body, field) in new[]
        {
            (MergeUri(target), $$"""{"sourceIds":["{{target}}"]}""", "sourceIds"),
            (MergeUri(target), $$"""{"sourceIds":["{{source}}","{{target}}"]}""", "sourceIds"),
            (MergeUri(target), $$"""{"sourceIds":["{{source}}","{{missing}}"]}""", "sourceIds"),
            (MergeUri(target), $$"""{"sourceIds":["{{source}}","not-an-id"]}""", "sourceIds"),
            (MergeUri(target), """{"sourceIds":[]}""", "sourceIds"),
            (MergeUri(target), """{}""", "sourceIds"),
            (MergeUri(target), """{"sourceIds":"x"}""", "sourceIds"),
            (MergeUri(missing), $$"""{"sourceIds":["{{source}}"]}""", "id"),
        })
        {
            using var response = await SendAsync(client, HttpMethod.Post, uri, 1, body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        using (var stale = await SendAsync(client, HttpMethod.Post, MergeUri(target), 2, $$"""{"sourceIds":["{{source}}"]}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(target, problem.GetProperty("current").GetProperty("id").GetString());
        }

        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM genres;"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT revision FROM genres WHERE name = 'Indie Rock';"));
        await AssertUnchangedAsync(client, song, ["indie-rock"]);
    }

    [Fact]
    public async Task DeletingAGenreInUseRemovesItFromItsSongsWhenAsked()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var doomed = await CreateGenreAsync(client, "Doomed");
        var folk = await CreateGenreAsync(client, "Folk");
        var first = await AssignAsync(client, await SongApi.CreateAsync(client, "First"), doomed, folk);
        var second = await AssignAsync(client, await SongApi.CreateAsync(client, "Second"), doomed);
        var untouched = await AssignAsync(client, await SongApi.CreateAsync(client, "Untouched"), folk);
        clock.Advance(TimeSpan.FromMinutes(10));

        // Without a choice it is refused with the count.
        using (var refused = await SendAsync(client, HttpMethod.Delete, GenreUri(doomed), 1, json: null))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "genre_in_use");
            Assert.Equal(2, problem.GetProperty("songCount").GetInt32());
        }

        using (var falseIsNoChoice = await SendAsync(client, HttpMethod.Delete, new Uri($"{GenreUri(doomed)}?removeFromSongs=false", UriKind.Relative), 1, json: null))
        {
            await SetupApi.ProblemAsync(falseIsNoChoice, HttpStatusCode.Conflict, "genre_in_use");
        }

        using var deleted = await SendAsync(client, HttpMethod.Delete, new Uri($"{GenreUri(doomed)}?removeFromSongs=true", UriKind.Relative), 1, json: null);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("Folk", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name) FROM genres;"));
        await AssertMovedAsync(client, first, ["Folk"], "2026-10-01T09:10:00Z");
        await AssertMovedAsync(client, second, [], "2026-10-01T09:10:00Z");
        await AssertUnchangedAsync(client, untouched, ["Folk"]);
    }

    [Fact]
    public async Task DeletingAGenreInUseCanReassignItsSongsToAnotherGenre()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var doomed = await CreateGenreAsync(client, "Doomed");
        var folk = await CreateGenreAsync(client, "Folk");
        var rock = await CreateGenreAsync(client, "Rock");
        var both = await AssignAsync(client, await SongApi.CreateAsync(client, "Both"), doomed, folk);
        var only = await AssignAsync(client, await SongApi.CreateAsync(client, "Only"), doomed, rock);
        var untouched = await AssignAsync(client, await SongApi.CreateAsync(client, "Untouched"), folk);
        clock.Advance(TimeSpan.FromMinutes(10));

        using var deleted = await SendAsync(client, HttpMethod.Delete, new Uri($"{GenreUri(doomed)}?reassignTo={folk}", UriKind.Relative), 1, json: null);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertMovedAsync(client, both, ["Folk"], "2026-10-01T09:10:00Z");
        await AssertMovedAsync(client, only, ["Folk", "Rock"], "2026-10-01T09:10:00Z");
        await AssertUnchangedAsync(client, untouched, ["Folk"]);
        var list = await SetupApi.JsonAsync(await client.GetAsync(Genres));
        Assert.Equal(
            ["Folk 3", "Rock 1"],
            list.GetProperty("items").EnumerateArray().Select(static genre => $"{genre.GetProperty("name").GetString()} {genre.GetProperty("songCount").GetInt32()}"));
    }

    [Fact]
    public async Task AnUnusedGenreIsDeletedDirectlyAndTheWrongChoicesAre422()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var unused = await CreateGenreAsync(client, "Unused");
        var used = await CreateGenreAsync(client, "Used");
        var other = await CreateGenreAsync(client, "Other");
        await AssignAsync(client, await SongApi.CreateAsync(client, "Song"), used);
        var missing = Guid.CreateVersion7().ToString();

        foreach (var (genre, query, field) in new[]
        {
            (unused, "removeFromSongs=true", "removeFromSongs"),
            (unused, $"reassignTo={other}", "reassignTo"),
            (used, $"reassignTo={other}&removeFromSongs=true", "reassignTo"),
            (used, $"reassignTo={used}", "reassignTo"),
            (used, $"reassignTo={missing}", "reassignTo"),
            (used, "reassignTo=folk", "reassignTo"),
            (used, "removeFromSongs=yes", "removeFromSongs"),
        })
        {
            using var response = await SendAsync(client, HttpMethod.Delete, new Uri($"{GenreUri(genre)}?{query}", UriKind.Relative), 1, json: null);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), query);
        }

        using (var stale = await SendAsync(client, HttpMethod.Delete, GenreUri(unused), 2, json: null))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        Assert.Equal("3", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM genres;"));

        using (var deleted = await SendAsync(client, HttpMethod.Delete, GenreUri(unused), 1, json: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var again = await SendAsync(client, HttpMethod.Delete, GenreUri(unused), 1, json: null))
        {
            await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal("Other,Used", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name) FROM (SELECT name FROM genres ORDER BY name_key);"));
    }

    [Fact]
    public async Task ManagingGenresNeedsASignedInSession()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var genre = await CreateGenreAsync(client, "Folk");
        var other = await CreateGenreAsync(client, "Rock");

        // A token holding every scope is refused each one, and nothing changes.
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        foreach (var (method, uri, json) in new (HttpMethod, Uri, string)[]
        {
            (HttpMethod.Patch, GenreUri(genre), """{"name":"Renamed"}"""),
            (HttpMethod.Post, MergeUri(genre), $$"""{"sourceIds":["{{other}}"]}"""),
            (HttpMethod.Delete, GenreUri(genre), "{}"),
        })
        {
            using var request = new HttpRequestMessage(method, uri) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", "\"1\""));
            using var response = await client.SendAsync(request);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, "session_required");
        }

        // Signed out, nothing is served.
        using var signedOut = factory.CreateClient();
        using (var request = new HttpRequestMessage(HttpMethod.Delete, GenreUri(genre)))
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", "\"1\""));
            using var response = await signedOut.SendAsync(request);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        Assert.Equal("Folk|1,Rock|1", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name || '|' || revision) FROM (SELECT name, revision FROM genres ORDER BY name_key);"));
    }

    private static Uri GenreUri(string id) => new($"/api/v1/genres/{id}", UriKind.Relative);

    private static Uri MergeUri(string id) => new($"/api/v1/genres/{id}/merge", UriKind.Relative);

    /// <summary>Sends a session request with the anti-forgery header and, when given, the revision in If-Match.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, int? revision, string? json)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (revision is { } value)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
        }

        return await client.SendAsync(request);
    }

    /// <summary>Creates a Genre and returns its ID.</summary>
    private static async Task<string> CreateGenreAsync(HttpClient client, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Genres) { Content = JsonContent.Create(new { name }) };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    /// <summary>The Genre with <paramref name="id"/> as the list shows it.</summary>
    private static async Task<JsonElement> GenreAsync(HttpClient client, string id) =>
        (await SetupApi.JsonAsync(await client.GetAsync(Genres))).GetProperty("items").EnumerateArray()
            .Single(genre => genre.GetProperty("id").GetString() == id);

    /// <summary>Gives a Song exactly <paramref name="genres"/> and returns it as it is then.</summary>
    private static async Task<JsonElement> AssignAsync(HttpClient client, JsonElement song, params string[] genres)
    {
        var id = song.GetProperty("id").GetString()!;
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(id)))).GetProperty("revision").GetInt32();
        return await SongApi.EditAsync(client, id, revision, JsonSerializer.Serialize(new { genreIds = genres }));
    }

    /// <summary>The Song now has exactly <paramref name="names"/>, its revision is one more than <paramref name="before"/>'s, and it was updated at <paramref name="updatedAt"/>.</summary>
    private static async Task AssertMovedAsync(HttpClient client, JsonElement before, string[] names, string updatedAt)
    {
        var now = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(before.GetProperty("id").GetString()!)));
        Assert.Equal(names, Names(now));
        Assert.Equal(before.GetProperty("revision").GetInt32() + 1, now.GetProperty("revision").GetInt32());
        Assert.Equal(updatedAt, now.GetProperty("updatedAt").GetString());
    }

    /// <summary>The Song has exactly <paramref name="names"/> and the revision and updated time it had.</summary>
    private static async Task AssertUnchangedAsync(HttpClient client, JsonElement before, string[] names)
    {
        var now = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(before.GetProperty("id").GetString()!)));
        Assert.Equal(names, Names(now));
        Assert.Equal(before.GetProperty("revision").GetInt32(), now.GetProperty("revision").GetInt32());
        Assert.Equal(before.GetProperty("updatedAt").GetString(), now.GetProperty("updatedAt").GetString());
    }

    private static List<string> Names(JsonElement song) =>
        [.. song.GetProperty("genres").EnumerateArray().Select(static genre => genre.GetProperty("name").GetString()!)];
}
