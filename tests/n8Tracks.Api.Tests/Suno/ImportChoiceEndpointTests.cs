using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using static n8Tracks.Api.Tests.Suno.ProposalApi;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Changing choices (#138): <c>PATCH /api/v1/suno/exports/{id}/records</c> checks every changed record on
/// the server and is refused whole, with reasons per record, when any is invalid. Choices live on the
/// staged export only.
/// </summary>
public sealed class ImportChoiceEndpointTests
{
    /// <summary>A clip may go to a mutable Version holding its inputs; the export's revision goes up and the record holds the choice.</summary>
    [Fact]
    public async Task AttachingToAMutableVersionWithTheSameInputsIsAccepted()
    {
        await using var review = await ReviewAsync();

        var export = await ChangedAsync(review.Client, review.ExportId, 1, Change(Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v2" }), "a"));

        Assert.Equal(2, export.GetProperty("revision").GetInt32());
        var record = (await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId))["a"];
        Assert.Equal(review.MutableVersion, record.GetProperty("choice").GetProperty("target").GetProperty("version").GetGuid());
        Assert.Equal("newSong", Text(Target(record), "kind"));
    }

    /// <summary>
    /// A clip whose inputs are not the Version's is refused, and the whole change with it: the record
    /// whose inputs match keeps its choice too, and the revision does not move (complement).
    /// </summary>
    [Fact]
    public async Task AttachingToAVersionWithDifferentInputsIsRefusedAndNoChoiceChanges()
    {
        await using var review = await ReviewAsync();
        var before = await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId);

        var reasons = await RefusedAsync(review.Client, review.ExportId, 1, Change(Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v2" }), "a", "b"));

        Assert.Equal(["b"], reasons.Keys);
        Assert.Equal(["inputs_differ"], reasons["b"]);
        reasons = await RefusedAsync(review.Client, review.ExportId, 1, Change(Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v1" }), "a"));
        Assert.Equal(["inputs_differ"], reasons["a"]);

        var after = await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId);
        Assert.All(before.Keys, id => Assert.Equal(before[id].GetProperty("choice").GetRawText(), after[id].GetProperty("choice").GetRawText()));
        Assert.Equal(1, (await SunoExportApi.GetAsync(review.Client, null, review.ExportId)).GetProperty("revision").GetInt32());
    }

    /// <summary>
    /// A new Version's number: a child or sibling of the chosen parent as #61 offers them, or a top-level
    /// number above every one the Song used; anything else is refused.
    /// </summary>
    [Fact]
    public async Task ANewVersionGetsANumberTheRulesAllow()
    {
        await using var review = await ReviewAsync();

        static JsonObject NewVersion(string number, string? parent) => Import(new JsonObject
        {
            ["kind"] = "newVersion",
            ["key"] = "new:20",
            ["song"] = "n8-1",
            ["parentVersion"] = parent,
            ["number"] = number,
        });

        Assert.Equal(["invalid_number"], (await RefusedAsync(review.Client, review.ExportId, 1, Change(NewVersion("5", "n8-1-v2"), "a")))["a"]);
        Assert.Equal(["invalid_number"], (await RefusedAsync(review.Client, review.ExportId, 1, Change(NewVersion("2", null), "a")))["a"]);
        Assert.Equal(["invalid_number"], (await RefusedAsync(review.Client, review.ExportId, 1, Change(NewVersion("3.1", null), "a")))["a"]);
        await ChangedAsync(review.Client, review.ExportId, 1, Change(NewVersion("2.1", "n8-1-v2"), "a"));
        await ChangedAsync(review.Client, review.ExportId, 2, Change(NewVersion("4", "n8-1-v2"), "a"));
        Assert.Equal(["invalid_number"], (await RefusedAsync(review.Client, review.ExportId, 3, Change(NewVersion("3", "n8-1-v2"), "a")))["a"]);
        await ChangedAsync(review.Client, review.ExportId, 3, Change(NewVersion("7", null), "a"));

        var target = (await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId))["a"].GetProperty("choice").GetProperty("target");
        Assert.Equal(review.SongId, target.GetProperty("song").GetGuid());
        Assert.Equal("7", Text(target, "number"));

        // Two new Versions of one Song cannot take the same number.
        Assert.Equal(
            ["invalid_number"],
            (await RefusedAsync(review.Client, review.ExportId, 4, Change(Import(new JsonObject { ["kind"] = "newVersion", ["key"] = "new:21", ["song"] = "n8-1", ["parentVersion"] = null, ["number"] = "7" }), "b")))["b"]);
    }

    /// <summary>
    /// New targets are shared by temporary key: every record naming a key must describe the same target and
    /// hold the same inputs; a new Version of a new Song needs a record creating that Song, and a change that
    /// would leave one without it is refused.
    /// </summary>
    [Fact]
    public async Task RecordsSharingATemporaryKeyMustAgree()
    {
        await using var review = await ReviewAsync();
        var records = await SunoExportApi.RecordsByIdAsync(review.Client, review.ExportId);
        var songOfA = Target(records["a"]);
        var key = Text(songOfA, "key")!;

        var renamed = Import(new JsonObject { ["kind"] = "newSong", ["key"] = key, ["title"] = "Another title", ["workspaceId"] = "free" });
        Assert.Equal(["inputs_differ", "target_conflict"], (await RefusedAsync(review.Client, review.ExportId, 1, Change(renamed, "b")))["b"]);
        var same = Import(JsonNode.Parse(songOfA.GetRawText())!.AsObject());
        Assert.Equal(["inputs_differ"], (await RefusedAsync(review.Client, review.ExportId, 1, Change(same, "b")))["b"]);

        var unknownSong = Import(new JsonObject { ["kind"] = "newVersion", ["key"] = "new:30", ["song"] = "new:29", ["parentVersion"] = null, ["number"] = "2" });
        Assert.Equal(["target_missing"], (await RefusedAsync(review.Client, review.ExportId, 1, Change(unknownSong, "b")))["b"]);
        var ofSongOfA = Import(new JsonObject { ["kind"] = "newVersion", ["key"] = "new:30", ["song"] = key, ["parentVersion"] = null, ["number"] = "2" });
        await ChangedAsync(review.Client, review.ExportId, 1, Change(ofSongOfA, "b"));

        // Skipping a would leave b's new Version without its new Song.
        Assert.Equal(["target_missing"], (await RefusedAsync(review.Client, review.ExportId, 2, Change(new JsonObject { ["action"] = "skip" }, "a")))["b"]);
    }

    /// <summary>Every refusal reason the server gives for a record or a target.</summary>
    [Fact]
    public async Task ChoicesThatCannotBeMadeAreRefusedWithTheirReason()
    {
        await using var review = await ReviewAsync();
        var other = await SongApi.CreateAsync(review.Client, "Other song");

        async Task<string[]> ReasonsAsync(JsonObject choice, string sunoId) =>
            (await RefusedAsync(review.Client, review.ExportId, 1, Change(choice, sunoId)))[sunoId];

        Assert.Equal(["already_linked"], await ReasonsAsync(Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v3" }), "linked"));
        Assert.Equal(["already_linked"], await ReasonsAsync(new JsonObject { ["action"] = "ignore" }, "linked"));
        Assert.Equal(["record_not_found"], await ReasonsAsync(new JsonObject { ["action"] = "skip" }, "not-in-export"));
        Assert.Equal(["target_missing"], await ReasonsAsync(Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v9" }), "a"));
        Assert.Equal(["target_missing"], await ReasonsAsync(Import(new JsonObject { ["kind"] = "newVersion", ["key"] = "new:40", ["song"] = "n8-99", ["parentVersion"] = null, ["number"] = "2" }), "a"));
        Assert.Equal(
            ["parent_not_in_song"],
            await ReasonsAsync(Import(new JsonObject { ["kind"] = "newVersion", ["key"] = "new:40", ["song"] = other.GetProperty("shortcode").GetString(), ["parentVersion"] = "n8-1-v2", ["number"] = "2.1" }), "a"));
        Assert.Equal(["invalid_title"], await ReasonsAsync(Import(new JsonObject { ["kind"] = "newSong", ["key"] = "new:40", ["title"] = "Two\nlines" }), "a"));

        // Skip and ignore are always open to a new record, and Skip to a linked one.
        await ChangedAsync(review.Client, review.ExportId, 1, Change(new JsonObject { ["action"] = "skip" }, "linked"));
        await ChangedAsync(review.Client, review.ExportId, 2, Change(new JsonObject { ["action"] = "ignore" }, "a", "b"));
    }

    /// <summary>The request itself: If-Match on the export's revision, a ready export, a well-formed body, and a signed-in session.</summary>
    [Fact]
    public async Task AChangeNeedsTheRevisionAReadyExportAWellFormedBodyAndASession()
    {
        await using var review = await ReviewAsync();
        var skip = Change(new JsonObject { ["action"] = "skip" }, "a");

        using (var response = await PatchAsync(review.Client, review.ExportId, null, skip))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        }

        using (var response = await PatchAsync(review.Client, review.ExportId, "\"2\"", skip))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await SetupApi.JsonAsync(response);
            Assert.Equal("revision_conflict", problem.GetProperty("code").GetString());
            Assert.Equal(1, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var response = await PatchAsync(review.Client, review.ExportId, "\"1\"", """{"sunoIds":["a"],"choice":{"action":"later"}}"""))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await SetupApi.JsonAsync(response);
            Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
            Assert.True(problem.GetProperty("errors").TryGetProperty("choice.action", out _));
        }

        using (var response = await PatchAsync(review.Client, review.ExportId, "\"1\"", skip, review.Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("session_required", (await SetupApi.JsonAsync(response)).GetProperty("code").GetString());
        }

        using (var response = await PatchAsync(review.Client, Guid.CreateVersion7(), "\"1\"", skip))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var response = await review.Client.GetAsync(SunoExportApi.Export(review.ExportId)))
        {
            Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        }

        using (var discarded = await SunoExportApi.SendAsync(review.Client, HttpMethod.Post, SunoExportApi.Export(review.ExportId, "/discard"), null))
        {
            Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
        }

        using (var response = await PatchAsync(review.Client, review.ExportId, "\"1\"", skip))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("export_not_ready", (await SetupApi.JsonAsync(response)).GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// A ready export of three records: <c>a</c> (a new clip with the template inputs, in an unassociated
    /// workspace), <c>b</c> (a new clip with other lyrics, made later), and <c>linked</c> (held by a
    /// Generation of Version 3). The Song <c>n8-1</c> has Version 1 (its own inputs), Version 2 (mutable,
    /// holding the template inputs), and Version 3 (frozen).
    /// </summary>
    private static async Task<Review> ReviewAsync()
    {
        var factory = SongApi.Host();
        var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Target");
        var mutable = await ImportedVersions.AddAsync(factory, song.GetProperty("id").GetGuid(), "2", Clip("template", "free", At, 0));
        var linked = Clip("linked", "free", At.AddDays(-1), 0, lyrics: "[Verse]\nHeld");
        await ImportedVersions.AttachAsync(factory, song, "3", linked);

        var (id, _) = await ExportAsync(client, token, Clip("a", "free", At, 0), Clip("b", "free", At.AddMinutes(1), 0, lyrics: "[Verse]\nOther"), linked);
        return new Review(factory, client, token, id, song.GetProperty("id").GetGuid(), mutable);
    }

    private sealed record Review(N8TracksApiFactory Factory, HttpClient Client, string Token, Guid ExportId, Guid SongId, Guid MutableVersion) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
