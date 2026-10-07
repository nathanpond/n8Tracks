using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using Microsoft.Extensions.DependencyInjection;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Suno workspaces (#129): kept by Suno ID from the extension's reports (<c>suno.sync</c>), listed by
/// name with live Song counts, a Song's own association changed with its PATCH under its revision,
/// availability changed only by a complete list and losing nothing, and Songs moved in bulk.
/// </summary>
public sealed class SunoWorkspaceEndpointTests
{
    [Fact]
    public async Task ACapturedWorkspaceListIsKeptByIdWithItsRawProjectAndListedByName()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var first = SunoWorkspaceApi.FixtureProjects("project-me.page-1.response.json");
        var last = SunoWorkspaceApi.FixtureProjects("project-me.last-page.response.json");
        JsonNode[] projects = [.. first.Concat(last).Select(static project => project!)];

        var answer = await SunoWorkspaceApi.ReportAsync(client, token, complete: true, projects);

        var items = await SunoWorkspaceApi.ListAsync(client);
        Assert.Equal(first.Count + last.Count, items.Length);
        Assert.Equal(answer.GetProperty("items").EnumerateArray().Select(static item => item.GetRawText()), items.Select(static item => item.GetRawText()));
        Assert.Equal(items.Select(Name).Order(StringComparer.InvariantCultureIgnoreCase), items.Select(Name));
        Assert.All(items, item =>
        {
            Assert.Equal("available", item.GetProperty("state").GetString());
            Assert.Equal(0, item.GetProperty("songCount").GetInt32());
            Assert.Equal(item.GetProperty("firstSeen").GetString(), item.GetProperty("lastSeen").GetString());
        });

        // Suno's default workspace is a workspace like any other.
        var byDefault = await SunoWorkspaceApi.OneAsync(client, "default");
        Assert.Equal((string?)first[0]!["name"], byDefault.GetProperty("name").GetString());
        Assert.Equal((string?)first[0]!["description"], byDefault.GetProperty("description").GetString());
        Assert.Equal(answer.GetProperty("added").EnumerateArray().Select(static id => id.GetString()), projects.Select(static project => (string?)project["id"]));

        // The raw project is kept as sent, and never answered.
        Assert.Equal(first[1]!.ToJsonString(), JsonNode.Parse(TestDatabase.Scalar(factory.DataPath, $"SELECT raw_json FROM suno_workspaces WHERE suno_id = '{first[1]!["id"]}';"))!.ToJsonString());
        Assert.False(items[0].TryGetProperty("rawJson", out _));
        Assert.False(items[0].TryGetProperty("clip_count", out _));
    }

    [Fact]
    public async Task ReportingNeedsSunoSyncOrSunoGenerateAndListingNeedsCatalogRead()
    {
        using var factory = SongApi.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.SunoSync && scope != CredentialScopes.SunoGenerate)]);
        var sync = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var generate = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var body = new JsonObject { ["complete"] = false, ["workspaces"] = new JsonArray(SunoWorkspaceApi.Project("w-1", "One")) }.ToJsonString();

        using (var refused = await SunoWorkspaceApi.SendReportAsync(client, others, body))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(
                [CredentialScopes.SunoSync, CredentialScopes.SunoGenerate],
                problem.GetProperty("requiredScope").EnumerateArray().Select(static scope => scope.GetString()));
        }

        using (var listRefused = await CredentialApi.SendAsync(client, HttpMethod.Get, SunoWorkspaceApi.Workspaces, sync))
        {
            await SetupApi.ProblemAsync(listRefused, HttpStatusCode.Forbidden, "insufficient_scope");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_workspaces;"));

        // Complement: suno.sync reports, so does suno.generate (Generate on Suno, #145), and catalog.read lists.
        using (var reported = await SunoWorkspaceApi.SendReportAsync(client, sync, body))
        {
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        using (var fromGenerate = await SunoWorkspaceApi.SendReportAsync(client, generate, body))
        {
            Assert.Equal(HttpStatusCode.OK, fromGenerate.StatusCode);
        }

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using var listed = await CredentialApi.SendAsync(client, HttpMethod.Get, SunoWorkspaceApi.Workspaces, reader);
        Assert.Equal("w-1", (await SetupApi.JsonAsync(listed)).GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ARenameInSunoChangesTheNameAndNothingElse()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "Before", description: "Kept"));
        await SongApi.CreateAsync(client, "Lives there");
        var associated = await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-1");
        var before = await SunoWorkspaceApi.OneAsync(client, "w-1");
        var songRow = TestDatabase.Scalar(factory.DataPath, "SELECT title || '|' || revision || '|' || updated_utc || '|' || suno_workspace_id FROM songs;");

        clock.Advance(TimeSpan.FromMinutes(5));
        var answer = await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "After"));

        Assert.Equal(["w-1"], answer.GetProperty("renamed").EnumerateArray().Select(static id => id.GetString()));
        var after = await SunoWorkspaceApi.OneAsync(client, "w-1");
        Assert.Equal("After", after.GetProperty("name").GetString());
        Assert.Equal("Kept", after.GetProperty("description").GetString());
        Assert.Equal(before.GetProperty("firstSeen").GetString(), after.GetProperty("firstSeen").GetString());
        Assert.NotEqual(before.GetProperty("lastSeen").GetString(), after.GetProperty("lastSeen").GetString());
        Assert.Equal("available", after.GetProperty("state").GetString());

        // The Song is untouched: same title, revision, updated time, and association, and shows the new name.
        Assert.Equal(songRow, TestDatabase.Scalar(factory.DataPath, "SELECT title || '|' || revision || '|' || updated_utc || '|' || suno_workspace_id FROM songs;"));
        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal(associated.GetProperty("revision").GetInt32(), song.GetProperty("revision").GetInt32());
        Assert.Equal("""{"id":"w-1","name":"After","state":"available"}""", song.GetProperty("sunoWorkspace").GetRawText());

        // A blank name never overwrites a known one, and a workspace never named shows as (unnamed) in order.
        await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("w-1", "  "), SunoWorkspaceApi.Project("w-0"), SunoWorkspaceApi.Project("w-2", "(a"));
        Assert.Equal("After", (await SunoWorkspaceApi.OneAsync(client, "w-1")).GetProperty("name").GetString());
        Assert.Equal(["w-2", "w-0", "w-1"], (await SunoWorkspaceApi.ListAsync(client)).Select(static item => item.GetProperty("id").GetString()));
        Assert.Equal(string.Empty, (await SunoWorkspaceApi.OneAsync(client, "w-0")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChangingASongsTitleNeverRenamesItsWorkspace()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "Same as the Song"));
        var song = await SongApi.CreateAsync(client, "Same as the Song");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-1");
        var row = TestDatabase.Scalar(factory.DataPath, "SELECT name || '|' || description || '|' || last_seen_utc || '|' || raw_json FROM suno_workspaces;");

        var renamed = await SongApi.EditAsync(client, song.GetProperty("id").GetString()!, (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")))).GetProperty("revision").GetInt32(), """{"title":"A new title"}""");

        Assert.Equal("A new title", renamed.GetProperty("title").GetString());
        Assert.Equal("Same as the Song", renamed.GetProperty("sunoWorkspace").GetProperty("name").GetString());
        Assert.Equal(row, TestDatabase.Scalar(factory.DataPath, "SELECT name || '|' || description || '|' || last_seen_utc || '|' || raw_json FROM suno_workspaces;"));
    }

    [Fact]
    public async Task AWorkspaceWithTheSameNameAndAnotherIdIsAnotherRecord()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "Twin"), SunoWorkspaceApi.Project("w-2", "Twin"));
        await SongApi.CreateAsync(client, "In the second");
        var song = await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-2");

        var items = await SunoWorkspaceApi.ListAsync(client);
        Assert.Equal(["w-1", "w-2"], items.Select(static item => item.GetProperty("id").GetString()));
        Assert.Equal([0, 1], items.Select(static item => item.GetProperty("songCount").GetInt32()));
        Assert.Equal("w-2", song.GetProperty("sunoWorkspace").GetProperty("id").GetString());

        // Complement: the same ID twice in one report is one record, the last one kept.
        await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("w-3", "First"), SunoWorkspaceApi.Project("w-3", "Last"));
        Assert.Equal("Last", (await SunoWorkspaceApi.OneAsync(client, "w-3")).GetProperty("name").GetString());
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_workspaces WHERE suno_id = 'w-3';"));
    }

    [Fact]
    public async Task AnIncompleteListNeverMarksAnythingUnavailable()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "One"), SunoWorkspaceApi.Project("w-2", "Two"));

        // Neither the one left out nor the one named trashed changes.
        var answer = await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("w-2", "Two", trashed: true));

        Assert.Empty(answer.GetProperty("becameUnavailable").EnumerateArray());
        Assert.All(await SunoWorkspaceApi.ListAsync(client), static item => Assert.Equal("available", item.GetProperty("state").GetString()));

        // An Unavailable one stays so too until a complete list shows it.
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-2", "Two"));
        await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("w-1", "One"));
        Assert.Equal("unavailable", (await SunoWorkspaceApi.OneAsync(client, "w-1")).GetProperty("state").GetString());

        // A workspace first seen in an incomplete list is Available, and one first seen trashed is not.
        await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("w-new"), SunoWorkspaceApi.Project("w-gone", trashed: true));
        Assert.Equal("available", (await SunoWorkspaceApi.OneAsync(client, "w-new")).GetProperty("state").GetString());
        Assert.Equal("unavailable", (await SunoWorkspaceApi.OneAsync(client, "w-gone")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AWorkspaceMissingFromACompleteListBecomesUnavailableLosingNothingAndComesBack()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "Going"), SunoWorkspaceApi.Project("w-2", "Staying"), SunoWorkspaceApi.Project("w-3", "Trashed"));
        await SongApi.CreateAsync(client, "In the first");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("workspace-clip"));
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-1");
        await SongApi.CreateAsync(client, "In the third");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-2", "w-3");
        var counts = Counts(factory);
        var songs = TestDatabase.Rows(factory.DataPath, "SELECT id || '|' || revision || '|' || updated_utc || '|' || suno_workspace_id FROM songs ORDER BY id;");

        var answer = await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-2", "Staying"), SunoWorkspaceApi.Project("w-3", "Trashed", trashed: true));

        Assert.Equal(["w-3", "w-1"], answer.GetProperty("becameUnavailable").EnumerateArray().Select(static id => id.GetString()));
        Assert.Equal("unavailable", (await SunoWorkspaceApi.OneAsync(client, "w-1")).GetProperty("state").GetString());
        Assert.Equal("unavailable", (await SunoWorkspaceApi.OneAsync(client, "w-3")).GetProperty("state").GetString());
        Assert.Equal("available", (await SunoWorkspaceApi.OneAsync(client, "w-2")).GetProperty("state").GetString());

        // Nothing is detached or deleted: the same Songs, Versions, and Generations, the Songs as they
        // were, and the Song shows its workspace as unavailable.
        Assert.Equal(counts, Counts(factory));
        Assert.Equal(songs, TestDatabase.Rows(factory.DataPath, "SELECT id || '|' || revision || '|' || updated_utc || '|' || suno_workspace_id FROM songs ORDER BY id;"));
        Assert.Equal(1, (await SunoWorkspaceApi.OneAsync(client, "w-1")).GetProperty("songCount").GetInt32());
        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal("""{"id":"w-1","name":"Going","state":"unavailable"}""", song.GetProperty("sunoWorkspace").GetRawText());

        // Listed again (and no longer trashed), each is Available by itself.
        var back = await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "Going"), SunoWorkspaceApi.Project("w-2", "Staying"), SunoWorkspaceApi.Project("w-3", "Trashed", trashed: false));
        Assert.Equal(["w-1", "w-3"], back.GetProperty("becameAvailable").EnumerateArray().Select(static id => id.GetString()));
        Assert.All(await SunoWorkspaceApi.ListAsync(client), static item => Assert.Equal("available", item.GetProperty("state").GetString()));
        Assert.Equal(counts, Counts(factory));
    }

    [Theory]
    [InlineData("""{"complete":true,"workspaces":[{"id":"w-1","name":"One"},{"name":"No ID"}]}""", "workspaces[1].id")]
    [InlineData("""{"complete":true,"workspaces":[{"id":"","name":"Blank"}]}""", "workspaces[0].id")]
    [InlineData("""{"complete":true,"workspaces":[{"id":5}]}""", "workspaces[0].id")]
    [InlineData("""{"complete":true,"workspaces":["w-1"]}""", "workspaces[0]")]
    [InlineData("""{"complete":true,"workspaces":{"id":"w-1"}}""", "workspaces")]
    [InlineData("""{"complete":"yes","workspaces":[{"id":"w-1"}]}""", "complete")]
    [InlineData("""{"workspaces":[{"id":"w-1"}]}""", "complete")]
    public async Task AReportThatCannotBeReadStoresNothing(string body, string field)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-0", "Known"));
        var before = TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || name || '|' || state || '|' || last_seen_utc FROM suno_workspaces;");

        using var response = await SunoWorkspaceApi.SendReportAsync(client, token, body);

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.GetRawText());
        Assert.Equal(before, TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || name || '|' || state || '|' || last_seen_utc FROM suno_workspaces;"));
    }

    [Fact]
    public async Task ASongIsAssociatedByWorkspaceIdUnderItsRevision()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "One"), SunoWorkspaceApi.Project("w-2", "Two"));
        var created = await SongApi.CreateAsync(client, "Associated");
        Assert.Equal(JsonValueKind.Null, created.GetProperty("sunoWorkspace").ValueKind);

        var song = await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-1");
        Assert.Equal(created.GetProperty("revision").GetInt32() + 1, song.GetProperty("revision").GetInt32());
        Assert.Equal("""{"id":"w-1","name":"One","state":"available"}""", song.GetProperty("sunoWorkspace").GetRawText());

        // The Version's effectiveInputs reports it, for Generate on Suno; its inputs never hold it.
        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        Assert.Equal("""{"id":"w-1","name":"One","state":"available"}""", version.GetProperty("effectiveInputs").GetProperty("workspace").GetRawText());
        Assert.False(version.GetProperty("inputs").TryGetProperty("workspace", out _));

        // Another one; omitted leaves it; the same again stores nothing; null clears it.
        Assert.Equal("w-2", (await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-2")).GetProperty("sunoWorkspace").GetProperty("id").GetString());
        var concept = await SongApi.EditAsync(client, "n8-1", song.GetProperty("revision").GetInt32() + 1, """{"concept":"Kept"}""");
        Assert.Equal("w-2", concept.GetProperty("sunoWorkspace").GetProperty("id").GetString());
        var same = await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-2");
        Assert.Equal(concept.GetProperty("revision").GetInt32(), same.GetProperty("revision").GetInt32());
        var cleared = await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", null);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("sunoWorkspace").ValueKind);
        var bare = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1", UriKind.Relative)));
        Assert.False(bare.GetProperty("effectiveInputs").TryGetProperty("workspace", out _));

        // A stale revision changes nothing.
        using var stale = await SongApi.PatchAsync(client, "n8-1", SongApi.Quoted(1), """{"sunoWorkspaceId":"w-1"}""");
        await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        Assert.Equal(string.Empty, TestDatabase.Scalar(factory.DataPath, "SELECT ifnull(suno_workspace_id, '') FROM songs;"));
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("\"ONE\"")]
    [InlineData("\"One\"")]
    [InlineData("5")]
    public async Task AnUnknownWorkspaceOrOneNamedByItsNameIsRefused(string sent)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("one", "One"));
        await SongApi.CreateAsync(client, "Unassociated");

        using var response = await SongApi.PatchAsync(client, "n8-1", SongApi.Quoted(1), $$"""{"sunoWorkspaceId":{{sent}},"concept":"Not stored"}""");

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("sunoWorkspaceId", out _));
        Assert.Equal("1||", TestDatabase.Scalar(factory.DataPath, "SELECT revision || '|' || ifnull(concept, '') || '|' || ifnull(suno_workspace_id, '') FROM songs;"));
    }

    [Fact]
    public async Task OnlyAnAvailableWorkspaceCanBeChosenButASongKeepsItsUnavailableOne()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "One"), SunoWorkspaceApi.Project("w-2", "Two"));
        await SongApi.CreateAsync(client, "Keeps it");
        await SongApi.CreateAsync(client, "Wants it");
        var kept = await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-1");
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-2", "Two"));

        using (var refused = await SunoWorkspaceApi.AssociateAsync(client, "n8-2", "w-1"))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("sunoWorkspaceId", out _));
        }

        // Resending the Unavailable one a Song already has is accepted as unchanged.
        var resent = await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-1");
        Assert.Equal(kept.GetProperty("revision").GetInt32(), resent.GetProperty("revision").GetInt32());
        Assert.Equal("unavailable", resent.GetProperty("sunoWorkspace").GetProperty("state").GetString());

        // A Song's other edits go on while its workspace is Unavailable.
        var edited = await SongApi.EditAsync(client, "n8-1", resent.GetProperty("revision").GetInt32(), """{"concept":"Still editable","sunoWorkspaceId":"w-1"}""");
        Assert.Equal("w-1", edited.GetProperty("sunoWorkspace").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ChangingASongsWorkspaceNeedsSongsWrite()
    {
        using var factory = SongApi.Host();
        using var session = await SessionApi.SignedInClientAsync(factory);
        using var client = factory.CreateClient();
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(session, token, complete: true, SunoWorkspaceApi.Project("w-1", "One"));
        await SongApi.CreateAsync(session, "By token");
        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.SongsWrite)]);
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);

        using (var refused = await PatchWithTokenAsync(client, others))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
        }

        using var allowed = await PatchWithTokenAsync(client, writer);
        Assert.True(allowed.StatusCode == HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
        Assert.Equal("w-1", TestDatabase.Scalar(factory.DataPath, "SELECT suno_workspace_id FROM songs;"));

        static async Task<HttpResponseMessage> PatchWithTokenAsync(HttpClient client, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, SongApi.Song("n8-1"))
            {
                Content = new StringContent("""{"sunoWorkspaceId":"w-1"}""", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(1)));
            return await client.SendAsync(request);
        }
    }

    [Fact]
    public async Task ADeletedSongComesBackInItsWorkspace()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-1", "One"));
        await SongApi.CreateAsync(client, "Retained");
        var song = await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "w-1");

        using (var deleted = await SongApi.SendJsonAsync(client, HttpMethod.Delete, SongApi.Song("n8-1"), """{"confirmTitle":"Retained"}"""))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, deleted.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Delete, SongApi.Song("n8-1")) { Content = new StringContent("""{"confirmTitle":"Retained"}""", System.Text.Encoding.UTF8, "application/json") })
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(song.GetProperty("revision").GetInt32())));
            using var deleted = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        // The workspace stays, now with no Songs; the Song restored is in it again.
        Assert.Equal(0, (await SunoWorkspaceApi.OneAsync(client, "w-1")).GetProperty("songCount").GetInt32());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.IsType<n8Tracks.Application.Retention.DeletedItemRestoreOutcome.Restored>(
                await scope.ServiceProvider.GetRequiredService<n8Tracks.Application.Retention.DeletedItemsService>().RestoreAsync("n8-1", CancellationToken.None));
        }

        var restored = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")));
        Assert.Equal("w-1", restored.GetProperty("sunoWorkspace").GetProperty("id").GetString());
        Assert.Equal(1, (await SunoWorkspaceApi.OneAsync(client, "w-1")).GetProperty("songCount").GetInt32());
    }

    [Fact]
    public async Task SongsMoveInBulkByIdShortcodeOrAllEachAtItsNextRevision()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (ids, _) = await ThreeSongsInAsync(factory, client);

        // Named by ID and by shortcode, the same Song twice: it moves once.
        using (var some = await SunoWorkspaceApi.MoveAsync(client, "w-from", $$"""{"songIds":["{{ids[0]}}","n8-1","N8-1"],"targetWorkspaceId":"w-to"}"""))
        {
            Assert.True(some.StatusCode == HttpStatusCode.OK, await some.Content.ReadAsStringAsync());
            var moved = await SetupApi.JsonAsync(some);
            Assert.Equal(1, moved.GetProperty("moved").GetInt32());
            Assert.Equal("w-from", moved.GetProperty("from").GetProperty("id").GetString());
            Assert.Equal("w-to", moved.GetProperty("to").GetProperty("id").GetString());
        }

        Assert.Equal(["3|w-to", "2|w-from", "1|"], TestDatabase.Rows(factory.DataPath, "SELECT revision || '|' || ifnull(suno_workspace_id, '') FROM songs ORDER BY shortcode_number;"));

        using (var all = await SunoWorkspaceApi.MoveAsync(client, "w-from", """{"all":true,"expectedCount":1,"targetWorkspaceId":"w-to"}"""))
        {
            Assert.Equal(1, (await SetupApi.JsonAsync(all)).GetProperty("moved").GetInt32());
        }

        Assert.Equal(["3|w-to", "3|w-to", "1|"], TestDatabase.Rows(factory.DataPath, "SELECT revision || '|' || ifnull(suno_workspace_id, '') FROM songs ORDER BY shortcode_number;"));
        Assert.Equal([0, 0, 2], (await SunoWorkspaceApi.ListAsync(client)).Select(static item => item.GetProperty("songCount").GetInt32()));

        // Nothing left to move with all is a move of none.
        using var none = await SunoWorkspaceApi.MoveAsync(client, "w-from", """{"all":true,"expectedCount":0,"targetWorkspaceId":"w-to"}""");
        Assert.Equal(0, (await SetupApi.JsonAsync(none)).GetProperty("moved").GetInt32());
    }

    /// <summary>
    /// #346: an "all" move carries the count the user confirmed. A Song that joined the workspace while
    /// the confirmation was open (a sync, an import, another tab) refuses the move, and nothing moves;
    /// so does one that left. Sent again with the new count, it moves them all.
    /// </summary>
    [Fact]
    public async Task AnAllMoveIsRefusedMovingNothingWhenTheWorkspaceNoLongerHoldsTheCountConfirmed()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ThreeSongsInAsync(factory, client);
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-3", "w-from");
        var before = TestDatabase.Rows(factory.DataPath, "SELECT revision || '|' || updated_utc || '|' || ifnull(suno_workspace_id, '') FROM songs ORDER BY shortcode_number;");

        foreach (var confirmed in new[] { 2, 4 })
        {
            using var response = await SunoWorkspaceApi.MoveAsync(client, "w-from", $$"""{"all":true,"expectedCount":{{confirmed}},"targetWorkspaceId":"w-to"}""");

            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "song_count_changed");
            Assert.Equal(3, problem.GetProperty("count").GetInt32());
            Assert.Equal(confirmed, problem.GetProperty("expected").GetInt32());
            Assert.Equal(before, TestDatabase.Rows(factory.DataPath, "SELECT revision || '|' || updated_utc || '|' || ifnull(suno_workspace_id, '') FROM songs ORDER BY shortcode_number;"));
        }

        using (var moved = await SunoWorkspaceApi.MoveAsync(client, "w-from", """{"all":true,"expectedCount":3,"targetWorkspaceId":"w-to"}"""))
        {
            Assert.Equal(3, (await SetupApi.JsonAsync(moved)).GetProperty("moved").GetInt32());
        }

        Assert.Equal("3", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs WHERE suno_workspace_id = 'w-to';"));
    }

    [Fact]
    public async Task ABulkMoveNamingOneSongNotInTheWorkspaceChangesNoSong()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (ids, outside) = await ThreeSongsInAsync(factory, client);
        var before = TestDatabase.Rows(factory.DataPath, "SELECT revision || '|' || updated_utc || '|' || ifnull(suno_workspace_id, '') FROM songs ORDER BY shortcode_number;");

        foreach (var foreign in new[] { outside, "n8-99", "n8-1-v1", "not-a-song" })
        {
            using var response = await SunoWorkspaceApi.MoveAsync(client, "w-from", $$"""{"songIds":["{{ids[0]}}","{{ids[1]}}","{{foreign}}"],"targetWorkspaceId":"w-to"}""");

            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "song_not_in_workspace");
            Assert.Equal([foreign], problem.GetProperty("songs").EnumerateArray().Select(static song => song.GetString()));
            Assert.Equal(before, TestDatabase.Rows(factory.DataPath, "SELECT revision || '|' || updated_utc || '|' || ifnull(suno_workspace_id, '') FROM songs ORDER BY shortcode_number;"));
        }
    }

    [Theory]
    [InlineData("""{"songIds":["n8-1"],"targetWorkspaceId":"w-from"}""", "targetWorkspaceId")]
    [InlineData("""{"songIds":["n8-1"],"targetWorkspaceId":"w-gone"}""", "targetWorkspaceId")]
    [InlineData("""{"songIds":["n8-1"],"targetWorkspaceId":"w-unknown"}""", "targetWorkspaceId")]
    [InlineData("""{"songIds":["n8-1"]}""", "targetWorkspaceId")]
    [InlineData("""{"songIds":["n8-1"],"all":true,"targetWorkspaceId":"w-to"}""", "songIds")]
    [InlineData("""{"targetWorkspaceId":"w-to"}""", "songIds")]
    [InlineData("""{"songIds":[],"targetWorkspaceId":"w-to"}""", "songIds")]
    [InlineData("""{"songIds":[1],"targetWorkspaceId":"w-to"}""", "songIds")]
    [InlineData("""{"all":"yes","targetWorkspaceId":"w-to"}""", "all")]
    [InlineData("""{"all":true,"targetWorkspaceId":"w-to"}""", "expectedCount")]
    [InlineData("""{"all":true,"expectedCount":-1,"targetWorkspaceId":"w-to"}""", "expectedCount")]
    [InlineData("""{"all":true,"expectedCount":"2","targetWorkspaceId":"w-to"}""", "expectedCount")]
    public async Task AWrongMoveIsRefusedMovingNothing(string body, string field)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ThreeSongsInAsync(factory, client);
        var before = TestDatabase.Rows(factory.DataPath, "SELECT revision || '|' || ifnull(suno_workspace_id, '') FROM songs ORDER BY shortcode_number;");

        using var response = await SunoWorkspaceApi.MoveAsync(client, "w-from", body);

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.GetRawText());
        Assert.Equal(before, TestDatabase.Rows(factory.DataPath, "SELECT revision || '|' || ifnull(suno_workspace_id, '') FROM songs ORDER BY shortcode_number;"));
    }

    [Fact]
    public async Task SongsLeaveAnUnavailableWorkspaceAndAnUnknownOneIsNotFound()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ThreeSongsInAsync(factory, client);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-to", "To"));

        using (var moved = await SunoWorkspaceApi.MoveAsync(client, "w-from", """{"all":true,"expectedCount":2,"targetWorkspaceId":"w-to"}"""))
        {
            Assert.Equal(2, (await SetupApi.JsonAsync(moved)).GetProperty("moved").GetInt32());
        }

        using var unknown = await SunoWorkspaceApi.MoveAsync(client, "w-unknown", """{"all":true,"expectedCount":0,"targetWorkspaceId":"w-to"}""");
        await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task ABulkMoveIsSessionOnly()
    {
        using var factory = SongApi.Host();
        using var session = await SessionApi.SignedInClientAsync(factory);
        await ThreeSongsInAsync(factory, session);
        using var client = factory.CreateClient();
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        using var request = new HttpRequestMessage(HttpMethod.Post, SunoWorkspaceApi.MoveSongs("w-from"))
        {
            Content = new StringContent("""{"all":true,"expectedCount":2,"targetWorkspaceId":"w-to"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);

        await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, "session_required");
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs WHERE suno_workspace_id = 'w-from';"));
    }

    [Fact]
    public async Task TheSongListShowsAWorkspacesSongsAndFollowsABulkMove()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ThreeSongsInAsync(factory, client);

        // #151: the workspace page lists its Songs through the Songs list's workspace filter.
        Assert.Equal(["n8-1", "n8-2"], SongApi.Shortcodes(await SongApi.ListAsync(client, "workspace=w-from&sort=title")));
        Assert.Empty(SongApi.Shortcodes(await SongApi.ListAsync(client, "workspace=w-to")));
        Assert.Equal(["n8-2"], SongApi.Shortcodes(await SongApi.ListAsync(client, "workspace=w-from&q=Second")));
        var page = await SongApi.ListAsync(client, "workspace=w-from&pageSize=1");
        Assert.Equal(2, page.GetProperty("total").GetInt32());

        using (var moved = await SunoWorkspaceApi.MoveAsync(client, "w-from", """{"all":true,"expectedCount":2,"targetWorkspaceId":"w-to"}"""))
        {
            Assert.Equal(2, (await SetupApi.JsonAsync(moved)).GetProperty("moved").GetInt32());
        }

        Assert.Empty(SongApi.Shortcodes(await SongApi.ListAsync(client, "workspace=w-from")));
        Assert.Equal(["n8-1", "n8-2"], SongApi.Shortcodes(await SongApi.ListAsync(client, "workspace=w-to&sort=title")));

        // An Unavailable workspace's Songs are listed too: that is how they are found to move them off it.
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-3", "w-to");
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-from", "From"));
        Assert.Equal(["n8-1", "n8-2", "n8-3"], SongApi.Shortcodes(await SongApi.ListAsync(client, "workspace=w-to&sort=title")));
    }

    [Theory]
    [InlineData("workspace=w-unknown")]
    [InlineData("workspace=")]
    [InlineData("workspace=%20")]
    [InlineData("workspace=w-from&workspace=w-to")]
    public async Task AnUnknownBlankOrRepeatedWorkspaceFilterIsRefused(string query)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ThreeSongsInAsync(factory, client);

        using var response = await client.GetAsync(new Uri($"/api/v1/songs?{query}", UriKind.Relative));

        await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, ApiProblem.InvalidRequestCode);
    }

    [Fact]
    public async Task MoreThanFiveThousandSongsAreRefusedWithTooManySongs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ThreeSongsInAsync(factory, client);

        // Named: 5,001 distinct IDs are refused before any is looked up.
        var named = string.Join(",", Enumerable.Range(1, 5_001).Select(static n => $"\"n8-{n.ToString(CultureInfo.InvariantCulture)}\""));
        using (var response = await SunoWorkspaceApi.MoveAsync(client, "w-from", $$"""{"songIds":[{{named}}],"targetWorkspaceId":"w-to"}"""))
        {
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "too_many_songs");
            Assert.Equal(5_000, problem.GetProperty("limit").GetInt32());
        }

        // All of a workspace holding 5,001 Songs (copies of the first, written straight in).
        var columns = TestDatabase.Rows(factory.DataPath, "SELECT name FROM pragma_table_info('songs') ORDER BY cid;");
        var values = columns.Select(static column => column switch
        {
            "id" => "printf('%08X-0000-4000-8000-%012X', n.i, n.i)",
            "shortcode_number" => "1000 + n.i",
            _ => "s." + column,
        });
        TestDatabase.Execute(
            factory.DataPath,
            $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 4999) INSERT INTO songs ({string.Join(", ", columns)}) SELECT {string.Join(", ", values)} FROM songs AS s, n WHERE s.shortcode_number = 1;");
        Assert.Equal("5001", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs WHERE suno_workspace_id = 'w-from';"));

        using (var all = await SunoWorkspaceApi.MoveAsync(client, "w-from", """{"all":true,"expectedCount":5001,"targetWorkspaceId":"w-to"}"""))
        {
            var problem = await SetupApi.ProblemAsync(all, HttpStatusCode.UnprocessableEntity, "too_many_songs");
            Assert.Equal(5_001, problem.GetProperty("count").GetInt32());
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs WHERE suno_workspace_id = 'w-to';"));
    }

    /// <summary>The rows each catalog table holds: what a workspace becoming unavailable must not change.</summary>
    private static string Counts(N8TracksApiFactory factory) => TestDatabase.Scalar(
        factory.DataPath,
        "SELECT (SELECT count(*) FROM songs) || '|' || (SELECT count(*) FROM versions) || '|' || (SELECT count(*) FROM generations) || '|' || (SELECT count(*) FROM provider_records);");

    private static string? Name(JsonElement item) => item.GetProperty("name").GetString() is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name) ? name : "(unnamed)";

    /// <summary>
    /// Workspaces <c>w-from</c> and <c>w-to</c> (available) and <c>w-gone</c> (unavailable), with Songs
    /// n8-1 and n8-2 in <c>w-from</c> and n8-3 in none: the IDs of the two, and of the third.
    /// </summary>
    private static async Task<(string[] Ids, string Outside)> ThreeSongsInAsync(N8TracksApiFactory factory, HttpClient client)
    {
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("w-from", "From"), SunoWorkspaceApi.Project("w-to", "To"), SunoWorkspaceApi.Project("w-gone", "Gone", trashed: true));
        var ids = new List<string>();
        foreach (var title in new[] { "First", "Second" })
        {
            ids.Add((await SongApi.CreateAsync(client, title)).GetProperty("id").GetString()!);
            await SunoWorkspaceApi.AssociatedAsync(client, ids[^1], "w-from");
        }

        var outside = (await SongApi.CreateAsync(client, "Third")).GetProperty("id").GetString()!;
        return ([.. ids], outside);
    }
}
