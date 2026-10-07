using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Generations;

/// <summary>
/// The user's judgement of a Generation (#119): <c>PATCH /api/v1/generations/{reference}</c> sets,
/// changes, or clears its rating under the Generation's revision, and
/// <c>POST .../comments</c>, <c>PATCH</c> and <c>DELETE .../comments/{id}</c> keep comments on it,
/// each under its own revision; all need <c>generations.evaluate</c>. Neither is a creation input, so
/// both work in any state and on a frozen or archived Version; nothing Suno reports changes them.
/// </summary>
public sealed class GenerationEvaluationEndpointTests
{
    private const string Generation = "n8-1-v1-g1";

    [Fact]
    public async Task ARatingIsSetChangedAndClearedEachRaisingTheGenerationsRevisionAndTheSongsUpdatedTimeOnly()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Rated");
        var attached = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("rated-clip"));
        var songBefore = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));

        // Set by shortcode, changed by ID, cleared with null: each raises the revision by one.
        clock.Advance(TimeSpan.FromMinutes(5));
        var rated = await RateAsync(client, Generation, 1, """{"rating":4}""", HttpStatusCode.OK);
        Assert.Equal(4, rated.GetProperty("rating").GetInt32());
        Assert.Equal(2, rated.GetProperty("revision").GetInt32());
        var changed = await RateAsync(client, attached.Generation.Id.ToString(), 2, """{"rating":5}""", HttpStatusCode.OK);
        Assert.Equal(5, changed.GetProperty("rating").GetInt32());
        var cleared = await RateAsync(client, Generation, 3, """{"rating":null}""", HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("rating").ValueKind);
        Assert.Equal(4, cleared.GetProperty("revision").GetInt32());

        // A missing rating, or the one it already has, changes nothing, not even the revision.
        Assert.Equal(4, (await RateAsync(client, Generation, 4, "{}", HttpStatusCode.OK)).GetProperty("revision").GetInt32());
        Assert.Equal(5, (await RateAsync(client, Generation, 4, """{"rating":3}""", HttpStatusCode.OK)).GetProperty("revision").GetInt32());
        Assert.Equal(5, (await RateAsync(client, Generation, 5, """{"rating":3}""", HttpStatusCode.OK)).GetProperty("revision").GetInt32());

        // It is read back everywhere a Generation is answered.
        var read = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/generations/{Generation}", UriKind.Relative)));
        Assert.Equal(3, read.GetProperty("rating").GetInt32());
        var listed = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/songs/n8-1/generations", UriKind.Relative)));
        Assert.Equal(3, listed.GetProperty("items")[0].GetProperty("rating").GetInt32());

        // The Song's updated time moved; its revision did not. Nothing else of the Generation changed.
        var songAfter = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal(songBefore.GetProperty("revision").GetInt32(), songAfter.GetProperty("revision").GetInt32());
        Assert.Equal(clock.GetUtcNow().UtcDateTime, songAfter.GetProperty("updatedAt").GetDateTime());
        Assert.Equal("rated-clip", read.GetProperty("sunoId").GetString());
        Assert.Equal("active", read.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ARatingThatIsNotAWholeNumberFromOneToFiveOrAStaleRevisionIsRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Refused");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("refused-clip"));
        var comment = await AddCommentAsync(client, Generation, "Kept with it");
        var before = Rows(factory);

        foreach (var body in new[] { """{"rating":0}""", """{"rating":6}""", """{"rating":2.5}""", """{"rating":-1}""", """{"rating":"4"}""", """{"rating":true}""", """{"rating":[4]}""" })
        {
            using var response = await PatchAsync(client, $"generations/{Generation}", "\"1\"", body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("rating", out _), body);
        }

        // No If-Match, a malformed one, and an unknown Generation.
        using (var missing = await PatchAsync(client, $"generations/{Generation}", null, """{"rating":4}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, Revisions.RequiredCode);
        }

        using (var malformed = await PatchAsync(client, $"generations/{Generation}", "1", """{"rating":4}"""))
        {
            await SetupApi.ProblemAsync(malformed, HttpStatusCode.BadRequest, Revisions.InvalidCode);
        }

        using (var unknown = await PatchAsync(client, "generations/n8-1-v1-g9", "\"1\"", """{"rating":4}"""))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal(before, Rows(factory));

        // A stale rating write is 409 with the Generation as it is now, its comments included.
        await RateAsync(client, Generation, 1, """{"rating":2}""", HttpStatusCode.OK);
        using var stale = await PatchAsync(client, $"generations/{Generation}", "\"1\"", """{"rating":5}""");
        var conflict = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
        var current = conflict.GetProperty("current");
        Assert.Equal(2, current.GetProperty("rating").GetInt32());
        Assert.Equal(2, current.GetProperty("revision").GetInt32());
        Assert.Equal(comment.GetProperty("id").GetString(), current.GetProperty("comments")[0].GetProperty("id").GetString());
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT rating FROM generations;"));

        // The database refuses a rating out of range too.
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(factory.DataPath, "UPDATE generations SET rating = 6;"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(factory.DataPath, "UPDATE generations SET rating = 0;"));
    }

    [Fact]
    public async Task CommentsAreAddedEditedAndDeletedListedOldestFirstWithoutTouchingTheGenerationsRevision()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Commented");
        var attached = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("commented-clip"));

        // Added: trimmed, newlines kept, not edited, at revision 1, with its address.
        using (var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/generations/{Generation}/comments", UriKind.Relative), """{"text":"  Good piano intro\nand a long outro  "}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var first = await SetupApi.JsonAsync(created);
            Assert.Equal("Good piano intro\nand a long outro", first.GetProperty("text").GetString());
            Assert.Equal(JsonValueKind.Null, first.GetProperty("editedAt").ValueKind);
            Assert.Equal(1, first.GetProperty("revision").GetInt32());
            Assert.Equal(clock.GetUtcNow().UtcDateTime, first.GetProperty("createdAt").GetDateTime());
            Assert.Equal("\"1\"", created.Headers.ETag?.Tag);
            Assert.EndsWith($"/api/v1/generations/{attached.Generation.Id}/comments/{first.GetProperty("id").GetString()}", created.Headers.Location!.ToString(), StringComparison.Ordinal);
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await AddCommentAsync(client, attached.Generation.Id.ToString(), "Second thought");
        var firstId = Comments(await ReadAsync(client))[0].GetProperty("id").GetString()!;

        // Edited: the text and its edited time change, and its revision goes up.
        clock.Advance(TimeSpan.FromMinutes(1));
        using (var edited = await PatchAsync(client, $"generations/{Generation}/comments/{firstId}", "\"1\"", """{"text":"Great piano intro"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            var comment = await SetupApi.JsonAsync(edited);
            Assert.Equal("Great piano intro", comment.GetProperty("text").GetString());
            Assert.Equal(clock.GetUtcNow().UtcDateTime, comment.GetProperty("editedAt").GetDateTime());
            Assert.Equal(2, comment.GetProperty("revision").GetInt32());
        }

        // The same text once trimmed is no edit: nothing changes.
        using (var same = await PatchAsync(client, $"generations/{Generation}/comments/{second.GetProperty("id").GetString()}", "\"1\"", """{"text":" Second thought "}"""))
        {
            var comment = await SetupApi.JsonAsync(same);
            Assert.Equal(JsonValueKind.Null, comment.GetProperty("editedAt").ValueKind);
            Assert.Equal(1, comment.GetProperty("revision").GetInt32());
        }

        // Listed oldest first, embedded in every Generation answer; the Generation's revision is untouched.
        var generation = await ReadAsync(client);
        Assert.Equal(["Great piano intro", "Second thought"], Comments(generation).Select(static comment => comment.GetProperty("text").GetString()));
        Assert.Equal(1, generation.GetProperty("revision").GetInt32());
        var listed = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1/generations", UriKind.Relative)));
        Assert.Equal(2, listed.GetProperty("items")[0].GetProperty("comments").GetArrayLength());

        // Deleted after its revision is confirmed; deleting it again is 404, and it is not retained.
        using (var deleted = await SendAsync(client, HttpMethod.Delete, $"generations/{Generation}/comments/{firstId}", "\"2\""))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var again = await SendAsync(client, HttpMethod.Delete, $"generations/{Generation}/comments/{firstId}", "\"2\""))
        {
            await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        Assert.Equal(["Second thought"], Comments(await ReadAsync(client)).Select(static comment => comment.GetProperty("text").GetString()));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_records;"));
        Assert.Equal(1, (await ReadAsync(client)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task AnEmptyOrOverLongCommentOrAStaleCommentWriteIsRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Refused comments");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null);
        var kept = await AddCommentAsync(client, Generation, "Kept");
        var id = kept.GetProperty("id").GetString();
        var before = Rows(factory);

        var tooLong = JsonSerializer.Serialize(new { text = new string('x', GenerationComment.MaximumLength + 1) });
        foreach (var body in new[] { "{}", """{"text":null}""", """{"text":""}""", """{"text":"  \n\t "}""", tooLong, """{"text":4}""", """{"text":["a"]}""" })
        {
            using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/generations/{Generation}/comments", UriKind.Relative), body);
            var problem = await SetupApi.ProblemAsync(created, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("text", out _), body);

            using var edited = await PatchAsync(client, $"generations/{Generation}/comments/{id}", "\"1\"", body);
            await SetupApi.ProblemAsync(edited, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        }

        // Unknown Generation, unknown comment, a comment of another Generation, and no If-Match.
        using (var nowhere = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/generations/n8-1-v1-g7/comments", UriKind.Relative), """{"text":"Lost"}"""))
        {
            await SetupApi.ProblemAsync(nowhere, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        foreach (var path in new[] { $"generations/{Generation}/comments/{Guid.CreateVersion7()}", $"generations/n8-1-v1-g2/comments/{id}" })
        {
            using var edited = await PatchAsync(client, path, "\"1\"", """{"text":"Changed"}""");
            await SetupApi.ProblemAsync(edited, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
            using var deleted = await SendAsync(client, HttpMethod.Delete, path, "\"1\"");
            await SetupApi.ProblemAsync(deleted, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using (var unrevised = await PatchAsync(client, $"generations/{Generation}/comments/{id}", null, """{"text":"Changed"}"""))
        {
            await SetupApi.ProblemAsync(unrevised, HttpStatusCode.PreconditionRequired, Revisions.RequiredCode);
        }

        using (var unrevisedDelete = await SendAsync(client, HttpMethod.Delete, $"generations/{Generation}/comments/{id}", null))
        {
            await SetupApi.ProblemAsync(unrevisedDelete, HttpStatusCode.PreconditionRequired, Revisions.RequiredCode);
        }

        Assert.Equal(before, Rows(factory));

        // A stale edit or deletion is 409 with the comment as it is now.
        using (var edited = await PatchAsync(client, $"generations/{Generation}/comments/{id}", "\"1\"", """{"text":"Changed once"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        using (var staleEdit = await PatchAsync(client, $"generations/{Generation}/comments/{id}", "\"1\"", """{"text":"Changed twice"}"""))
        {
            var current = (await SetupApi.ProblemAsync(staleEdit, HttpStatusCode.Conflict, Revisions.ConflictCode)).GetProperty("current");
            Assert.Equal("Changed once", current.GetProperty("text").GetString());
            Assert.Equal(2, current.GetProperty("revision").GetInt32());
        }

        using (var staleDelete = await SendAsync(client, HttpMethod.Delete, $"generations/{Generation}/comments/{id}", "\"1\""))
        {
            await SetupApi.ProblemAsync(staleDelete, HttpStatusCode.Conflict, Revisions.ConflictCode);
        }

        Assert.Equal("Changed once", Comments(await ReadAsync(client))[0].GetProperty("text").GetString());

        // Exactly the longest is kept; the database refuses longer too.
        var longest = new string('y', GenerationComment.MaximumLength);
        Assert.Equal(longest, (await AddCommentAsync(client, Generation, longest)).GetProperty("text").GetString());
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(factory.DataPath, "UPDATE generation_comments SET text = '';"));
    }

    [Fact]
    public async Task RatingAndCommentingNeedGenerationsEvaluate()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Scoped");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null);
        var id = (await AddCommentAsync(client, Generation, "Scoped comment")).GetProperty("id").GetString();
        using var tool = factory.CreateClient();
        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.GenerationsEvaluate)]);
        var evaluator = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.GenerationsEvaluate);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Patch, $"generations/{Generation}"),
            (HttpMethod.Post, $"generations/{Generation}/comments"),
            (HttpMethod.Patch, $"generations/{Generation}/comments/{id}"),
            (HttpMethod.Delete, $"generations/{Generation}/comments/{id}"),
        })
        {
            using var refused = await CredentialApi.SendAsync(tool, method, new Uri("/api/v1/" + path, UriKind.Relative), others);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.GenerationsEvaluate, problem.GetProperty("requiredScope").GetString());
        }

        // A token holding only generations.evaluate rates and comments.
        using (var rate = await TokenSendAsync(tool, evaluator, HttpMethod.Patch, $"generations/{Generation}", "\"1\"", """{"rating":4}"""))
        {
            Assert.Equal(HttpStatusCode.OK, rate.StatusCode);
        }

        using (var comment = await TokenSendAsync(tool, evaluator, HttpMethod.Post, $"generations/{Generation}/comments", null, """{"text":"From a tool"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, comment.StatusCode);
        }

        using (var deleted = await TokenSendAsync(tool, evaluator, HttpMethod.Delete, $"generations/{Generation}/comments/{id}", "\"1\"", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    [Fact]
    public async Task RatingAndCommentingWorkInAnyStateOnAFrozenAndOnAnArchivedVersion()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Any state");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("generating-clip", "submitted"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("trashed-clip"));
        TestDatabase.Execute(factory.DataPath, "UPDATE generations SET state = 'archived', remote_state = 'trashed' WHERE ordinal = 2;");

        // Archive the (frozen) Version.
        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        Assert.True(version.GetProperty("isFrozen").GetBoolean());
        await SongApi.CreateAsync(client, "Other");
        using (var branched = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative), """{"sourceVersionId":"n8-1-v1","number":"2"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, branched.StatusCode);
        }

        using (var current = await SongApi.SendJsonAsync(client, HttpMethod.Put, new Uri("/api/v1/songs/n8-1/current-version", UriKind.Relative), """{"versionId":"n8-1-v2"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }

        version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        using (var archived = await PatchAsync(client, "versions/n8-1-v1", SongApi.Quoted(version.GetProperty("revision").GetInt32()), """{"archived":true}"""))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
            Assert.True((await SetupApi.JsonAsync(archived)).GetProperty("archived").GetBoolean());
        }

        foreach (var shortcode in new[] { "n8-1-v1-g1", "n8-1-v1-g2" })
        {
            Assert.Equal(5, (await RateAsync(client, shortcode, 1, """{"rating":5}""", HttpStatusCode.OK)).GetProperty("rating").GetInt32());
            await AddCommentAsync(client, shortcode, "Still worth a note");
        }

        var read = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/generations/n8-1-v1-g2", UriKind.Relative)));
        Assert.Equal("archived", read.GetProperty("state").GetString());
        Assert.Equal(1, read.GetProperty("comments").GetArrayLength());
    }

    [Fact]
    public async Task NothingSunoReportsChangesARatingOrComments()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Provider data");
        var raw = Clips.Minimal("judged-clip");
        var attached = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", raw);
        await RateAsync(client, Generation, 1, """{"rating":4}""", HttpStatusCode.OK);
        await AddCommentAsync(client, Generation, "Mine, not Suno's");
        var before = Judgement(factory);

        // The same clip reported again through the application service (refused: the Suno ID is
        // live), another clip attached to the same Version, and a new provider record for it.
        Assert.IsType<GenerationAttachOutcome.SunoIdExists>(await SongApi.AttachAsync(factory, "n8-1-v1", raw));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("sibling-clip"));
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<IGenerationStore>().SaveProviderRecordAsync(
                new ProviderRecord(attached.Generation.Id, "judged-clip", ProviderRecord.ClipKind, Clips.Minimal("judged-clip", "error"), DateTimeOffset.UtcNow, null),
                CancellationToken.None);
        }

        Assert.Equal(before, Judgement(factory));
        var read = await ReadAsync(client);
        Assert.Equal(4, read.GetProperty("rating").GetInt32());
        Assert.Equal("Mine, not Suno's", Comments(read)[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ADeletedVersionsGenerationTakesItsRatingAndCommentsIntoRetentionAndBack()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Retained");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", null);
        await RateAsync(client, Generation, 1, """{"rating":3}""", HttpStatusCode.OK);
        await AddCommentAsync(client, Generation, "First");
        await AddCommentAsync(client, Generation, "Second");
        using (var branched = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative), """{"sourceVersionId":"n8-1-v1","number":"2"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, branched.StatusCode);
        }

        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v1", SongApi.Quoted(version.GetProperty("revision").GetInt32())))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generation_comments;"));
        Assert.Equal(
            ["generation", "generation-comment", "generation-comment"],
            TestDatabase.Rows(factory.DataPath, "SELECT record_type FROM retention_records WHERE record_type LIKE 'generation%' ORDER BY position;"));

        var group = await WithServiceAsync(factory, service => service.FindByShortcodeAsync("n8-1-v1", CancellationToken.None));
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group!.Id));

        var read = await ReadAsync(client);
        Assert.Equal(3, read.GetProperty("rating").GetInt32());
        Assert.Equal(["First", "Second"], Comments(read).Select(static comment => comment.GetProperty("text").GetString()));
    }

    /// <summary>Rates a Generation with <paramref name="body"/> under <paramref name="revision"/>, expecting <paramref name="status"/>; the answer.</summary>
    private static async Task<JsonElement> RateAsync(HttpClient client, string reference, int revision, string body, HttpStatusCode status)
    {
        using var response = await PatchAsync(client, $"generations/{reference}", SongApi.Quoted(revision), body);
        Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
        Assert.Equal(SongApi.Quoted((await SetupApi.JsonAsync(response)).GetProperty("revision").GetInt32()), response.Headers.ETag?.Tag);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> AddCommentAsync(HttpClient client, string reference, string text)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/generations/{reference}/comments", UriKind.Relative), JsonSerializer.Serialize(new { text }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/generations/{Generation}", UriKind.Relative)));

    private static List<JsonElement> Comments(JsonElement generation) => [.. generation.GetProperty("comments").EnumerateArray()];

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string path, string? ifMatch, string json) =>
        SendAsync(client, HttpMethod.Patch, path, ifMatch, json);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? ifMatch, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri("/api/v1/" + path, UriKind.Relative));
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

    private static async Task<HttpResponseMessage> TokenSendAsync(HttpClient client, string token, HttpMethod method, string path, string? ifMatch, string? json)
    {
        using var request = new HttpRequestMessage(method, new Uri("/api/v1/" + path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    /// <summary>The rows a rating or a comment write changes: the Generations' ratings and revisions, and every comment.</summary>
    private static string Rows(N8TracksApiFactory factory) =>
        string.Join(
            Environment.NewLine,
            TestDatabase.Rows(factory.DataPath, "SELECT quote(rating) || '|' || revision || '|' FROM generations ORDER BY ordinal;")
                .Concat(TestDatabase.Rows(factory.DataPath, "SELECT id || '|' || text || '|' || quote(edited_utc) || '|' || revision || '|' FROM generation_comments ORDER BY created_utc;")));

    /// <summary>The first Generation's rating and its comments, as stored.</summary>
    private static string Judgement(N8TracksApiFactory factory) =>
        TestDatabase.Scalar(factory.DataPath, "SELECT quote(rating) FROM generations WHERE ordinal = 1;") + Environment.NewLine
        + string.Join(Environment.NewLine, TestDatabase.Rows(factory.DataPath, "SELECT id || text || quote(edited_utc) || revision || created_utc FROM generation_comments ORDER BY created_utc;"));
}
