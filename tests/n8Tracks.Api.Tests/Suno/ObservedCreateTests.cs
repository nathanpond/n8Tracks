using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Retention;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Suno.Generate;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The user's Create in Suno, observed by the extension during Generate on Suno (#149): the clips are
/// attached to the requested Version when what was submitted is what it holds, or to a new child Version
/// holding what was submitted when the user changed the form; one observed Generation Event links them.
/// </summary>
public sealed class ObservedCreateTests
{
    private const string Mode = "songs-advanced";

    [Fact]
    public void ACreateIsMappedFromTheResponseThenTheRequestAndTheModelIsNeverRead()
    {
        var response = ObservedCreateApi.Response(Mode, "suno-request", ["clip-a"]);
        using var clip = JsonDocument.Parse(response["clips"]![0]!.ToJsonString());
        using var request = JsonDocument.Parse(ObservedCreateApi.Request(Mode).ToJsonString());

        var mapped = ClipInputMapper.MapCreate(clip.RootElement, request.RootElement, []);

        // From the response: the submitted styles (the feed only has Suno's rewrite), the sliders, Max Mode.
        Assert.Equal("<redacted 20 chars>", mapped.Styles);
        Assert.Equal(70, mapped.Inputs.Weirdness);
        Assert.Equal(30, mapped.Inputs.StyleInfluence);
        Assert.Equal("high", mapped.Inputs.Variety);
        Assert.True(mapped.Inputs.MaxMode);

        // From the request only: Vocal Gender, Duration (present, so Custom), and Personalize.
        Assert.Equal("female", mapped.Inputs.VocalGender);
        Assert.Equal("custom", mapped.Inputs.DurationMode);
        Assert.Equal(30, mapped.Inputs.DurationSeconds);
        Assert.True(mapped.Inputs.Personalize);
        Assert.Contains("vocalGender", mapped.Compared.Keys);

        // The model is assumed: the response has no badge and mv names Suno's engine, not the label.
        Assert.Contains("model", mapped.Marks.NotReturned);
        Assert.Null(mapped.Model);

        // Without the request, the request-only options are assumed from the Version, never guessed.
        var unread = ClipInputMapper.MapCreate(clip.RootElement, null, []);
        Assert.Equal(["durationMode", "durationSeconds", "model", "personalize", "vocalGender"], unread.Marks.NotReturned.Where(static key => key is "durationMode" or "durationSeconds" or "model" or "personalize" or "vocalGender").Order(StringComparer.Ordinal));
        Assert.DoesNotContain("vocalGender", unread.Compared.Keys);
        Assert.Equal("<redacted 20 chars>", unread.Styles);

        // A request without a duration means Auto, and one without a Vocal Gender means none.
        var auto = ObservedCreateApi.Request(Mode);
        auto.Remove("duration");
        auto["metadata"]!.AsObject().Remove("vocal_gender");
        using var autoRequest = JsonDocument.Parse(auto.ToJsonString());
        var autoMapped = ClipInputMapper.MapCreate(clip.RootElement, autoRequest.RootElement, []);
        Assert.Equal("auto", autoMapped.Inputs.DurationMode);
        Assert.Null(autoMapped.Inputs.VocalGender);
        Assert.Contains("durationSeconds", autoMapped.Marks.NotReturned);

        // A value the map cannot decode is assumed, kept raw, and compared on nothing.
        var male = ObservedCreateApi.Request(Mode);
        male["metadata"]!["vocal_gender"] = "m";
        using var maleRequest = JsonDocument.Parse(male.ToJsonString());
        var maleMapped = ClipInputMapper.MapCreate(clip.RootElement, maleRequest.RootElement, []);
        Assert.Contains("vocalGender", maleMapped.Marks.NotReturned);
        Assert.Equal("\"m\"", maleMapped.Marks.RawValues["vocalGender"]);
        Assert.DoesNotContain("vocalGender", maleMapped.Compared.Keys);
    }

    [Theory]
    [InlineData("songs-simple")]
    [InlineData("songs-advanced")]
    [InlineData("speech-simple")]
    [InlineData("speech-advanced")]
    [InlineData("sounds")]
    public async Task WhatWasSubmittedIsAttachedToTheVersionHoldingItWhichFreezes(string mode)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, $"Observed {mode}");
        var versionId = VersionId(song);
        var response = ObservedCreateApi.Response(mode, $"suno-request-{mode}", [$"{mode}-clip-1", $"{mode}-clip-2"]);
        var request = ObservedCreateApi.Request(mode);
        await EditAsync(client, versionId, ObservedCreateApi.MatchingEdit(response, request));
        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);

        var answered = await ObservedCreateApi.RecordAsync(client, token, id, response, request);

        Assert.Equal("waiting", answered.GetProperty("state").GetString());
        Assert.True(answered.GetProperty("active").GetBoolean());
        var observed = Assert.Single(answered.GetProperty("observed").EnumerateArray());
        Assert.Equal(ObservedCreates.Attached, observed.GetProperty("outcome").GetString());
        Assert.Equal(versionId, observed.GetProperty("version").GetProperty("id").GetGuid());
        Assert.Empty(observed.GetProperty("differing").EnumerateArray());
        Assert.True(observed.GetProperty("requestRead").GetBoolean());
        Assert.Equal(2, observed.GetProperty("generations").GetArrayLength());

        var version = await VersionAsync(client, versionId);
        Assert.True(version.GetProperty("isFrozen").GetBoolean());
        var generations = await GenerationsAsync(client, song);
        Assert.Equal([$"{mode}-clip-1", $"{mode}-clip-2"], generations.Select(static item => item.GetProperty("sunoId").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(generations, static item => Assert.Equal("submitted", item.GetProperty("providerStatus").GetString()));
        Assert.All(generations, item => Assert.Equal(versionId, item.GetProperty("version").GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task TheClipsOfOneCreateShareOneObservedEventWithSunosRequestIdWhichNoAnswerCarries()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Observed event");
        var versionId = VersionId(song);
        var response = ObservedCreateApi.Response(Mode, "suno-request-event-7", ["event-clip-1", "event-clip-2"]);
        var request = ObservedCreateApi.Request(Mode);
        await EditAsync(client, versionId, ObservedCreateApi.MatchingEdit(response, request));
        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);

        using var answer = await ObservedCreateApi.PostAsync(client, token, id, response, request);
        var text = await answer.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var events = RetentionApi.Dump(factory, "generation_events");
        var recorded = Assert.Single(events);
        Assert.Contains("'suno-request-event-7'", recorded, StringComparison.Ordinal);
        Assert.Contains("'observed'", recorded, StringComparison.Ordinal);
        Assert.Contains("'high'", recorded, StringComparison.Ordinal);
        Assert.Contains("2", recorded, StringComparison.Ordinal);
        Assert.Equal(2, TestDatabase.Rows(factory.DataPath, "SELECT generation_id FROM generation_event_links;").Count);

        // No answer carries Suno's request ID: not the report's, the request's, or a Generation's.
        Assert.DoesNotContain("suno-request-event-7", text, StringComparison.Ordinal);
        using var current = await client.GetAsync(new Uri($"/api/v1/versions/{versionId}/generation-request", UriKind.Relative));
        Assert.DoesNotContain("suno-request-event-7", await current.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var generations = await client.GetAsync(new Uri($"/api/v1/songs/{song.GetProperty("shortcode").GetString()}/generations", UriKind.Relative));
        Assert.DoesNotContain("suno-request-event-7", await generations.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedFormMakesANewChildVersionAndLeavesTheRequestedVersionAsItWasAndMutable()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Observed branch");
        var versionId = VersionId(song);
        var request = ObservedCreateApi.Request(Mode);
        var matching = ObservedCreateApi.Response(Mode, "branch-request-0", ["branch-clip-0"]);
        await EditAsync(client, versionId, ObservedCreateApi.MatchingEdit(matching, request));
        var stored = Invariants.VersionImmutabilityGuardTests.Stored(factory, versionId);
        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);

        // The user moved Weirdness to 40 before clicking Create.
        static void Weirder(JsonObject clip) => clip["metadata"]!["control_sliders"]!["weirdness_constraint"] = 0.4;
        var changed = ObservedCreateApi.Response(Mode, "branch-request-1", ["branch-clip-1", "branch-clip-2"], Weirder);
        var answered = await ObservedCreateApi.RecordAsync(client, token, id, changed, request);

        var observed = Assert.Single(answered.GetProperty("observed").EnumerateArray());
        Assert.Equal(ObservedCreates.Branched, observed.GetProperty("outcome").GetString());
        Assert.Equal(["weirdness"], observed.GetProperty("differing").EnumerateArray().Select(static key => key.GetString()));
        var branch = observed.GetProperty("version");
        Assert.Equal("1.1", branch.GetProperty("number").GetString());
        var branchId = branch.GetProperty("id").GetGuid();

        // The requested Version is byte-identical and still mutable; the new one holds what was submitted.
        Assert.Equal(stored, Invariants.VersionImmutabilityGuardTests.Stored(factory, versionId));
        Assert.False((await VersionAsync(client, versionId)).GetProperty("isFrozen").GetBoolean());
        var made = await VersionAsync(client, branchId);
        Assert.True(made.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(40, made.GetProperty("inputs").GetProperty("weirdness").GetInt32());
        Assert.Equal(ObservedCreates.BranchNote, made.GetProperty("notes").GetString());
        Assert.Equal((await VersionAsync(client, versionId)).GetProperty("lyrics").GetString(), made.GetProperty("lyrics").GetString());
        var current = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song.GetProperty("shortcode").GetString()!)));
        Assert.Equal(branchId, current.GetProperty("currentVersion").GetProperty("id").GetGuid());
        Assert.All(await GenerationsAsync(client, song), item => Assert.Equal(branchId, item.GetProperty("version").GetProperty("id").GetGuid()));

        // A further Create with the same change goes to that Version; one as requested to the requested Version.
        var again = await ObservedCreateApi.RecordAsync(client, token, id, ObservedCreateApi.Response(Mode, "branch-request-2", ["branch-clip-3"], Weirder), request);
        var second = again.GetProperty("observed")[1];
        Assert.Equal(ObservedCreates.Branched, second.GetProperty("outcome").GetString());
        Assert.Equal(branchId, second.GetProperty("version").GetProperty("id").GetGuid());
        var asRequested = await ObservedCreateApi.RecordAsync(client, token, id, ObservedCreateApi.Response(Mode, "branch-request-3", ["branch-clip-4"]), request);
        var third = asRequested.GetProperty("observed")[2];
        Assert.Equal(ObservedCreates.Attached, third.GetProperty("outcome").GetString());
        Assert.Equal(versionId, third.GetProperty("version").GetProperty("id").GetGuid());
        Assert.True((await VersionAsync(client, versionId)).GetProperty("isFrozen").GetBoolean());
        Assert.Equal(2, (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/songs/{song.GetProperty("shortcode").GetString()}/versions", UriKind.Relative)))).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AFrozenRequestedVersionBranchesTooAndItsInputsStayAsTheyWere()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Observed frozen");
        var versionId = VersionId(song);
        await SongApi.AttachGenerationAsync(factory, versionId.ToString(), Clips.Minimal("frozen-first-clip"));
        var stored = Invariants.VersionImmutabilityGuardTests.Stored(factory, versionId);
        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);

        // The new Song's defaults are not what the fixture submitted: a new Version is made.
        var answered = await ObservedCreateApi.RecordAsync(client, token, id, ObservedCreateApi.Response(Mode, "frozen-request", ["frozen-clip-1", "frozen-clip-2"]), ObservedCreateApi.Request(Mode));

        var observed = Assert.Single(answered.GetProperty("observed").EnumerateArray());
        Assert.Equal(ObservedCreates.Branched, observed.GetProperty("outcome").GetString());
        Assert.Contains("weirdness", observed.GetProperty("differing").EnumerateArray().Select(static key => key.GetString()));
        Assert.Equal(stored, Invariants.VersionImmutabilityGuardTests.Stored(factory, versionId));
        Assert.Equal(3, (await GenerationsAsync(client, song)).Count);
    }

    [Fact]
    public async Task AClipALiveGenerationHoldsOrOneDeletedFromN8TracksIsSkippedAndReportedAndTheRestAttached()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Observed skips");
        var versionId = VersionId(song);
        var other = await SongApi.CreateAsync(client, "Synced first");
        await SongApi.AttachGenerationAsync(factory, VersionId(other).ToString(), Clips.Minimal("skip-linked"));
        var deleted = await SongApi.AttachGenerationAsync(factory, VersionId(other).ToString(), Clips.Minimal("skip-deleted"));
        await ProposalApi.DeleteGenerationAsync(client, deleted.Shortcode);
        var response = ObservedCreateApi.Response(Mode, "skip-request", ["skip-linked", "skip-deleted", "skip-fresh"]);
        var request = ObservedCreateApi.Request(Mode);
        await EditAsync(client, versionId, ObservedCreateApi.MatchingEdit(response, request));
        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);

        var answered = await ObservedCreateApi.RecordAsync(client, token, id, response, request);

        var observed = Assert.Single(answered.GetProperty("observed").EnumerateArray());
        Assert.Equal(ObservedCreates.Attached, observed.GetProperty("outcome").GetString());
        Assert.Equal("skip-fresh", Assert.Single(observed.GetProperty("generations").EnumerateArray()).GetProperty("sunoId").GetString());
        var skipped = observed.GetProperty("skipped").EnumerateArray().ToDictionary(static item => item.GetProperty("sunoId").GetString()!, static item => item.GetProperty("reason").GetString());
        Assert.Equal(ObservedCreates.AlreadyLinked, skipped["skip-linked"]);
        Assert.Equal(ObservedCreates.Tombstoned, skipped["skip-deleted"]);
        Assert.Contains("2 skipped", answered.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(["skip-fresh"], (await GenerationsAsync(client, song)).Select(static item => item.GetProperty("sunoId").GetString()));

        // Every clip skipped: nothing attached, no Version made or frozen, no event.
        var events = RetentionApi.Dump(factory, "generation_events").Count;
        var none = await ObservedCreateApi.RecordAsync(client, token, id, ObservedCreateApi.Response(Mode, "skip-request-2", ["skip-linked"], static clip => clip["metadata"]!["control_sliders"]!["weirdness_constraint"] = 0.1), request);
        Assert.Equal(ObservedCreates.NothingAttached, none.GetProperty("observed")[1].GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("observed")[1].GetProperty("version").ValueKind);
        Assert.Equal(events, RetentionApi.Dump(factory, "generation_events").Count);
        Assert.Single((await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/songs/{song.GetProperty("shortcode").GetString()}/versions", UriKind.Relative)))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task TheSameCreateSentAgainAnswersWhatItCameToAndStoresNothingMore()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Observed twice");
        var versionId = VersionId(song);
        var response = ObservedCreateApi.Response(Mode, "twice-request", ["twice-clip-1", "twice-clip-2"]);
        var request = ObservedCreateApi.Request(Mode);
        await EditAsync(client, versionId, ObservedCreateApi.MatchingEdit(response, request));
        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);

        var first = await ObservedCreateApi.RecordAsync(client, token, id, response, request);
        var second = await ObservedCreateApi.RecordAsync(client, token, id, response, request);

        Assert.Equal(first.GetProperty("observed").GetRawText(), second.GetProperty("observed").GetRawText());
        Assert.Equal(2, (await GenerationsAsync(client, song)).Count);
        Assert.Single(RetentionApi.Dump(factory, "generation_events"));
    }

    [Fact]
    public async Task OnlyTheClaimingExtensionReportsACreateOnAnActiveRequestAndAMalformedResponseStoresNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Observed refusals");
        var versionId = VersionId(song);
        var response = ObservedCreateApi.Response(Mode, "refused-request", ["refused-clip"]);
        var request = ObservedCreateApi.Request(Mode);

        // Not claimed yet.
        var extension = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/versions/{versionId}/generation-requests", UriKind.Relative), "{}");
        var unclaimed = (await SetupApi.JsonAsync(created)).GetProperty("id").GetGuid();
        await ExpectAsync(await ObservedCreateApi.PostAsync(client, extension, unclaimed, response, request), HttpStatusCode.Conflict, "request_not_claimed");

        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);

        // The session never reports one; nor does another credential, nor the claimer with a malformed response.
        await ExpectAsync(await ObservedCreateApi.PostAsync(client, null, id, response, request), HttpStatusCode.Forbidden, "credential_required");
        await ExpectAsync(await ObservedCreateApi.PostAsync(client, extension, id, response, request), HttpStatusCode.Forbidden, "request_claimed");
        var malformed = new[]
        {
            new JsonObject { ["clips"] = new JsonArray() },
            new JsonObject { ["id"] = "no-clips", ["clips"] = new JsonArray() },
            new JsonObject { ["id"] = "clip-without-id", ["clips"] = new JsonArray(new JsonObject { ["status"] = "submitted" }) },
            ObservedCreateApi.Response(Mode, "same-clip-twice", ["twin", "twin"]),
        };
        foreach (var bad in malformed)
        {
            await ExpectAsync(await ObservedCreateApi.PostAsync(client, token, id, bad, request), HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        using var notObject = await ObservedCreateApi.SendAsync(client, HttpMethod.Post, ObservedCreateApi.Path(id), token, new JsonObject { ["response"] = response.DeepClone(), ["request"] = "sent" }.ToJsonString());
        await ExpectAsync(notObject, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Empty(await GenerationsAsync(client, song));
        Assert.Empty(RetentionApi.Dump(factory, "generation_events"));
        Assert.False((await VersionAsync(client, versionId)).GetProperty("isFrozen").GetBoolean());

        // Scope: suno.generate only.
        var sync = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync, CredentialScopes.VersionsWrite, CredentialScopes.CatalogRead);
        using var scoped = await ObservedCreateApi.PostAsync(client, sync, id, response, request);
        Assert.Equal(HttpStatusCode.Forbidden, scoped.StatusCode);

        // Once the request has ended, nothing more is recorded.
        using var cancelled = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/suno/generation-requests/{id}/cancel", UriKind.Relative), "{}");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        await ExpectAsync(await ObservedCreateApi.PostAsync(client, token, id, response, request), HttpStatusCode.Conflict, "request_ended");
        Assert.Empty(await GenerationsAsync(client, song));
    }

    [Fact]
    public async Task WithoutTheRequestBodyTheRequestOnlyOptionsAreAssumedFromTheVersionAndSaidToBe()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Observed unread");
        var versionId = VersionId(song);
        var response = ObservedCreateApi.Response(Mode, "unread-request", ["unread-clip"]);
        await EditAsync(client, versionId, ObservedCreateApi.MatchingEdit(response, request: null));
        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);

        var answered = await ObservedCreateApi.RecordAsync(client, token, id, response, request: null);

        var observed = Assert.Single(answered.GetProperty("observed").EnumerateArray());
        Assert.Equal(ObservedCreates.Attached, observed.GetProperty("outcome").GetString());
        Assert.False(observed.GetProperty("requestRead").GetBoolean());
        var assumed = observed.GetProperty("assumed").EnumerateArray().Select(static key => key.GetString()).ToList();
        Assert.Contains("vocalGender", assumed);
        Assert.Contains("personalize", assumed);
        Assert.Contains("model", assumed);
        Assert.DoesNotContain("weirdness", assumed);
    }

    [Fact]
    public async Task ARequestIsDoneThirtyMinutesAfterTheLastCreateAndTheExtensionMayEndItSooner()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Observed done");
        var versionId = VersionId(song);
        var response = ObservedCreateApi.Response(Mode, "done-request", ["done-clip-1", "done-clip-2"]);
        var request = ObservedCreateApi.Request(Mode);
        await EditAsync(client, versionId, ObservedCreateApi.MatchingEdit(response, request));
        var (id, token) = await ObservedCreateApi.WaitingRequestAsync(factory, client, versionId);
        await ObservedCreateApi.RecordAsync(client, token, id, response, request);

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal("waiting", (await CurrentAsync(client, versionId)).GetProperty("state").GetString());
        clock.Advance(TimeSpan.FromMinutes(1));
        var done = await CurrentAsync(client, versionId);
        Assert.Equal("done", done.GetProperty("state").GetString());
        Assert.Equal("1 Create recorded, with 2 Generations.", done.GetProperty("message").GetString());

        // The extension ends it when the user leaves the Create form.
        var next = await SongApi.CreateAsync(client, "Observed left");
        var (other, otherToken) = await ObservedCreateApi.WaitingRequestAsync(factory, client, VersionId(next));
        using var left = await ObservedCreateApi.SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/suno/generation-requests/{other}", UriKind.Relative), otherToken, """{"state":"done","step":"left the Create form"}""");
        Assert.Equal(HttpStatusCode.OK, left.StatusCode);
        Assert.Equal("done", (await CurrentAsync(client, VersionId(next))).GetProperty("state").GetString());
    }

    private static Guid VersionId(JsonElement song) => song.GetProperty("currentVersion").GetProperty("id").GetGuid();

    private static async Task<JsonElement> VersionAsync(HttpClient client, Guid versionId)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{versionId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> CurrentAsync(HttpClient client, Guid versionId)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{versionId}/generation-request", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("request");
    }

    private static async Task<List<JsonElement>> GenerationsAsync(HttpClient client, JsonElement song)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/songs/{song.GetProperty("shortcode").GetString()}/generations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
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

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, body);
            Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        }
    }
}
