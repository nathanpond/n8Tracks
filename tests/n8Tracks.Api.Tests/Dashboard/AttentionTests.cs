using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Media;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Api.Tests.Suno;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;
using n8Tracks.Application.Suno;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Api.Tests.Dashboard;

/// <summary>
/// #229: what needs attention, on <c>GET /api/v1/attention</c> and in the dashboard: Unmatched Files,
/// Suno reviews, and Suno problems. Each condition is seeded and so is its absence; each count is
/// checked against the total of the page it links to; a failure is listed until a later success, its
/// dismissal, or 14 days; and a bearer token gets counts only.
/// </summary>
public sealed class AttentionTests
{
    private static readonly Uri Attention = new("/api/v1/attention", UriKind.Relative);
    private static readonly Uri Dashboard = new("/api/v1/dashboard", UriKind.Relative);
    private static readonly Uri Dismissals = new("/api/v1/attention/dismissals", UriKind.Relative);

    private const string ChangedId = "attention-changed";
    private const string ConflictId = "attention-conflict";
    private const string NewId = "attention-new";

    [Fact]
    public async Task UnmatchedFilesCountsWhatTheUnmatchedFilesPageListsAndSaysWhenTheMediaFolderIsUnavailable()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // Nothing to place: the section is there, with nothing in it.
        Assert.Equal((0, false), Unmatched(await AttentionAsync(client)));

        await SongApi.CreateAsync(client, "Placed");
        MediaApi.Place(factory, "a/one.mp3", "mp3");
        MediaApi.Place(factory, "b/two.mp3", "mp3");
        await MediaApi.ScanAsync(client);
        Assert.Equal((2, false), Unmatched(await AttentionAsync(client)));
        Assert.Equal(2, await UnmatchedPageTotalAsync(client));

        // Demo step 2: associate one file; the count is one, as the page's.
        var file = (await MediaApi.ListAsync(client, "?association=none")).Items[0];
        using (var associated = await SendWithRevisionAsync(client, HttpMethod.Put, $"/api/v1/audio-files/{file.GetProperty("id").GetGuid()}/association", 1, """{"song":"n8-1"}"""))
        {
            Assert.True(associated.StatusCode == HttpStatusCode.OK, await associated.Content.ReadAsStringAsync());
        }

        Assert.Equal((1, false), Unmatched(await AttentionAsync(client)));
        Assert.Equal(1, await UnmatchedPageTotalAsync(client));

        // The folder becomes unavailable: the section says so (the file still counts, as the page lists it).
        await WithServiceAsync<MediaAvailability>(factory, static media => media.RecordAsync(false, default));
        Assert.Equal((1, true), Unmatched(await AttentionAsync(client)));

        // And it is the dashboard's section too.
        Assert.Equal((1, true), Unmatched(await DashboardAsync(client)));
    }

    [Fact]
    public async Task SunoReviewsListsTheExportWaitingWithItsRecordsAndItsChangedAndConflictCounts()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);

        // Nothing waiting yet.
        Assert.Equal(0, Data(await AttentionAsync(client), "sunoReviews").GetProperty("count").GetInt32());

        var (exportId, _) = await ProposalApi.ExportAsync(client, token, Changed(), Conflict(), ProposalApi.Clip(NewId, null, ProposalApi.At, 0));
        var reviews = Data(await AttentionAsync(client), "sunoReviews");
        Assert.Equal(1, reviews.GetProperty("count").GetInt32());
        var review = Assert.Single(reviews.GetProperty("exports").EnumerateArray());
        Assert.Equal(exportId, review.GetProperty("exportId").GetGuid());

        // Each number is the review page's own count.
        var export = await SunoExportApi.GetAsync(client, null, exportId);
        Assert.Equal(SunoExportApi.Count(export, "total"), review.GetProperty("recordCount").GetInt32());
        Assert.Equal((3, 1, 1), (review.GetProperty("recordCount").GetInt32(), review.GetProperty("changedCount").GetInt32(), review.GetProperty("conflictCount").GetInt32()));
        Assert.Equal(SunoExportApi.Count(export, "changed"), review.GetProperty("changedCount").GetInt32());
        Assert.Equal(SunoExportApi.Count(export, "conflict"), review.GetProperty("conflictCount").GetInt32());
        Assert.Equal(export.GetProperty("readyAt").GetDateTime(), review.GetProperty("arrivedAt").GetDateTime());

        // Committed (its Conflict resolved as the user chose): no longer waiting.
        await ImportCommitApi.CommitAsync(client, exportId);
        Assert.Equal(0, Data(await AttentionAsync(client), "sunoReviews").GetProperty("count").GetInt32());

        // Discarded: not listed.
        var (discarded, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("attention-other", null, ProposalApi.At, 0));
        Assert.Equal(1, Data(await AttentionAsync(client), "sunoReviews").GetProperty("count").GetInt32());
        await DiscardAsync(client, token, discarded, null, HttpStatusCode.OK);
        Assert.Equal(0, Data(await AttentionAsync(client), "sunoReviews").GetProperty("count").GetInt32());

        // Expired: not listed.
        var (expiring, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("attention-late", null, ProposalApi.At, 0));
        Assert.Equal(1, Data(await AttentionAsync(client), "sunoReviews").GetProperty("count").GetInt32());
        TestDatabase.Execute(factory.DataPath, $"UPDATE suno_exports SET ready_utc = '2026-01-01T00:00:00.000Z' WHERE upper(id) = '{Upper(expiring)}';");
        var expired = await SunoExportApi.WithServiceAsync(factory, static service => service.ExpireAsync());
        Assert.Equal(1, expired.Expired);
        Assert.Equal(0, Data(await AttentionAsync(client), "sunoReviews").GetProperty("count").GetInt32());
        client.Dispose();
    }

    [Fact]
    public async Task AFailedSyncIsListedWithItsStepUntilALaterExportBecomesReadyAndACancelIsNot()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        // A cancel and a replacement are not failures.
        var cancelled = await SunoExportApi.CreateAsync(client, token);
        await DiscardAsync(client, token, cancelled, """{"reason":"cancelled"}""", HttpStatusCode.OK);
        var replaced = (await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, [ProposalApi.Clip("attention-one", null, ProposalApi.At, 0)]))).Id;
        await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, [ProposalApi.Clip("attention-two", null, ProposalApi.At, 0)]));
        Assert.Equal("replaced", EndReason(factory, replaced));
        Assert.Equal("cancelled", EndReason(factory, cancelled));
        Assert.Empty(Problems(await AttentionAsync(client)));

        // The extension stops at a step: a failed sync, named with its step, leading to the Suno page.
        var failed = await SunoExportApi.CreateAsync(client, token);
        await DiscardAsync(client, token, failed, """{"reason":"failed","step":"Read the library"}""", HttpStatusCode.OK);
        var problem = Assert.Single(Problems(await AttentionAsync(client)));
        Assert.Equal("failedSync", problem.GetProperty("kind").GetString());
        Assert.Equal(failed.ToString(), problem.GetProperty("subject").GetString());
        Assert.Equal("failed", problem.GetProperty("reason").GetString());
        Assert.Equal("Read the library", problem.GetProperty("step").GetString());
        Assert.True(problem.GetProperty("dismissible").GetBoolean());
        Assert.Equal(JsonValueKind.String, problem.GetProperty("occurredAt").ValueKind);

        // A later export that becomes ready clears it.
        await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, [ProposalApi.Clip("attention-three", null, ProposalApi.At, 0)]));
        Assert.Empty(Problems(await AttentionAsync(client)));

        // Classification that never finished fails on the server, at the step "classifying".
        var stuck = await SunoExportApi.CreateAsync(client, token);
        await SunoExportApi.PartAsync(client, token, stuck, SunoExportApi.Part(1, [ProposalApi.Clip("attention-four", null, ProposalApi.At, 0)]));
        TestDatabase.Execute(factory.DataPath, $"UPDATE suno_exports SET state = 'classifying', completed_utc = '2026-01-01T00:00:00.000Z' WHERE upper(id) = '{Upper(stuck)}';");
        Assert.Equal(1, (await SunoExportApi.WithServiceAsync(factory, static service => service.ExpireAsync())).Failed);
        var classifying = Assert.Single(Problems(await AttentionAsync(client)));
        Assert.Equal((stuck.ToString(), "classifying"), (classifying.GetProperty("subject").GetString(), classifying.GetProperty("step").GetString()));
    }

    [Fact]
    public async Task TheDiscardTakesAnOptionalReasonAndRefusesAnyOther()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var id = await SunoExportApi.CreateAsync(client, token);

        foreach (var (body, field) in new[]
        {
            ("""{"reason":"replaced"}""", "reason"),
            ("""{"reason":42}""", "reason"),
            ("""{"reason":"cancelled","step":"Read the library"}""", "step"),
            ("""{"reason":"failed","step":""}""", "step"),
            ($$"""{"reason":"failed","step":"{{new string('x', 201)}}"}""", "step"),
        })
        {
            using var refused = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/discard"), token, body);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        foreach (var body in new[] { "[]", "not json" })
        {
            using var refused = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/discard"), token, body);
            await SetupApi.ProblemAsync(refused, HttpStatusCode.BadRequest, "invalid_request");
        }

        // Nothing was discarded by a refusal.
        Assert.Equal("receiving", (await SunoExportApi.GetAsync(client, token, id)).GetProperty("state").GetString());

        // No body: discarded, without a reason; and an ended export keeps what it ended with.
        await DiscardAsync(client, token, id, null, HttpStatusCode.OK);
        Assert.Null(EndReason(factory, id));
        await DiscardAsync(client, token, id, """{"reason":"failed","step":"Read the library"}""", HttpStatusCode.OK);
        Assert.Null(EndReason(factory, id));
    }

    [Fact]
    public async Task AFailedGenerateRequestIsListedWithItsReasonAndVersionUntilALaterOneIsDone()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Generated");
        var versionId = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);

        // A request still going is not a problem.
        var first = await CreateRequestAsync(client, versionId);
        await ClaimAsync(client, extension, first);
        Assert.Empty(Problems(await AttentionAsync(client)));

        await ReportAsync(client, extension, first, """{"state":"stopped","step":"fill-lyrics","message":"The lyrics field was not found."}""");
        var problem = Assert.Single(Problems(await AttentionAsync(client)));
        Assert.Equal("failedGenerate", problem.GetProperty("kind").GetString());
        Assert.Equal(first.ToString(), problem.GetProperty("subject").GetString());
        Assert.Equal(("stopped", "fill-lyrics", "The lyrics field was not found."), (problem.GetProperty("reason").GetString(), problem.GetProperty("step").GetString(), problem.GetProperty("message").GetString()));
        Assert.Equal(("n8-1-v1", "n8-1", "1"), (problem.GetProperty("versionShortcode").GetString(), problem.GetProperty("songShortcode").GetString(), problem.GetProperty("versionNumber").GetString()));

        // A later request that ends done clears it.
        var second = await CreateRequestAsync(client, versionId);
        await ClaimAsync(client, extension, second);
        await ReportAsync(client, extension, second, """{"state":"waiting","step":"review and create"}""");
        Assert.Single(Problems(await AttentionAsync(client)));
        await ReportAsync(client, extension, second, """{"state":"done"}""");
        Assert.Empty(Problems(await AttentionAsync(client)));
    }

    [Fact]
    public async Task ARequestDueToStopCountsAsFailedWithoutBeingWrittenAndAFailureDropsOffAfterFourteenDays()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = (await SongApi.CreateAsync(client, "Unclaimed")).GetProperty("currentVersion").GetProperty("id").GetGuid();
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        var request = await CreateRequestAsync(client, versionId);
        clock.Advance(TimeSpan.FromSeconds(15));
        var problem = Assert.Single(Problems(await AttentionAsync(client)));
        Assert.Equal(("failedGenerate", "stopped"), (problem.GetProperty("kind").GetString(), problem.GetProperty("reason").GetString()));

        // Reading it wrote nothing: the request is still stored pending.
        Assert.Equal("pending", TestDatabase.Scalar(factory.DataPath, $"SELECT state FROM suno_generation_requests WHERE upper(id) = '{Upper(request)}';"));

        // An hour later a sync fails too.
        clock.Advance(TimeSpan.FromHours(1));
        var failed = await SunoExportApi.CreateAsync(client, token);
        await DiscardAsync(client, token, failed, """{"reason":"failed","step":"Read the library"}""", HttpStatusCode.OK);
        Assert.Equal(2, Data(await AttentionAsync(client), "sunoProblems").GetProperty("count").GetInt32());

        // Fourteen days after each failed, it drops off: the request (failed when it fell due) first.
        clock.Advance(TimeSpan.FromDays(14) - TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        Assert.Equal(2, Data(await AttentionAsync(client), "sunoProblems").GetProperty("count").GetInt32());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["failedSync"], Problems(await AttentionAsync(client)).Select(static item => item.GetProperty("kind").GetString()));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(Problems(await AttentionAsync(client)));
    }

    [Fact]
    public async Task AnUnavailableWorkspaceWithSongsIsListedUntilItIsAvailableAgainAndCannotBeDismissed()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("ws-songs", "Demos"), SunoWorkspaceApi.Project("ws-empty", "Empty"), SunoWorkspaceApi.Project("ws-kept", "Kept"));
        await SongApi.CreateAsync(client, "In Demos");
        await SongApi.CreateAsync(client, "Also in Demos");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "ws-songs");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-2", "ws-songs");
        Assert.Empty(Problems(await AttentionAsync(client)));

        // Both leave Suno's list: only the one with Songs needs attention.
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("ws-kept", "Kept"));
        var problem = Assert.Single(Problems(await AttentionAsync(client)));
        Assert.Equal(("unavailableWorkspace", "ws-songs", "Demos"), (problem.GetProperty("kind").GetString(), problem.GetProperty("subject").GetString(), problem.GetProperty("workspaceName").GetString()));
        Assert.Equal((await SunoWorkspaceApi.OneAsync(client, "ws-songs")).GetProperty("songCount").GetInt32(), problem.GetProperty("songCount").GetInt32());
        Assert.Equal(2, problem.GetProperty("songCount").GetInt32());
        Assert.False(problem.GetProperty("dismissible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("occurredAt").ValueKind);

        // A standing state cannot be dismissed.
        using (var refused = await SongApi.SendJsonAsync(client, HttpMethod.Post, Dismissals, """{"kind":"unavailableWorkspace","subject":"ws-songs"}"""))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        // Available again: it drops out.
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("ws-songs", "Demos"), SunoWorkspaceApi.Project("ws-kept", "Kept"));
        Assert.Empty(Problems(await AttentionAsync(client)));
    }

    [Fact]
    public async Task DismissingAFailureRemovesItFromTheDashboardAndANewerFailureIsANewEntry()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var failed = await SunoExportApi.CreateAsync(client, token);
        await DiscardAsync(client, token, failed, """{"reason":"failed","step":"Read the library"}""", HttpStatusCode.OK);
        Assert.Single(Problems(await DashboardAsync(client)));

        await DismissAsync(client, "failedSync", failed);
        Assert.Empty(Problems(await DashboardAsync(client)));
        Assert.Empty(Problems(await AttentionAsync(client)));

        // Dismissing it again changes nothing.
        await DismissAsync(client, "failedSync", failed);
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM attention_dismissals;"));

        // A newer failure of that kind is a new entry.
        var again = await SunoExportApi.CreateAsync(client, token);
        await DiscardAsync(client, token, again, """{"reason":"failed","step":"Read the Trash"}""", HttpStatusCode.OK);
        var problem = Assert.Single(Problems(await AttentionAsync(client)));
        Assert.Equal((again.ToString(), "Read the Trash"), (problem.GetProperty("subject").GetString(), problem.GetProperty("step").GetString()));

        // Refusals: an unknown export or request, a kind or subject that is not one, a body that is not an object.
        using (var missing = await SongApi.SendJsonAsync(client, HttpMethod.Post, Dismissals, $$"""{"kind":"failedGenerate","subject":"{{Guid.CreateVersion7()}}"}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }

        foreach (var (body, field) in new[] { ("""{"kind":"other","subject":"0199c0de-0000-7000-8000-000000000001"}""", "kind"), ("""{"kind":"failedSync","subject":"n8-1"}""", "subject"), ("""{"kind":"failedSync"}""", "subject") })
        {
            using var invalid = await SongApi.SendJsonAsync(client, HttpMethod.Post, Dismissals, body);
            var problemBody = await SetupApi.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problemBody.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        using (var notObject = await SongApi.SendJsonAsync(client, HttpMethod.Post, Dismissals, "[]"))
        {
            await SetupApi.ProblemAsync(notObject, HttpStatusCode.BadRequest, "invalid_request");
        }

        // Session-only: a token with every scope is refused.
        var all = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Post, Dismissals, all))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Single(Problems(await AttentionAsync(client)));
    }

    [Fact]
    public async Task ATokenWithCatalogReadGetsTheSectionsAsCountsOnly()
    {
        using var factory = SongApi.Host();
        var (session, extension) = await ImportedAsync(factory);
        await ProposalApi.ExportAsync(session, extension, Changed());
        var failed = await SunoExportApi.CreateAsync(session, extension);
        await DiscardAsync(session, extension, failed, """{"reason":"failed","step":"Read the library"}""", HttpStatusCode.OK);

        using var client = factory.CreateClient();
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        foreach (var uri in new[] { Attention, Dashboard })
        {
            using var response = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, reader);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
            var answer = await SetupApi.JsonAsync(response);
            var reviews = Data(answer, "sunoReviews");
            Assert.Equal(1, reviews.GetProperty("count").GetInt32());
            Assert.False(reviews.TryGetProperty("exports", out _));
            var problems = Data(answer, "sunoProblems");
            Assert.Equal(1, problems.GetProperty("count").GetInt32());
            Assert.False(problems.TryGetProperty("problems", out _));
            Assert.Equal(0, Data(answer, "unmatchedFiles").GetProperty("count").GetInt32());
        }

        // The session sees the lists.
        Assert.Single(Data(await AttentionAsync(session), "sunoReviews").GetProperty("exports").EnumerateArray());

        // Without catalog.read: refused.
        var syncOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Attention, syncOnly))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        session.Dispose();
    }

    [Fact]
    public async Task AnAreaThatCannotBeReadIsAnsweredAsFailedAndTheOthersAreRead()
    {
        using var factory = FailingHost(workspaces: true, files: false, exports: false);
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var answer in new[] { await AttentionAsync(client), await DashboardAsync(client) })
        {
            Assert.Equal("section_failed", answer.GetProperty("sunoProblems").GetProperty("error").GetProperty("code").GetString());
            Assert.False(answer.GetProperty("sunoProblems").TryGetProperty("data", out _));
            Assert.Equal(0, Data(answer, "unmatchedFiles").GetProperty("count").GetInt32());
            Assert.Equal(0, Data(answer, "sunoReviews").GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public async Task EveryAreaFailingIsA500OnTheAttentionEndpointButNotOnTheDashboard()
    {
        using var factory = FailingHost(workspaces: true, files: true, exports: true);
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var response = await client.GetAsync(Attention))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("section_failed", (await SetupApi.JsonAsync(response)).GetProperty("code").GetString());
        }

        // The dashboard's catalog sections still answer.
        var dashboard = await DashboardAsync(client);
        Assert.True(dashboard.GetProperty("recentlyEdited").TryGetProperty("data", out _));
        Assert.All(new[] { "unmatchedFiles", "sunoReviews", "sunoProblems" }, name => Assert.True(dashboard.GetProperty(name).TryGetProperty("error", out _)));
    }

    /// <summary>A host whose workspace, audio file, or export store throws when listing, as chosen.</summary>
    internal static N8TracksApiFactory FailingHost(bool workspaces, bool files, bool exports) =>
        new()
        {
            TestServices = services =>
            {
                DashboardTests.Wrap<ISunoWorkspaceStore>(services, (method, _) => workspaces && method.Name == nameof(ISunoWorkspaceStore.ListAsync));
                DashboardTests.Wrap<IAudioFileStore>(services, (method, _) => files && method.Name == nameof(IAudioFileStore.ListAsync));
                DashboardTests.Wrap<ISunoExportStore>(services, (method, _) => exports && method.Name == nameof(ISunoExportStore.InStatesAsync));
            },
        };

    /// <summary>A signed-in client and an extension token, with <see cref="ChangedId"/> and <see cref="ConflictId"/> imported into two Songs.</summary>
    private static async Task<(HttpClient Client, string Token)> ImportedAsync(N8TracksApiFactory factory)
    {
        var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await ImportedVersions.AttachAsync(factory, await SongApi.CreateAsync(client, "Changed in Suno"), "2", Original(ChangedId));
        await ImportedVersions.AttachAsync(factory, await SongApi.CreateAsync(client, "In Conflict"), "2", Original(ConflictId));
        return (client, token);
    }

    private static JsonNode Original(string sunoId) => ProposalApi.Clip(sunoId, null, ProposalApi.At, 0, "Original words", "Original title");

    /// <summary>The first imported clip as Suno now has it: retitled, its inputs the same (Changed).</summary>
    private static JsonNode Changed()
    {
        var clip = Original(ChangedId);
        clip["title"] = "New title";
        return clip;
    }

    /// <summary>The second imported clip with other lyrics (Conflict).</summary>
    private static JsonNode Conflict()
    {
        var clip = Original(ConflictId);
        clip["metadata"]!["prompt"] = "Different words";
        return clip;
    }

    private static async Task<JsonElement> AttentionAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Attention);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> DashboardAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Dashboard);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static JsonElement Data(JsonElement answer, string section) => answer.GetProperty(section).GetProperty("data");

    private static (int Count, bool MediaUnavailable) Unmatched(JsonElement answer)
    {
        var section = Data(answer, "unmatchedFiles");
        return (section.GetProperty("count").GetInt32(), section.GetProperty("mediaUnavailable").GetBoolean());
    }

    private static JsonElement[] Problems(JsonElement answer) => [.. Data(answer, "sunoProblems").GetProperty("problems").EnumerateArray()];

    /// <summary>The Unmatched Files page's total: its list of files associated with nothing.</summary>
    private static async Task<int> UnmatchedPageTotalAsync(HttpClient client) =>
        (await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/audio-files?association=none&include=suggestions&sort=firstSeen&direction=desc&offset=0&limit=100", UriKind.Relative)))).GetProperty("total").GetInt32();

    private static async Task DiscardAsync(HttpClient client, string token, Guid id, string? body, HttpStatusCode expected)
    {
        using var response = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/discard"), token, body);
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
    }

    private static async Task DismissAsync(HttpClient client, string kind, Guid subject)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, Dismissals, $$"""{"kind":"{{kind}}","subject":"{{subject}}"}""");
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static string? EndReason(N8TracksApiFactory factory, Guid id)
    {
        var reason = TestDatabase.Scalar(factory.DataPath, $"SELECT coalesce(end_reason, '<null>') FROM suno_exports WHERE upper(id) = '{Upper(id)}';");
        return reason == "<null>" ? null : reason;
    }

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    private static async Task<Guid> CreateRequestAsync(HttpClient client, Guid versionId)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/versions/{versionId}/generation-requests", UriKind.Relative), "{}");
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task ClaimAsync(HttpClient client, string token, Guid id)
    {
        using var response = await CredentialApi.SendAsync(client, HttpMethod.Post, new Uri($"/api/v1/suno/generation-requests/{id}/claim", UriKind.Relative), token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task ReportAsync(HttpClient client, string token, Guid id, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/suno/generation-requests/{id}", UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SendWithRevisionAsync(HttpClient client, HttpMethod method, string path, int revision, string json)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }

    private static async Task WithServiceAsync<T>(N8TracksApiFactory factory, Func<T, Task> call)
        where T : notnull
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await call(scope.ServiceProvider.GetRequiredService<T>());
        }
    }
}
