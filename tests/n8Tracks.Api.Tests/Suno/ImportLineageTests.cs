using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Lineage in the import review and on Versions (#153): each record of a review says what its clip was
/// made from and where each source is (a Generation, a record of this export, or not in this sync); a
/// commit records the Suno playlists and personas its imported clips touch, for the Sources pickers;
/// a Not imported source can be put on the ignore list and stays a source; and importing the source
/// later links the two Versions and relates their Songs without the user doing anything.
/// </summary>
public sealed class ImportLineageTests
{
    private static readonly Uri Playlists = new("/api/v1/suno/playlists", UriKind.Relative);
    private static readonly Uri Personas = new("/api/v1/suno/personas", UriKind.Relative);
    private static readonly Uri Ignored = new("/api/v1/suno/ignored", UriKind.Relative);

    [Fact]
    public async Task TheReviewShowsEachRecordsLineageAndWhereItsSourcesAre()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SongApi.CreateAsync(client, "Already imported");
        var imported = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("gen-parent"));

        var at = ProposalApi.At;
        var voiced = Voiced(ProposalApi.Clip("voiced-1", null, at.AddHours(4), 0, "Voiced words"), "pe-1", "Smoky");
        voiced["metadata"]!["playlist_id"] = "pl-1";
        voiced["metadata"]!["playlist_clip_ids"] = new JsonArray("x-1", "x-2");
        JsonNode[] clips =
        [
            Cover(ProposalApi.Clip("cover-absent", null, at, 0, "Cover words", "Cover alone"), "absent-1"),
            Cover(ProposalApi.Clip("cover-here", null, at.AddHours(1), 0, "Cover two words", "Cover with parent"), "parent-1"),
            ProposalApi.Clip("parent-1", null, at.AddHours(2), 0, "Parent words", "The parent"),
            Mashup(ProposalApi.Clip("mashup-1", null, at.AddHours(3), 0, "Mashup words"), "gen-parent", "absent-2"),
            voiced,
            ProposalApi.Clip("plain-1", null, at.AddHours(5), 0, "Plain words"),
        ];
        var (id, _) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(playlists: [SunoExportApi.Playlist("pl-1", "Listed name", "x-1", "x-2")]),
            SunoExportApi.Part(1, clips));
        var records = await SunoExportApi.RecordsByIdAsync(client, id);

        // Not in this sync: the reference's title, "Suno clip" and the ID's first eight characters.
        var alone = Assert.Single(Sources(records["cover-absent"]));
        Assert.Equal(("audio", "Cover", "cover", "absent-1"), (Text(alone, "group"), Text(alone, "typeName"), Text(alone, "sunoAction"), Text(alone, "sunoId")));
        Assert.Equal(SystemRelationshipTypes.Cover.Id, alone.GetProperty("typeId").GetGuid());
        Assert.Equal(("Suno clip absent-1", "not_imported"), (Text(alone, "title"), Text(alone, "place")));
        Assert.Equal(JsonValueKind.Null, alone.GetProperty("record").ValueKind);

        // In this export: its record, with its class, choice, and proposal, so the user can include it.
        var here = Assert.Single(Sources(records["cover-here"]));
        Assert.Equal(("The parent", "export"), (Text(here, "title"), Text(here, "place")));
        var parent = here.GetProperty("record");
        Assert.Equal(("parent-1", "The parent", "new"), (Text(parent, "sunoId"), Text(parent, "title"), Text(parent, "class")));
        Assert.Equal("import", parent.GetProperty("choice").GetProperty("action").GetString());
        Assert.Equal("import", parent.GetProperty("proposal").GetProperty("choice").GetProperty("action").GetString());

        // Mashup: both, in order; the first a Generation already, linked to.
        var mashup = Sources(records["mashup-1"]);
        Assert.Equal(["gen-parent", "absent-2"], mashup.Select(static source => Text(source, "sunoId")));
        Assert.Equal(["generation", "not_imported"], mashup.Select(static source => Text(source, "place")));
        var generation = mashup[0].GetProperty("generation");
        Assert.Equal((imported.Generation.Id, "n8-1-v1-g1", "n8-1"), (generation.GetProperty("id").GetGuid(), Text(generation, "shortcode"), Text(generation, "songShortcode")));
        Assert.Equal("Minimal clip", Text(mashup[0], "title"));

        // Voice and an Inspiration playlist, named from the export's list.
        var lineage = records["voiced-1"].GetProperty("lineage");
        Assert.Empty(lineage.GetProperty("sources").EnumerateArray());
        Assert.Equal(("pe-1", "Smoky"), (Text(lineage.GetProperty("voice"), "personaId"), Text(lineage.GetProperty("voice"), "name")));
        var playlist = lineage.GetProperty("playlist");
        Assert.Equal(("pl-1", "Listed name", 2), (Text(playlist, "sunoPlaylistId"), Text(playlist, "name"), playlist.GetProperty("clipCount").GetInt32()));

        // Complement: a clip made from nothing has no lineage.
        Assert.Equal(JsonValueKind.Null, records["plain-1"].GetProperty("lineage").ValueKind);
        Assert.Equal(JsonValueKind.Null, records["parent-1"].GetProperty("lineage").ValueKind);
    }

    [Fact]
    public async Task PlaylistsAndPersonasOfTheClipsImportedAppearInThePickersAfterTheCommitAndNotBefore()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var at = ProposalApi.At;

        var voiced = Voiced(ProposalApi.Clip("voiced-1", null, at, 0, "Voiced words"), "pe-1", "Smoky");
        voiced["metadata"]!["playlist_id"] = "pl-insp";
        voiced["metadata"]!["playlist_clip_ids"] = new JsonArray("a-1", "a-2");
        var unlisted = ProposalApi.Clip("unlisted-1", null, at.AddHours(1), 0, "Unlisted words");
        unlisted["metadata"]!["playlist_id"] = "pl-unlisted";
        unlisted["metadata"]!["playlist_clip_ids"] = new JsonArray("z-1");
        var skipped = Voiced(ProposalApi.Clip("skipped-1", null, at.AddHours(2), 0, "Skipped words"), "pe-2", "Unused voice");
        var (id, _) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(playlists:
            [
                SunoExportApi.Playlist("pl-listed", "Night drive", "voiced-1", "not-staged"),
                SunoExportApi.Playlist("pl-insp", "Inspiration list", "a-1", "a-2"),
            ]),
            SunoExportApi.Part(1, [voiced, unlisted, skipped], playlists: [SunoExportApi.Playlist("pl-skipped", "Only skipped", "skipped-1")]));
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(new JsonObject { ["action"] = "skip" }, "skipped-1"));

        // Before the commit: nothing to offer.
        Assert.Empty(Items(await ReadAsync(client, Playlists)));
        Assert.Empty(Items(await ReadAsync(client, Personas)));

        await ImportCommitApi.CommitAsync(client, id);

        // Listed playlists holding an imported clip, an imported clip's Inspiration playlist (named from
        // the list, or blank with the clip's snapshot when unlisted), and the imported clip's Voice.
        var playlists = Items(await ReadAsync(client, Playlists));
        Assert.Equal(["pl-unlisted", "pl-insp", "pl-listed"], playlists.Select(static playlist => Text(playlist, "id")));
        Assert.Equal(["", "Inspiration list", "Night drive"], playlists.Select(static playlist => Text(playlist, "name")));
        Assert.Equal(["z-1"], ClipIds(playlists[0]));
        Assert.Equal(["voiced-1", "not-staged"], ClipIds(playlists[2]));
        Assert.Equal("""{"items":[{"id":"pe-1","name":"Smoky"}]}""", (await ReadAsync(client, Personas)).GetRawText());

        // A later commit refreshes what its clips touch: a listed playlist takes its name and clips as
        // seen; a playlist known only as a clip's Inspiration, and a Voice the clip leaves unnamed, keep
        // what n8Tracks knows.
        var again = Voiced(ProposalApi.Clip("voiced-2", null, at.AddHours(3), 0, "Voiced again"), "pe-1", null);
        again["metadata"]!["playlist_id"] = "pl-insp";
        again["metadata"]!["playlist_clip_ids"] = new JsonArray("q-1");
        var (second, _) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(playlists: [SunoExportApi.Playlist("pl-listed", "Night drive II", "voiced-2")]),
            SunoExportApi.Part(1, [again]));
        await ImportCommitApi.CommitAsync(client, second);

        playlists = Items(await ReadAsync(client, Playlists));
        Assert.Equal(["", "Inspiration list", "Night drive II"], playlists.Select(static playlist => Text(playlist, "name")));
        Assert.Equal(["a-1", "a-2"], ClipIds(playlists[1]));
        Assert.Equal(["voiced-2"], ClipIds(playlists[2]));
        Assert.Equal("""{"items":[{"id":"pe-1","name":"Smoky"}]}""", (await ReadAsync(client, Personas)).GetRawText());
    }

    /// <summary>
    /// The Demo: a cover imported without its source shows it Not imported; the next sync carries the
    /// source, the cover is already linked, and confirming the source links the cover's Version to it and
    /// relates the two Songs.
    /// </summary>
    [Fact]
    public async Task ImportingTheSourceLaterLinksTheCoverAndRelatesTheSongs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var at = ProposalApi.At;
        var cover = Cover(ProposalApi.Clip("cover-1", null, at, 0, "Cover words", "My cover"), "source-1");

        var (first, records) = await ProposalApi.ExportAsync(client, token, cover);
        Assert.Equal("not_imported", Text(Assert.Single(Sources(records["cover-1"])), "place"));
        var coverRecord = ImportCommitApi.Records(await ImportCommitApi.CommitAsync(client, first))["cover-1"];
        var coverGeneration = coverRecord.GetProperty("generation");
        var coverVersion = await VersionOfAsync(client, Text(coverGeneration, "shortcode")!);

        var source = Assert.Single(coverVersion.GetProperty("inputs").GetProperty("sources").EnumerateArray());
        Assert.Equal("not_imported", Text(source, "availability"));
        Assert.Equal(("source-1", "Suno clip source-1"), (Text(source.GetProperty("external"), "sunoId"), Text(source.GetProperty("external"), "title")));

        var (second, again) = await ProposalApi.ExportAsync(
            client,
            token,
            cover,
            ProposalApi.Clip("source-1", null, at.AddHours(1), 0, "Source words", "The original"));
        Assert.Equal("linked", Text(again["cover-1"], "class"));
        var inExport = Assert.Single(Sources(again["cover-1"]));
        Assert.Equal(("export", "The original"), (Text(inExport, "place"), Text(inExport, "title")));

        var result = ImportCommitApi.Records(await ImportCommitApi.CommitAsync(client, second));
        var original = result["source-1"].GetProperty("generation");

        source = Assert.Single((await VersionOfAsync(client, Text(coverGeneration, "shortcode")!)).GetProperty("inputs").GetProperty("sources").EnumerateArray());
        Assert.Equal("ok", Text(source, "availability"));
        Assert.Equal(original.GetProperty("id").GetGuid(), source.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal(
            "1",
            TestDatabase.Scalar(
                factory.DataPath,
                $"""
                SELECT count(*) FROM song_relationships
                WHERE upper(from_song_id) = '{Upper(coverGeneration.GetProperty("songId").GetGuid())}'
                  AND upper(to_song_id) = '{Upper(original.GetProperty("songId").GetGuid())}'
                  AND upper(type_id) = '{Upper(SystemRelationshipTypes.Cover.Id)}';
                """));
    }

    [Fact]
    public async Task ANotImportedSourceGoesOnTheIgnoreListAndStaysASource()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var child = await SongApi.CreateAsync(client, "The cover");
        var clip = Cover(new JsonObject { ["id"] = "cover-x", ["status"] = "complete", ["title"] = "Cover x" }, "source-x");
        var imported = await ImportedVersions.ImportAsync(factory, child, "2", clip);
        string Stored() => TestDatabase.Scalar(factory.DataPath, $"SELECT external_reference_id || '/' || ifnull(generation_id, '') FROM version_sources WHERE upper(version_id) = '{Upper(imported.VersionId)}';");
        var before = Stored();

        var added = await AddAsync(client, """{"sunoId":"source-x"}""", HttpStatusCode.OK);
        Assert.Equal(("source-x", true), (Text(added, "sunoId"), added.GetProperty("added").GetBoolean()));

        // On the list with the reference's title and no status until a sync sees it; the source and its
        // reference stay as they were.
        var item = Assert.Single(Items(await IgnoreListTests.ListAsync(client)));
        Assert.Equal(("source-x", "Suno clip source-x"), (Text(item, "sunoId"), Text(item, "title")));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("status").ValueKind);
        Assert.Equal(before, Stored());
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM external_suno_references WHERE suno_id = 'source-x';"));

        // Again: nothing changes.
        Assert.False((await AddAsync(client, """{"sunoId":"source-x"}""", HttpStatusCode.OK)).GetProperty("added").GetBoolean());
        Assert.Equal(1, (await IgnoreListTests.ListAsync(client)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task OnlyANotImportedSourceCanBeAddedToTheIgnoreList()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var child = await SongApi.CreateAsync(client, "The cover");
        var parentSong = await SongApi.CreateAsync(client, "The parent");
        await ImportedVersions.ImportAsync(factory, child, "2", Cover(new JsonObject { ["id"] = "cover-y", ["status"] = "complete" }, "source-y"));

        // No Version names it as a source: 404.
        await SetupApi.ProblemAsync(await SendAsync(client, """{"sunoId":"never-named"}"""), HttpStatusCode.NotFound, "not_found");

        // A body that names no Suno ID: 422.
        foreach (var body in new[] { "{}", """{"sunoId":""}""", """{"sunoId":12}""", """{"sunoId":"two words"}""" })
        {
            await SetupApi.ProblemAsync(await SendAsync(client, body), HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        // Imported since: it is a Generation now.
        var parent = await ImportedVersions.ImportAsync(factory, parentSong, "2", new JsonObject { ["id"] = "source-y", ["status"] = "complete" });
        await SetupApi.ProblemAsync(await SendAsync(client, """{"sunoId":"source-y"}"""), HttpStatusCode.Conflict, "already_imported");

        // Deleted in n8Tracks: a deleted clip is never ignored.
        await ProposalApi.DeleteGenerationAsync(client, parent.Generation.Shortcode);
        await SetupApi.ProblemAsync(await SendAsync(client, """{"sunoId":"source-y"}"""), HttpStatusCode.Conflict, "tombstoned");

        Assert.Equal(0, (await IgnoreListTests.ListAsync(client)).GetProperty("total").GetInt32());
    }

    private static JsonNode Cover(JsonNode clip, string sourceId)
    {
        var metadata = clip["metadata"] as JsonObject ?? [];
        metadata["task"] = "cover";
        metadata["cover_clip_id"] = sourceId;
        metadata["edited_clip_id"] = sourceId;
        clip["metadata"] = metadata;
        return clip;
    }

    private static JsonNode Mashup(JsonNode clip, string first, string second)
    {
        clip["metadata"]!["task"] = "mashup_condition";
        clip["metadata"]!["mashup_clip_ids"] = new JsonArray(first, second);
        return clip;
    }

    private static JsonNode Voiced(JsonNode clip, string personaId, string? name)
    {
        clip["metadata"]!["persona_id"] = personaId;
        if (name is not null)
        {
            clip["persona"] = new JsonObject { ["id"] = personaId, ["name"] = name };
        }

        return clip;
    }

    private static List<JsonElement> Sources(JsonElement record) =>
        [.. record.GetProperty("lineage").GetProperty("sources").EnumerateArray()];

    private static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();

    private static List<JsonElement> Items(JsonElement list) => [.. list.GetProperty("items").EnumerateArray()];

    private static List<string?> ClipIds(JsonElement playlist) => [.. playlist.GetProperty("clipIds").EnumerateArray().Select(static clip => clip.GetString())];

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    private static async Task<JsonElement> ReadAsync(HttpClient client, Uri path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> VersionOfAsync(HttpClient client, string generationShortcode)
    {
        var generation = await ReadAsync(client, new Uri("/api/v1/generations/" + generationShortcode, UriKind.Relative));
        return await ReadAsync(client, new Uri("/api/v1/versions/" + generation.GetProperty("version").GetProperty("id").GetGuid().ToString("D", CultureInfo.InvariantCulture), UriKind.Relative));
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string json) =>
        SunoExportApi.SendAsync(client, HttpMethod.Post, Ignored, null, json);

    private static async Task<JsonElement> AddAsync(HttpClient client, string json, HttpStatusCode expected)
    {
        using var response = await SendAsync(client, json);
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }
}
