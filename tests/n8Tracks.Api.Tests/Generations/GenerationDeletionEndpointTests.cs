using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Assets;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Backups;
using n8Tracks.Api.Tests.Invariants;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Retention;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Generations;

/// <summary>
/// Deleting a Generation (#124): <c>GET /api/v1/generations/{reference}/deletion-impact</c> and
/// <c>DELETE /api/v1/generations/{reference}</c>, session-only, under the Generation's revision. The
/// Generation, its comments, provider record, and own artwork go into retention as one group; the
/// Song's selection is resolved first by the user's choice; its Version stays frozen; and its ordinal
/// and shortcode are never given out again. A restore brings back the rating, comments, and artwork,
/// never the selection or the Song's state.
/// </summary>
public sealed class GenerationDeletionEndpointTests
{
    private const string Origin = "n8-1";
    private const string First = "n8-1-v1-g1";
    private const string Second = "n8-1-v1-g2";

    [Fact]
    public async Task TheImpactSaysWhatGoesWhoUsesItAndWhatTheSongMaySelectInstead()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await EvaluateAsync(client, First);
        await DerivedFromAsync(factory, client, First);

        // Not selected: no replacements are offered.
        var impact = await ImpactAsync(client, First);
        Assert.Equal(First, impact.GetProperty("shortcode").GetString());
        Assert.False(impact.GetProperty("isSelected").GetBoolean());
        Assert.Equal(0, impact.GetProperty("replacements").GetArrayLength());
        Assert.Equal(1, impact.GetProperty("commentCount").GetInt32());
        Assert.Equal(1, impact.GetProperty("artworkCount").GetInt32());
        Assert.Equal(1, impact.GetProperty("sourceVersionCount").GetInt32());
        Assert.Equal((await GenerationAsync(client, First)).GetProperty("revision").GetInt32(), impact.GetProperty("revision").GetInt32());

        // Selected: every other live Generation of the Song, archived ones included.
        await PatchAsync(client, Second, """{"state":"archived"}""");
        await SelectAsync(client, First);
        impact = await ImpactAsync(client, First);
        Assert.True(impact.GetProperty("isSelected").GetBoolean());
        Assert.Equal([Second], impact.GetProperty("replacements").EnumerateArray().Select(static each => each.GetProperty("shortcode").GetString()).ToList());

        // Complement: a Generation with nothing attached has nothing to count.
        var bare = await ImpactAsync(client, Second);
        Assert.Equal(0, bare.GetProperty("commentCount").GetInt32());
        Assert.Equal(0, bare.GetProperty("artworkCount").GetInt32());
        Assert.Equal(0, bare.GetProperty("sourceVersionCount").GetInt32());

        using (var missing = await client.GetAsync(Uri("generations/n8-1-v1-g9/deletion-impact")))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }
    }

    [Fact]
    public async Task AGenerationNotSelectedGoesIntoOneGroupWithItsCommentsProviderRecordAndArtworkAndTheSongsArtworkStays()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionId = await OriginAsync(factory, client);
        await EvaluateAsync(client, First);
        var derived = await DerivedFromAsync(factory, client, First);
        var derivedBefore = VersionImmutabilityGuardTests.Stored(factory, derived);

        // The Song's own artwork is a copy of the Generation's image.
        var songBefore = await SongAsync(client);
        using (var copied = await SendAsync(client, HttpMethod.Post, $"songs/{Origin}/artwork/from-generation", songBefore.GetProperty("revision").GetInt32(), $$"""{"generation":"{{First}}"}"""))
        {
            Assert.True(copied.StatusCode == HttpStatusCode.OK, await copied.Content.ReadAsStringAsync());
        }

        songBefore = await SongAsync(client);
        var generation = await GenerationAsync(client, First);
        var deleted = await DeleteAsync(client, First, generation.GetProperty("revision").GetInt32(), json: null, HttpStatusCode.OK);

        // Gone from every read; the Song is answered with its selection and state as they were.
        Assert.Equal(songBefore.GetProperty("revision").GetInt32(), deleted.GetProperty("song").GetProperty("revision").GetInt32());
        Assert.Equal(songBefore.GetProperty("state").GetRawText(), deleted.GetProperty("song").GetProperty("state").GetRawText());
        using (var gone = await client.GetAsync(Uri($"generations/{First}")))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        Assert.Equal([Second], await ShortcodesAsync(client, $"songs/{Origin}/generations"));

        // One group: the Generation, its comment, and its provider record, with its image's files.
        var group = await GroupAsync(factory, First);
        Assert.Equal(RetainedRecordTypes.Generation, group.Kind);
        Assert.Equal($"Generation {First}", group.Label);
        Assert.Equal(
            [RetainedRecordTypes.Generation, RetainedRecordTypes.GenerationComment, RetainedRecordTypes.ProviderRecord],
            group.Records.Select(static record => record.RecordType).Order(StringComparer.Ordinal));
        Assert.NotEmpty(group.Files);
        Assert.All(group.Files, file => Assert.StartsWith("artwork/", file, StringComparison.Ordinal));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM generation_comments WHERE generation_id = '{Upper(generation.GetProperty("id").GetGuid())}';"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM provider_records WHERE generation_id = '{Upper(generation.GetProperty("id").GetGuid())}';"));

        // The Song's own artwork is untouched.
        var songAfter = await SongAsync(client);
        Assert.Equal(songBefore.GetProperty("artwork").GetRawText(), songAfter.GetProperty("artwork").GetRawText());

        // The Version stays frozen, and the next Generation takes the next ordinal, never the deleted one's.
        var version = await SetupApi.JsonAsync(await client.GetAsync(Uri($"versions/{versionId}")));
        Assert.True(version.GetProperty("isFrozen").GetBoolean());
        Assert.Equal("n8-1-v1-g3", (await SongApi.AttachGenerationAsync(factory, "n8-1-v1")).Shortcode);
        using (var stillGone = await client.GetAsync(Uri($"generations/{First}")))
        {
            Assert.Equal(HttpStatusCode.NotFound, stillGone.StatusCode);
        }

        // A Version that used it as a source still points at its Suno ID, labelled Deleted, unchanged.
        var source = Assert.Single((await SetupApi.JsonAsync(await client.GetAsync(Uri($"versions/{derived}")))).GetProperty("inputs").GetProperty("sources").EnumerateArray());
        Assert.Equal("clip-one", source.GetProperty("external").GetProperty("sunoId").GetString());
        Assert.Equal("Deleted", source.GetProperty("external").GetProperty("label").GetString());
        Assert.Equal(derivedBefore, VersionImmutabilityGuardTests.Stored(factory, derived));
    }

    [Fact]
    public async Task WithAReplacementTheSongSelectsItAndKeepsItsWorkflowState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await SelectAsync(client, First);
        var before = await SongAsync(client);

        var deleted = await DeleteAsync(client, First, 1, $$"""{"replacementGeneration":"{{Second}}"}""", HttpStatusCode.OK);

        var song = deleted.GetProperty("song");
        Assert.Equal(Second, song.GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
        Assert.Equal(before.GetProperty("state").GetRawText(), song.GetProperty("state").GetRawText());
        Assert.Equal(before.GetProperty("revision").GetInt32() + 1, song.GetProperty("revision").GetInt32());
        Assert.Equal(Second, (await SongAsync(client)).GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task WithAWorkflowStateTheSelectionIsClearedAndTheStateAppliedWhichIsTheOnlyChoiceForASongWithNoOtherGeneration()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await SelectAsync(client, First);
        var state = DefaultWorkflowStates.All[2].Id;

        var deleted = await DeleteAsync(client, First, 1, $$"""{"workflowState":"{{state}}"}""", HttpStatusCode.OK);

        var song = deleted.GetProperty("song");
        Assert.Equal(JsonValueKind.Null, song.GetProperty("selectedGeneration").ValueKind);
        Assert.False(song.GetProperty("hasSelectedGeneration").GetBoolean());
        Assert.Equal(state, song.GetProperty("state").GetProperty("id").GetGuid());

        // The Song's last Generation: there is no other to choose, so only a state will do.
        await SelectAsync(client, Second);
        var impact = await ImpactAsync(client, Second);
        Assert.Equal(0, impact.GetProperty("replacements").GetArrayLength());
        await ProblemAsync(client, Second, 1, $$"""{"replacementGeneration":"{{Second}}"}""", "invalid_replacement");
        var other = DefaultWorkflowStates.All[1].Id;
        var last = await DeleteAsync(client, Second, 1, $$"""{"workflowState":"{{other}}"}""", HttpStatusCode.OK);
        Assert.Equal(other, last.GetProperty("song").GetProperty("state").GetProperty("id").GetGuid());
        Assert.Empty(await ShortcodesAsync(client, $"songs/{Origin}/generations"));
    }

    [Fact]
    public async Task AMissingOrWrongChoiceAStaleOrMissingRevisionAnUnknownGenerationAndABearerTokenAreRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        var other = await SongApi.CreateAsync(client, "Other");
        var foreign = await SongApi.AttachGenerationAsync(factory, other.GetProperty("currentVersion").GetProperty("id").GetString()!);
        await SelectAsync(client, First);
        var state = DefaultWorkflowStates.All[1].Id;
        var before = RestoreApi.Fingerprint(factory.DataPath, "credentials");

        // Selected, with no choice: 422 selection_choice_required, naming it.
        var required = await ProblemAsync(client, First, 1, json: null, "selection_choice_required");
        Assert.Equal(First, required.GetProperty("shortcode").GetString());
        await ProblemAsync(client, First, 1, "{}", "selection_choice_required");

        // Both choices, a Generation of another Song, itself, or no such Generation or state: invalid_replacement.
        var both = await ProblemAsync(client, First, 1, $$"""{"replacementGeneration":"{{Second}}","workflowState":"{{state}}"}""", "invalid_replacement");
        Assert.True(both.GetProperty("errors").TryGetProperty("replacementGeneration", out _));
        await ProblemAsync(client, First, 1, $$"""{"replacementGeneration":"{{foreign.Shortcode}}"}""", "invalid_replacement");
        await ProblemAsync(client, First, 1, $$"""{"replacementGeneration":"{{First}}"}""", "invalid_replacement");
        await ProblemAsync(client, First, 1, """{"replacementGeneration":"n8-1-v1-g9"}""", "invalid_replacement");
        var badState = await ProblemAsync(client, First, 1, $$"""{"workflowState":"{{Guid.CreateVersion7()}}"}""", "invalid_replacement");
        Assert.True(badState.GetProperty("errors").TryGetProperty("workflowState", out _));

        // A choice for a Generation that is not selected is not wanted.
        await ProblemAsync(client, Second, 1, $$"""{"workflowState":"{{state}}"}""", "invalid_replacement");

        // A field of the wrong type; a body that is not a JSON object.
        await ProblemAsync(client, First, 1, """{"workflowState":7}""", "validation_failed");
        await ProblemAsync(client, First, 1, "[]", "invalid_request", HttpStatusCode.BadRequest);
        await ProblemAsync(client, First, 1, "not json", "invalid_request", HttpStatusCode.BadRequest);

        // Stale, missing, and malformed revisions; an unknown Generation.
        var conflict = await ProblemAsync(client, First, 2, $$"""{"workflowState":"{{state}}"}""", "revision_conflict", HttpStatusCode.Conflict);
        Assert.Equal(First, conflict.GetProperty("current").GetProperty("shortcode").GetString());
        await ProblemAsync(client, First, revision: null, $$"""{"workflowState":"{{state}}"}""", "revision_required", HttpStatusCode.PreconditionRequired);
        await ProblemAsync(client, "n8-1-v1-g9", 1, json: null, "not_found", HttpStatusCode.NotFound);

        // Session-only: a token with every scope is refused, for the delete and the impact alike.
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        using (var bearer = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Delete, Uri($"generations/{Second}"), token))
        {
            await SetupApi.ProblemAsync(bearer, HttpStatusCode.Forbidden, "session_required");
        }

        using (var bearerImpact = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Get, Uri($"generations/{Second}/deletion-impact"), token))
        {
            await SetupApi.ProblemAsync(bearerImpact, HttpStatusCode.Forbidden, "session_required");
        }

        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath, "credentials"));
        Assert.Equal([First, Second], await ShortcodesAsync(client, $"songs/{Origin}/generations"));
    }

    [Fact]
    public async Task ARestoreBringsBackTheRatingCommentsAndArtworkButNeverTheSelectionOrTheState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await EvaluateAsync(client, First);
        var before = await GenerationAsync(client, First);
        await SelectAsync(client, First);
        var state = DefaultWorkflowStates.All[3].Id;
        await DeleteAsync(client, First, before.GetProperty("revision").GetInt32(), $$"""{"workflowState":"{{state}}"}""", HttpStatusCode.OK);
        var songAfterDelete = await SongAsync(client);

        var restored = Assert.IsType<DeletedItemRestoreOutcome.Restored>(await RestoreAsync(factory, First));
        Assert.Equal(RetainedRecordTypes.Generation, restored.Item.Kind);

        var back = await GenerationAsync(client, First);
        foreach (var field in new[] { "id", "rating", "artwork", "state", "sunoId", "title", "ordinal" })
        {
            Assert.Equal(before.GetProperty(field).GetRawText(), back.GetProperty(field).GetRawText());
        }

        // The comment comes back as it was, its revision raised as every restored record's is (#95).
        var comment = Assert.Single(back.GetProperty("comments").EnumerateArray());
        var original = Assert.Single(before.GetProperty("comments").EnumerateArray());
        Assert.Equal(original.GetProperty("id").GetString(), comment.GetProperty("id").GetString());
        Assert.Equal("Keeper", comment.GetProperty("text").GetString());
        Assert.Equal(original.GetProperty("createdAt").GetString(), comment.GetProperty("createdAt").GetString());

        Assert.Equal(before.GetProperty("revision").GetInt32() + 1, back.GetProperty("revision").GetInt32());
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM provider_records WHERE generation_id = '{Upper(back.GetProperty("id").GetGuid())}';"));
        using (var image = await client.GetAsync(new Uri(back.GetProperty("artwork").GetProperty("urls").GetProperty("96").GetString()!, UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        }

        // The selection stays cleared and the state as chosen.
        var song = await SongAsync(client);
        Assert.Equal(JsonValueKind.Null, song.GetProperty("selectedGeneration").ValueKind);
        Assert.Equal(state, song.GetProperty("state").GetProperty("id").GetGuid());
        Assert.Equal(songAfterDelete.GetProperty("revision").GetInt32(), song.GetProperty("revision").GetInt32());
        Assert.False(back.GetProperty("isSelected").GetBoolean());
    }

    [Fact]
    public async Task ARestoreWhoseVersionIsGoneIsRefusedNamingWhereTheVersionWent()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await DeleteAsync(client, First, 1, json: null, HttpStatusCode.OK);
        var version = await SetupApi.JsonAsync(await client.GetAsync(Uri("versions/n8-1-v1")));
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v1", version.GetProperty("revision").GetInt32()))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        var before = RestoreApi.Fingerprint(factory.DataPath);
        var refused = Assert.IsType<DeletedItemRestoreOutcome.Refused>(await RestoreAsync(factory, First));
        Assert.Contains("Version n8-1-v1", refused.Message, StringComparison.Ordinal);
        Assert.Contains("restore-deleted n8-1-v1", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));

        // Once its Version is back, so is it.
        Assert.IsType<DeletedItemRestoreOutcome.Restored>(await RestoreAsync(factory, "n8-1-v1"));
        Assert.IsType<DeletedItemRestoreOutcome.Restored>(await RestoreAsync(factory, First));
        Assert.Equal([First, Second], await ShortcodesAsync(client, $"songs/{Origin}/generations"));
    }

    /// <summary>Song n8-1 with two Generations (Suno IDs clip-one and clip-two) on its frozen Version 1; returns that Version's ID.</summary>
    private static async Task<Guid> OriginAsync(N8TracksApiFactory factory, HttpClient client)
    {
        var song = await SongApi.CreateAsync(client, "Origin");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("clip-one"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("clip-two"));
        return song.GetProperty("currentVersion").GetProperty("id").GetGuid();
    }

    /// <summary>Rates the Generation, comments on it, and gives it an image.</summary>
    private static async Task EvaluateAsync(HttpClient client, string generation)
    {
        await PatchAsync(client, generation, """{"rating":4}""");
        using (var commented = await SongApi.SendJsonAsync(client, HttpMethod.Post, Uri($"generations/{generation}/comments"), """{"text":"Keeper"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, commented.StatusCode);
        }

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(ArtworkImages.Solid(SkiaSharp.SKEncodedImageFormat.Png, 64, 64, ArtworkImages.Red)), "file", "cover.png");
        using var request = new HttpRequestMessage(HttpMethod.Put, Uri($"generations/{generation}/artwork")) { Content = form };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A new Song whose Version 1 uses <paramref name="generation"/> as a Sample source, frozen; returns that Version's ID.</summary>
    private static async Task<Guid> DerivedFromAsync(N8TracksApiFactory factory, HttpClient client, string generation)
    {
        var derived = await SongApi.CreateAsync(client, "Derived");
        var id = derived.GetProperty("currentVersion").GetProperty("id").GetGuid();
        using (var written = await SendAsync(client, HttpMethod.Patch, $"versions/{id}", 1, $$$"""{"inputs":{"sources":[{"typeId":"{{{SystemRelationshipTypes.SampleThisSong.Id}}}","generation":"{{{generation}}}"}]}}"""))
        {
            Assert.True(written.StatusCode == HttpStatusCode.OK, await written.Content.ReadAsStringAsync());
        }

        await SongApi.AttachGenerationAsync(factory, id.ToString());
        return id;
    }

    private static async Task PatchAsync(HttpClient client, string generation, string json)
    {
        var revision = (await GenerationAsync(client, generation)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Patch, $"generations/{generation}", revision, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task SelectAsync(HttpClient client, string generation)
    {
        var revision = (await SongAsync(client)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Put, $"songs/{Origin}/selected-generation", revision, $$"""{"generation":"{{generation}}"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> ImpactAsync(HttpClient client, string generation)
    {
        using var response = await client.GetAsync(Uri($"generations/{generation}/deletion-impact"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> DeleteAsync(HttpClient client, string generation, int revision, string? json, HttpStatusCode expected)
    {
        using var response = await SendAsync(client, HttpMethod.Delete, $"generations/{generation}", revision, json);
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> ProblemAsync(
        HttpClient client,
        string generation,
        int? revision,
        string? json,
        string code,
        HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        using var response = await SendAsync(client, HttpMethod.Delete, $"generations/{generation}", revision, json);
        return await SetupApi.ProblemAsync(response, status, code);
    }

    private static async Task<RetentionGroup> GroupAsync(N8TracksApiFactory factory, string shortcode)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var group = await scope.ServiceProvider.GetRequiredService<RetentionService>().FindByShortcodeAsync(shortcode, CancellationToken.None);
            Assert.NotNull(group);
            return group;
        }
    }

    private static async Task<DeletedItemRestoreOutcome> RestoreAsync(N8TracksApiFactory factory, string reference)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<DeletedItemsService>().RestoreAsync(reference, CancellationToken.None);
        }
    }

    private static async Task<JsonElement> SongAsync(HttpClient client) => await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(Origin)));

    private static async Task<JsonElement> GenerationAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(Uri($"generations/{reference}"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<List<string>> ShortcodesAsync(HttpClient client, string path) =>
        [.. (await SetupApi.JsonAsync(await client.GetAsync(Uri(path)))).GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("shortcode").GetString()!)];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int? revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, Uri(path));
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

    private static Uri Uri(string path) => new($"/api/v1/{path}", UriKind.Relative);

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();
}
