using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno.Generate;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Generate on Suno requests (#144): made from a Version as a snapshot of its effective inputs, read
/// with <c>suno.generate</c>, claimed and reported on by the extension, cancelled by the user, and
/// settled when read (unclaimed, expired, stale, replaced). Making one changes nothing in the catalog.
/// </summary>
public sealed class GenerationRequestTests
{
    private static readonly string Cover = SystemRelationshipTypes.Cover.Id.ToString();
    private static readonly string Mashup = SystemRelationshipTypes.Mashup.Id.ToString();

    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        "kind", "songMode", "speechMode", "sources", "inspiration", "voice", "fileInputs", "workspace",
    };

    [Fact]
    public async Task TheSnapshotIsTheVersionsEffectiveInputsKeyedByTheAdapterFieldMap()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Snapshot Song");
        var versionId = VersionId(song);
        await EditAsync(client, versionId, """{"lyrics":"[Verse]\nline one","styles":"dream pop","inputs":{"weirdness":61,"excludeStyles":"metal"}}""");
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);

        var created = await CreateAsync(client, versionId);
        var read = await ReadAsync(client, token, created.GetProperty("id").GetGuid());
        var version = await VersionAsync(client, versionId);
        var effective = version.GetProperty("effectiveInputs");
        var snapshot = read.GetProperty("snapshot");

        Assert.Equal(1, snapshot.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("song", snapshot.GetProperty("kind").GetString());
        Assert.Equal("advanced", snapshot.GetProperty("mode").GetString());
        var entries = snapshot.GetProperty("entries").EnumerateArray()
            .ToDictionary(static entry => entry.GetProperty("key").GetString()!, static entry => entry.GetProperty("value").GetRawText(), StringComparer.Ordinal);
        var expected = effective.EnumerateObject()
            .Where(static property => !Structural.Contains(property.Name))
            .ToDictionary(static property => "songs.advanced." + (VersionInputRules.InventoryKey(property.Name) ?? property.Name), static property => property.Value.GetRawText(), StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(static pair => pair.Key, StringComparer.Ordinal), entries.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal("\"[Verse]\\nline one\"", entries["songs.advanced.lyrics"]);
        Assert.Equal("61", entries["songs.advanced.weirdness"]);
        Assert.All(entries.Keys, static key => Assert.True(AdapterFieldMap.Contains(key), key));
        Assert.Empty(snapshot.GetProperty("unsupported").EnumerateArray());
        Assert.Empty(snapshot.GetProperty("sources").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("workspace").ValueKind);
        Assert.Equal("Snapshot Song", snapshot.GetProperty("song").GetProperty("title").GetString());
        Assert.Equal(song.GetProperty("shortcode").GetString(), snapshot.GetProperty("song").GetProperty("shortcode").GetString());
        Assert.Equal(version.GetProperty("shortcode").GetString(), snapshot.GetProperty("version").GetProperty("shortcode").GetString());
        Assert.Equal("pending", read.GetProperty("state").GetString());
        Assert.True(read.GetProperty("active").GetBoolean());
        Assert.False(read.GetProperty("claimed").GetBoolean());
    }

    [Fact]
    public async Task ASimpleSongCarriesItsAddedLyricsInTheSectionsEntryAndItsSourcesWithSunoIds()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var original = await SongApi.CreateAsync(client, "Cover original");
        var generation = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("cover-source-clip"));
        var song = await SongApi.CreateAsync(client, "Simple cover");
        var versionId = VersionId(song);
        await EditAsync(client, versionId, $$$"""
            {"lyrics":"simple words","inputs":{"songMode":"simple","simpleLyricsAdded":true,"simpleStylesAdded":false,
             "sources":[{"typeId":"{{{Cover}}}","generation":"{{{generation.Shortcode}}}"}],
             "fileInputs":[{"kind":"image","description":"the cover photo"}]}}
            """);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);

        var snapshot = (await ReadAsync(client, token, (await CreateAsync(client, versionId)).GetProperty("id").GetGuid())).GetProperty("snapshot");

        Assert.Equal("simple", snapshot.GetProperty("mode").GetString());
        var entries = snapshot.GetProperty("entries").EnumerateArray().ToDictionary(static entry => entry.GetProperty("key").GetString()!, static entry => entry.GetProperty("value"), StringComparer.Ordinal);
        Assert.Equal("simple words", entries["songs.simple.simple_add_lyrics"].GetString());
        Assert.Equal(JsonValueKind.Null, entries["songs.simple.simple_add_styles"].ValueKind);
        Assert.DoesNotContain("songs.simple.lyrics", entries.Keys);
        var source = Assert.Single(snapshot.GetProperty("sources").EnumerateArray());
        Assert.Equal("songs.simple.audio", source.GetProperty("key").GetString());
        Assert.Equal("cover", source.GetProperty("sunoAction").GetString());
        Assert.Equal("cover-source-clip", source.GetProperty("target").GetProperty("sunoId").GetString());
        Assert.Equal(generation.Shortcode, source.GetProperty("shortcode").GetString());
        var file = Assert.Single(snapshot.GetProperty("fileInputs").EnumerateArray());
        Assert.Equal("songs.simple.simple_add_image", file.GetProperty("key").GetString());
        Assert.Equal("the cover photo", file.GetProperty("description").GetString());
    }

    [Fact]
    public async Task MakingARequestChangesNothingInTheCatalogForAFrozenVersion()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Frozen request");
        var versionId = VersionId(song);
        await SongApi.AttachGenerationAsync(factory, versionId.ToString(), Clips.Minimal("frozen-request-clip"));
        var before = await VersionAsync(client, versionId);
        var generationsBefore = await GenerationCountAsync(client, song);
        var stored = Invariants.VersionImmutabilityGuardTests.Stored(factory, versionId);

        var created = await CreateAsync(client, versionId);

        Assert.Equal("pending", created.GetProperty("state").GetString());
        var after = await VersionAsync(client, versionId);
        Assert.True(after.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(before.GetProperty("revision").GetInt32(), after.GetProperty("revision").GetInt32());
        Assert.Equal(generationsBefore, await GenerationCountAsync(client, song));
        Assert.Equal(stored, Invariants.VersionImmutabilityGuardTests.Stored(factory, versionId));

        // Complement: a mutable Version stays mutable.
        var mutable = await SongApi.CreateAsync(client, "Mutable request");
        await CreateAsync(client, VersionId(mutable));
        Assert.False((await VersionAsync(client, VersionId(mutable))).GetProperty("isFrozen").GetBoolean());
        Assert.Equal(0, await GenerationCountAsync(client, mutable));
    }

    [Fact]
    public async Task OnlySunoGenerateReadsTheRequestAndOnlyVersionsWriteMakesOne()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = VersionId(await SongApi.CreateAsync(client, "Scoped request"));
        var id = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        var sync = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync, CredentialScopes.CatalogRead);
        var generate = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);

        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Request(id), sync))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, Request(id), generate))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        // The Version's own view has no snapshot, so catalog.read may see how far it got.
        using (var current = await CredentialApi.SendAsync(client, HttpMethod.Get, Current(versionId), sync))
        {
            var request = (await SetupApi.JsonAsync(current)).GetProperty("request");
            Assert.Equal(id, request.GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, request.GetProperty("snapshot").ValueKind);
        }

        // The extension never makes one: suno.generate alone cannot.
        using (var create = await CredentialApi.SendAsync(client, HttpMethod.Post, Requests(versionId), generate))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var cancel = await CredentialApi.SendAsync(client, HttpMethod.Post, Cancel(id), generate))
        {
            Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        }
    }

    [Fact]
    public async Task TheExtensionClaimsAndReportsAndOnlyItsCredentialMay()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = VersionId(await SongApi.CreateAsync(client, "Claimed request"));
        var id = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var other = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);

        // Before a claim, no report is taken.
        using (var early = await ReportAsync(client, extension, id, """{"state":"opening","step":"open-create"}"""))
        {
            await ExpectProblemAsync(early, HttpStatusCode.Conflict, "request_not_claimed");
        }

        // A session cannot claim: only the extension does, with its credential.
        using (var session = await SongApi.SendJsonAsync(client, HttpMethod.Post, Claim(id), "{}"))
        {
            await ExpectProblemAsync(session, HttpStatusCode.Forbidden, "credential_required");
        }

        var claimed = await ClaimAsync(client, extension, id, HttpStatusCode.OK);
        Assert.Equal("claimed", claimed.GetProperty("state").GetString());
        Assert.True(claimed.GetProperty("claimed").GetBoolean());
        Assert.Equal("song", claimed.GetProperty("snapshot").GetProperty("kind").GetString());

        // Claiming again with the same credential answers it as it is; another credential is refused.
        Assert.Equal("claimed", (await ClaimAsync(client, extension, id, HttpStatusCode.OK)).GetProperty("state").GetString());
        using (var taken = await CredentialApi.SendAsync(client, HttpMethod.Post, Claim(id), other))
        {
            await ExpectProblemAsync(taken, HttpStatusCode.Conflict, "request_claimed");
        }

        using (var foreign = await ReportAsync(client, other, id, """{"state":"filling"}"""))
        {
            await ExpectProblemAsync(foreign, HttpStatusCode.Forbidden, "request_claimed");
        }

        foreach (var state in new[] { "opening", "workspace", "filling", "waiting" })
        {
            using var reported = await ReportAsync(client, extension, id, $$"""{"state":"{{state}}","step":"step-{{state}}"}""");
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
            var current = await CurrentAsync(client, versionId);
            Assert.Equal(state, current.GetProperty("state").GetString());
            Assert.Equal("step-" + state, current.GetProperty("step").GetString());
        }

        // Refusals: an unknown state, a state a report may not set, a stop without a reason.
        foreach (var body in new[] { """{"state":"generating"}""", """{"state":"cancelled"}""", """{"state":"stopped","step":"fill"}""", """{"state":"done","step":42}""" })
        {
            using var invalid = await ReportAsync(client, extension, id, body);
            await ExpectProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        using (var stopped = await ReportAsync(client, extension, id, """{"state":"stopped","step":"fill-lyrics","message":"The lyrics field was not found."}"""))
        {
            Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        }

        var ended = await CurrentAsync(client, versionId);
        Assert.Equal("stopped", ended.GetProperty("state").GetString());
        Assert.Equal("fill-lyrics", ended.GetProperty("step").GetString());
        Assert.False(ended.GetProperty("active").GetBoolean());

        // Out of a terminal state nothing moves.
        using (var after = await ReportAsync(client, extension, id, """{"state":"waiting"}"""))
        {
            await ExpectProblemAsync(after, HttpStatusCode.Conflict, "request_ended");
        }

        using (var cancelled = await SongApi.SendJsonAsync(client, HttpMethod.Post, Cancel(id), "{}"))
        {
            await ExpectProblemAsync(cancelled, HttpStatusCode.Conflict, "request_ended");
        }
    }

    [Fact]
    public async Task TheUserCancelsAnActiveRequest()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = VersionId(await SongApi.CreateAsync(client, "Cancelled request"));
        var id = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);

        using (var cancel = await SongApi.SendJsonAsync(client, HttpMethod.Post, Cancel(id), "{}"))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            var body = await SetupApi.JsonAsync(cancel);
            Assert.Equal("cancelled", body.GetProperty("state").GetString());
            Assert.Equal(GenerationRequestRules.CancelledMessage, body.GetProperty("message").GetString());
        }

        // The extension reads it before its next step and sees it is no longer active.
        var read = await ReadAsync(client, extension, id);
        Assert.False(read.GetProperty("active").GetBoolean());
        using (var report = await ReportAsync(client, extension, id, """{"state":"opening"}"""))
        {
            await ExpectProblemAsync(report, HttpStatusCode.Conflict, "request_ended");
        }

        using var missing = await SongApi.SendJsonAsync(client, HttpMethod.Post, Cancel(Guid.CreateVersion7()), "{}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task AnUnclaimedRequestStopsAfterFifteenSecondsAndAQuietOneExpiresAfterAnHour()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = VersionId(await SongApi.CreateAsync(client, "Timed request"));
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);

        await CreateAsync(client, versionId);
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal("pending", (await CurrentAsync(client, versionId)).GetProperty("state").GetString());
        clock.Advance(TimeSpan.FromSeconds(1));
        var unclaimed = await CurrentAsync(client, versionId);
        Assert.Equal("stopped", unclaimed.GetProperty("state").GetString());
        Assert.Equal(GenerationRequestRules.NotClaimedMessage, unclaimed.GetProperty("message").GetString());

        // A new request may be made at once after a terminal one.
        var id = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(59));
        using (var report = await ReportAsync(client, extension, id, """{"state":"waiting","step":"wait-for-create"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        // The hour runs from the last report.
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal("waiting", (await CurrentAsync(client, versionId)).GetProperty("state").GetString());
        clock.Advance(TimeSpan.FromMinutes(1));
        var expired = await CurrentAsync(client, versionId);
        Assert.Equal("expired", expired.GetProperty("state").GetString());
        Assert.Equal(GenerationRequestRules.ExpiredMessage, expired.GetProperty("message").GetString());
        Assert.NotEqual(JsonValueKind.Null, expired.GetProperty("endedAt").ValueKind);
    }

    [Fact]
    public async Task ASecondRequestForTheVersionReplacesTheFirst()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = VersionId(await SongApi.CreateAsync(client, "Replaced request"));
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);

        var first = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        var second = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();

        var replaced = await ReadAsync(client, token, first);
        Assert.Equal("cancelled", replaced.GetProperty("state").GetString());
        Assert.Equal(GenerationRequestRules.SupersededMessage, replaced.GetProperty("message").GetString());
        var current = await CurrentAsync(client, versionId);
        Assert.Equal(second, current.GetProperty("id").GetGuid());
        Assert.Equal("pending", current.GetProperty("state").GetString());

        // Complement: another Version's request is not touched.
        var other = VersionId(await SongApi.CreateAsync(client, "Other request"));
        await CreateAsync(client, other);
        Assert.Equal("pending", (await CurrentAsync(client, versionId)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task EditingTheVersionCancelsTheRequestAndAnnotationsDoNot()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = VersionId(await SongApi.CreateAsync(client, "Stale request"));
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var id = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);

        // A name is not sent to Suno: the request stays.
        await EditAsync(client, versionId, """{"name":"Renamed","notes":"a note"}""");
        Assert.Equal("claimed", (await CurrentAsync(client, versionId)).GetProperty("state").GetString());

        await EditAsync(client, versionId, """{"styles":"something else entirely"}""");
        var stale = await CurrentAsync(client, versionId);
        Assert.Equal("cancelled", stale.GetProperty("state").GetString());
        Assert.Equal(GenerationRequestRules.StaleMessage, stale.GetProperty("message").GetString());

        using var report = await ReportAsync(client, extension, id, """{"state":"filling"}""");
        await ExpectProblemAsync(report, HttpStatusCode.Conflict, "request_ended");
    }

    [Fact]
    public async Task EachUnavailableSourceBlocksTheRequestByNameAndANotImportedOneDoesNot()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var original = await SongApi.CreateAsync(client, "Blocking original");
        var present = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("present-source"));
        var trashed = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("trashed-source"));
        var gone = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("gone-source"));
        TestDatabase.Execute(factory.DataPath, $"""
            UPDATE generations SET remote_state = 'trashed' WHERE id = '{trashed.Generation.Id.ToString().ToUpperInvariant()}';
            UPDATE generations SET remote_state = 'missing' WHERE id = '{gone.Generation.Id.ToString().ToUpperInvariant()}';
            """);
        var versionId = VersionId(await SongApi.CreateAsync(client, "Blocked cover"));

        // A Suno clip never imported does not block.
        await EditAsync(client, versionId, $$$"""{"inputs":{"sources":[{"typeId":"{{{Cover}}}","external":{"sunoId":"never-imported"}}]}}""");
        await CreateAsync(client, versionId);

        // A Mashup of a clip in Suno's Trash and one Suno no longer lists; an Inspiration that is fine.
        await EditAsync(client, versionId, $$$"""
            {"inputs":{
              "sources":[{"typeId":"{{{Mashup}}}","generation":"{{{trashed.Shortcode}}}"},{"typeId":"{{{Mashup}}}","generation":"{{{gone.Shortcode}}}"}],
              "inspiration":{"sources":[{"generation":"{{{present.Shortcode}}}"}]}
            }}
            """);
        var current = await CurrentAsync(client, versionId);
        using (var blocked = await SongApi.SendJsonAsync(client, HttpMethod.Post, Requests(versionId), "{}"))
        {
            var problem = await ExpectProblemAsync(blocked, HttpStatusCode.UnprocessableEntity, "sources_unavailable");
            var sources = problem.GetProperty("sources").EnumerateArray().ToList();
            Assert.Equal(["audio:1:trashed:" + trashed.Shortcode, "audio:2:missing:" + gone.Shortcode], sources.Select(static source =>
                $"{source.GetProperty("group").GetString()}:{source.GetProperty("position").GetInt32()}:{source.GetProperty("availability").GetString()}:{source.GetProperty("shortcode").GetString()}"));
            Assert.All(sources, static source => Assert.Equal("Minimal clip", source.GetProperty("title").GetString()));
            Assert.Equal(JsonValueKind.Null, problem.GetProperty("lastSyncAt").ValueKind);
        }

        // Nothing was made: the request shown is still the earlier one (now stale, as the Version changed).
        Assert.Equal(current.GetProperty("id").GetGuid(), (await CurrentAsync(client, versionId)).GetProperty("id").GetGuid());

        // Deleted: the original Song goes, and every one of its Generations with it.
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(original.GetProperty("shortcode").GetString()!)))).GetProperty("revision").GetInt32();
        using (var request = new HttpRequestMessage(HttpMethod.Delete, SongApi.Song(original.GetProperty("shortcode").GetString()!))
        {
            Content = new StringContent("""{"confirmTitle":"Blocking original"}""", System.Text.Encoding.UTF8, "application/json"),
        })
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
            using var deleted = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var blocked = await SongApi.SendJsonAsync(client, HttpMethod.Post, Requests(versionId), "{}"))
        {
            var problem = await ExpectProblemAsync(blocked, HttpStatusCode.UnprocessableEntity, "sources_unavailable");
            Assert.Equal(["deleted", "deleted", "deleted"], problem.GetProperty("sources").EnumerateArray().Select(static source => source.GetProperty("availability").GetString()));
        }
    }

    [Fact]
    public async Task TheSnapshotCarriesTheSongsWorkspaceWithItsState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "In a workspace");
        var shortcode = song.GetProperty("shortcode").GetString()!;
        var sync = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        await SunoWorkspaceApi.ReportAsync(client, sync, complete: true, SunoWorkspaceApi.Project("w-1", "Studio"));
        await SunoWorkspaceApi.AssociatedAsync(client, shortcode, "w-1");

        var available = await ReadAsync(client, extension, (await CreateAsync(client, VersionId(song))).GetProperty("id").GetGuid());
        var workspace = available.GetProperty("snapshot").GetProperty("workspace");
        Assert.Equal("w-1", workspace.GetProperty("sunoId").GetString());
        Assert.Equal("Studio", workspace.GetProperty("name").GetString());
        Assert.Equal("available", workspace.GetProperty("state").GetString());

        // Complement: once a complete list leaves it out, the next request says it is unavailable.
        await SunoWorkspaceApi.ReportAsync(client, sync, complete: true, SunoWorkspaceApi.Project("w-2", "Elsewhere"));
        var unavailable = await ReadAsync(client, extension, (await CreateAsync(client, VersionId(song))).GetProperty("id").GetGuid());
        Assert.Equal("unavailable", unavailable.GetProperty("snapshot").GetProperty("workspace").GetProperty("state").GetString());
    }

    [Fact]
    public async Task TheWorkspaceTheUserCreatesInSunoBecomesTheSongsAndLaterRequestsCarryIt()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Needs a workspace");
        var shortcode = song.GetProperty("shortcode").GetString()!;
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var id = (await CreateAsync(client, VersionId(song))).GetProperty("id").GetGuid();
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);

        using (var created = await ReportAsync(client, extension, id, """{"state":"workspace","step":"create workspace","resolvedWorkspace":{"sunoId":"w-new","name":"Needs a workspace","how":"created"}}"""))
        {
            Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
            Assert.Equal("workspace", (await SetupApi.JsonAsync(created)).GetProperty("state").GetString());
        }

        var details = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(shortcode)));
        Assert.Equal("w-new", details.GetProperty("sunoWorkspace").GetProperty("id").GetString());
        Assert.Equal("available", details.GetProperty("sunoWorkspace").GetProperty("state").GetString());
        var recorded = await SunoWorkspaceApi.OneAsync(client, "w-new");
        Assert.Equal("Needs a workspace", recorded.GetProperty("name").GetString());
        Assert.Equal(1, recorded.GetProperty("songCount").GetInt32());

        // The same report again (an answer lost on the way) changes nothing and is not refused.
        var revision = details.GetProperty("revision").GetInt32();
        using (var again = await ReportAsync(client, extension, id, """{"state":"workspace","step":"create workspace","resolvedWorkspace":{"sunoId":"w-new","name":"Needs a workspace","how":"created"}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        Assert.Equal(revision, (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(shortcode)))).GetProperty("revision").GetInt32());

        // A later request of the Song carries the workspace, so the extension selects it without asking.
        var later = await ReadAsync(client, extension, (await CreateAsync(client, VersionId(song))).GetProperty("id").GetGuid());
        Assert.Equal("w-new", later.GetProperty("snapshot").GetProperty("workspace").GetProperty("sunoId").GetString());
    }

    [Fact]
    public async Task AResolvedWorkspaceIsRefusedWhenTheSongAlreadyHasAnAvailableOne()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Already placed");
        var shortcode = song.GetProperty("shortcode").GetString()!;
        var sync = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        await SunoWorkspaceApi.ReportAsync(client, sync, complete: true, SunoWorkspaceApi.Project("w-1", "Home"), SunoWorkspaceApi.Project("w-2", "Other"));
        await SunoWorkspaceApi.AssociatedAsync(client, shortcode, "w-1");
        var id = (await CreateAsync(client, VersionId(song))).GetProperty("id").GetGuid();
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);
        var before = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(shortcode)));

        foreach (var how in new[] { "picked", "created" })
        {
            using var refused = await ReportAsync(client, extension, id, $$$"""{"state":"workspace","resolvedWorkspace":{"sunoId":"w-2","name":"Other","how":"{{{how}}}"}}""");
            var problem = await ExpectProblemAsync(refused, HttpStatusCode.Conflict, "workspace_already_set");
            Assert.Equal("w-1", problem.GetProperty("workspaceId").GetString());
        }

        var after = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(shortcode)));
        Assert.Equal("w-1", after.GetProperty("sunoWorkspace").GetProperty("id").GetString());
        Assert.Equal(before.GetProperty("revision").GetInt32(), after.GetProperty("revision").GetInt32());
        Assert.Equal("claimed", (await CurrentAsync(client, VersionId(song))).GetProperty("state").GetString());

        // Complement: the same report without a workspace moves the request on.
        using var plain = await ReportAsync(client, extension, id, """{"state":"workspace","step":"select workspace"}""");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
    }

    [Fact]
    public async Task AnUnavailableWorkspaceIsReplacedByTheOneTheUserPicks()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Lost its workspace");
        var shortcode = song.GetProperty("shortcode").GetString()!;
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        await SunoWorkspaceApi.ReportAsync(client, extension, complete: true, SunoWorkspaceApi.Project("w-gone", "Gone"), SunoWorkspaceApi.Project("w-2", "Other"), SunoWorkspaceApi.Project("w-3", "Third"));
        await SunoWorkspaceApi.AssociatedAsync(client, shortcode, "w-gone");

        // Generate on Suno read Suno's complete list without the Song's workspace: n8Tracks marks it unavailable.
        await SunoWorkspaceApi.ReportAsync(client, extension, complete: true, SunoWorkspaceApi.Project("w-2", "Other"), SunoWorkspaceApi.Project("w-3", "Third", trashed: true));
        Assert.Equal("unavailable", (await SunoWorkspaceApi.OneAsync(client, "w-gone")).GetProperty("state").GetString());

        var id = (await CreateAsync(client, VersionId(song))).GetProperty("id").GetGuid();
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);

        // An unavailable workspace cannot be the replacement.
        using (var trashed = await ReportAsync(client, extension, id, """{"state":"workspace","resolvedWorkspace":{"sunoId":"w-3","name":"Third","how":"picked"}}"""))
        {
            var problem = await ExpectProblemAsync(trashed, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("resolvedWorkspace", out _));
        }

        using (var picked = await ReportAsync(client, extension, id, """{"state":"workspace","step":"choose workspace","resolvedWorkspace":{"sunoId":"w-2","name":"Other","how":"picked"}}"""))
        {
            Assert.True(picked.StatusCode == HttpStatusCode.OK, await picked.Content.ReadAsStringAsync());
        }

        var details = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(shortcode)));
        Assert.Equal("w-2", details.GetProperty("sunoWorkspace").GetProperty("id").GetString());
        Assert.Equal("unavailable", (await SunoWorkspaceApi.OneAsync(client, "w-gone")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AResolvedWorkspaceMustBeWellFormedAndComeFromTheClaimingExtension()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Badly sent");
        var shortcode = song.GetProperty("shortcode").GetString()!;
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var other = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var id = (await CreateAsync(client, VersionId(song))).GetProperty("id").GetGuid();

        using (var early = await ReportAsync(client, extension, id, """{"state":"workspace","resolvedWorkspace":{"sunoId":"w-1","name":"One","how":"picked"}}"""))
        {
            await ExpectProblemAsync(early, HttpStatusCode.Conflict, "request_not_claimed");
        }

        await ClaimAsync(client, extension, id, HttpStatusCode.OK);
        foreach (var body in new[]
        {
            """{"state":"workspace","resolvedWorkspace":"w-1"}""",
            """{"state":"workspace","resolvedWorkspace":{"sunoId":"w-1","name":"One","how":"renamed"}}""",
            """{"state":"workspace","resolvedWorkspace":{"sunoId":"w-1","how":"picked"}}""",
            """{"state":"workspace","resolvedWorkspace":{"sunoId":"","name":"One","how":"picked"}}""",
            $$$"""{"state":"workspace","resolvedWorkspace":{"sunoId":"w-1","name":"{{{new string('n', SunoWorkspaceRules.NameMaximumLength + 1)}}}","how":"created"}}""",
        })
        {
            using var invalid = await ReportAsync(client, extension, id, body);
            var problem = await ExpectProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("resolvedWorkspace", out _), body);
        }

        using (var foreign = await ReportAsync(client, other, id, """{"state":"workspace","resolvedWorkspace":{"sunoId":"w-1","name":"One","how":"picked"}}"""))
        {
            await ExpectProblemAsync(foreign, HttpStatusCode.Forbidden, "request_claimed");
        }

        var details = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(shortcode)));
        Assert.Equal(JsonValueKind.Null, details.GetProperty("sunoWorkspace").ValueKind);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_workspaces;"));
    }

    /// <summary>A summary as the extension sends it (#146): lyrics as length and hash only; a source verified on the form (#148).</summary>
    private const string Verification = """
        {"adapterVersion":5,"mode":"advanced","checkedAt":"2026-10-07T12:00:00.000Z","entries":[
          {"key":"songs.advanced.lyrics","outcome":"set","expected":{"length":11,"sha256":"5f6955e3e1f2c0a0d1b3b1e3b2b0c4b6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2"}},
          {"key":"songs.advanced.weirdness","outcome":"failed","expected":70,"found":65},
          {"key":"songs.advanced.max_mode","outcome":"set","expected":false},
          {"key":"songs.advanced.model","outcome":"unavailable","expected":"v6-wild","note":"Suno’s model menu does not offer this model."},
          {"key":"songs.advanced.audio","outcome":"manual","note":"Attach the file by hand: the demo."},
          {"key":"songs.advanced.crop","outcome":"unsupported"},
          {"key":"songs.advanced.audio","outcome":"verified","note":"The source is on the form."}
        ]}
        """;

    [Fact]
    public async Task TheExtensionReportsItsVerificationSummaryAndTheVersionPageReadsIt()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Verified");
        var versionId = VersionId(song);
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var id = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, (await CurrentAsync(client, versionId)).GetProperty("verification").ValueKind);

        using (var waiting = await ReportAsync(client, extension, id, $$"""{"state":"waiting","step":"review and create","verification":{{Verification}}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
        }

        var current = await CurrentAsync(client, versionId);
        Assert.Equal("waiting", current.GetProperty("state").GetString());
        var verification = current.GetProperty("verification");
        Assert.Equal(5, verification.GetProperty("adapterVersion").GetInt32());
        Assert.Equal("advanced", verification.GetProperty("mode").GetString());
        Assert.Equal("2026-10-07T12:00:00.0000000+00:00", verification.GetProperty("checkedAt").GetString());
        var entries = verification.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["set", "failed", "set", "unavailable", "manual", "unsupported", "verified"], entries.Select(static entry => entry.GetProperty("outcome").GetString()));
        Assert.Equal(11, entries[0].GetProperty("expected").GetProperty("length").GetInt32());
        Assert.Equal(65, entries[1].GetProperty("found").GetInt32());
        Assert.False(entries[2].GetProperty("expected").GetBoolean());
        Assert.Equal("Attach the file by hand: the demo.", entries[4].GetProperty("note").GetString());
        Assert.False(entries[5].TryGetProperty("expected", out _));

        // A later report without a summary keeps it; Check again's summary replaces it.
        using (var step = await ReportAsync(client, extension, id, """{"state":"waiting","step":"check form"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        }

        Assert.Equal(7, (await CurrentAsync(client, versionId)).GetProperty("verification").GetProperty("entries").GetArrayLength());
        using (var again = await ReportAsync(client, extension, id, """{"state":"waiting","step":"check form","verification":{"adapterVersion":5,"mode":"advanced","checkedAt":"2026-10-07T12:05:00Z","entries":[{"key":"songs.advanced.weirdness","outcome":"set","expected":70}]}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        var replaced = (await CurrentAsync(client, versionId)).GetProperty("verification");
        Assert.Equal("set", Assert.Single(replaced.GetProperty("entries").EnumerateArray()).GetProperty("outcome").GetString());
        Assert.Equal(replaced.GetRawText(), (await ReadAsync(client, extension, id)).GetProperty("verification").GetRawText());
    }

    [Fact]
    public async Task AVerificationSummaryMustBeWellFormedAndNeverCarryText()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Badly verified");
        var versionId = VersionId(song);
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var id = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);
        const string Head = "\"adapterVersion\":5,\"mode\":\"advanced\",\"checkedAt\":\"2026-10-07T12:00:00Z\"";
        var tooMany = string.Join(",", Enumerable.Repeat("""{"key":"songs.advanced.title","outcome":"set"}""", GenerationVerification.MaximumEntries + 1));

        foreach (var verification in new[]
        {
            "\"set\"",
            "{" + Head + "}",
            "{" + Head + ""","entries":[{"key":"songs.advanced.lyrics","outcome":"done"}]}""",
            "{" + Head + ""","entries":[{"key":"","outcome":"set"}]}""",
            "{" + Head + ",\"entries\":[{\"key\":\"songs.advanced.lyrics\",\"outcome\":\"set\",\"expected\":\"" + new string('l', GenerationVerification.MaximumChoiceLength + 1) + "\"}]}",
            "{" + Head + ""","entries":[{"key":"songs.advanced.lyrics","outcome":"set","expected":{"length":3,"sha256":"not a hash"}}]}""",
            "{" + Head + ""","entries":[{"key":"songs.advanced.lyrics","outcome":"set","expected":{"text":"secret words"}}]}""",
            "{" + Head + ""","entries":[{"key":"songs.advanced.lyrics","outcome":"set","lyrics":"secret words"}]}""",
            "{" + Head + ""","entries":[],"lyrics":"secret words"}""",
            """{"adapterVersion":0,"mode":"advanced","checkedAt":"2026-10-07T12:00:00Z","entries":[]}""",
            """{"adapterVersion":5,"mode":"advanced","checkedAt":"yesterday","entries":[]}""",
            "{" + Head + ""","entries":[""" + tooMany + """]}""",
        })
        {
            using var invalid = await ReportAsync(client, extension, id, $$"""{"state":"waiting","step":"review and create","verification":{{verification}}}""");
            var problem = await ExpectProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("verification", out _), verification);
        }

        // Complement: nothing was stored, and the request did not move.
        var current = await CurrentAsync(client, versionId);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("verification").ValueKind);
        Assert.Equal("claimed", current.GetProperty("state").GetString());
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_generation_requests WHERE verification_json IS NOT NULL;"));
    }

    /// <summary>
    /// #340, with the verifier's probe values: a text entry's <c>expected</c> or <c>found</c> sent as
    /// plain text, however short, is refused, and so is a note that is not one line (a pasted block of
    /// the Version's lyrics, or a line break, tab, or separator in it) or is too long (#379: a note's
    /// words carry no Version text by the extension's construction, so only its shape is checked here);
    /// nothing is stored and the request does not move. Complement: the same entries as the extension
    /// sends them, hashed and with the adapter's words, are stored.
    /// </summary>
    [Fact]
    public async Task ATextEntryInPlainTextOrANoteThatIsNotOneLineIsRefusedAndNothingIsStored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = VersionId(await SongApi.CreateAsync(client, "Probe 146"));
        await EditAsync(client, versionId, """{"lyrics":"PROBE-SECRET-LYRIC line one\nsecond line","styles":"my private prompt text","inputs":{"fileInputs":[{"kind":"audio","description":"the hummed demo take"}]}}""");
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var id = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);
        const string Head = "\"adapterVersion\":5,\"mode\":\"advanced\",\"checkedAt\":\"2026-10-07T12:00:00Z\"";

        foreach (var entries in new[]
        {
            """[{"key":"songs.advanced.lyrics","outcome":"failed","expected":"PROBE-SECRET-LYRIC line one","found":"PROBE-SECRET-FOUND"}]""",
            """[{"key":"songs.advanced.lyrics","outcome":"set","expected":"la"}]""",
            """[{"key":"songs.advanced.exclude_styles","outcome":"failed","expected":null,"found":"metal"}]""",
            """[{"key":"songs.advanced.title","outcome":"unavailable","expected":"Probe 146"}]""",
            """[{"key":"songs.advanced.lyrics","outcome":"manual","note":"PROBE-SECRET-LYRIC line one\nsecond line"}]""",
            """[{"key":"songs.advanced.styles","outcome":"manual","note":"Check the styles:\r\nmy private prompt text"}]""",
            """[{"key":"songs.advanced.styles","outcome":"manual","note":"PROBE-SECRET-NOTE\tmy private prompt text"}]""",
            """[{"key":"songs.advanced.lyrics","outcome":"manual","note":"PROBE-SECRET-LYRIC line one\u2028second line"}]""",
            "[{\"key\":\"songs.advanced.model\",\"outcome\":\"set\",\"note\":\"" + new string('n', GenerationVerification.MaximumTextLength + 1) + "\"}]",
        })
        {
            using var refused = await ReportAsync(client, extension, id, Report(Head, entries));
            var problem = await ExpectProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("verification", out _), entries);
        }

        var current = await CurrentAsync(client, versionId);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("verification").ValueKind);
        Assert.Equal("claimed", current.GetProperty("state").GetString());
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_generation_requests WHERE verification_json IS NOT NULL;"));

        // Complement: hashed text, null, a choice, and the adapter's own words are stored.
        const string Accepted = """
            [{"key":"songs.advanced.lyrics","outcome":"failed","expected":{"length":39,"sha256":"5f6955e3e1f2c0a0d1b3b1e3b2b0c4b6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2"},"found":null},
             {"key":"songs.advanced.model","outcome":"set","expected":"v6-mini"},
             {"key":"songs.advanced.audio","outcome":"manual","note":"Attach the audio file by hand (the Version's file note)."}]
            """;
        using (var stored = await ReportAsync(client, extension, id, Report(Head, Accepted)))
        {
            Assert.True(stored.StatusCode == HttpStatusCode.OK, await stored.Content.ReadAsStringAsync());
        }

        var verification = (await CurrentAsync(client, versionId)).GetProperty("verification").GetRawText();
        Assert.DoesNotContain("PROBE-SECRET", verification, StringComparison.Ordinal);
        Assert.Equal(3, JsonDocument.Parse(verification).RootElement.GetProperty("entries").GetArrayLength());
    }

    /// <summary>
    /// The extension's own notes, as it sends them for an advanced Song (#379): fixed text that names
    /// things such as Duration, the workspace step, and the Key picker.
    /// </summary>
    private const string OwnNotes = """
        [{"key":"songs.simple.workspace","outcome":"set","note":"Selected by the workspace step."},
         {"key":"songs.advanced.duration_seconds","outcome":"not_applicable","note":"Duration is Auto, so no length is set."},
         {"key":"songs.advanced.duration_mode","outcome":"manual","expected":"auto","note":"Set Duration to Auto by hand: the extension cannot read Suno’s Duration mode yet."},
         {"key":"songs.advanced.audio","outcome":"manual","note":"Attach the file by hand: the Version’s file note says which."},
         {"key":"sounds.single.sound_key","outcome":"manual","expected":"any","note":"Choose the key in Suno’s Key picker, then press Apply: the extension cannot use the Key picker yet."}]
        """;

    /// <summary>
    /// #379: the extension's own notes are stored whatever the Song is called, here a word of a note,
    /// which a check of the notes against the Version's text refused (422, and Generate on Suno stopped).
    /// </summary>
    [Theory]
    [InlineData("Duration")]
    [InlineData("Selected")]
    [InlineData("workspace")]
    public async Task TheExtensionsOwnNotesAreStoredWhateverTheSongIsCalled(string title)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = VersionId(await SongApi.CreateAsync(client, title));

        await AssertOwnNotesStoredAsync(factory, client, versionId);
    }

    /// <summary>
    /// #379: the extension's own notes are stored whatever the Version's text entries and file note say,
    /// here whole phrases of the notes as lines of the lyrics, the styles, and a file note.
    /// </summary>
    [Fact]
    public async Task TheExtensionsOwnNotesAreStoredWhateverTheVersionsTextSays()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = VersionId(await SongApi.CreateAsync(client, "Ordinary song"));
        await EditAsync(client, versionId, """{"lyrics":"first line\nSelected by the workspace step\nthe extension cannot use the Key picker yet","styles":"Duration is Auto","inputs":{"fileInputs":[{"kind":"audio","description":"the Version’s file note says which"}]}}""");

        await AssertOwnNotesStoredAsync(factory, client, versionId);
    }

    /// <summary>Claims a request for the Version, reports <see cref="OwnNotes"/>, and reads them back as stored.</summary>
    private static async Task AssertOwnNotesStoredAsync(N8TracksApiFactory factory, HttpClient client, Guid versionId)
    {
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var id = (await CreateAsync(client, versionId)).GetProperty("id").GetGuid();
        await ClaimAsync(client, extension, id, HttpStatusCode.OK);
        const string Head = "\"adapterVersion\":5,\"mode\":\"advanced\",\"checkedAt\":\"2026-10-07T12:00:00Z\"";

        using (var stored = await ReportAsync(client, extension, id, Report(Head, OwnNotes)))
        {
            Assert.True(stored.StatusCode == HttpStatusCode.OK, await stored.Content.ReadAsStringAsync());
        }

        var notes = (await CurrentAsync(client, versionId)).GetProperty("verification").GetProperty("entries").EnumerateArray()
            .Select(static entry => entry.GetProperty("note").GetString())
            .ToList();
        Assert.Equal(
            ["Selected by the workspace step.", "Duration is Auto, so no length is set.", "Set Duration to Auto by hand: the extension cannot read Suno’s Duration mode yet.", "Attach the file by hand: the Version’s file note says which.", "Choose the key in Suno’s Key picker, then press Apply: the extension cannot use the Key picker yet."],
            notes);
    }

    /// <summary>A waiting report carrying a summary with <paramref name="head"/> and <paramref name="entries"/>.</summary>
    private static string Report(string head, string entries) =>
        "{\"state\":\"waiting\",\"step\":\"review and create\",\"verification\":{" + head + ",\"entries\":" + entries + "}}";

    private static Guid VersionId(JsonElement song) => song.GetProperty("currentVersion").GetProperty("id").GetGuid();

    private static Uri Requests(Guid versionId) => new($"/api/v1/versions/{versionId}/generation-requests", UriKind.Relative);

    private static Uri Current(Guid versionId) => new($"/api/v1/versions/{versionId}/generation-request", UriKind.Relative);

    private static Uri Request(Guid id) => new($"/api/v1/suno/generation-requests/{id}", UriKind.Relative);

    private static Uri Claim(Guid id) => new($"/api/v1/suno/generation-requests/{id}/claim", UriKind.Relative);

    private static Uri Cancel(Guid id) => new($"/api/v1/suno/generation-requests/{id}/cancel", UriKind.Relative);

    private static async Task<JsonElement> CreateAsync(HttpClient client, Guid versionId)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, Requests(versionId), "{}");
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> CurrentAsync(HttpClient client, Guid versionId)
    {
        using var response = await client.GetAsync(Current(versionId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("request");
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string token, Guid id)
    {
        using var response = await CredentialApi.SendAsync(client, HttpMethod.Get, Request(id), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> ClaimAsync(HttpClient client, string token, Guid id, HttpStatusCode expected)
    {
        using var response = await CredentialApi.SendAsync(client, HttpMethod.Post, Claim(id), token);
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<HttpResponseMessage> ReportAsync(HttpClient client, string token, Guid id, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, Request(id)) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> VersionAsync(HttpClient client, Guid versionId)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{versionId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<int> GenerationCountAsync(HttpClient client, JsonElement song)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/songs/{song.GetProperty("shortcode").GetString()}/generations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("items").GetArrayLength();
    }

    private static async Task EditAsync(HttpClient client, Guid versionId, string json)
    {
        var revision = (await VersionAsync(client, versionId)).GetProperty("revision").GetInt32();
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/versions/{versionId}", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }
}

/// <summary>The adapter's field map in code is the one in <c>docs/suno-adapter-field-map.md</c> (#144).</summary>
public sealed class AdapterFieldMapTests
{
    [Fact]
    public void TheEntriesAreTheDocumentsEntries()
    {
        var document = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "suno-adapter-field-map.md"));
        var entries = document
            .Select(static line => System.Text.RegularExpressions.Regex.Match(line, "^\\| `([a-z_]+\\.[a-z_]+\\.[a-z_]+)` \\|"))
            .Where(static match => match.Success)
            .Select(static match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(38, entries.Count);
        Assert.Equal(entries, AdapterFieldMap.Entries);
    }

    [Fact]
    public void AValueWithNoEntryIsListedAsUnsupported()
    {
        // A Speech in Simple mode has no field on the map but its prompt: any other value of the tab
        // in that mode would be listed under unsupported rather than dropped.
        Assert.True(AdapterFieldMap.Contains("speech.simple.speech_prompt"));
        Assert.False(AdapterFieldMap.Contains("speech.simple.speech_tone"));
        Assert.False(AdapterFieldMap.Contains("songs.advanced.workspace"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "n8Tracks.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
