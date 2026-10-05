using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// Managing the Tag list (Settings → Tags): <c>PATCH /api/v1/tags/{id}</c> (rename and/or
/// recolour), <c>POST /api/v1/tags/{id}/merge</c>, and <c>DELETE /api/v1/tags/{id}</c>, each
/// session-only under the Tag's own revision. A merge or delete changes each affected Song's Tags
/// and moves that Song's last-updated time and revision on; a rename or recolour moves no Song. The
/// Tag merged into keeps its own colour, and a Tag in use is only ever removed from its Songs.
/// </summary>
public sealed class TagManagementEndpointTests
{
    private static readonly Uri Tags = new("/api/v1/tags", UriKind.Relative);

    [Fact]
    public async Task RenamingOrRecolouringATagChangesItEverywhereAndMovesNoSong()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var tag = await CreateTagAsync(client, "Sumer");
        var song = await SongApi.CreateAsync(client, "Pack");
        var assigned = await AssignAsync(client, song, tag);
        Assert.Equal(1, (await TagAsync(client, tag)).GetProperty("revision").GetInt32());
        clock.Advance(TimeSpan.FromMinutes(5));

        using var renamed = await SendAsync(client, HttpMethod.Patch, TagUri(tag), 1, """{"name":"  summer   time "}""");

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("\"2\"", renamed.Headers.ETag?.ToString());
        Assert.Contains("no-store", renamed.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var answer = await SetupApi.JsonAsync(renamed);
        Assert.Equal("summer time", answer.GetProperty("name").GetString());
        Assert.Equal("gray", answer.GetProperty("colour").GetString());
        Assert.Equal(1, answer.GetProperty("songCount").GetInt32());
        Assert.Equal(2, answer.GetProperty("revision").GetInt32());

        using (var recoloured = await SendAsync(client, HttpMethod.Patch, TagUri(tag), 2, """{"colour":"orange"}"""))
        {
            var body = await SetupApi.JsonAsync(recoloured);
            Assert.Equal("summer time orange 3", $"{body.GetProperty("name").GetString()} {body.GetProperty("colour").GetString()} {body.GetProperty("revision").GetInt32()}");
        }

        using (var both = await SendAsync(client, HttpMethod.Patch, TagUri(tag), 3, """{"name":"summer","colour":"teal"}"""))
        {
            var body = await SetupApi.JsonAsync(both);
            Assert.Equal("summer teal 4", $"{body.GetProperty("name").GetString()} {body.GetProperty("colour").GetString()} {body.GetProperty("revision").GetInt32()}");
        }

        // The Song shows the new name and colour, and its revision and updated time are where they were.
        var read = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song.GetProperty("id").GetString()!)));
        Assert.Equal(["summer teal"], Labels(read));
        Assert.Equal(assigned.GetProperty("revision").GetInt32(), read.GetProperty("revision").GetInt32());
        Assert.Equal(assigned.GetProperty("updatedAt").GetString(), read.GetProperty("updatedAt").GetString());
        Assert.Equal("summer|SUMMER|teal|4", TestDatabase.Scalar(factory.DataPath, "SELECT name, name_key, colour, revision FROM tags;"));

        // Its own name in another letter case is a rename; the same name and colour again is no change.
        using (var cased = await SendAsync(client, HttpMethod.Patch, TagUri(tag), 4, """{"name":"Summer"}"""))
        {
            Assert.Equal(5, (await SetupApi.JsonAsync(cased)).GetProperty("revision").GetInt32());
        }

        using (var same = await SendAsync(client, HttpMethod.Patch, TagUri(tag), 5, """{"name":"Summer","colour":"teal"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal(5, (await SetupApi.JsonAsync(same)).GetProperty("revision").GetInt32());
        }
    }

    [Fact]
    public async Task RenamingToAnotherTagsNameInAnotherCaseIs409WithItsIdForTheMergeOffer()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var running = await CreateTagAsync(client, "running");
        var typo = await CreateTagAsync(client, "runing");
        await AssignAsync(client, await SongApi.CreateAsync(client, "Song"), running);

        using var response = await SendAsync(client, HttpMethod.Patch, TagUri(typo), 1, """{"name":"RUNNING","colour":"red"}""");

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "tag_name_taken");
        Assert.Equal(running, problem.GetProperty("tagId").GetString());
        Assert.Equal("running", problem.GetProperty("tag").GetProperty("name").GetString());
        Assert.Equal(1, problem.GetProperty("tag").GetProperty("songCount").GetInt32());

        // Nothing changed, the colour sent with the name included.
        Assert.Equal("runing|red|1,running|gray|1", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name || '|' || colour || '|' || revision) FROM (SELECT * FROM tags ORDER BY name_key);"));
    }

    [Fact]
    public async Task AnEditIsRefusedWhenStaleMissingWrongOrWithoutARevision()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var tag = await CreateTagAsync(client, "summer");

        using (var stale = await SendAsync(client, HttpMethod.Patch, TagUri(tag), 4, """{"colour":"red"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal("summer", problem.GetProperty("current").GetProperty("name").GetString());
            Assert.Equal("gray", problem.GetProperty("current").GetProperty("colour").GetString());
            Assert.Equal(1, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var missing = await SendAsync(client, HttpMethod.Patch, TagUri(Guid.CreateVersion7().ToString()), 1, """{"colour":"red"}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }

        foreach (var (body, field) in new[]
        {
            ("{}", "name"),
            ("""{"name":null,"colour":null}""", "name"),
            ("""{"name":"  "}""", "name"),
            ("""{"name":42}""", "name"),
            ($$"""{"name":"{{new string('a', 51)}}"}""", "name"),
            ("""{"colour":"purple"}""", "colour"),
            ("""{"colour":"Red"}""", "colour"),
            ("""{"colour":3}""", "colour"),
            ("""{"name":"fine","colour":"#ff0000"}""", "colour"),
        })
        {
            using var wrong = await SendAsync(client, HttpMethod.Patch, TagUri(tag), 1, body);
            var problem = await SetupApi.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        using (var unrevisioned = await SendAsync(client, HttpMethod.Patch, TagUri(tag), revision: null, """{"colour":"red"}"""))
        {
            await SetupApi.ProblemAsync(unrevisioned, (HttpStatusCode)428, "revision_required");
        }

        Assert.Equal("summer|gray|1", TestDatabase.Scalar(factory.DataPath, "SELECT name, colour, revision FROM tags;"));
    }

    [Fact]
    public async Task MergingMovesSongsToTheTargetOnceAndTheTargetKeepsItsColour()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var target = await CreateTagAsync(client, "running");
        var typo = await CreateTagAsync(client, "runing");
        var spaced = await CreateTagAsync(client, "run ning");
        var night = await CreateTagAsync(client, "night");
        Assert.Equal("gray", (await TagAsync(client, target)).GetProperty("colour").GetString());
        var both = await AssignAsync(client, await SongApi.CreateAsync(client, "Both"), target, typo);
        var onlyTypo = await AssignAsync(client, await SongApi.CreateAsync(client, "Only typo"), typo, night);
        var onlySpaced = await AssignAsync(client, await SongApi.CreateAsync(client, "Only spaced"), spaced);
        var onlyTarget = await AssignAsync(client, await SongApi.CreateAsync(client, "Only target"), target);
        var untouched = await AssignAsync(client, await SongApi.CreateAsync(client, "Untouched"), night);
        clock.Advance(TimeSpan.FromMinutes(10));

        using var merged = await SendAsync(client, HttpMethod.Post, MergeUri(target), 1, $$"""{"sourceIds":["{{typo}}","{{spaced}}","{{typo}}"]}""");

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal("\"2\"", merged.Headers.ETag?.ToString());
        var answer = await SetupApi.JsonAsync(merged);
        Assert.Equal(target, answer.GetProperty("id").GetString());
        Assert.Equal("running", answer.GetProperty("name").GetString());
        Assert.Equal("gray", answer.GetProperty("colour").GetString());
        Assert.Equal(4, answer.GetProperty("songCount").GetInt32());
        Assert.Equal(2, answer.GetProperty("revision").GetInt32());

        // The merged Tags are gone; a Song that had both has the target once, in the target's colour.
        Assert.Equal("night|grape,running|gray", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name || '|' || colour) FROM (SELECT * FROM tags ORDER BY name_key);"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM song_tags WHERE song_id = '{both.GetProperty("id").GetString()!.ToUpperInvariant()}';"));

        // Each Song whose Tags changed moved on; the others did not.
        await AssertMovedAsync(client, both, ["running gray"], "2026-10-01T09:10:00Z");
        await AssertMovedAsync(client, onlyTypo, ["night grape", "running gray"], "2026-10-01T09:10:00Z");
        await AssertMovedAsync(client, onlySpaced, ["running gray"], "2026-10-01T09:10:00Z");
        await AssertUnchangedAsync(client, onlyTarget, ["running gray"]);
        await AssertUnchangedAsync(client, untouched, ["night grape"]);

        // A client holding a moved Song's old revision gets the ordinary conflict.
        using var stale = await SongApi.PatchAsync(client, both.GetProperty("id").GetString()!, SongApi.Quoted(both.GetProperty("revision").GetInt32()), """{"title":"Stale"}""");
        await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
    }

    [Fact]
    public async Task AMergeIntoItselfAMissingTagOrWithOneBadSourceIs422AndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var target = await CreateTagAsync(client, "running");
        var source = await CreateTagAsync(client, "runing");
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

        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM tags;"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT revision FROM tags WHERE name = 'running';"));
        await AssertUnchangedAsync(client, song, ["runing red"]);
    }

    [Fact]
    public async Task DeletingATagInUseRemovesItFromItsSongsAndChangesNothingElseAboutThem()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var doomed = await CreateTagAsync(client, "doomed");
        var night = await CreateTagAsync(client, "night");
        var genre = await CreateGenreAsync(client, "Folk");
        var first = await AssignAsync(client, await SongApi.CreateAsync(client, "First"), doomed, night);
        first = await SongApi.EditAsync(client, first.GetProperty("id").GetString()!, first.GetProperty("revision").GetInt32(), JsonSerializer.Serialize(new { genreIds = new[] { genre }, notes = "Keep these notes.", concept = "A concept" }));
        var second = await AssignAsync(client, await SongApi.CreateAsync(client, "Second"), doomed);
        var untouched = await AssignAsync(client, await SongApi.CreateAsync(client, "Untouched"), night);
        clock.Advance(TimeSpan.FromMinutes(10));

        // Without removeFromSongs=true it is refused with the count.
        using (var refused = await SendAsync(client, HttpMethod.Delete, TagUri(doomed), 1, json: null))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "tag_in_use");
            Assert.Equal(2, problem.GetProperty("songCount").GetInt32());
        }

        using (var falseIsNoChoice = await SendAsync(client, HttpMethod.Delete, new Uri($"{TagUri(doomed)}?removeFromSongs=false", UriKind.Relative), 1, json: null))
        {
            await SetupApi.ProblemAsync(falseIsNoChoice, HttpStatusCode.Conflict, "tag_in_use");
        }

        using var deleted = await SendAsync(client, HttpMethod.Delete, new Uri($"{TagUri(doomed)}?removeFromSongs=true", UriKind.Relative), 1, json: null);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("night", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name) FROM tags;"));
        await AssertMovedAsync(client, first, ["night red"], "2026-10-01T09:10:00Z");
        await AssertMovedAsync(client, second, [], "2026-10-01T09:10:00Z");
        await AssertUnchangedAsync(client, untouched, ["night red"]);

        // Apart from its Tags, revision, and last-updated time, the Song reads exactly as it did.
        var now = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(first.GetProperty("id").GetString()!)));
        Assert.Equal(WithoutTagsAndChange(first), WithoutTagsAndChange(now));
        Assert.Equal("Keep these notes.", now.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task AnUnusedTagIsDeletedDirectlyAndTheWrongChoicesAre422()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var unused = await CreateTagAsync(client, "unused");
        var used = await CreateTagAsync(client, "used");
        var other = await CreateTagAsync(client, "other");
        await AssignAsync(client, await SongApi.CreateAsync(client, "Song"), used);

        foreach (var (tag, query, field) in new[]
        {
            (unused, "removeFromSongs=true", "removeFromSongs"),
            (used, $"reassignTo={other}", "reassignTo"),
            (used, $"reassignTo={other}&removeFromSongs=true", "reassignTo"),
            (unused, $"reassignTo={other}", "reassignTo"),
            (used, "removeFromSongs=yes", "removeFromSongs"),
        })
        {
            using var response = await SendAsync(client, HttpMethod.Delete, new Uri($"{TagUri(tag)}?{query}", UriKind.Relative), 1, json: null);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), query);
        }

        using (var stale = await SendAsync(client, HttpMethod.Delete, TagUri(unused), 2, json: null))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        Assert.Equal("3", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM tags;"));

        using (var deleted = await SendAsync(client, HttpMethod.Delete, TagUri(unused), 1, json: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var again = await SendAsync(client, HttpMethod.Delete, TagUri(unused), 1, json: null))
        {
            await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal("other,used", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name) FROM (SELECT name FROM tags ORDER BY name_key);"));
    }

    [Fact]
    public async Task ManagingTagsNeedsASignedInSession()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var tag = await CreateTagAsync(client, "summer");
        var other = await CreateTagAsync(client, "night");

        // A token holding every scope is refused each one, and nothing changes.
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        foreach (var (method, uri, json) in new (HttpMethod, Uri, string)[]
        {
            (HttpMethod.Patch, TagUri(tag), """{"name":"Renamed","colour":"red"}"""),
            (HttpMethod.Post, MergeUri(tag), $$"""{"sourceIds":["{{other}}"]}"""),
            (HttpMethod.Delete, TagUri(tag), "{}"),
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
        using (var request = new HttpRequestMessage(HttpMethod.Delete, TagUri(tag)))
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", "\"1\""));
            using var response = await signedOut.SendAsync(request);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        Assert.Equal("night|red|1,summer|gray|1", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name || '|' || colour || '|' || revision) FROM (SELECT * FROM tags ORDER BY name_key);"));
    }

    private static Uri TagUri(string id) => new($"/api/v1/tags/{id}", UriKind.Relative);

    private static Uri MergeUri(string id) => new($"/api/v1/tags/{id}/merge", UriKind.Relative);

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

    /// <summary>Creates a Tag (in the next colour) and returns its ID.</summary>
    private static async Task<string> CreateTagAsync(HttpClient client, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Tags) { Content = JsonContent.Create(new { name }) };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    /// <summary>Creates a Genre and returns its ID.</summary>
    private static async Task<string> CreateGenreAsync(HttpClient client, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/genres", UriKind.Relative)) { Content = JsonContent.Create(new { name }) };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    /// <summary>The Tag with <paramref name="id"/> as the list shows it.</summary>
    private static async Task<JsonElement> TagAsync(HttpClient client, string id) =>
        (await SetupApi.JsonAsync(await client.GetAsync(Tags))).GetProperty("items").EnumerateArray()
            .Single(tag => tag.GetProperty("id").GetString() == id);

    /// <summary>Gives a Song exactly <paramref name="tags"/> and returns it as it is then.</summary>
    private static async Task<JsonElement> AssignAsync(HttpClient client, JsonElement song, params string[] tags)
    {
        var id = song.GetProperty("id").GetString()!;
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(id)))).GetProperty("revision").GetInt32();
        return await SongApi.EditAsync(client, id, revision, JsonSerializer.Serialize(new { tagIds = tags }));
    }

    /// <summary>The Song now has exactly <paramref name="labels"/>, its revision is one more than <paramref name="before"/>'s, and it was updated at <paramref name="updatedAt"/>.</summary>
    private static async Task AssertMovedAsync(HttpClient client, JsonElement before, string[] labels, string updatedAt)
    {
        var now = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(before.GetProperty("id").GetString()!)));
        Assert.Equal(labels, Labels(now));
        Assert.Equal(before.GetProperty("revision").GetInt32() + 1, now.GetProperty("revision").GetInt32());
        Assert.Equal(updatedAt, now.GetProperty("updatedAt").GetString());
    }

    /// <summary>The Song has exactly <paramref name="labels"/> and the revision and updated time it had.</summary>
    private static async Task AssertUnchangedAsync(HttpClient client, JsonElement before, string[] labels)
    {
        var now = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(before.GetProperty("id").GetString()!)));
        Assert.Equal(labels, Labels(now));
        Assert.Equal(before.GetProperty("revision").GetInt32(), now.GetProperty("revision").GetInt32());
        Assert.Equal(before.GetProperty("updatedAt").GetString(), now.GetProperty("updatedAt").GetString());
    }

    /// <summary>The Song's JSON without its Tags, revision, and last-updated time.</summary>
    private static string WithoutTagsAndChange(JsonElement song)
    {
        var node = JsonNode.Parse(song.GetRawText())!.AsObject();
        Assert.True(node.Remove("tags"));
        Assert.True(node.Remove("revision"));
        Assert.True(node.Remove("updatedAt"));
        return node.ToJsonString();
    }

    /// <summary>Each of a Song's Tags as "name colour", in the order the Song lists them.</summary>
    private static List<string> Labels(JsonElement song) =>
        [.. song.GetProperty("tags").EnumerateArray().Select(static tag => $"{tag.GetProperty("name").GetString()} {tag.GetProperty("colour").GetString()}")];
}
