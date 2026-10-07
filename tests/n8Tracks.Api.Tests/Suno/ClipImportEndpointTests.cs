using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Suno;
using n8Tracks.Infrastructure.Retention;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// A clip's mapped inputs in the catalog (#135, #136 for Speech and Sounds): an imported Version keeps out-of-range values whole
/// and marked, and is frozen like any other once it has a Generation; a sync review classes a linked
/// clip <c>conflict</c> when its inputs differ from its Version's; and a model Suno reports that is not
/// on the list is added only by a commit, never by classifying.
/// </summary>
public sealed class ClipImportEndpointTests
{
    private const string Advanced = "feed-v3.songs-advanced.response.json";

    /// <summary>
    /// A 6,000-character lyric and an unknown Variety are kept as Suno returned them and the Version is
    /// marked; once a Generation is attached the Version still refuses an edit of its inputs as frozen,
    /// and the marks stay. A Version made in n8Tracks has no marks.
    /// </summary>
    [Fact]
    public async Task AnImportedVersionKeepsOutOfRangeInputsMarkedAndIsFrozenOnceItHasAGeneration()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Imported");
        var clip = Fixture(Advanced);
        var lyrics = new string('l', 6000);
        clip["metadata"]!["prompt"] = lyrics;
        clip["metadata"]!["control_sliders"]!["aug_creativity"] = 7;
        await ImportedVersions.AddAsync(factory, song.GetProperty("id").GetGuid(), "2", clip);

        var imported = await GetAsync(client, "n8-1-v2");
        Assert.Equal(lyrics, imported.GetProperty("lyrics").GetString());
        Assert.Equal("normal", imported.GetProperty("inputs").GetProperty("variety").GetString());
        var marks = imported.GetProperty("imported");
        Assert.Equal(["lyrics", "variety"], Strings(marks.GetProperty("outOfRange")));
        Assert.Equal("7", marks.GetProperty("rawValues").GetProperty("variety").GetString());
        Assert.Equal(
            ["simpleLyricsAdded", "simpleStylesAdded", "styles", "vocalGender", "durationMode", "durationSeconds", "personalize"],
            Strings(marks.GetProperty("notReturned")));
        Assert.Equal(JsonValueKind.Null, (await GetAsync(client, "n8-1-v1")).GetProperty("imported").ValueKind);

        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", clip.ToJsonString());
        var frozen = await GetAsync(client, "n8-1-v2");
        Assert.True(frozen.GetProperty("isFrozen").GetBoolean());
        var revision = frozen.GetProperty("revision").GetInt32();
        foreach (var body in new[] { """{"lyrics":"Shorter"}""", """{"inputs":{"variety":"high"}}""" })
        {
            using var response = await PatchAsync(client, "n8-1-v2", revision, body);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "version_frozen");
        }

        var after = await GetAsync(client, "n8-1-v2");
        Assert.Equal(lyrics, after.GetProperty("lyrics").GetString());
        Assert.Equal(marks.GetRawText(), after.GetProperty("imported").GetRawText());
    }

    /// <summary>A Version retained before import marks existed restores as one made in n8Tracks.</summary>
    [Fact]
    public void AVersionRetainedBeforeImportMarksRestoresWithNone()
    {
        var shape1 = new JsonObject { ["id"] = "A", ["lyrics"] = string.Empty };

        var shape2 = RetainedTypes.VersionShape1To2(shape1);

        Assert.True(shape2.ContainsKey("imported_inputs"));
        Assert.Null(shape2["imported_inputs"]);
        Assert.Equal(2, RetainedTypes.Version.ShapeVersion);
        Assert.True(RetainedTypes.Version.Upgraders.ContainsKey(1));
    }

    /// <summary>
    /// A linked clip is <c>conflict</c> when its mapped inputs differ from its Version's on an option
    /// Suno returns: the same clip with Weirdness changed, or a clip on a Version made in n8Tracks with
    /// other lyrics. A change only in what Suno does not return (its rewritten styles) is <c>changed</c>,
    /// and a clip whose Version holds its own inputs is <c>linked</c>.
    /// </summary>
    [Fact]
    public async Task ALinkedClipWhoseInputsDifferFromItsVersionsIsAConflict()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = (await SongApi.CreateAsync(client, "Linked")).GetProperty("id").GetGuid();
        var clips = SunoExportApi.FixtureClips(Advanced);
        var simple = SunoExportApi.FixtureClips("feed-v3.songs-simple.response.json");
        var (same, weirder) = (clips[0], clips[1]);
        var (restyled, onBlank) = (simple[0], simple[1]);

        await ImportedVersions.AddAsync(factory, song, "2", same);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", same.ToJsonString());
        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", weirder.ToJsonString());
        await ImportedVersions.AddAsync(factory, song, "3", restyled);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v3", restyled.ToJsonString());
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", onBlank.ToJsonString());

        var changedSlider = weirder.DeepClone();
        changedSlider["metadata"]!["control_sliders"]!["weirdness_constraint"] = 0.4;
        var changedTags = restyled.DeepClone();
        changedTags["metadata"]!["tags"] = "Suno rewrote the styles again";

        var (id, export) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(),
            SunoExportApi.Part(1, [same, changedSlider, changedTags, onBlank]));

        Assert.Equal(1, SunoExportApi.Count(export, "linked"));
        Assert.Equal(1, SunoExportApi.Count(export, "changed"));
        Assert.Equal(2, SunoExportApi.Count(export, "conflict"));
        var records = await SunoExportApi.RecordsByIdAsync(client, id);
        Assert.Equal("linked", records[SunoExportApi.IdOf(same)].GetProperty("class").GetString());
        Assert.Equal("conflict", records[SunoExportApi.IdOf(changedSlider)].GetProperty("class").GetString());
        Assert.Equal("changed", records[SunoExportApi.IdOf(changedTags)].GetProperty("class").GetString());
        Assert.Equal("conflict", records[SunoExportApi.IdOf(onBlank)].GetProperty("class").GetString());
    }

    /// <summary>
    /// Speech and Sounds in the catalog (#136): an imported Speech and Sound Version hold their kind and
    /// their own options through the store, with their not-returned marks; a sync review classes each
    /// clip <c>linked</c> on its own Version, and <c>conflict</c> on a Version of another kind (here a
    /// Song Version made in n8Tracks).
    /// </summary>
    [Fact]
    public async Task ASpeechAndASoundArriveAsWhatTheyAreAndConflictWithAnotherKind()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = (await SongApi.CreateAsync(client, "Speech and Sound")).GetProperty("id").GetGuid();
        var speech = SunoExportApi.FixtureClips("feed-v3.speech-advanced.response.json");
        var sound = SunoExportApi.FixtureClips("feed-v3.sounds.response.json");

        await ImportedVersions.AddAsync(factory, song, "2", speech[0]);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", speech[0].ToJsonString());
        await ImportedVersions.AddAsync(factory, song, "3", sound[0]);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v3", sound[0].ToJsonString());
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", sound[1].ToJsonString());

        var speechVersion = await GetAsync(client, "n8-1-v2");
        var speechInputs = speechVersion.GetProperty("inputs");
        Assert.Equal("speech", speechInputs.GetProperty("kind").GetString());
        Assert.Equal("advanced", speechInputs.GetProperty("speechMode").GetString());
        Assert.Equal("high", speechInputs.GetProperty("speechVariety").GetString());
        Assert.Equal(speech[0]["metadata"]!["prompt"]!.GetValue<string>(), speechInputs.GetProperty("speechScript").GetString());
        Assert.Equal(["speechTone", "speechVocalGender", "speechBackgroundMusic"], Strings(speechVersion.GetProperty("imported").GetProperty("notReturned")));

        var soundVersion = await GetAsync(client, "n8-1-v3");
        var soundInputs = soundVersion.GetProperty("inputs");
        Assert.Equal("sound", soundInputs.GetProperty("kind").GetString());
        Assert.Equal("loop", soundInputs.GetProperty("soundType").GetString());
        Assert.Equal(120, soundInputs.GetProperty("soundBpm").GetInt32());
        Assert.Equal("A", soundInputs.GetProperty("soundKey").GetString());
        Assert.Equal("minor", soundInputs.GetProperty("soundScale").GetString());
        Assert.Equal(["soundsModel"], Strings(soundVersion.GetProperty("imported").GetProperty("notReturned")));

        var (id, _) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(),
            SunoExportApi.Part(1, [speech[0], sound[0], sound[1]]));

        var records = await SunoExportApi.RecordsByIdAsync(client, id);
        Assert.Equal("linked", records[SunoExportApi.IdOf(speech[0])].GetProperty("class").GetString());
        Assert.Equal("linked", records[SunoExportApi.IdOf(sound[0])].GetProperty("class").GetString());
        Assert.Equal("conflict", records[SunoExportApi.IdOf(sound[1])].GetProperty("class").GetString());
    }

    /// <summary>
    /// A model Suno reports that is not on the list is proposed, not added: classifying an export that
    /// reports one leaves the list as it was. A commit adds it, in its own transaction, once however many
    /// clips report it, named and reported as Suno reported it and marked discovered; a model already on
    /// the list is matched, not added.
    /// </summary>
    [Fact]
    public async Task AnUnknownModelIsAddedOnlyByACommitAndOnlyOnce()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = (await SongApi.CreateAsync(client, "Models")).GetProperty("id").GetGuid();
        var clips = SunoExportApi.FixtureClips(Advanced);
        await ImportedVersions.AddAsync(factory, song, "2", clips[0]);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", clips[0].ToJsonString());
        var reportingNew = clips[0].DeepClone();
        reportingNew["metadata"]!["model_badges"]!["songrow"]!["display_name"] = "V9-NEW";
        var otherNew = clips[1].DeepClone();
        otherNew["metadata"]!["model_badges"]!["songrow"]!["display_name"] = "V9-NEW";
        var models = Models(factory);

        var (id, export) = await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, [reportingNew, otherNew]));

        Assert.Equal("ready", export.GetProperty("state").GetString());
        Assert.Equal("conflict", (await SunoExportApi.RecordsByIdAsync(client, id))[SunoExportApi.IdOf(reportingNew)].GetProperty("class").GetString());
        Assert.Equal(models, Models(factory));

        var names = await CommitAsync(factory, "V9-NEW", "v9-new", "V6-MINI");

        Assert.Equal(["V9-NEW", "V9-NEW", "v6-mini"], names);
        Assert.Equal([.. models, "V9-NEW|V9-NEW|1|4"], Models(factory));
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT json_extract(value, '$.revision') FROM settings WHERE key = 'suno.models';"));
    }

    /// <summary>Calls <see cref="ModelCatalogService.EnsureReportedAsync"/> for each name in one transaction, as a commit would.</summary>
    private static async Task<List<string>> CommitAsync(N8TracksApiFactory factory, params string[] reported)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalogService>();
            return await scope.ServiceProvider.GetRequiredService<IExclusiveTransaction>().RunAsync(
                async ct =>
                {
                    var names = new List<string>();
                    foreach (var name in reported)
                    {
                        names.Add(await catalog.EnsureReportedAsync(name, ct));
                    }

                    return names;
                },
                CancellationToken.None);
        }
    }

    /// <summary>Every model as <c>name|reported as|discovered|position</c>, in order.</summary>
    private static List<string> Models(N8TracksApiFactory factory) =>
        TestDatabase.Rows(factory.DataPath, "SELECT name || '|' || coalesce(reported_as, '') || '|' || discovered || '|' || position FROM suno_models ORDER BY position;");

    private static JsonNode Fixture(string name) => JsonNode.Parse(Clips.FixtureClip(name))!;

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(static item => item.GetString())];

    private static async Task<JsonElement> GetAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{reference}", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string reference, int revision, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/versions/{reference}", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision));
        return await client.SendAsync(request);
    }
}
