using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Invariants;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Changed and Conflict records of a sync review (#141). <c>GET .../records/{sunoId}/diff</c> answers
/// each differing field side by side, and a Conflict's differing inputs. The records PATCH takes
/// <c>apply</c> with <c>acceptFields</c>, <c>moveToNewVersion</c>, and <c>keep</c>. The commit writes
/// exactly the accepted fields, replaces the provider record only when something was accepted or the
/// conflict was resolved, and moves a Conflict's Generation to a new child Version holding the clip's
/// inputs. The original Version is never altered. Doing nothing (Skip, the default) changes nothing. The
/// invariant guards are <c>ImportNeverOverwritesGuardTests</c> (3) and <c>VersionImmutabilityGuardTests</c> (1).
/// </summary>
public sealed class ChangeResolutionTests
{
    private const string SunoId = "cr-1";
    private const string Shortcode = "n8-1-v2-g1";

    [Fact]
    public async Task AChangedRecordsDiffListsEachDifferingFieldSideBySide()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        var (exportId, records) = await ProposalApi.ExportAsync(client, token, Changed());
        Assert.Equal("changed", records[SunoId].GetProperty("class").GetString());
        Assert.Equal("skip", records[SunoId].GetProperty("choice").GetProperty("action").GetString());

        var diff = await DiffAsync(client, exportId, SunoId);

        Assert.Equal("changed", diff.GetProperty("class").GetString());
        var fields = diff.GetProperty("fields").EnumerateArray().ToDictionary(static field => field.GetProperty("field").GetString()!);
        Assert.Equal(["title", "tags"], fields.Keys);
        Assert.Equal(("Original title", "New title"), (fields["title"].GetProperty("current").GetString(), fields["title"].GetProperty("incoming").GetString()));
        Assert.Equal("new tags, brighter", fields["tags"].GetProperty("incoming").GetString());
        Assert.Empty(diff.GetProperty("inputs").EnumerateArray());
        client.Dispose();
    }

    [Fact]
    public async Task OnlyAChangedOrConflictRecordHasADiff()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        var (exportId, _) = await ProposalApi.ExportAsync(client, token, ProposalApi.Clip("brand-new", null, ProposalApi.At, 0, "New words"));

        using (var notChanged = await client.GetAsync(DiffUri(exportId, "brand-new")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, notChanged.StatusCode);
            Assert.Equal("record_not_changed", (await SetupApi.JsonAsync(notChanged)).GetProperty("code").GetString());
        }

        using (var missing = await client.GetAsync(DiffUri(exportId, "no-such-record")))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using var noExport = await client.GetAsync(DiffUri(Guid.NewGuid(), SunoId));
        Assert.Equal(HttpStatusCode.NotFound, noExport.StatusCode);
        client.Dispose();
    }

    [Fact]
    public async Task AcceptingSomeFieldsWritesExactlyThoseAndReplacesTheRawClip()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        await ImportCommitApi.RateAndCommentAsync(client, Shortcode, 4, "Keep this comment");
        var before = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var changed = Changed();
        var (exportId, _) = await ProposalApi.ExportAsync(client, token, changed);
        await ProposalApi.ChangedAsync(client, exportId, 1, ProposalApi.Change(Apply("title"), SunoId));
        using (var summary = await client.GetAsync(SunoExportApi.Export(exportId, "/summary")))
        {
            var read = await SetupApi.JsonAsync(summary);
            Assert.Equal((1, 0, false), (read.GetProperty("resolved").GetInt32(), read.GetProperty("skipped").GetInt32(), read.GetProperty("nothingToDo").GetBoolean()));
        }

        var result = await ImportCommitApi.CommitAsync(client, exportId);

        var record = ImportCommitApi.Records(result)[SunoId];
        Assert.Equal("updated", ImportCommitApi.Outcome(record));
        var after = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var id = GenerationRowId(after);
        Assert.Equal("New title", after["generations"][id]["suno_title"]);
        Assert.Equal(before["generations"][id]["style_tags"], after["generations"][id]["style_tags"]);
        Assert.Equal(["suno_title", "declined_hash"], ChangedColumns(before["generations"][id], after["generations"][id]));
        Assert.NotNull(after["generations"][id]["declined_hash"]);
        Assert.Equal(changed.ToJsonString(), after["provider_records"][id]["payload"]);
        Assert.Equal(Comments(before), Comments(after));
        client.Dispose();
    }

    [Fact]
    public async Task DecliningEveryFieldChangesOnlyTheRememberedHashAndSkipChangesNoRow()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        await ImportCommitApi.RateAndCommentAsync(client, Shortcode, 3, "A comment");
        var before = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);

        // Skip (the proposal) for a Changed and a Conflict record alike: no row changes.
        foreach (var (clip, recordClass) in new[] { (Changed(), "changed"), (ConflictOf(SunoId), "conflict") })
        {
            var (skipped, records) = await ProposalApi.ExportAsync(client, token, clip);
            Assert.Equal((recordClass, "skip"), (records[SunoId].GetProperty("class").GetString(), records[SunoId].GetProperty("choice").GetProperty("action").GetString()));
            Assert.Equal("skipped", ImportCommitApi.Outcome(ImportCommitApi.Records(await ImportCommitApi.CommitAsync(client, skipped))[SunoId]));
        }

        var skippedRows = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        foreach (var table in new[] { "generations", "provider_records", "versions", "generation_comments", "shortcode_aliases" })
        {
            Assert.Equal(before[table].Values.Select(static row => row.Text), skippedRows[table].Values.Select(static row => row.Text));
        }

        // Declined: apply with nothing accepted. Only the remembered hash changes: not a clip column, the
        // raw clip, the rating, the comments, or the revision.
        var (declined, _) = await ProposalApi.ExportAsync(client, token, Changed());
        await ProposalApi.ChangedAsync(client, declined, 1, ProposalApi.Change(Apply(), SunoId));
        Assert.Equal("declined", ImportCommitApi.Outcome(ImportCommitApi.Records(await ImportCommitApi.CommitAsync(client, declined))[SunoId]));
        var after = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var id = GenerationRowId(after);
        Assert.Equal(["declined_hash"], ChangedColumns(before["generations"][id], after["generations"][id]));
        foreach (var table in new[] { "provider_records", "versions", "generation_comments", "shortcode_aliases" })
        {
            Assert.Equal(before[table].Values.Select(static row => row.Text), after[table].Values.Select(static row => row.Text));
        }

        client.Dispose();
    }

    /// <summary>
    /// AC 3: a declined change is remembered. The same data again is Already linked (no diff, proposed
    /// as linked); a further change in Suno, to a declined field or another one, is Changed again with
    /// every field that differs.
    /// </summary>
    [Fact]
    public async Task ADeclinedChangeIsAlreadyLinkedUntilSunosDataChangesAgain()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        var (declined, _) = await ProposalApi.ExportAsync(client, token, Changed());
        await ProposalApi.ChangedAsync(client, declined, 1, ProposalApi.Change(Apply(), SunoId));
        await ImportCommitApi.CommitAsync(client, declined);

        var (same, records) = await ProposalApi.ExportAsync(client, token, Changed());
        Assert.Equal("linked", records[SunoId].GetProperty("class").GetString());
        Assert.Empty(records[SunoId].GetProperty("changedFields").EnumerateArray());
        using (var noDiff = await client.GetAsync(DiffUri(same, SunoId)))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noDiff.StatusCode);
        }

        // An image address whose signature (query string) changed is still the same data.
        var signed = Changed();
        var image = Assert.IsType<string>((string?)Original()["image_url"]);
        signed["image_url"] = $"{image.Split('?')[0]}?sig=later";
        Assert.Equal("linked", (await ProposalApi.ExportAsync(client, token, signed)).Records[SunoId].GetProperty("class").GetString());

        // Suno changes a declined field again, or another one: Changed, with every field that differs.
        var retagged = Changed();
        retagged["metadata"]!["tags"] = "tags changed once more";
        var lengthened = Changed();
        lengthened["metadata"]!["duration"] = 321.5;
        foreach (var (clip, fields) in new[] { (retagged, new[] { "title", "tags" }), (lengthened, new[] { "title", "tags", "duration" }) })
        {
            var (again, changedRecords) = await ProposalApi.ExportAsync(client, token, clip);
            Assert.Equal("changed", changedRecords[SunoId].GetProperty("class").GetString());
            Assert.Equal(fields, (await DiffAsync(client, again, SunoId)).GetProperty("fields").EnumerateArray().Select(static field => field.GetProperty("field").GetString()));
        }

        client.Dispose();
    }

    /// <summary>
    /// A partly accepted change remembers the fields left declined; accepting everything Suno has
    /// clears the remembered hash, so nothing is left to hide.
    /// </summary>
    [Fact]
    public async Task APartlyAcceptedChangeIsRememberedAndAcceptingEverythingForgetsIt()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        var (partly, _) = await ProposalApi.ExportAsync(client, token, Changed());
        await ProposalApi.ChangedAsync(client, partly, 1, ProposalApi.Change(Apply("title"), SunoId));
        await ImportCommitApi.CommitAsync(client, partly);
        Assert.Equal("linked", (await ProposalApi.ExportAsync(client, token, Changed())).Records[SunoId].GetProperty("class").GetString());

        var retagged = Changed();
        retagged["metadata"]!["tags"] = "tags changed once more";
        var (again, records) = await ProposalApi.ExportAsync(client, token, retagged);
        Assert.Equal("changed", records[SunoId].GetProperty("class").GetString());
        await ProposalApi.ChangedAsync(client, again, 1, ProposalApi.Change(Apply("tags"), SunoId));
        Assert.Equal("updated", ImportCommitApi.Outcome(ImportCommitApi.Records(await ImportCommitApi.CommitAsync(client, again))[SunoId]));

        var rows = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var id = GenerationRowId(rows);
        Assert.Equal(("tags changed once more", null), (rows["generations"][id]["style_tags"], rows["generations"][id]["declined_hash"]));
        client.Dispose();
    }

    /// <summary>
    /// AC 6: a kept conflict is remembered like a declined change. The same inputs again are not a
    /// Conflict (Already linked, or Changed when only metadata differs); other inputs are a Conflict again.
    /// </summary>
    [Fact]
    public async Task AKeptConflictIsNotAConflictAgainUntilTheClipsInputsChangeAgain()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        var before = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var (kept, _) = await ProposalApi.ExportAsync(client, token, ConflictOf(SunoId));
        await ProposalApi.ChangedAsync(client, kept, 1, ProposalApi.Change(new JsonObject { ["action"] = "keep" }, SunoId));
        await ImportCommitApi.CommitAsync(client, kept);
        var id = GenerationRowId(before);
        Assert.Equal(["kept_inputs_hash"], ChangedColumns(before["generations"][id], ImportNeverOverwritesGuardTests.Rows(factory.DataPath)["generations"][id]));

        Assert.Equal("linked", (await ProposalApi.ExportAsync(client, token, ConflictOf(SunoId))).Records[SunoId].GetProperty("class").GetString());

        var retitled = ConflictOf(SunoId);
        retitled["title"] = "Retitled on Suno";
        Assert.Equal("changed", (await ProposalApi.ExportAsync(client, token, retitled)).Records[SunoId].GetProperty("class").GetString());

        var rewritten = ConflictOf(SunoId);
        rewritten["metadata"]!["prompt"] = "Third words";
        Assert.Equal("conflict", (await ProposalApi.ExportAsync(client, token, rewritten)).Records[SunoId].GetProperty("class").GetString());
        client.Dispose();
    }

    [Fact]
    public async Task AChangeChoiceMustFitTheRecordsClassAndDiff()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        var (exportId, _) = await ProposalApi.ExportAsync(client, token, Changed(), ProposalApi.Clip("brand-new", null, ProposalApi.At, 0, "New words"));

        Assert.Equal(["not_changed"], (await ProposalApi.RefusedAsync(client, exportId, 1, ProposalApi.Change(Apply(), "brand-new")))["brand-new"]);
        Assert.Equal(["not_conflict"], (await ProposalApi.RefusedAsync(client, exportId, 1, ProposalApi.Change(new JsonObject { ["action"] = "moveToNewVersion" }, SunoId)))[SunoId]);
        Assert.Equal(["not_conflict"], (await ProposalApi.RefusedAsync(client, exportId, 1, ProposalApi.Change(new JsonObject { ["action"] = "keep" }, SunoId)))[SunoId]);
        Assert.Equal(["field_not_changed"], (await ProposalApi.RefusedAsync(client, exportId, 1, ProposalApi.Change(Apply("duration"), SunoId)))[SunoId]);
        Assert.Equal(["already_linked"], (await ProposalApi.RefusedAsync(client, exportId, 1, ProposalApi.Change(new JsonObject { ["action"] = "ignore" }, SunoId)))[SunoId]);

        // A field name that is not a diffed field, and fields on an action that takes none, are malformed.
        foreach (var choice in new JsonNode[] { Apply("rating"), new JsonObject { ["action"] = "skip", ["acceptFields"] = new JsonArray("title") } })
        {
            using var response = await ProposalApi.PatchAsync(client, exportId, "\"1\"", ProposalApi.Change(choice, SunoId));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.True((await SetupApi.JsonAsync(response)).GetProperty("errors").TryGetProperty("choice.acceptFields", out _));
        }

        var saved = await ProposalApi.ChangedAsync(client, exportId, 1, ProposalApi.Change(Apply("title", "tags"), SunoId));
        Assert.Equal(2, saved.GetProperty("revision").GetInt32());
        client.Dispose();
    }

    [Fact]
    public async Task MovingAConflictCreatesAFrozenChildVersionWithTheClipsInputsAndLeavesAnAlias()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        await ImportCommitApi.RateAndCommentAsync(client, Shortcode, 5, "Stays with it");
        var before = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var conflict = ConflictOf(SunoId);
        conflict["title"] = "Retitled on Suno";
        var (exportId, records) = await ProposalApi.ExportAsync(client, token, conflict);
        Assert.Equal("conflict", records[SunoId].GetProperty("class").GetString());
        Assert.Equal("skip", records[SunoId].GetProperty("choice").GetProperty("action").GetString());

        var diff = await DiffAsync(client, exportId, SunoId);
        var inputs = diff.GetProperty("inputs").EnumerateArray().ToDictionary(static input => input.GetProperty("field").GetString()!);
        Assert.Equal(("Original words", "Different words"), (inputs["lyrics"].GetProperty("current").GetString(), inputs["lyrics"].GetProperty("incoming").GetString()));
        Assert.Equal(["title"], diff.GetProperty("fields").EnumerateArray().Select(static field => field.GetProperty("field").GetString()));

        await ProposalApi.ChangedAsync(client, exportId, 1, ProposalApi.Change(new JsonObject { ["action"] = "moveToNewVersion" }, SunoId));
        var result = await ImportCommitApi.CommitAsync(client, exportId);

        var record = ImportCommitApi.Records(result)[SunoId];
        Assert.Equal("moved", ImportCommitApi.Outcome(record));
        Assert.Equal(1, result.GetProperty("created").GetProperty("versions").GetInt32());
        var after = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var id = GenerationRowId(after);
        var moved = after["generations"][id];
        var child = after["versions"][moved["version_id"]!];
        Assert.Equal(("2.1", "Different words"), (child["number"], child["lyrics"]));
        Assert.Equal("1", child["is_frozen"]);
        Assert.Equal("n8-1-v2.1-g1", record.GetProperty("generation").GetProperty("shortcode").GetString());

        // The Version it left is byte-identical; the Generation keeps its Suno ID, rating, and comments;
        // the title was not accepted; the raw clip is Suno's latest.
        var original = before["generations"][id]["version_id"]!;
        Assert.Equal(before["versions"][original].Text, after["versions"][original].Text);
        Assert.Equal((SunoId, "5", before["generations"][id]["suno_title"]), (moved["suno_id"], moved["rating"], moved["suno_title"]));
        Assert.Equal(Comments(before), Comments(after));
        Assert.Equal(conflict.ToJsonString(), after["provider_records"][id]["payload"]);

        // The old shortcode is a permanent alias.
        using var resolved = await client.GetAsync(new Uri($"/api/v1/generations/{Shortcode}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal("n8-1-v2.1-g1", (await SetupApi.JsonAsync(resolved)).GetProperty("shortcode").GetString());
        client.Dispose();
    }

    [Fact]
    public async Task KeepingAConflictChangesOnlyWhatIsRememberedUnlessAFieldIsAccepted()
    {
        using var factory = SongApi.Host();
        var (client, token) = await ImportedAsync(factory);
        var before = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var conflict = ConflictOf(SunoId);
        conflict["title"] = "Retitled on Suno";

        // Kept, the title declined: the Generation changes only in the two remembered hashes.
        var (kept, _) = await ProposalApi.ExportAsync(client, token, conflict);
        await ProposalApi.ChangedAsync(client, kept, 1, ProposalApi.Change(new JsonObject { ["action"] = "keep" }, SunoId));
        Assert.Equal("kept", ImportCommitApi.Outcome(ImportCommitApi.Records(await ImportCommitApi.CommitAsync(client, kept))[SunoId]));
        var after = ImportNeverOverwritesGuardTests.Rows(factory.DataPath);
        var id = GenerationRowId(after);
        Assert.Equal(["declined_hash", "kept_inputs_hash"], ChangedColumns(before["generations"][id], after["generations"][id]));
        foreach (var table in new[] { "provider_records", "versions", "shortcode_aliases" })
        {
            Assert.Equal(before[table].Values.Select(static row => row.Text), after[table].Values.Select(static row => row.Text));
        }

        // Suno's inputs and title change again (a Conflict once more, with the title beneath): kept with the
        // title accepted, only the title changes, and the Generation stays on its Version. Nothing is left
        // declined, so the declined hash is cleared.
        var rewritten = conflict.DeepClone();
        rewritten["metadata"]!["prompt"] = "Third words";
        rewritten["title"] = "Retitled again";
        var (titled, records) = await ProposalApi.ExportAsync(client, token, rewritten);
        Assert.Equal("conflict", records[SunoId].GetProperty("class").GetString());
        await ProposalApi.ChangedAsync(client, titled, 1, ProposalApi.Change(new JsonObject { ["action"] = "keep", ["acceptFields"] = new JsonArray("title") }, SunoId));
        Assert.Equal("updated", ImportCommitApi.Outcome(ImportCommitApi.Records(await ImportCommitApi.CommitAsync(client, titled))[SunoId]));
        Assert.Equal(["suno_title", "kept_inputs_hash"], ChangedColumns(before["generations"][id], ImportNeverOverwritesGuardTests.Rows(factory.DataPath)["generations"][id]));
        client.Dispose();
    }

    /// <summary>A signed-in client and an extension token, with the clip <see cref="SunoId"/> imported as Generation <see cref="Shortcode"/> of a Version holding its inputs.</summary>
    private static async Task<(HttpClient Client, string Token)> ImportedAsync(N8TracksApiFactory factory)
    {
        var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Diffed");
        await ImportedVersions.AttachAsync(factory, song, "2", Original());
        return (client, token);
    }

    private static JsonNode Original() => ProposalApi.Clip(SunoId, null, ProposalApi.At, 0, "Original words", "Original title");

    /// <summary>The clip as Suno now has it: retitled and retagged, its inputs the same (Changed).</summary>
    private static JsonNode Changed()
    {
        var clip = Original();
        clip["title"] = "New title";
        clip["metadata"]!["tags"] = "new tags, brighter";
        return clip;
    }

    /// <summary>The clip <paramref name="sunoId"/> with other lyrics (a Conflict when it is the imported clip).</summary>
    private static JsonNode ConflictOf(string sunoId)
    {
        var clip = Original();
        clip["id"] = sunoId;
        clip["metadata"]!["prompt"] = "Different words";
        return clip;
    }

    private static JsonObject Apply(params string[] fields) =>
        new() { ["action"] = "apply", ["acceptFields"] = new JsonArray([.. fields.Select(static field => (JsonNode?)JsonValue.Create(field))]) };

    private static Uri DiffUri(Guid exportId, string sunoId) => SunoExportApi.Export(exportId, $"/records/{sunoId}/diff");

    private static async Task<JsonElement> DiffAsync(HttpClient client, Guid exportId, string sunoId)
    {
        using var response = await client.GetAsync(DiffUri(exportId, sunoId));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static string GenerationRowId(Dictionary<string, Dictionary<string, ImportNeverOverwritesGuardTests.Row>> rows) =>
        rows["generations"].Single(static pair => pair.Value["suno_id"] == SunoId).Key;

    private static IEnumerable<string> Comments(Dictionary<string, Dictionary<string, ImportNeverOverwritesGuardTests.Row>> rows) =>
        rows["generation_comments"].Values.Select(static row => row.Text);

    private static List<string> ChangedColumns(ImportNeverOverwritesGuardTests.Row before, ImportNeverOverwritesGuardTests.Row after) =>
        [.. before.Columns.Where(column => !string.Equals(before[column], after[column], StringComparison.Ordinal))];
}
