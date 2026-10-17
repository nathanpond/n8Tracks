using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Domain.Catalog;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// A Version's settings from the source chosen for its Cover, Extend, or Remix (#431): the preview
/// says what the source fills (the Version's empty settings), what differs (each the user's choice),
/// and what is not known; applying sets the source with the fills and the chosen settings in one edit.
/// A preview changes nothing, so cancelling is not calling apply; a frozen Version is refused; other
/// source actions fill nothing.
/// </summary>
public sealed class VersionSourceSettingsEndpointTests
{
    private static readonly string Cover = SystemRelationshipTypes.Cover.Id.ToString();
    private static readonly string Extend = SystemRelationshipTypes.Extend.Id.ToString();
    private static readonly string Sample = SystemRelationshipTypes.SampleThisSong.Id.ToString();

    [Fact]
    public async Task PreviewFillsTheEmptySettingsListsTheDifferingOnesAndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generation = await SourceAsync(factory, client, "Preview original");
        var target = await TargetAsync(client, "Preview cover");
        var before = await GetAsync(client, target);

        var plan = await PreviewAsync(client, target, CoverOf(generation));

        Assert.Equal("cover", plan.GetProperty("action").GetString());
        Assert.Equal(before.GetProperty("revision").GetInt32(), plan.GetProperty("revision").GetInt32());
        var fills = Keyed(plan, "fills");
        Assert.Equal(["lyrics", "excludeStyles", "vocalGender", "voice"], fills.Keys);
        Assert.Equal("[Verse]\nFrom the source", fills["lyrics"].GetProperty("sourceValue").GetString());
        Assert.Equal("female", fills["vocalGender"].GetProperty("sourceValue").GetString());
        Assert.Equal("persona-1", fills["voice"].GetProperty("sourceValue").GetProperty("personaId").GetString());
        var conflicts = Keyed(plan, "conflicts");
        Assert.Equal(["styles", "weirdness"], conflicts.Keys);
        Assert.Equal("my own styles", conflicts["styles"].GetProperty("versionValue").GetString());
        Assert.Equal("source styles", conflicts["styles"].GetProperty("sourceValue").GetString());
        Assert.Equal(50, conflicts["weirdness"].GetProperty("versionValue").GetInt32());
        Assert.Equal(70, conflicts["weirdness"].GetProperty("sourceValue").GetInt32());
        Assert.Empty(plan.GetProperty("unknown").EnumerateArray());

        // The Suno title and the form choosers are never offered, and a setting both hold alike is not listed.
        Assert.DoesNotContain("title", fills.Keys.Concat(conflicts.Keys));
        Assert.DoesNotContain("songMode", fills.Keys.Concat(conflicts.Keys));
        Assert.DoesNotContain("styleInfluence", fills.Keys.Concat(conflicts.Keys));

        // Previewing (and so cancelling) changes nothing, not even the empty settings or the sources.
        Assert.Equal(before.GetRawText(), (await GetAsync(client, target)).GetRawText());
    }

    [Fact]
    public async Task ApplyFillsTheEmptySettingsAndTakesOnlyTheChosenConflictsInOneEdit()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generation = await SourceAsync(factory, client, "Apply original");
        var target = await TargetAsync(client, "Apply cover");
        var plan = await PreviewAsync(client, target, CoverOf(generation));
        var revision = plan.GetProperty("revision").GetInt32();

        using var response = await ApplyAsync(client, target, revision, CoverOf(generation), """["weirdness"]""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var applied = await SetupApi.JsonAsync(response);

        Assert.Equal(revision + 1, applied.GetProperty("revision").GetInt32());
        Assert.Equal("[Verse]\nFrom the source", applied.GetProperty("lyrics").GetString());
        Assert.Equal("my own styles", applied.GetProperty("styles").GetString());
        var inputs = applied.GetProperty("inputs");
        Assert.Equal(70, inputs.GetProperty("weirdness").GetInt32());
        Assert.Equal("no drums", inputs.GetProperty("excludeStyles").GetString());
        Assert.Equal("female", inputs.GetProperty("vocalGender").GetString());
        Assert.Equal("persona-1", inputs.GetProperty("voice").GetProperty("personaId").GetString());
        Assert.Equal("My cover title", inputs.GetProperty("title").GetString());
        var source = Assert.Single(inputs.GetProperty("sources").EnumerateArray());
        Assert.Equal("cover", source.GetProperty("sunoAction").GetString());
        Assert.Equal(generation, source.GetProperty("generation").GetProperty("shortcode").GetString());

        // Complement: with nothing taken, every conflicting setting keeps the Version's value.
        var kept = await TargetAsync(client, "Apply cover keeping");
        var keptPlan = await PreviewAsync(client, kept, CoverOf(generation));
        using var keeping = await ApplyAsync(client, kept, keptPlan.GetProperty("revision").GetInt32(), CoverOf(generation), "[]");
        var keptVersion = await SetupApi.JsonAsync(keeping);
        Assert.Equal("my own styles", keptVersion.GetProperty("styles").GetString());
        Assert.Equal(50, keptVersion.GetProperty("inputs").GetProperty("weirdness").GetInt32());
        Assert.Equal("[Verse]\nFrom the source", keptVersion.GetProperty("lyrics").GetString());
    }

    [Fact]
    public async Task ExtendFillsLikeCoverAndOtherActionsFillNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generation = await SourceAsync(factory, client, "Extend original");

        var extended = await TargetAsync(client, "Extended");
        var extend = $$$"""[{"typeId":"{{{Extend}}}","generation":"{{{generation}}}","continueAtSeconds":12.5}]""";
        var extendPlan = await PreviewAsync(client, extended, extend);
        Assert.Equal("extend", extendPlan.GetProperty("action").GetString());
        Assert.Contains("lyrics", Keyed(extendPlan, "fills").Keys);

        var sampled = await TargetAsync(client, "Sampled");
        var sample = $$$"""[{"typeId":"{{{Sample}}}","generation":"{{{generation}}}"}]""";
        var samplePlan = await PreviewAsync(client, sampled, sample);
        Assert.Equal(JsonValueKind.Null, samplePlan.GetProperty("action").ValueKind);
        Assert.Empty(samplePlan.GetProperty("fills").EnumerateArray());
        Assert.Empty(samplePlan.GetProperty("conflicts").EnumerateArray());

        // Applying another action sets the sources as an edit would, and nothing else.
        using var response = await ApplyAsync(client, sampled, samplePlan.GetProperty("revision").GetInt32(), sample, """["weirdness"]""");
        var applied = await SetupApi.JsonAsync(response);
        Assert.Equal("sample", Assert.Single(applied.GetProperty("inputs").GetProperty("sources").EnumerateArray()).GetProperty("sunoAction").GetString());
        Assert.Equal(string.Empty, applied.GetProperty("lyrics").GetString());
        Assert.Equal(50, applied.GetProperty("inputs").GetProperty("weirdness").GetInt32());
    }

    [Fact]
    public async Task ASourceWithNoVersionBehindItFillsNothingAndSaysWhy()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var target = await TargetAsync(client, "Unknown cover");

        var clip = $$$"""[{"typeId":"{{{Cover}}}","external":{"sunoId":"never-imported"}}]""";
        var plan = await PreviewAsync(client, target, clip);
        Assert.Equal("cover", plan.GetProperty("action").GetString());
        Assert.Empty(plan.GetProperty("fills").EnumerateArray());
        Assert.Empty(plan.GetProperty("conflicts").EnumerateArray());
        var unknown = plan.GetProperty("unknown").EnumerateArray().ToList();
        Assert.Contains(unknown, static item => item.GetProperty("key").GetString() == "lyrics");
        Assert.All(unknown, static item => Assert.Equal("not_imported", item.GetProperty("reason").GetString()));

        using var response = await ApplyAsync(client, target, plan.GetProperty("revision").GetInt32(), clip, "[]");
        var applied = await SetupApi.JsonAsync(response);
        Assert.Equal("never-imported", Assert.Single(applied.GetProperty("inputs").GetProperty("sources").EnumerateArray()).GetProperty("external").GetProperty("sunoId").GetString());
        Assert.Equal(string.Empty, applied.GetProperty("lyrics").GetString());

        var song = await SongApi.CreateAsync(client, "A Song as the source");
        var songPlan = await PreviewAsync(client, target, $$$"""[{"typeId":"{{{Cover}}}","song":"{{{song.GetProperty("shortcode").GetString()}}}"}]""");
        Assert.All(songPlan.GetProperty("unknown").EnumerateArray(), static item => Assert.Equal("song_only", item.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task AnImportedSourceFillsOnlyWhatSunoReturned()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generation = await SourceAsync(factory, client, "Imported original", static async (factory, versionId) =>
        {
            using var scope = factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>();
            var record = await context.Set<VersionRecord>().SingleAsync(version => version.Id == versionId);
            record.ImportedInputs = """{"notReturned":["excludeStyles","vocalGender"],"outOfRange":[],"rawValues":{}}""";
            await context.SaveChangesAsync();
        });
        var target = await TargetAsync(client, "Imported cover");

        var plan = await PreviewAsync(client, target, CoverOf(generation));

        Assert.Equal(["lyrics", "voice"], Keyed(plan, "fills").Keys);
        var unknown = plan.GetProperty("unknown").EnumerateArray().ToDictionary(static item => item.GetProperty("key").GetString()!, static item => item.GetProperty("reason").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["excludeStyles"] = "not_returned", ["vocalGender"] = "not_returned" }, unknown);
    }

    [Fact]
    public async Task AFrozenVersionIsRefusedAndKeepsItsInputs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generation = await SourceAsync(factory, client, "Frozen original");
        var target = await TargetAsync(client, "Frozen cover");
        await SongApi.AttachGenerationAsync(factory, target.ToString());
        var before = await GetAsync(client, target);

        using (var preview = await PostAsync(client, target, $$"""{"sources":{{CoverOf(generation)}}}"""))
        {
            await SetupApi.ProblemAsync(preview, HttpStatusCode.Conflict, "version_frozen");
        }

        using (var applied = await ApplyAsync(client, target, before.GetProperty("revision").GetInt32(), CoverOf(generation), """["styles","weirdness"]"""))
        {
            await SetupApi.ProblemAsync(applied, HttpStatusCode.Conflict, "version_frozen");
        }

        Assert.Equal(before.GetRawText(), (await GetAsync(client, target)).GetRawText());
    }

    [Fact]
    public async Task AStaleRevisionAnUnknownSettingAndMissingSourcesAreRefused()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generation = await SourceAsync(factory, client, "Refused original");
        var target = await TargetAsync(client, "Refused cover");
        var plan = await PreviewAsync(client, target, CoverOf(generation));
        var revision = plan.GetProperty("revision").GetInt32();

        using (var stale = await ApplyAsync(client, target, revision - 1, CoverOf(generation), "[]"))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(revision, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var unknown = await ApplyAsync(client, target, revision, CoverOf(generation), """["title"]"""))
        {
            var problem = await SetupApi.ProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("take", out _));
        }

        using (var missing = await PostAsync(client, target, "{}"))
        {
            var problem = await SetupApi.ProblemAsync(missing, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("sources", out _));
        }

        using (var noRevision = await SongApi.SendJsonAsync(client, HttpMethod.Post, Path(target), $$"""{"sources":{{CoverOf(generation)}}}"""))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, noRevision.StatusCode);
        }

        Assert.Equal(revision, (await GetAsync(client, target)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task ASimpleSourceGivesAnAdvancedVersionTheSongsLyricsAndStylesFromItsClip()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generation = await SimpleSourceAsync(factory, client, "Simple original", SungClip("simple-original"));
        var target = await TargetAsync(client, "Advanced cover");

        var plan = await PreviewAsync(client, target, CoverOf(generation));

        // The lyrics are empty on the Version, so they are filled (as is the Song description, as before); its own styles differ, so they are asked about.
        var fills = Keyed(plan, "fills");
        Assert.Equal(["lyrics", "simplePrompt", "voice"], fills.Keys);
        Assert.Equal(ClipLyrics, fills["lyrics"].GetProperty("sourceValue").GetString());
        var conflicts = Keyed(plan, "conflicts");
        Assert.Equal(["styles"], conflicts.Keys);
        Assert.Equal("my own styles", conflicts["styles"].GetProperty("versionValue").GetString());
        Assert.Equal(ClipStyles, conflicts["styles"].GetProperty("sourceValue").GetString());
        Assert.Empty(plan.GetProperty("unknown").EnumerateArray());

        // The Simple form's section switches are part of the lyrics and styles, never asked about alone.
        Assert.DoesNotContain("simpleLyricsAdded", fills.Keys.Concat(conflicts.Keys));
        Assert.DoesNotContain("simpleStylesAdded", fills.Keys.Concat(conflicts.Keys));

        using var response = await ApplyAsync(client, target, plan.GetProperty("revision").GetInt32(), CoverOf(generation), """["styles"]""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var applied = await SetupApi.JsonAsync(response);
        Assert.Equal(ClipLyrics, applied.GetProperty("lyrics").GetString());
        Assert.Equal(ClipStyles, applied.GetProperty("styles").GetString());
        Assert.Equal("advanced", applied.GetProperty("inputs").GetProperty("songMode").GetString());

        // Complement: a Version already holding the same lyrics is not asked about them.
        var same = await TargetAsync(client, "Advanced cover with the lyrics", lyrics: ClipLyrics);
        var samePlan = await PreviewAsync(client, same, CoverOf(generation));
        Assert.DoesNotContain("lyrics", Keyed(samePlan, "fills").Keys.Concat(Keyed(samePlan, "conflicts").Keys));
    }

    [Fact]
    public async Task ASimpleSourceGivesASimpleVersionTheSongsLyricsAndStylesInItsSections()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generation = await SimpleSourceAsync(factory, client, "Simple to simple", SungClip("simple-to-simple"));
        var target = await SimpleTargetAsync(client, "Simple cover", """{"songMode":"simple"}""");

        var plan = await PreviewAsync(client, target, CoverOf(generation));

        var fills = Keyed(plan, "fills");
        Assert.Equal(["lyrics", "styles", "simplePrompt", "voice"], fills.Keys);
        Assert.Equal(ClipLyrics, fills["lyrics"].GetProperty("sourceValue").GetString());
        Assert.Equal(ClipStyles, fills["styles"].GetProperty("sourceValue").GetString());
        Assert.Empty(plan.GetProperty("conflicts").EnumerateArray());

        using var response = await ApplyAsync(client, target, plan.GetProperty("revision").GetInt32(), CoverOf(generation), "[]");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var applied = await SetupApi.JsonAsync(response);
        var inputs = applied.GetProperty("inputs");
        Assert.Equal("simple", inputs.GetProperty("songMode").GetString());
        Assert.True(inputs.GetProperty("simpleLyricsAdded").GetBoolean());
        Assert.True(inputs.GetProperty("simpleStylesAdded").GetBoolean());
        Assert.Equal(ClipLyrics, applied.GetProperty("lyrics").GetString());
        Assert.Equal(ClipStyles, applied.GetProperty("styles").GetString());

        // A Simple Version whose Add: Styles section holds its own text is asked, and keeps it unless it takes the source's.
        var own = await SimpleTargetAsync(client, "Simple cover with styles", """{"songMode":"simple","simpleStylesAdded":true}""", styles: "my simple styles");
        var ownPlan = await PreviewAsync(client, own, CoverOf(generation));
        var conflict = Assert.Single(ownPlan.GetProperty("conflicts").EnumerateArray());
        Assert.Equal("styles", conflict.GetProperty("key").GetString());
        Assert.Equal("my simple styles", conflict.GetProperty("versionValue").GetString());
        Assert.Equal(ClipStyles, conflict.GetProperty("sourceValue").GetString());
        using var keeping = await ApplyAsync(client, own, ownPlan.GetProperty("revision").GetInt32(), CoverOf(generation), "[]");
        var kept = await SetupApi.JsonAsync(keeping);
        Assert.Equal("my simple styles", kept.GetProperty("styles").GetString());
        Assert.Equal(ClipLyrics, kept.GetProperty("lyrics").GetString());
        Assert.True(kept.GetProperty("inputs").GetProperty("simpleLyricsAdded").GetBoolean());
    }

    [Fact]
    public async Task ASimpleSourceWhoseClipLacksThemTooReportsTheLyricsAndStylesUnknown()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generation = await SimpleSourceAsync(factory, client, "Simple bare", Clips.Minimal("simple-bare"));
        var target = await TargetAsync(client, "Bare cover");

        var plan = await PreviewAsync(client, target, CoverOf(generation));

        var unknown = plan.GetProperty("unknown").EnumerateArray().ToDictionary(static item => item.GetProperty("key").GetString()!, static item => item.GetProperty("reason").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["lyrics"] = "not_returned", ["styles"] = "not_returned" }, unknown);
        var offered = Keyed(plan, "fills").Keys.Concat(Keyed(plan, "conflicts").Keys).ToList();
        Assert.DoesNotContain("lyrics", offered);
        Assert.DoesNotContain("styles", offered);
    }

    private const string ClipLyrics = "[Verse]\nWritten by Suno\n[Chorus]\nSung back";
    private const string ClipStyles = "dreamy synth-pop with airy female vocals and a slow build";

    /// <summary>A clip whose song has lyrics and styles, as Suno returns a Simple-mode clip: Suno wrote them.</summary>
    private static string SungClip(string sunoId) => new JsonObject
    {
        ["id"] = sunoId,
        ["status"] = "complete",
        ["title"] = "Sung clip",
        ["metadata"] = new JsonObject
        {
            ["prompt"] = ClipLyrics,
            ["tags"] = ClipStyles,
            ["gpt_description_prompt"] = "a dreamy song",
            ["task"] = "agentic_thinking",
        },
    }.ToJsonString();

    /// <summary>
    /// A Song whose Version was made in Simple mode: a Song description and a Voice, no sections added,
    /// no lyrics or styles of its own, frozen by a Generation with <paramref name="clip"/>. The Generation's shortcode.
    /// </summary>
    private static async Task<string> SimpleSourceAsync(N8TracksApiFactory factory, HttpClient client, string title, string clip)
    {
        var song = await SongApi.CreateAsync(client, title);
        var id = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        using (var edited = await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative), SongApi.Quoted(1), """
            {"inputs":{"songMode":"simple","simplePrompt":"a dreamy song","voice":{"personaId":"persona-1","name":"Smoky"}}}
            """))
        {
            Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        }

        return (await SongApi.AttachGenerationAsync(factory, id.ToString(), clip)).Shortcode;
    }

    /// <summary>A Song whose Version holds the Simple-mode <paramref name="inputs"/> and <paramref name="styles"/>: the Version taking the source. Its ID.</summary>
    private static async Task<Guid> SimpleTargetAsync(HttpClient client, string title, string inputs, string styles = "")
    {
        var song = await SongApi.CreateAsync(client, title);
        var id = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        var body = new JsonObject { ["styles"] = styles, ["inputs"] = JsonNode.Parse(inputs) }.ToJsonString();
        using var edited = await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative), SongApi.Quoted(1), body);
        Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        return id;
    }

    private static string CoverOf(string generation) => $$$"""[{"typeId":"{{{Cover}}}","generation":"{{{generation}}}"}]""";

    /// <summary>
    /// A Song whose Version holds lyrics, styles, Exclude Styles, a Vocal Gender, Weirdness 70, and a
    /// Voice, frozen by a Generation: the source. <paramref name="before"/> runs on its Version before
    /// the Generation is attached. Returns the Generation's shortcode.
    /// </summary>
    private static async Task<string> SourceAsync(N8TracksApiFactory factory, HttpClient client, string title, Func<N8TracksApiFactory, Guid, Task>? before = null)
    {
        var song = await SongApi.CreateAsync(client, title);
        var id = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        using (var edited = await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative), SongApi.Quoted(1), """
            {"lyrics":"[Verse]\nFrom the source","styles":"source styles","inputs":{
              "excludeStyles":"no drums","vocalGender":"female","weirdness":70,"title":"Source title",
              "voice":{"personaId":"persona-1","name":"Smoky"}}}
            """))
        {
            Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        }

        if (before is not null)
        {
            await before(factory, id);
        }

        return (await SongApi.AttachGenerationAsync(factory, id.ToString(), Clips.Minimal("source-" + title.Replace(' ', '-')))).Shortcode;
    }

    /// <summary>A Song whose Version holds its own styles and title and nothing else: the Version taking the source. Its ID.</summary>
    private static async Task<Guid> TargetAsync(HttpClient client, string title, string lyrics = "")
    {
        var song = await SongApi.CreateAsync(client, title);
        var id = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        var body = new JsonObject { ["lyrics"] = lyrics, ["styles"] = "my own styles", ["inputs"] = new JsonObject { ["title"] = "My cover title" } }.ToJsonString();
        using var edited = await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative), SongApi.Quoted(1), body);
        Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        return id;
    }

    private static Dictionary<string, JsonElement> Keyed(JsonElement plan, string list) =>
        plan.GetProperty(list).EnumerateArray().ToDictionary(static item => item.GetProperty("key").GetString()!, static item => item, StringComparer.Ordinal);

    private static Uri Path(Guid id) => new($"/api/v1/versions/{id}/source-settings", UriKind.Relative);

    private static async Task<JsonElement> PreviewAsync(HttpClient client, Guid id, string sources)
    {
        using var response = await PostAsync(client, id, $$"""{"sources":{{sources}}}""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string json) =>
        SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/versions/{id}/source-settings/preview", UriKind.Relative), json);

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, Guid id, int revision, string sources, string take) =>
        SendAsync(client, HttpMethod.Post, Path(id), SongApi.Quoted(revision), $$"""{"sources":{{sources}},"take":{{take}}}""");

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string ifMatch, string json)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        return await client.SendAsync(request);
    }
}
