using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.References;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// Deleting a Version (#101): <c>GET /api/v1/versions/{reference}/deletion-impact</c> and
/// <c>DELETE /api/v1/versions/{reference}</c>, both session only. The Version, its Generations, and
/// its history go into one retention group; its descendants stay with their numbers under a
/// placeholder; its number is never offered again; the current Version moves when it was current;
/// and the last Version is replaced by a blank one in the same operation.
/// </summary>
public sealed class VersionDeletionEndpointTests
{
    [Fact]
    public async Task DeletingAVersionKeepsItsDescendantsUnderAPlaceholderAndRetainsItWithItsHistory()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongWithVersionsAsync(client, "Tree", "1.1", "1.1.1", "2");
        var other = await SongWithVersionsAsync(client, "Bystander", "1.1");
        var deleted = await VersionAsync(client, "n8-1-v1.1");
        using (var write = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/versions/n8-1-v1.1/snapshots", UriKind.Relative), """{"lyrics":"Kept in history","styles":""}"""))
        {
            Assert.Equal(HttpStatusCode.Created, write.StatusCode);
        }

        const string Descendants = "SELECT number || '|' || revision || '|' || lyrics || '|' || inputs || '|' || updated_utc FROM versions WHERE number IN ('1', '1.1.1', '2') AND song_id = (SELECT id FROM songs WHERE shortcode_number = 1) ORDER BY number_sort_key;";
        var descendantsBefore = Rows(factory, Descendants);
        var otherBefore = Rows(factory, "SELECT number || '|' || revision FROM versions WHERE song_id = (SELECT id FROM songs WHERE shortcode_number = 2) ORDER BY number;");
        var songRevision = song.GetProperty("revision").GetInt32();

        // The confirmation's counts: one descendant remains, no Generation goes with it.
        var impact = await ImpactAsync(client, "n8-1-v1.1");
        Assert.Equal(0, impact.GetProperty("generationCount").GetInt32());
        Assert.Equal(1, impact.GetProperty("remainingDescendantCount").GetInt32());
        Assert.False(impact.GetProperty("isLastVersion").GetBoolean());
        Assert.Equal(deleted.GetProperty("revision").GetInt32(), impact.GetProperty("revision").GetInt32());

        clock.Advance(TimeSpan.FromMinutes(1));
        using (var response = await DeleteAsync(client, "n8-1-v1.1", deleted.GetProperty("revision").GetInt32()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

            // The answer is the Song's current Version, unchanged here (2 was current), in full.
            var current = await SetupApi.JsonAsync(response);
            Assert.Equal("2", current.GetProperty("number").GetString());
            Assert.True(current.GetProperty("current").GetBoolean());
            Assert.Equal(JsonValueKind.String, current.GetProperty("lyrics").ValueKind);
        }

        // The tree: 1, a placeholder for 1.1, 1.1.1 beneath it, and 2.
        var list = await ListAsync(client, "n8-1");
        Assert.Equal(["1", "1.1.1", "2"], Numbers(list));
        Assert.Equal(["1.1"], list.GetProperty("deletedPlaceholders").EnumerateArray().Select(static number => number.GetString()));

        // The descendants' content, numbers, and revisions are as they were; another Song is untouched.
        Assert.Equal(descendantsBefore, Rows(factory, Descendants));
        Assert.Equal(otherBefore, Rows(factory, "SELECT number || '|' || revision FROM versions WHERE song_id = (SELECT id FROM songs WHERE shortcode_number = 2) ORDER BY number;"));
        Assert.Equal(other.GetProperty("revision").GetInt32(), (await SongAsync(client, "n8-2")).GetProperty("revision").GetInt32());
        Assert.Empty((await ListAsync(client, "n8-2")).GetProperty("deletedPlaceholders").EnumerateArray());

        // The delete raises the Song's revision.
        Assert.Equal(songRevision + 1, (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32());

        // One group: the Version and its history, under its shortcode.
        var group = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        Assert.Equal(RetainedRecordTypes.Version, group.Kind);
        Assert.Equal("Version n8-1-v1.1", group.Label);
        Assert.Equal("n8-1-v1.1", group.Shortcode);
        Assert.Equal(
            [RetainedRecordTypes.Version, RetainedRecordTypes.EditorSnapshot],
            group.Records.Select(static record => record.RecordType));
        Assert.Equal(Upper(deleted.GetProperty("id").GetGuid()), group.Records[0].OriginalId);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM editor_revisions WHERE version_id = '{Upper(deleted.GetProperty("id").GetGuid())}';"));
    }

    [Fact]
    public async Task ADeletedVersionsNumberIsNeverOfferedAgainAndCannotBeTaken()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithVersionsAsync(client, "Numbers", "1.1", "1.1.1", "2");
        await DeleteAsync(client, "n8-1-v1.1");

        var options = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1/next-numbers", UriKind.Relative)));
        var offered = options.GetProperty("options").EnumerateArray().Select(static option => option.GetProperty("number").GetString()).ToList();
        Assert.DoesNotContain("1.1", offered);
        Assert.Equal(["1.2", "3"], offered.Order(StringComparer.Ordinal));

        using var taken = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative), """{"sourceVersionId":"n8-1-v1","number":"1.1"}""");
        Assert.True(taken.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity, await taken.Content.ReadAsStringAsync());
        Assert.Equal(["1", "1.1.1", "2"], Numbers(await ListAsync(client, "n8-1")));
    }

    [Fact]
    public async Task DeletingTheCurrentVersionMakesItsNearestUnarchivedAncestorCurrent()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithVersionsAsync(client, "Ancestor", "1.1", "1.1.1", "2");
        await SetCurrentAsync(client, "n8-1", "n8-1-v1.1.1");

        // 1.1 is archived, so the nearest ancestor that is not is 1.
        await ArchiveAsync(client, "n8-1-v1.1");
        var current = await DeleteAsync(client, "n8-1-v1.1.1");
        Assert.Equal("1", current.GetProperty("number").GetString());
        Assert.Equal("1", (await SongAsync(client, "n8-1")).GetProperty("currentVersion").GetProperty("number").GetString());

        // Complement: with 1.1 not archived, it is the nearest ancestor.
        await SongWithVersionsAsync(client, "Parent", "1.1", "1.1.1");
        var parent = await DeleteAsync(client, "n8-2-v1.1.1");
        Assert.Equal("1.1", parent.GetProperty("number").GetString());
    }

    [Fact]
    public async Task WithNoUnarchivedAncestorTheLowestUnarchivedVersionBecomesCurrentElseTheLowest()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // 3's only ancestor is none (top level): the lowest Version not archived, 2, takes over (1 is archived).
        await SongWithVersionsAsync(client, "Lowest", "2", "3");
        await ArchiveAsync(client, "n8-1-v1");
        Assert.Equal("2", (await DeleteAsync(client, "n8-1-v3")).GetProperty("number").GetString());

        // Every other Version archived: the lowest-numbered one takes over.
        await SongWithVersionsAsync(client, "All archived", "2", "3");
        await ArchiveAsync(client, "n8-2-v1");
        await ArchiveAsync(client, "n8-2-v2");
        var lowest = await DeleteAsync(client, "n8-2-v3");
        Assert.Equal("1", lowest.GetProperty("number").GetString());
        Assert.True(lowest.GetProperty("archived").GetBoolean());

        // Deleting a Version that is not current leaves the current one alone.
        await SongWithVersionsAsync(client, "Not current", "2", "3");
        Assert.Equal("3", (await DeleteAsync(client, "n8-3-v2")).GetProperty("number").GetString());
        Assert.Equal("3", (await SongAsync(client, "n8-3")).GetProperty("currentVersion").GetProperty("number").GetString());
    }

    [Fact]
    public async Task DeletingTheLastVersionCreatesABlankOneWithTheNextNeverUsedTopLevelNumber()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Only one");
        await EditAsync(client, "n8-1-v1", """{"lyrics":"Gone","name":"Old"}""");

        var impact = await ImpactAsync(client, "n8-1-v1");
        Assert.True(impact.GetProperty("isLastVersion").GetBoolean());
        Assert.Equal(0, impact.GetProperty("remainingDescendantCount").GetInt32());

        var blank = await DeleteAsync(client, "n8-1-v1");
        Assert.Equal("2", blank.GetProperty("number").GetString());
        Assert.True(blank.GetProperty("current").GetBoolean());
        Assert.False(blank.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(1, blank.GetProperty("revision").GetInt32());
        Assert.Equal(string.Empty, blank.GetProperty("lyrics").GetString());
        Assert.Equal(JsonValueKind.Null, blank.GetProperty("name").ValueKind);
        Assert.Equal(["2"], Numbers(await ListAsync(client, "n8-1")));
        var after = await SongAsync(client, "n8-1");
        Assert.Equal("2", after.GetProperty("currentVersion").GetProperty("number").GetString());
        Assert.Equal(song.GetProperty("revision").GetInt32() + 1, after.GetProperty("revision").GetInt32());

        // After Versions up to 5.2 have all been deleted, the new Version is 6.
        await SongWithVersionsAsync(client, "Up to five", "2", "3", "4", "5", "5.1", "5.2");
        foreach (var number in new[] { "5.2", "5.1", "4", "3", "2", "1", "5" })
        {
            await DeleteAsync(client, $"n8-2-v{number}");
        }

        Assert.Equal(["6"], Numbers(await ListAsync(client, "n8-2")));
        Assert.Empty((await ListAsync(client, "n8-2")).GetProperty("deletedPlaceholders").EnumerateArray());
    }

    [Fact]
    public async Task AFrozenVersionIsDeletedWithItsGenerationsAndItsInputsAreKeptAsTheyWere()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithVersionsAsync(client, "Frozen", "2");
        await EditAsync(client, "n8-1-v1", """{"lyrics":"Frozen words","styles":"frozen style"}""");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1");
        var id = (await VersionAsync(client, "n8-1-v1")).GetProperty("id").GetGuid();
        var stored = Rows(factory, $"SELECT hex(lyrics) || hex(styles) || hex(kind) || hex(model) || hex(inputs) FROM versions WHERE id = '{Upper(id)}';");

        Assert.Equal(2, (await ImpactAsync(client, "n8-1-v1")).GetProperty("generationCount").GetInt32());
        await DeleteAsync(client, "n8-1-v1");

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM generations WHERE version_id = '{Upper(id)}';"));
        var group = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        Assert.Equal(
            [RetainedRecordTypes.Version, RetainedRecordTypes.Generation, RetainedRecordTypes.Generation],
            group.Records.Select(static record => record.RecordType));

        // The Generation shortcodes no longer resolve; restoring brings the frozen Version back byte for byte.
        using (var generation = await client.GetAsync(new Uri("/api/v1/resolve/n8-1-v1-g1", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.NotFound, generation.StatusCode);
        }

        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        Assert.Equal(stored, Rows(factory, $"SELECT hex(lyrics) || hex(styles) || hex(kind) || hex(model) || hex(inputs) FROM versions WHERE id = '{Upper(id)}';"));
        var restored = await VersionAsync(client, "n8-1-v1");
        Assert.True(restored.GetProperty("isFrozen").GetBoolean());
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM generations WHERE version_id = '{Upper(id)}';"));
        using var resolved = await client.GetAsync(new Uri("/api/v1/resolve/n8-1-v1-g2", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
    }

    [Fact]
    public async Task ADeletedVersionResolvesAndReadsAsDeletedForThirtyDays()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithVersionsAsync(client, "Gone", "2");
        var version = await VersionAsync(client, "n8-1-v1");
        var id = version.GetProperty("id").GetGuid();
        await DeleteAsync(client, "n8-1-v1");

        foreach (var reference in new[] { "n8-1-v1", "N8-1-V1", id.ToString() })
        {
            var resolved = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/resolve/{reference}", UriKind.Relative)));
            Assert.Equal(ReferenceResolver.DeletedStatus, resolved.GetProperty("status").GetString());
            Assert.Equal("version", resolved.GetProperty("entityType").GetString());
            Assert.Equal(id, resolved.GetProperty("id").GetGuid());
            Assert.Equal("n8-1-v1", resolved.GetProperty("shortcode").GetString());
            Assert.Equal("n8-1", resolved.GetProperty("song").GetProperty("shortcode").GetString());

            // Reads and writes say it was deleted; the editor still open on it learns it on its next save.
            using var read = await client.GetAsync(new Uri($"/api/v1/versions/{reference}", UriKind.Relative));
            var problem = await SetupApi.ProblemAsync(read, HttpStatusCode.NotFound, VersionsEndpoints.DeletedCode);
            Assert.Equal("n8-1-v1", problem.GetProperty("versionShortcode").GetString());
            Assert.Equal("1", problem.GetProperty("number").GetString());
            Assert.Equal(TestClock.Start.UtcDateTime, problem.GetProperty("deletedAt").GetDateTime().ToUniversalTime());
            using var patched = await SendAsync(client, HttpMethod.Patch, $"/api/v1/versions/{reference}", 1, """{"lyrics":"typed after"}""");
            await SetupApi.ProblemAsync(patched, HttpStatusCode.NotFound, VersionsEndpoints.DeletedCode);
            using var snapshot = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/versions/{reference}/snapshots", UriKind.Relative), """{"lyrics":"typed after","styles":""}""");
            await SetupApi.ProblemAsync(snapshot, HttpStatusCode.NotFound, VersionsEndpoints.DeletedCode);
            using var again = await SendAsync(client, HttpMethod.Delete, $"/api/v1/versions/{reference}", 1);
            await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, VersionsEndpoints.DeletedCode);
        }

        // Complement: a Version that never existed is plainly not found.
        using (var never = await client.GetAsync(new Uri("/api/v1/versions/n8-1-v7", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(never, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // After 30 days it is not found (the prune may not have run yet; the group is past its time).
        clock.Advance(RetentionService.RetentionPeriod);
        using var scope = factory.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<ReferenceResolver>().ResolveAsync(CatalogReference.Parse("n8-1-v1"), CancellationToken.None));
        Assert.Null(await scope.ServiceProvider.GetRequiredService<VersionDeletionService>().FindDeletedAsync(CatalogReference.Parse(id.ToString()), CancellationToken.None));
    }

    [Fact]
    public async Task RestoringTheGroupPutsTheVersionAndItsHistoryBackAndRemovesThePlaceholder()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithVersionsAsync(client, "Restore", "1.1", "1.1.1", "2");
        await EditAsync(client, "n8-1-v1.1", """{"lyrics":"Middle words","name":"Middle"}""");
        using (var write = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/versions/n8-1-v1.1/snapshots", UriKind.Relative), """{"lyrics":"Older middle","styles":""}"""))
        {
            Assert.Equal(HttpStatusCode.Created, write.StatusCode);
        }

        var before = await VersionAsync(client, "n8-1-v1.1");
        await DeleteAsync(client, "n8-1-v1.1");
        var group = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        var revision = (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32();

        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));

        var list = await ListAsync(client, "n8-1");
        Assert.Equal(["1", "1.1", "1.1.1", "2"], Numbers(list));
        Assert.Empty(list.GetProperty("deletedPlaceholders").EnumerateArray());
        var restored = await VersionAsync(client, "n8-1-v1.1");
        Assert.Equal(before.GetProperty("id").GetGuid(), restored.GetProperty("id").GetGuid());
        Assert.Equal("Middle words", restored.GetProperty("lyrics").GetString());
        Assert.Equal("Middle", restored.GetProperty("name").GetString());
        Assert.Equal(before.GetProperty("revision").GetInt32() + 1, restored.GetProperty("revision").GetInt32());
        var history = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1.1/snapshots", UriKind.Relative)));
        Assert.Equal(1, history.GetProperty("items").GetArrayLength());

        // Restoring does not change the current Version; the number is still used, so it is still not offered.
        var song = await SongAsync(client, "n8-1");
        Assert.Equal("2", song.GetProperty("currentVersion").GetProperty("number").GetString());
        Assert.Equal(revision + 1, song.GetProperty("revision").GetInt32());
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM used_version_numbers WHERE number = '1.1' AND song_id = (SELECT id FROM songs WHERE shortcode_number = 1);"));
        using var resolved = await client.GetAsync(new Uri("/api/v1/resolve/n8-1-v1.1", UriKind.Relative));
        Assert.Equal(ReferenceResolver.ActiveStatus, (await SetupApi.JsonAsync(resolved)).GetProperty("status").GetString());

        // Complement: a new Version still cannot take a used number after the restore.
        using var taken = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative), """{"sourceVersionId":"n8-1-v1","number":"1.1"}""");
        Assert.False(taken.IsSuccessStatusCode);
    }

    [Fact]
    public async Task RestoringTheLastVersionRemovesTheBlankOneOnlyWhenItWasNeverEdited()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // Never edited: the blank Version 2 goes, and the restored Version is current again.
        await SongApi.CreateAsync(client, "Untouched blank");
        await DeleteAsync(client, "n8-1-v1");
        var untouched = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, untouched.Id));
        Assert.Equal(["1"], Numbers(await ListAsync(client, "n8-1")));
        Assert.Equal("1", (await SongAsync(client, "n8-1")).GetProperty("currentVersion").GetProperty("number").GetString());
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM used_version_numbers WHERE number = '2' AND song_id = (SELECT id FROM songs WHERE shortcode_number = 1);"));

        // The removed blank's number stays used: deleting the last Version again creates 3.
        Assert.Equal("3", (await DeleteAsync(client, "n8-1-v1")).GetProperty("number").GetString());

        // Edited: the blank Version stays and stays current; the restored one is not current.
        await SongApi.CreateAsync(client, "Edited blank");
        await DeleteAsync(client, "n8-2-v1");
        await EditAsync(client, "n8-2-v2", """{"lyrics":"Started again"}""");
        var edited = (await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None))).First(group => group.Shortcode == "n8-2-v1");
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, edited.Id));
        Assert.Equal(["1", "2"], Numbers(await ListAsync(client, "n8-2")));
        Assert.Equal("2", (await SongAsync(client, "n8-2")).GetProperty("currentVersion").GetProperty("number").GetString());
    }

    [Fact]
    public async Task ADeleteIsRefusedOnAStaleOrMissingRevisionAnUnknownVersionAndATokenAndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongWithVersionsAsync(client, "Refusals", "2");
        var version = await VersionAsync(client, "n8-1-v1");
        var revision = version.GetProperty("revision").GetInt32();

        using (var stale = await SendAsync(client, HttpMethod.Delete, "/api/v1/versions/n8-1-v1", revision + 1))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
            Assert.Equal(revision, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var missing = await SongApi.SendJsonAsync(client, HttpMethod.Delete, new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative), "{}"))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        using (var unknown = await SendAsync(client, HttpMethod.Delete, "/api/v1/versions/n8-1-v9", 1))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using (var unknownImpact = await client.GetAsync(new Uri("/api/v1/versions/n8-1-v9/deletion-impact", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(unknownImpact, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // A token, whatever its scopes, is refused: deleting is the web UI's.
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        using (var byToken = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Delete, new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative), token))
        {
            await SetupApi.ProblemAsync(byToken, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        using (var impactByToken = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Get, new Uri("/api/v1/versions/n8-1-v1/deletion-impact", UriKind.Relative), token))
        {
            await SetupApi.ProblemAsync(impactByToken, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        Assert.Equal(["1", "2"], Numbers(await ListAsync(client, "n8-1")));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
    }

    /// <summary>A new Song with Version 1 and then each of <paramref name="numbers"/>, each from its parent (or from 1), in order; the last is current.</summary>
    private static async Task<JsonElement> SongWithVersionsAsync(HttpClient client, string title, params string[] numbers)
    {
        var song = await SongApi.CreateAsync(client, title);
        var shortcode = song.GetProperty("shortcode").GetString()!;
        foreach (var number in numbers)
        {
            var parent = number.Contains('.', StringComparison.Ordinal) ? number[..number.LastIndexOf('.')] : "1";
            using var created = await SongApi.SendJsonAsync(
                client,
                HttpMethod.Post,
                new Uri($"/api/v1/songs/{shortcode}/versions", UriKind.Relative),
                $$"""{"sourceVersionId":"{{shortcode}}-v{{parent}}","number":"{{number}}"}""");
            Assert.True(created.StatusCode == HttpStatusCode.Created, $"{number}: {await created.Content.ReadAsStringAsync()}");
        }

        return await SongAsync(client, shortcode);
    }

    private static async Task<JsonElement> SongAsync(HttpClient client, string reference) =>
        await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(reference)));

    private static async Task<JsonElement> VersionAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{reference}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string song) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/songs/{song}/versions", UriKind.Relative)));

    private static List<string?> Numbers(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("number").GetString())];

    private static async Task<JsonElement> ImpactAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{reference}/deletion-impact", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Deletes the Version at its current revision; the Song's current Version afterwards.</summary>
    private static async Task<JsonElement> DeleteAsync(HttpClient client, string reference)
    {
        var revision = (await VersionAsync(client, reference)).GetProperty("revision").GetInt32();
        using var response = await DeleteAsync(client, reference, revision);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string reference, int revision) =>
        SendAsync(client, HttpMethod.Delete, $"/api/v1/versions/{reference}", revision);

    private static async Task SetCurrentAsync(HttpClient client, string song, string version)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Put, new Uri($"/api/v1/songs/{song}/current-version", UriKind.Relative), $$"""{"versionId":"{{version}}"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task ArchiveAsync(HttpClient client, string version) => EditAsync(client, version, """{"archived":true}""");

    private static async Task EditAsync(HttpClient client, string version, string json)
    {
        var revision = (await VersionAsync(client, version)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/versions/{version}", revision, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }

    private static List<string> Rows(N8TracksApiFactory factory, string sql) => TestDatabase.Rows(factory.DataPath, sql);
}
