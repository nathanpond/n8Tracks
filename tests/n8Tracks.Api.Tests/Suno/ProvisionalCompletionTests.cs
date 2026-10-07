using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Assets;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Suno;
using SkiaSharp;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Filling in the Generations an observed Create made once Suno finishes their clips (#154): once, only
/// for a Generation that has never been complete and that the request's own observed Create made, never
/// touching what the user set meanwhile; a clip that ended in error is recorded as failed.
/// </summary>
public sealed class ProvisionalCompletionTests
{
    [Fact]
    public async Task AProvisionalGenerationIsFilledInOnceAndASecondReportIsRefusedChangingNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var observed = await ProvisionalCompletionApi.ObservedAsync(factory, client, "Completion once", "once-1", "once-2");
        var (generationId, shortcode) = observed.Generations["once-1"];
        Assert.Equal("submitted", (await ProvisionalCompletionApi.GenerationAsync(client, shortcode)).GetProperty("providerStatus").GetString());

        var answer = await ProvisionalCompletionApi.CompleteAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished("once-1"));

        Assert.Equal("completed", answer.GetProperty("outcome").GetString());
        Assert.Equal(generationId, answer.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal(shortcode, answer.GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal("complete", answer.GetProperty("generation").GetProperty("providerStatus").GetString());
        var completed = await ProvisionalCompletionApi.GenerationAsync(client, shortcode);
        Assert.Equal("complete", completed.GetProperty("providerStatus").GetString());
        Assert.Equal(143.52, completed.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("C_major", completed.GetProperty("key").GetString());
        Assert.Equal("https://cdn2.suno.ai/image_00000000-0000-4000-8000-000000000003.jpeg", completed.GetProperty("imageUrl").GetString());
        Assert.Equal("once-1", completed.GetProperty("sunoId").GetString());

        // The finished clip is the provider record now, as received.
        var payload = TestDatabase.Scalar(factory.DataPath, $"SELECT payload FROM provider_records WHERE generation_id = '{generationId.ToString().ToUpperInvariant()}';");
        Assert.Equal(143.52, JsonNode.Parse(payload)!["metadata"]!["duration"]!.GetValue<double>());

        // The other clip is still generating: one report fills in one Generation.
        var other = await ProvisionalCompletionApi.GenerationAsync(client, observed.Generations["once-2"].Shortcode);
        Assert.Equal("submitted", other.GetProperty("providerStatus").GetString());

        // A second report, even with other data, is refused and changes nothing.
        var before = Snapshot(factory);
        var later = ProvisionalCompletionApi.Finished("once-1");
        later["metadata"]!["duration"] = 99.0;
        await ProvisionalCompletionApi.ExpectAsync(
            await ProvisionalCompletionApi.PostAsync(client, observed.Token, observed.RequestId, later),
            HttpStatusCode.Conflict,
            "already_complete");
        Assert.Equal(before, Snapshot(factory));
    }

    [Fact]
    public async Task RatingCommentsAndStateSetWhileGeneratingAreByteIdenticalAfterwardsAndTheCoverGoesThroughTheArtworkUpload()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var observed = await ProvisionalCompletionApi.ObservedAsync(factory, client, "Completion keeps choices", "keep-1", "keep-2");
        var (generationId, shortcode) = observed.Generations["keep-1"];
        await ImportCommitApi.RateAndCommentAsync(client, shortcode, 4, "Keep this one.");
        await ArchiveAsync(client, shortcode);
        var id = generationId.ToString().ToUpperInvariant();
        string Kept() => string.Join(
            '\n',
            TestDatabase.Rows(factory.DataPath, $"SELECT rating, state, archived_by, revision, remote_state, coalesce(artwork_asset_id, 'none') FROM generations WHERE id = '{id}';")
                .Concat(TestDatabase.Rows(factory.DataPath, $"SELECT id, text, created_utc, coalesce(edited_utc, 'never'), revision FROM generation_comments WHERE generation_id = '{id}';")));
        var before = Kept();

        await ProvisionalCompletionApi.CompleteAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished("keep-1"));

        Assert.Equal(before, Kept());
        var completed = await ProvisionalCompletionApi.GenerationAsync(client, shortcode);
        Assert.Equal(4, completed.GetProperty("rating").GetInt32());
        Assert.Equal("archived", completed.GetProperty("state").GetString());
        Assert.Equal("Keep this one.", completed.GetProperty("comments")[0].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("artwork").ValueKind);

        // The cover goes through the Generation artwork upload (#121), with the extension's token.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(ArtworkImages.Solid(SKEncodedImageFormat.Png, 32, 32, ArtworkImages.Blue));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "cover.png");
        using var upload = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/generations/{generationId}/artwork", UriKind.Relative)) { Content = form };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", observed.Token);
        using var uploaded = await client.SendAsync(upload);
        Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
        Assert.NotEqual(JsonValueKind.Null, (await ProvisionalCompletionApi.GenerationAsync(client, shortcode)).GetProperty("artwork").ValueKind);
    }

    [Fact]
    public async Task AGenerationMadeByImportIsRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var observed = await ProvisionalCompletionApi.ObservedAsync(factory, client, "Completion by import", "own-1");
        var other = await SongApi.CreateAsync(client, "Imported");
        var otherVersion = other.GetProperty("currentVersion").GetProperty("id").GetGuid().ToString();
        await SongApi.AttachGenerationAsync(factory, otherVersion, Clips.Minimal("imported-streaming", "streaming"));
        await SongApi.AttachGenerationAsync(factory, otherVersion, Clips.Minimal("imported-complete"));
        var before = Snapshot(factory);

        foreach (var sunoId in new[] { "imported-streaming", "imported-complete" })
        {
            await ProvisionalCompletionApi.ExpectAsync(
                await ProvisionalCompletionApi.PostAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished(sunoId)),
                HttpStatusCode.Conflict,
                "not_provisional");
        }

        // A clip no Generation holds.
        await ProvisionalCompletionApi.ExpectAsync(
            await ProvisionalCompletionApi.PostAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished("never-seen")),
            HttpStatusCode.NotFound,
            "not_found");
        Assert.Equal(before, Snapshot(factory));
    }

    [Fact]
    public async Task AGenerationAnImportReviewDecidedAboutIsNeverCompleted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var observed = await ProvisionalCompletionApi.ObservedAsync(factory, client, "Completion after review", "review-1", "review-2");
        var declined = observed.Generations["review-1"].Id.ToString().ToUpperInvariant();
        var kept = observed.Generations["review-2"].Id.ToString().ToUpperInvariant();
        TestDatabase.Execute(factory.DataPath, $"UPDATE generations SET declined_hash = 'declined' WHERE id = '{declined}';");
        TestDatabase.Execute(factory.DataPath, $"UPDATE generations SET kept_inputs_hash = 'kept' WHERE id = '{kept}';");
        var before = Snapshot(factory);

        foreach (var sunoId in new[] { "review-1", "review-2" })
        {
            await ProvisionalCompletionApi.ExpectAsync(
                await ProvisionalCompletionApi.PostAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished(sunoId)),
                HttpStatusCode.Conflict,
                "not_provisional");
        }

        Assert.Equal(before, Snapshot(factory));
    }

    [Fact]
    public async Task AClipThatEndedInErrorIsRecordedAsFailedAndCanBeDeleted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var observed = await ProvisionalCompletionApi.ObservedAsync(factory, client, "Completion failed", "failed-1", "failed-2");
        var shortcode = observed.Generations["failed-2"].Shortcode;

        var answer = await ProvisionalCompletionApi.CompleteAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished("failed-2", ProvisionalCompletionRules.Error));

        Assert.Equal("failed", answer.GetProperty("outcome").GetString());
        Assert.Equal("error", (await ProvisionalCompletionApi.GenerationAsync(client, shortcode)).GetProperty("providerStatus").GetString());
        await ProvisionalCompletionApi.ExpectAsync(
            await ProvisionalCompletionApi.PostAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished("failed-2")),
            HttpStatusCode.Conflict,
            "already_complete");

        await ProposalApi.DeleteGenerationAsync(client, shortcode);
        using var gone = await client.GetAsync(new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task OnlyTheClaimingExtensionReportsAFinishedClipEvenOnceTheRequestHasEnded()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var observed = await ProvisionalCompletionApi.ObservedAsync(factory, client, "Completion callers", "caller-1");
        var other = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var finished = ProvisionalCompletionApi.Finished("caller-1");
        var before = Snapshot(factory);

        await ProvisionalCompletionApi.ExpectAsync(await ProvisionalCompletionApi.PostAsync(client, null, observed.RequestId, finished), HttpStatusCode.Forbidden, "credential_required");
        await ProvisionalCompletionApi.ExpectAsync(await ProvisionalCompletionApi.PostAsync(client, other, observed.RequestId, finished), HttpStatusCode.Forbidden, "request_claimed");
        await ProvisionalCompletionApi.ExpectAsync(await ProvisionalCompletionApi.PostAsync(client, observed.Token, Guid.NewGuid(), finished), HttpStatusCode.NotFound, "not_found");

        // Not a finished clip: still streaming, not an object, or no clip at all.
        await ProvisionalCompletionApi.ExpectAsync(
            await ProvisionalCompletionApi.PostAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished("caller-1", "streaming")),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");
        await ProvisionalCompletionApi.ExpectAsync(
            await ProvisionalCompletionApi.PostAsync(client, observed.Token, observed.RequestId, JsonValue.Create("caller-1")),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");
        await ProvisionalCompletionApi.ExpectAsync(
            await ProvisionalCompletionApi.PostAsync(client, observed.Token, observed.RequestId, null),
            HttpStatusCode.UnprocessableEntity,
            "validation_failed");
        Assert.Equal(before, Snapshot(factory));

        // The user left the Create page: the request is done, and the clip still completes.
        using (var done = await ObservedCreateApi.SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/suno/generation-requests/{observed.RequestId}", UriKind.Relative), observed.Token, """{"state":"done","step":"left the Create page"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        }

        var answer = await ProvisionalCompletionApi.CompleteAsync(client, observed.Token, observed.RequestId, finished);
        Assert.Equal("completed", answer.GetProperty("outcome").GetString());
    }

    [Fact]
    public void OnlyAClipNeverFinishedTakesAFinishedOneOfTheSameSunoId()
    {
        ClipFields Clip(string id, string? status) => new(id, status, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        Assert.True(ProvisionalCompletionRules.MayComplete(Clip("a", "submitted"), Clip("a", "complete")));
        Assert.True(ProvisionalCompletionRules.MayComplete(Clip("a", "streaming"), Clip("a", "error")));
        Assert.True(ProvisionalCompletionRules.MayComplete(Clip("a", null), Clip("a", "complete")));
        Assert.False(ProvisionalCompletionRules.MayComplete(Clip("a", "complete"), Clip("a", "complete")));
        Assert.False(ProvisionalCompletionRules.MayComplete(Clip("a", "error"), Clip("a", "complete")));
        Assert.False(ProvisionalCompletionRules.MayComplete(Clip("a", "submitted"), Clip("a", "streaming")));
        Assert.False(ProvisionalCompletionRules.MayComplete(Clip("a", "submitted"), Clip("b", "complete")));
    }

    /// <summary>Every catalog row, as the invariant 3 guard reads them, as one text.</summary>
    private static string Snapshot(N8TracksApiFactory factory) =>
        string.Join(
            '\n',
            Invariants.ImportNeverOverwritesGuardTests.Rows(factory.DataPath)
                .OrderBy(static table => table.Key, StringComparer.Ordinal)
                .SelectMany(static table => table.Value.OrderBy(static row => row.Key, StringComparer.Ordinal).Select(row => $"{table.Key}:{row.Value.Text}")));

    private static async Task ArchiveAsync(HttpClient client, string shortcode)
    {
        var uri = new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative);
        var revision = (await ProvisionalCompletionApi.GenerationAsync(client, shortcode)).GetProperty("revision").GetInt32();
        using var request = new HttpRequestMessage(HttpMethod.Patch, uri) { Content = new StringContent("""{"state":"archived"}""", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\""));
        using var archived = await client.SendAsync(request);
        Assert.True(archived.StatusCode == HttpStatusCode.OK, await archived.Content.ReadAsStringAsync());
    }
}
