using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Assets;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Suno.Import;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Suno exports (#131): uploaded in parts by the extension (<c>suno.sync</c>), completed, and classified
/// by Suno ID against the catalog into the six classes, with counts and a filtered, paged record list
/// for the signed-in user. Built from the committed TS-003 fixtures. The catalog half of invariant 3 is
/// in <see cref="SunoExportStagingGuardTests"/>.
/// </summary>
public sealed class SunoExportEndpointTests
{
    /// <summary>
    /// Every class from one export, uploaded in two parts out of order: a clip a Generation holds unchanged
    /// (linked, even though its image address has a new query and its status moved, and even though it is
    /// also on the ignore list: a live Generation decides first), one whose title Suno changed (changed),
    /// one whose Generation was deleted (deleted, though also ignored: a tombstone comes before the ignore
    /// list), one on the ignore list (ignored), and unknown ones from the library and the Trash (new).
    /// No record is conflict: each linked clip's Version holds the inputs it maps to (#135).
    /// </summary>
    [Fact]
    public async Task AnExportUploadedInPartsIsClassifiedByItsSunoIdsIntoEachClass()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var library = SunoExportApi.LibraryClips();
        var trash = SunoExportApi.FixtureClips(SunoExportApi.TrashFixture);
        var (linked, changed, deleted, ignored) = (library[0], library[1], library[2], library[3]);

        var song = await SongApi.CreateAsync(client, "Linked");
        await ImportedVersions.AttachAsync(factory, song, "2", linked);
        await ImportedVersions.AttachAsync(factory, song, "3", changed);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", deleted.ToJsonString());
        await DeleteGenerationAsync(client, "n8-1-v1-g1");
        SunoExportApi.Ignore(factory, SunoExportApi.IdOf(ignored));
        SunoExportApi.Ignore(factory, SunoExportApi.IdOf(linked));
        SunoExportApi.Ignore(factory, SunoExportApi.IdOf(deleted));

        var sameButResigned = linked.DeepClone();
        sameButResigned["image_url"] = linked["image_url"]!.GetValue<string>() + "?v=2";
        sameButResigned["status"] = "streaming";
        var retitled = changed.DeepClone();
        retitled["title"] = "A title Suno changed";
        var unknown = JsonNode.Parse(Clips.Minimal("unknown-library-clip"))!;

        var id = await SunoExportApi.CreateAsync(client, token);
        var afterTwo = await SunoExportApi.PartAsync(client, token, id, SunoExportApi.Part(2, [deleted, ignored], trash));
        Assert.Equal("receiving", afterTwo.GetProperty("state").GetString());
        Assert.Equal(4, afterTwo.GetProperty("clips").GetInt32());
        await SunoExportApi.PartAsync(client, token, id, SunoExportApi.Part(1, [sameButResigned, retitled, unknown]));
        var export = await SunoExportApi.CompleteAsync(client, token, id);

        Assert.Equal("ready", export.GetProperty("state").GetString());
        Assert.Equal(2, export.GetProperty("parts").GetInt32());
        Assert.Equal(7, export.GetProperty("clips").GetInt32());
        Assert.Equal(
            ["new", "linked", "changed", "conflict", "ignored", "deleted", "total"],
            export.GetProperty("counts").EnumerateObject().Select(static count => count.Name));
        Assert.Equal(3, SunoExportApi.Count(export, "new"));
        Assert.Equal(1, SunoExportApi.Count(export, "linked"));
        Assert.Equal(1, SunoExportApi.Count(export, "changed"));
        Assert.Equal(0, SunoExportApi.Count(export, "conflict"));
        Assert.Equal(1, SunoExportApi.Count(export, "ignored"));
        Assert.Equal(1, SunoExportApi.Count(export, "deleted"));
        Assert.Equal(7, SunoExportApi.Count(export, "total"));
        var readyAt = DateTime.Parse(export.GetProperty("readyAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(readyAt.AddDays(7), DateTime.Parse(export.GetProperty("expiresAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Assert.Equal(export.GetRawText(), (await SunoExportApi.GetAsync(client, token, id)).GetRawText());

        var records = await SunoExportApi.RecordsByIdAsync(client, id);
        Assert.Equal(7, records.Count);
        string ClassOf(JsonNode clip) => records[SunoExportApi.IdOf(clip)].GetProperty("class").GetString()!;
        Assert.Equal("linked", ClassOf(linked));
        Assert.Equal("changed", ClassOf(changed));
        Assert.Equal("deleted", ClassOf(deleted));
        Assert.Equal("ignored", ClassOf(ignored));
        Assert.Equal("new", ClassOf(unknown));
        Assert.All(trash, clip => Assert.Equal("new", ClassOf(clip)));
        Assert.All(trash, clip => Assert.True(records[SunoExportApi.IdOf(clip)].GetProperty("trashed").GetBoolean()));
        Assert.False(records[SunoExportApi.IdOf(linked)].GetProperty("trashed").GetBoolean());

        // A changed record says what changed, and both name the Generation that holds the Suno ID.
        Assert.Equal(["title"], records[SunoExportApi.IdOf(changed)].GetProperty("changedFields").EnumerateArray().Select(static field => field.GetString()));
        Assert.Empty(records[SunoExportApi.IdOf(linked)].GetProperty("changedFields").EnumerateArray());
        var generations = TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || lower(id) FROM generations ORDER BY rowid;");
        Assert.Equal($"{SunoExportApi.IdOf(linked)}|{records[SunoExportApi.IdOf(linked)].GetProperty("generationId").GetString()}", generations[0]);
        Assert.Equal($"{SunoExportApi.IdOf(changed)}|{records[SunoExportApi.IdOf(changed)].GetProperty("generationId").GetString()}", generations[1]);
        Assert.Equal(JsonValueKind.Null, records[SunoExportApi.IdOf(deleted)].GetProperty("generationId").ValueKind);

        // The record carries Suno's fields for the list, and never the raw clip.
        var shown = records[SunoExportApi.IdOf(retitled)];
        Assert.Equal("A title Suno changed", shown.GetProperty("title").GetString());
        Assert.Equal(changed["project"]!["id"]!.GetValue<string>(), shown.GetProperty("workspaceId").GetString());
        Assert.Equal(changed["metadata"]!["duration"]!.GetValue<double>(), shown.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("proposal").ValueKind);
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("choice").ValueKind);
        Assert.False(shown.GetProperty("hasArtwork").GetBoolean());
        Assert.DoesNotContain("metadata", shown.GetRawText(), StringComparison.Ordinal);
        Assert.False(shown.TryGetProperty("rawJson", out _));
    }

    /// <summary>
    /// The precedence the classes follow when several could apply, and "conflict", which the rules give
    /// a linked clip whose inputs differ, as the import mapping (#135–#137) will report. Pure rules.
    /// </summary>
    [Fact]
    public void ALiveGenerationDecidesFirstThenATombstoneThenTheIgnoreList()
    {
        string[] none = [];
        string[] title = ["title"];
        Assert.Equal(Domain.Suno.SunoRecordClass.Conflict, Domain.Suno.SunoExportRules.Classify(linked: true, inputsDiffer: true, title, tombstoned: true, ignored: true));
        Assert.Equal(Domain.Suno.SunoRecordClass.Changed, Domain.Suno.SunoExportRules.Classify(linked: true, inputsDiffer: false, title, tombstoned: true, ignored: true));
        Assert.Equal(Domain.Suno.SunoRecordClass.Linked, Domain.Suno.SunoExportRules.Classify(linked: true, inputsDiffer: false, none, tombstoned: true, ignored: true));
        Assert.Equal(Domain.Suno.SunoRecordClass.Deleted, Domain.Suno.SunoExportRules.Classify(linked: false, inputsDiffer: false, none, tombstoned: true, ignored: true));
        Assert.Equal(Domain.Suno.SunoRecordClass.Ignored, Domain.Suno.SunoExportRules.Classify(linked: false, inputsDiffer: false, none, tombstoned: false, ignored: true));
        Assert.Equal(Domain.Suno.SunoRecordClass.New, Domain.Suno.SunoExportRules.Classify(linked: false, inputsDiffer: false, none, tombstoned: false, ignored: false));
    }

    /// <summary>
    /// "Changed" compares title, tags, duration, reported model, BPM values, key, and the image address
    /// without its query; not Suno's status, the audio address, or anything not normalized.
    /// </summary>
    [Fact]
    public void ChangedComparesTheNormalizedFieldsButNotStatusAudioOrTheImageQuery()
    {
        var stored = Read(Clips.FixtureClip("feed-v3.library-page-1.response.json"));
        Assert.Empty(Domain.Suno.SunoExportRules.ChangedFields(stored, stored));
        Assert.Empty(Domain.Suno.SunoExportRules.ChangedFields(
            stored,
            stored with { Status = "error", AudioUrl = "https://elsewhere.example/a.mp3", ImageUrl = stored.ImageUrl + "?sig=1", BatchIndex = 9, ModelLabel = "other", WorkspaceId = "elsewhere" }));
        Assert.Equal(
            Domain.Suno.SunoExportRules.ComparedFields,
            Domain.Suno.SunoExportRules.ChangedFields(
                stored,
                stored with
                {
                    Title = "t",
                    StyleTags = "s",
                    DurationSeconds = 1,
                    ModelVersion = "v",
                    ModelName = "n",
                    MinimumBpm = 1,
                    MaximumBpm = 2,
                    AverageBpm = 3,
                    Key = "k",
                    ImageUrl = "https://cdn2.suno.ai/other.jpeg?x=1",
                }));

        static Domain.Suno.ClipFields Read(string raw) => Assert.IsType<Application.Suno.ClipReading.Read>(Application.Suno.ClipReader.Read(raw)).Fields;
    }

    /// <summary>
    /// A Suno ID that appears more than once is one record. Within one list the last copy wins; a clip in
    /// both the library and the Trash is treated as trashed whichever part brought which copy; the
    /// playlists of every copy are merged.
    /// </summary>
    [Fact]
    public async Task RepeatedSunoIdsAreOneRecordAndTheTrashCopyWins()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var clip = SunoExportApi.LibraryClips()[0];
        var id = SunoExportApi.IdOf(clip);
        JsonNode Titled(string title)
        {
            var copy = clip.DeepClone();
            copy["title"] = title;
            return copy;
        }

        var twice = JsonNode.Parse(Clips.Minimal("twice-in-the-library"))!;
        var twiceLater = twice.DeepClone();
        twiceLater["title"] = "The later copy";

        var (exportId, export) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(playlists: [SunoExportApi.Playlist("list-a", "A", id)]),
            SunoExportApi.Part(1, [Titled("Library, first part")], [Titled("Trash")], [SunoExportApi.Playlist("list-b", "B", id, "twice-in-the-library")]),
            SunoExportApi.Part(2, [Titled("Library, second part"), twice, twiceLater], playlists: [SunoExportApi.Playlist("list-c", "C", id)]));

        Assert.Equal(5, export.GetProperty("clips").GetInt32());
        Assert.Equal(2, SunoExportApi.Count(export, "total"));
        var records = await SunoExportApi.RecordsByIdAsync(client, exportId);
        Assert.Equal(2, records.Count);
        var both = records[id];
        Assert.Equal("Trash", both.GetProperty("title").GetString());
        Assert.True(both.GetProperty("trashed").GetBoolean());
        Assert.Equal(["alsoInLibrary", "repeated"], both.GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()));
        Assert.Equal(["list-a", "list-b", "list-c"], both.GetProperty("playlistIds").EnumerateArray().Select(static playlist => playlist.GetString()));
        Assert.Equal("The later copy", records["twice-in-the-library"].GetProperty("title").GetString());
        Assert.Equal(["repeated"], records["twice-in-the-library"].GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()));
        Assert.Equal(["list-b"], records["twice-in-the-library"].GetProperty("playlistIds").EnumerateArray().Select(static playlist => playlist.GetString()));
        Assert.Contains("\"Trash\"", TestDatabase.Scalar(factory.DataPath, $"SELECT raw_json FROM suno_export_records WHERE suno_id = '{id}';"), StringComparison.Ordinal);

        // The Trash copy wins whichever part it came in: here it comes last.
        var (again, _) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(),
            SunoExportApi.Part(1, trashed: [Titled("Trash")]),
            SunoExportApi.Part(2, [Titled("Library")]));
        Assert.True((await SunoExportApi.RecordsByIdAsync(client, again))[id].GetProperty("trashed").GetBoolean());

        // A part sent again by number replaces the earlier one.
        var replaced = await SunoExportApi.CreateAsync(client, token);
        await SunoExportApi.PartAsync(client, token, replaced, SunoExportApi.Part(1, [JsonNode.Parse(Clips.Minimal("first-try"))!]));
        var resent = await SunoExportApi.PartAsync(client, token, replaced, SunoExportApi.Part(1, [JsonNode.Parse(Clips.Minimal("second-try"))!]));
        Assert.Equal(1, resent.GetProperty("parts").GetInt32());
        Assert.Equal(1, resent.GetProperty("clips").GetInt32());
        await SunoExportApi.CompleteAsync(client, token, replaced);
        Assert.Equal(["second-try"], (await SunoExportApi.RecordsByIdAsync(client, replaced)).Keys);
    }

    /// <summary>The records list filters by class, workspace, and playlist, pages, and sorts newest in Suno first, then by Suno ID.</summary>
    [Fact]
    public async Task RecordsAreFilteredPagedAndNewestFirst()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var library = SunoExportApi.LibraryClips();
        var trash = SunoExportApi.FixtureClips(SunoExportApi.TrashFixture);
        List<JsonNode> undated = [JsonNode.Parse(Clips.Minimal("b-undated"))!, JsonNode.Parse(Clips.Minimal("a-undated"))!];
        await ImportedVersions.AttachAsync(factory, await SongApi.CreateAsync(client, "Holder"), "2", library[0]);
        var workspace = library[1]["project"]!["id"]!.GetValue<string>();

        var (id, _) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(playlists: [SunoExportApi.Playlist("favourites", "Favourites", SunoExportApi.IdOf(library[2]), SunoExportApi.IdOf(trash[0]), "not-in-the-export")]),
            SunoExportApi.Part(1, [.. library, .. undated], trash));

        var all = await SunoExportApi.RecordsAsync(client, id);
        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(100, all.GetProperty("pageSize").GetInt32());
        Assert.Equal(8, all.GetProperty("total").GetInt32());
        var expected = library.Concat(trash)
            .OrderByDescending(static clip => DateTimeOffset.Parse(clip["created_at"]!.GetValue<string>(), CultureInfo.InvariantCulture))
            .ThenBy(SunoExportApi.IdOf, StringComparer.Ordinal)
            .Select(SunoExportApi.IdOf)
            .Concat(["a-undated", "b-undated"]);
        Assert.Equal(expected, all.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("sunoId").GetString()));

        var second = await SunoExportApi.RecordsAsync(client, id, "?page=2&pageSize=3");
        Assert.Equal(expected.Skip(3).Take(3), second.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("sunoId").GetString()));
        Assert.Equal(8, second.GetProperty("total").GetInt32());
        Assert.Empty((await SunoExportApi.RecordsAsync(client, id, "?page=9")).GetProperty("items").EnumerateArray());

        Assert.Equal([SunoExportApi.IdOf(library[0])], (await SunoExportApi.RecordsByIdAsync(client, id, "?class=linked")).Keys);
        Assert.Equal(7, (await SunoExportApi.RecordsAsync(client, id, "?class=new")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await SunoExportApi.RecordsAsync(client, id, "?class=deleted")).GetProperty("total").GetInt32());
        Assert.Equal(
            library.Concat(trash).Where(clip => clip["project"]?["id"]?.GetValue<string>() == workspace).Select(SunoExportApi.IdOf).Order(StringComparer.Ordinal),
            (await SunoExportApi.RecordsByIdAsync(client, id, "?workspace=" + Uri.EscapeDataString(workspace))).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { SunoExportApi.IdOf(library[2]), SunoExportApi.IdOf(trash[0]) }.Order(StringComparer.Ordinal),
            (await SunoExportApi.RecordsByIdAsync(client, id, "?playlist=favourites")).Keys.Order(StringComparer.Ordinal));
        Assert.Empty((await SunoExportApi.RecordsByIdAsync(client, id, "?playlist=favourites&class=linked")).Keys);

        foreach (var query in new[] { "?class=other", "?page=0", "?pageSize=201", "?pageSize=x", "?sort=title", "?class=new&class=linked" })
        {
            using var refused = await client.GetAsync(SunoExportApi.Export(id, "/records" + query));
            await SetupApi.ProblemAsync(refused, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var unknown = await client.GetAsync(SunoExportApi.Export(Guid.CreateVersion7(), "/records"));
        await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, "not_found");
    }

    /// <summary>The header and parts are checked; each refusal stores nothing.</summary>
    [Fact]
    public async Task UnreadableExportsAndPartsAreRefused()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        using (var unsupported = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Exports, token, SunoExportApi.Header(formatVersion: 2).ToJsonString()))
        {
            var problem = await SetupApi.ProblemAsync(unsupported, HttpStatusCode.UnprocessableEntity, ExportStagingService.UnsupportedFormatCode);
            Assert.Equal(2, problem.GetProperty("formatVersion").GetInt32());
        }

        var missing = SunoExportApi.Header();
        missing.Remove("capturedAt");
        missing["scope"] = new JsonObject { ["kind"] = "everything" };
        missing["libraryComplete"] = "yes";
        missing["clips"] = new JsonArray(JsonNode.Parse(Clips.Minimal("in-the-header")));
        using (var invalid = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Exports, token, missing.ToJsonString()))
        {
            var problem = await SetupApi.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, ExportStagingService.InvalidExportCode);
            Assert.Equal(
                ["capturedAt", "clips", "libraryComplete", "scope.kind"],
                problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name).Order(StringComparer.Ordinal));
        }

        using (var notJson = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Exports, token, "{\"format\":"))
        {
            await SetupApi.ProblemAsync(notJson, HttpStatusCode.BadRequest, "invalid_request");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM suno_exports;"));

        var id = await SunoExportApi.CreateAsync(client, token);
        var noId = SunoExportApi.Part(1, [JsonNode.Parse(Clips.Minimal("good"))!, new JsonObject { ["id"] = 7, ["title"] = "A number for an ID" }]);
        using (var refused = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/parts"), token, noId.ToJsonString()))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, ExportStagingService.InvalidExportCode);
            Assert.Equal(["clips[1].id"], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
        }

        using (var numberless = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/parts"), token, new JsonObject { ["clips"] = new JsonArray() }.ToJsonString()))
        {
            var problem = await SetupApi.ProblemAsync(numberless, HttpStatusCode.UnprocessableEntity, ExportStagingService.InvalidExportCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("partNumber", out _));
        }

        var tooMany = SunoExportApi.Part(1, Enumerable.Range(0, 201).Select(static n => JsonNode.Parse(Clips.Minimal($"clip-{n}"))!));
        using (var refused = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/parts"), token, tooMany.ToJsonString()))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.RequestEntityTooLarge, ExportStagingService.TooLargeCode);
            Assert.Equal(200, problem.GetProperty("limit").GetInt32());
        }

        var huge = SunoExportApi.Part(1, [new JsonObject { ["id"] = "huge", ["padding"] = new string('x', (20 * 1024 * 1024) + 1) }]);
        using (var refused = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/parts"), token, huge.ToJsonString()))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.RequestEntityTooLarge, ExportStagingService.TooLargeCode);
        }

        Assert.Equal((0, 0, 0), SunoExportApi.StagedRows(factory, id));

        // Completed: no more parts, and no second completion.
        await SunoExportApi.CompleteAsync(client, token, id);
        using (var late = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/parts"), token, SunoExportApi.Part(1).ToJsonString()))
        {
            var problem = await SetupApi.ProblemAsync(late, HttpStatusCode.Conflict, ExportStagingService.NotReceivingCode);
            Assert.Equal("ready", problem.GetProperty("state").GetString());
        }

        using (var again = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/complete"), token))
        {
            await SetupApi.ProblemAsync(again, HttpStatusCode.Conflict, ExportStagingService.NotReceivingCode);
        }
    }

    /// <summary>An export carries at most 50,000 clips, counted as received; a part that would pass it is refused.</summary>
    [Fact]
    public async Task AnExportOfMoreThanFiftyThousandClipsIsRefused()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var id = await SunoExportApi.CreateAsync(client, token);

        // 249 full parts are stood in for in the database; the 250th fits exactly, one more does not.
        var key = id.ToString().ToUpperInvariant();
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO suno_export_parts (export_id, part_number, body, clip_count, received_utc) VALUES ('{key}', 1, '{{}}', 49800, '2026-10-06T00:00:00.000Z');");
        var full = SunoExportApi.Part(2, Enumerable.Range(0, 200).Select(static n => JsonNode.Parse(Clips.Minimal($"clip-{n}"))!));
        Assert.Equal(50_000, (await SunoExportApi.PartAsync(client, token, id, full)).GetProperty("clips").GetInt32());

        using var refused = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/parts"), token, SunoExportApi.Part(3, [JsonNode.Parse(Clips.Minimal("one-more"))!]).ToJsonString());
        var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.RequestEntityTooLarge, ExportStagingService.TooLargeCode);
        Assert.Equal(50_000, problem.GetProperty("limit").GetInt32());
        Assert.Equal(50_001, problem.GetProperty("count").GetInt32());
    }

    /// <summary>
    /// Uploading needs <c>suno.sync</c>; a credential reaches only the exports it created (another's is
    /// 404 everywhere); the signed-in user reaches every export; the records list is session-only.
    /// </summary>
    [Fact]
    public async Task ACredentialReachesOnlyItsOwnExportsAndTheRecordsAreSessionOnly()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var first = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var second = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var everything = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.SunoSync)]);

        using (var refused = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Exports, everything, SunoExportApi.Header().ToJsonString()))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SunoSync, problem.GetProperty("requiredScope").GetString());
        }

        var id = await SunoExportApi.CreateAsync(client, first);
        var part = SunoExportApi.Part(1, [JsonNode.Parse(Clips.Minimal("mine"))!]);
        foreach (var (method, suffix, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Get, string.Empty, null),
            (HttpMethod.Post, "/parts", part.ToJsonString()),
            (HttpMethod.Post, "/complete", null),
            (HttpMethod.Post, "/discard", null),
        })
        {
            using var other = await SunoExportApi.SendAsync(client, method, SunoExportApi.Export(id, suffix), second, body);
            await SetupApi.ProblemAsync(other, HttpStatusCode.NotFound, "not_found");
            using var scoped = await SunoExportApi.SendAsync(client, method, SunoExportApi.Export(id, suffix), everything, body);
            await SetupApi.ProblemAsync(scoped, HttpStatusCode.Forbidden, "insufficient_scope");
        }

        await SunoExportApi.PartAsync(client, first, id, part);
        await SunoExportApi.CompleteAsync(client, first, id);
        Assert.Equal("ready", (await SunoExportApi.GetAsync(client, first, id)).GetProperty("state").GetString());

        // The signed-in user reads (and could discard) any export; the credential that made it cannot list its records.
        Assert.Equal("ready", (await SunoExportApi.GetAsync(client, null, id)).GetProperty("state").GetString());
        Assert.Equal(["mine"], (await SunoExportApi.RecordsByIdAsync(client, id)).Keys);
        using (var records = await SunoExportApi.SendAsync(client, HttpMethod.Get, SunoExportApi.Export(id, "/records"), first))
        {
            await SetupApi.ProblemAsync(records, HttpStatusCode.Forbidden, "session_required");
        }

        // The signed-in user's own export is reached by the session alone.
        var own = await SunoExportApi.CreateAsync(client, null);
        Assert.Equal("receiving", (await SunoExportApi.GetAsync(client, null, own)).GetProperty("state").GetString());
        using var notTheirs = await SunoExportApi.SendAsync(client, HttpMethod.Get, SunoExportApi.Export(own), first);
        await SetupApi.ProblemAsync(notTheirs, HttpStatusCode.NotFound, "not_found");
    }

    /// <summary>
    /// One export under review at a time: completing a new one discards an earlier ready one (whoever
    /// created it) and its staged rows at once, while one still receiving is left alone; an export being
    /// committed makes a new one wait with 409 <c>import_in_progress</c>.
    /// </summary>
    [Fact]
    public async Task CompletingAnExportDiscardsTheOneUnderReviewAndWaitsForACommit()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var other = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var clip = JsonNode.Parse(Clips.Minimal("reviewed"))!;

        var (earlier, _) = await SunoExportApi.UploadAsync(client, other, SunoExportApi.Header(), SunoExportApi.Part(1, [clip], playlists: [SunoExportApi.Playlist("p", "P", "reviewed")]));
        Assert.Equal((1, 1, 1), SunoExportApi.StagedRows(factory, earlier));
        var receiving = await SunoExportApi.CreateAsync(client, token);
        await SunoExportApi.PartAsync(client, token, receiving, SunoExportApi.Part(1, [clip]));

        var (later, _) = await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, [clip]));

        var discarded = await SunoExportApi.GetAsync(client, other, earlier);
        Assert.Equal("discarded", discarded.GetProperty("state").GetString());
        Assert.Equal(0, SunoExportApi.Count(discarded, "total"));
        Assert.Equal((0, 0, 0), SunoExportApi.StagedRows(factory, earlier));
        Assert.Equal("receiving", (await SunoExportApi.GetAsync(client, token, receiving)).GetProperty("state").GetString());
        Assert.Equal("ready", (await SunoExportApi.GetAsync(client, token, later)).GetProperty("state").GetString());

        // The commit story moves an export to committing; while one is, no other completes.
        TestDatabase.Execute(factory.DataPath, $"UPDATE suno_exports SET state = 'committing' WHERE upper(id) = '{later.ToString().ToUpperInvariant()}';");
        using (var waiting = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(receiving, "/complete"), token))
        {
            var problem = await SetupApi.ProblemAsync(waiting, HttpStatusCode.Conflict, ExportStagingService.ImportInProgressCode);
            Assert.Equal(later, problem.GetProperty("committingExportId").GetGuid());
        }

        Assert.Equal("receiving", (await SunoExportApi.GetAsync(client, token, receiving)).GetProperty("state").GetString());
        using (var tooLate = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(later, "/discard"), token))
        {
            await SetupApi.ProblemAsync(tooLate, HttpStatusCode.Conflict, ExportStagingService.NotDiscardableCode);
        }

        // The extension discards its own export; doing it again answers it as it is.
        using (var discard = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(receiving, "/discard"), token))
        {
            Assert.Equal(HttpStatusCode.OK, discard.StatusCode);
            Assert.Equal("discarded", (await SetupApi.JsonAsync(discard)).GetProperty("state").GetString());
        }

        Assert.Equal((0, 0, 0), SunoExportApi.StagedRows(factory, receiving));
        using var twice = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(receiving, "/discard"), token);
        Assert.Equal(HttpStatusCode.OK, twice.StatusCode);
    }

    /// <summary>
    /// Above 2,000 clips, classification runs as a background job: the completion answers
    /// <c>classifying</c> with the job, and the export is ready when the job finishes.
    /// </summary>
    [Fact]
    public async Task ALargeExportIsClassifiedInTheBackground()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SongApi.CreateAsync(client, "Holder");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("clip-1000"));

        var id = await SunoExportApi.CreateAsync(client, token);
        for (var part = 0; part < 11; part++)
        {
            var clips = Enumerable.Range(part * 200, 200).Select(static n => JsonNode.Parse(Clips.Minimal($"clip-{n}"))!);
            await SunoExportApi.PartAsync(client, token, id, SunoExportApi.Part(part + 1, clips));
        }

        var completed = await SunoExportApi.CompleteAsync(client, token, id);
        Assert.Equal("classifying", completed.GetProperty("state").GetString());
        var job = completed.GetProperty("jobId").GetGuid();

        var finished = await TestJobs.WaitForStatusAsync(client, job, "succeeded");
        Assert.Equal("ready", finished.GetProperty("result").GetProperty("state").GetString());
        var export = await SunoExportApi.GetAsync(client, token, id);
        Assert.Equal("ready", export.GetProperty("state").GetString());
        Assert.Equal(2_200, SunoExportApi.Count(export, "total"));
        Assert.Equal(2_199, SunoExportApi.Count(export, "new"));
        Assert.Equal(1, SunoExportApi.Count(export, "linked"));
    }

    /// <summary>
    /// The daily retention job's second step: a ready export expires seven days after it became ready and
    /// its staged rows go; one still receiving after 24 hours is discarded; the catalog is untouched.
    /// </summary>
    [Fact]
    public async Task TheDailyJobExpiresAReadyExportAfterSevenDays()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SongApi.CreateAsync(client, "Kept");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.FixtureClip("feed-v3.library-page-1.response.json"));

        var (ready, _) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(),
            SunoExportApi.Part(1, SunoExportApi.LibraryClips(), playlists: [SunoExportApi.Playlist("p", "P", SunoExportApi.IdOf(SunoExportApi.LibraryClips()[1]))]));
        var receiving = await SunoExportApi.CreateAsync(client, token);
        await SunoExportApi.PartAsync(client, token, receiving, SunoExportApi.Part(1, [JsonNode.Parse(Clips.Minimal("never-completed"))!]));
        Assert.Equal((1, 4, 1), SunoExportApi.StagedRows(factory, ready));
        var catalog = SunoExportStagingGuardTests.Snapshot(factory.DataPath);
        var settings = TestDatabase.Rows(factory.DataPath, OtherSettings);

        // Not yet: six days and 23 hours after it was ready, only the receiving export has gone.
        clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromHours(1));
        var early = await SunoExportApi.WithServiceAsync(factory, static service => service.ExpireAsync());
        Assert.Equal(new ExportExpirySummary(0, 1, 0, 0), early);
        Assert.Equal("ready", (await SunoExportApi.GetAsync(client, token, ready)).GetProperty("state").GetString());
        Assert.Equal("discarded", (await SunoExportApi.GetAsync(client, token, receiving)).GetProperty("state").GetString());
        Assert.Equal((0, 0, 0), SunoExportApi.StagedRows(factory, receiving));

        // Seven days: the retention job, run as the daily task queues it, expires it as its second step.
        clock.Advance(TimeSpan.FromHours(1));
        var queue = factory.Services.GetRequiredService<Application.Jobs.IJobQueue>();
        var prune = await queue.EnqueueAsync(Application.Retention.RetentionPruneTask.JobType, null, CancellationToken.None);
        var result = await TestJobs.WaitForStatusAsync(client, prune, "succeeded");
        Assert.Equal(1, result.GetProperty("result").GetProperty("exportsExpired").GetInt32());

        var expired = await SunoExportApi.GetAsync(client, token, ready);
        Assert.Equal("expired", expired.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, expired.GetProperty("expiresAt").ValueKind);
        Assert.Equal((0, 0, 0), SunoExportApi.StagedRows(factory, ready));

        // The prune records its own run in settings; nothing else in the catalog changed.
        Assert.Equal(catalog.Where(static table => table.Key != "settings"), SunoExportStagingGuardTests.Snapshot(factory.DataPath, "settings"));
        Assert.Equal(settings, TestDatabase.Rows(factory.DataPath, OtherSettings));
    }

    /// <summary>
    /// A cover image staged with a record of a ready export: checked like any artwork, held with the
    /// export (the sweep keeps it while the record holds it), and nothing in the catalog changes.
    /// </summary>
    [Fact]
    public async Task ACoverImageIsHeldWithTheStagedRecordAndChangesNothingInTheCatalog()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var library = SunoExportApi.LibraryClips();
        await SongApi.CreateAsync(client, "Linked");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", library[0].ToJsonString());
        var id = await SunoExportApi.CreateAsync(client, token);
        await SunoExportApi.PartAsync(client, token, id, SunoExportApi.Part(1, library));
        var image = ArtworkImages.Solid(SKEncodedImageFormat.Png, 64, 64, ArtworkImages.Red);
        var linked = SunoExportApi.IdOf(library[0]);

        using (var early = await UploadArtworkAsync(client, token, id, linked, image))
        {
            var problem = await SetupApi.ProblemAsync(early, HttpStatusCode.Conflict, ExportStagingService.NotReadyCode);
            Assert.Equal("receiving", problem.GetProperty("state").GetString());
        }

        await SunoExportApi.CompleteAsync(client, token, id);
        var catalog = SunoExportStagingGuardTests.Snapshot(factory.DataPath);

        using (var staged = await UploadArtworkAsync(client, token, id, linked, image))
        {
            Assert.True(staged.StatusCode == HttpStatusCode.OK, await staged.Content.ReadAsStringAsync());
            var body = await SetupApi.JsonAsync(staged);
            Assert.Equal(linked, body.GetProperty("sunoId").GetString());
            var asset = body.GetProperty("artwork").GetProperty("id").GetGuid();
            Assert.Equal(asset.ToString().ToUpperInvariant(), TestDatabase.Scalar(factory.DataPath, $"SELECT upper(artwork_asset_id) FROM suno_export_records WHERE suno_id = '{linked}';"));
        }

        Assert.True((await SunoExportApi.RecordsByIdAsync(client, id))[linked].GetProperty("hasArtwork").GetBoolean());
        Assert.Equal(catalog, SunoExportStagingGuardTests.Snapshot(factory.DataPath));
        Assert.Equal(string.Empty, TestDatabase.Scalar(factory.DataPath, "SELECT coalesce(artwork_asset_id, '') FROM generations;"));

        // Held with the export: a day later the sweep keeps it.
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(0, (await ArtworkApi.SweepAsync(factory)).Removed);
        Assert.Equal(1, ArtworkApi.AssetRows(factory));

        using (var notAnImage = await UploadArtworkAsync(client, token, id, linked, "not an image"u8.ToArray()))
        {
            await SetupApi.ProblemAsync(notAnImage, HttpStatusCode.UnsupportedMediaType, "artwork_type_not_supported");
        }

        using (var noRecord = await UploadArtworkAsync(client, token, id, "not-in-the-export", image))
        {
            await SetupApi.ProblemAsync(noRecord, HttpStatusCode.NotFound, "not_found");
        }

        using (var otherCredential = await UploadArtworkAsync(client, await SunoWorkspaceApi.ExtensionTokenAsync(factory), id, linked, image))
        {
            await SetupApi.ProblemAsync(otherCredential, HttpStatusCode.NotFound, "not_found");
        }

        // Discarded with the export, the image is no longer held: the sweep removes it.
        using (var discard = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(id, "/discard"), token))
        {
            Assert.Equal(HttpStatusCode.OK, discard.StatusCode);
        }

        Assert.Equal(1, (await ArtworkApi.SweepAsync(factory)).Removed);
        Assert.Equal(0, ArtworkApi.AssetRows(factory));
    }

    /// <summary>
    /// What Suno says about its own workspaces is applied when the export completes, but only from a
    /// complete list: names and availability are provider state. No Song's workspace changes.
    /// </summary>
    [Fact]
    public async Task ACompleteWorkspaceListIsAppliedAtOnceAndNoSongMoves()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("studio", "Studio"), SunoWorkspaceApi.Project("gone", "Gone"));
        await SongApi.CreateAsync(client, "In the studio");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "gone");
        var song = TestDatabase.Rows(factory.DataPath, "SELECT id, suno_workspace_id, revision, updated_utc FROM songs;");

        // An incomplete list waits for the commit.
        await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(workspaces: [SunoWorkspaceApi.Project("studio", "Renamed later"), SunoWorkspaceApi.Project("brand-new", "New")], workspacesComplete: false),
            SunoExportApi.Part(1));
        Assert.Equal("Studio", (await SunoWorkspaceApi.OneAsync(client, "studio")).GetProperty("name").GetString());
        Assert.Equal(2, (await SunoWorkspaceApi.ListAsync(client)).Length);

        await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(workspaces: [SunoWorkspaceApi.Project("studio", "Studio B"), SunoWorkspaceApi.Project("brand-new", "New")], workspacesComplete: true),
            SunoExportApi.Part(1));
        Assert.Equal("Studio B", (await SunoWorkspaceApi.OneAsync(client, "studio")).GetProperty("name").GetString());
        Assert.Equal("available", (await SunoWorkspaceApi.OneAsync(client, "brand-new")).GetProperty("state").GetString());
        Assert.Equal("unavailable", (await SunoWorkspaceApi.OneAsync(client, "gone")).GetProperty("state").GetString());
        Assert.Equal(song, TestDatabase.Rows(factory.DataPath, "SELECT id, suno_workspace_id, revision, updated_utc FROM songs;"));

        // A workspace list that cannot be read refuses the header.
        using var refused = await SunoExportApi.SendAsync(
            client,
            HttpMethod.Post,
            SunoExportApi.Exports,
            token,
            SunoExportApi.Header(workspaces: [new JsonObject { ["name"] = "No ID" }], workspacesComplete: true).ToJsonString());
        var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, ExportStagingService.InvalidExportCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("workspaces[0].id", out _));
    }

    /// <summary>Every settings row but the retention prune's own record of its runs.</summary>
    private const string OtherSettings = "SELECT key || '=' || value FROM settings WHERE key <> 'retention.prune' ORDER BY key;";

    private static async Task<HttpResponseMessage> UploadArtworkAsync(HttpClient client, string token, Guid id, string sunoId, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "cover.png");
        using var request = new HttpRequestMessage(HttpMethod.Put, SunoExportApi.Export(id, "/artwork/" + Uri.EscapeDataString(sunoId))) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task DeleteGenerationAsync(HttpClient client, string shortcode)
    {
        var uri = new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative);
        var generation = await SetupApi.JsonAsync(await client.GetAsync(uri));
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(generation.GetProperty("revision").GetInt32())));
        using var deleted = await client.SendAsync(request);
        Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
    }
}
