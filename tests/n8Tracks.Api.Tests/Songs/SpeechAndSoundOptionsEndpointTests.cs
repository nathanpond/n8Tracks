using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// A Version's Speech and Sound options over <c>GET</c> and <c>PATCH /api/v1/versions/{reference}</c>:
/// their defaults, values, and refusals; <c>effectiveInputs</c> for each kind; that they are separate
/// from a Song's options with the same label; that they are creation inputs (copied and frozen); the
/// kind on a Song; and the upgrade that gives existing Versions these options.
/// </summary>
public sealed class SpeechAndSoundOptionsEndpointTests
{
    private const string SpeechAndSoundDefaults =
        ""","speechPrompt":"","speechScript":"","speechTone":"","speechVocalGender":null,"speechBackgroundMusic":true,"speechVariety":"normal","soundsModel":null,"soundDescription":"","soundType":"one_shot","soundBpm":null,"soundKey":"any","soundScale":null""";

    [Fact]
    public async Task ANewVersionHoldsEverySpeechAndSoundOptionAtSunosDefaults()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Defaults"));

        var inputs = (await GetAsync(client, id)).GetProperty("inputs").GetRawText();

        // Suno's defaults, but a Sound's model, which Suno leaves unset, is the first model the list offers.
        Assert.EndsWith(SpeechAndSoundDefaults.Replace("\"soundsModel\":null", "\"soundsModel\":\"v6\"", StringComparison.Ordinal) + "}", inputs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpeechTakesItsOwnOptionsAndSendsOnlyThoseOfItsMode()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Spoken"));
        await EditAsync(client, id, """{"lyrics":"[Verse]\nSung","inputs":{"vocalGender":"male","variety":"high"}}""");

        var advanced = await EditAsync(
            client,
            id,
            $$$"""{"inputs":{"kind":"speech","speechScript":"{{{new string('s', 5_000)}}}","speechTone":"Calm\r\nand slow","speechVocalGender":"female","speechBackgroundMusic":false,"speechVariety":"max"}}""");

        Assert.Equal(
            ["kind", "speechMode", "speechScript", "speechTone", "speechVocalGender", "speechBackgroundMusic", "speechVariety"],
            advanced.GetProperty("effectiveInputs").EnumerateObject().Select(static option => option.Name));
        var effective = advanced.GetProperty("effectiveInputs");
        Assert.Equal("Calm\nand slow", effective.GetProperty("speechTone").GetString());
        Assert.Equal("female", effective.GetProperty("speechVocalGender").GetString());
        Assert.False(effective.GetProperty("speechBackgroundMusic").GetBoolean());

        // Options with the same label are separate per kind: the Song's are untouched.
        var inputs = advanced.GetProperty("inputs");
        Assert.Equal("male", inputs.GetProperty("vocalGender").GetString());
        Assert.Equal("high", inputs.GetProperty("variety").GetString());
        Assert.Equal("max", inputs.GetProperty("speechVariety").GetString());

        // Simple: one prompt of up to 1,000 characters.
        var simple = await EditAsync(client, id, $$$"""{"inputs":{"speechMode":"simple","speechPrompt":"{{{new string('p', 1_000)}}}"}}""");
        Assert.Equal(["kind", "speechMode", "speechPrompt"], simple.GetProperty("effectiveInputs").EnumerateObject().Select(static option => option.Name));

        // Back to a Song: the lyrics and the Song's options are there; the Speech's are kept.
        var song = await EditAsync(client, id, """{"inputs":{"kind":"song"}}""");
        Assert.Equal("[Verse]\nSung", song.GetProperty("effectiveInputs").GetProperty("lyrics").GetString());
        Assert.Equal("male", song.GetProperty("effectiveInputs").GetProperty("vocalGender").GetString());
        Assert.Equal(5_000, song.GetProperty("inputs").GetProperty("speechScript").GetString()!.Length);
    }

    [Fact]
    public async Task ASoundTakesItsOwnModelTypeBpmKeyAndScale()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Sampled"));
        await EditAsync(client, id, """{"inputs":{"model":"v6"}}""");

        var sound = await EditAsync(
            client,
            id,
            $$$"""{"inputs":{"kind":"sound","soundsModel":"v6-mini","soundDescription":"{{{new string('d', 500)}}}","soundType":"loop","soundBpm":120,"soundKey":"A","soundScale":"minor"}}""");

        var effective = sound.GetProperty("effectiveInputs");
        Assert.Equal(
            ["kind", "soundsModel", "soundDescription", "soundType", "soundBpm", "soundKey", "soundScale"],
            effective.EnumerateObject().Select(static option => option.Name));
        Assert.Equal("v6-mini", effective.GetProperty("soundsModel").GetString());
        Assert.Equal(120, effective.GetProperty("soundBpm").GetInt32());
        Assert.Equal("minor", effective.GetProperty("soundScale").GetString());

        // The Song's model is its own; the model column keeps it.
        Assert.Equal("v6", sound.GetProperty("inputs").GetProperty("model").GetString());
        Assert.Equal("sound|v6", TestDatabase.Scalar(factory.DataPath, "SELECT kind || '|' || coalesce(model, '') FROM versions;"));

        // Complement: with the key back to Any, the stored scale is kept but not sent; BPM back to Auto is null.
        var any = await EditAsync(client, id, """{"inputs":{"soundKey":"any","soundBpm":null}}""");
        Assert.Equal("minor", any.GetProperty("inputs").GetProperty("soundScale").GetString());
        Assert.False(any.GetProperty("effectiveInputs").TryGetProperty("soundScale", out _));
        Assert.Equal(JsonValueKind.Null, any.GetProperty("effectiveInputs").GetProperty("soundBpm").ValueKind);

        // The edges of BPM, a sharp key, and no scale are taken.
        var edges = await EditAsync(client, id, """{"inputs":{"soundBpm":1,"soundKey":"C#","soundScale":null}}""");
        Assert.Equal(1, edges.GetProperty("inputs").GetProperty("soundBpm").GetInt32());
        Assert.Equal(300, (await EditAsync(client, id, """{"inputs":{"soundBpm":300}}""")).GetProperty("inputs").GetProperty("soundBpm").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await GetAsync(client, id)).GetProperty("effectiveInputs").GetProperty("soundScale").ValueKind);
    }

    [Fact]
    public async Task ASpeechOrSoundValueOutsideItsRangeListOrLimitIsRefusedAndChangesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Bounded"));
        var before = (await GetAsync(client, id)).GetRawText();

        foreach (var (json, field, message) in new[]
        {
            ("""{"inputs":{"soundBpm":0}}""", "inputs.soundBpm", "BPM is a whole number from 1 to 300, or null for none."),
            ("""{"inputs":{"soundBpm":301}}""", "inputs.soundBpm", "from 1 to 300"),
            ("""{"inputs":{"soundBpm":120.5}}""", "inputs.soundBpm", "BPM"),
            ("""{"inputs":{"soundBpm":"120"}}""", "inputs.soundBpm", "BPM"),
            ("""{"inputs":{"soundKey":"H"}}""", "inputs.soundKey", "Choose Key: any, C, C#, D, D#, E, F, F#, G, G#, A, A#, B."),
            ("""{"inputs":{"soundKey":"c"}}""", "inputs.soundKey", "Choose Key"),
            ("""{"inputs":{"soundKey":null}}""", "inputs.soundKey", "Choose Key"),
            ("""{"inputs":{"soundType":"drone"}}""", "inputs.soundType", "Choose Type: one_shot, loop."),
            ("""{"inputs":{"soundScale":"dorian"}}""", "inputs.soundScale", "Choose Key scale: major, minor, or null for none."),
            ("""{"inputs":{"soundsModel":"v5"}}""", "inputs.soundsModel", "the model list"),
            ("""{"inputs":{"speechVariety":"loud"}}""", "inputs.speechVariety", "Choose Variety: off, normal, high, extra, max."),
            ("""{"inputs":{"speechVocalGender":"other"}}""", "inputs.speechVocalGender", "Choose Vocal Gender"),
            ("""{"inputs":{"speechBackgroundMusic":"on"}}""", "inputs.speechBackgroundMusic", "Background music: send true or false."),
            ($$$"""{"inputs":{"speechScript":"{{{new string('x', 5_001)}}}"}}""", "inputs.speechScript", "Use at most 5,000 characters."),
            ($$$"""{"inputs":{"speechTone":"{{{new string('x', 1_001)}}}"}}""", "inputs.speechTone", "Use at most 1,000 characters."),
            ($$$"""{"inputs":{"speechPrompt":"{{{new string('x', 1_001)}}}"}}""", "inputs.speechPrompt", "Use at most 1,000 characters."),
            ($$$"""{"inputs":{"soundDescription":"{{{new string('x', 501)}}}"}}""", "inputs.soundDescription", "Use at most 500 characters."),
            ("""{"inputs":{"soundDescription":null}}""", "inputs.soundDescription", "Sound: send text."),
            ("""{"inputs":{"soundMode":"simple"}}""", "inputs.soundMode", "'soundMode' is not an option of a Version."),
        })
        {
            using var response = await PatchAsync(client, id, await IfMatchAsync(client, id), json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(static error => error.Name));
            Assert.Contains(message, problem.GetProperty("errors").GetProperty(field)[0].GetString(), StringComparison.Ordinal);
        }

        Assert.Equal(before, (await GetAsync(client, id)).GetRawText());
    }

    [Fact]
    public async Task SpeechAndSoundOptionsAreCopiedByCreateNewVersionFromAndFrozenWithTheVersion()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Branching"));
        var source = await EditAsync(
            client,
            id,
            """{"inputs":{"kind":"sound","speechScript":"Hello","speechVariety":"off","soundDescription":"Rain","soundBpm":90,"soundKey":"F#","soundScale":"major"}}""");
        await SongApi.AttachGenerationAsync(factory, id.ToString());

        foreach (var json in new[] { """{"inputs":{"soundBpm":91}}""", """{"inputs":{"speechScript":"Bye"}}""", """{"inputs":{"kind":"speech"}}""", """{"inputs":{"soundScale":null}}""" })
        {
            using var refused = await PatchAsync(client, id, await IfMatchAsync(client, id), json);
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "version_frozen");
        }

        using var created = await SongApi.SendJsonAsync(
            client,
            HttpMethod.Post,
            new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative),
            """{"sourceVersionId":"n8-1-v1","number":"2"}""");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var copyId = (await SetupApi.JsonAsync(created)).GetProperty("id").GetGuid();

        var copy = await GetAsync(client, copyId);
        Assert.Equal(source.GetProperty("inputs").GetRawText(), copy.GetProperty("inputs").GetRawText());
        Assert.Equal(91, (await EditAsync(client, copyId, """{"inputs":{"soundBpm":91}}""")).GetProperty("inputs").GetProperty("soundBpm").GetInt32());
    }

    [Fact]
    public async Task ASongShowsTheKindOfItsCurrentVersionInTheListAndOnItsOwnAndEachVersionItsOwn()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var created = await SongApi.CreateAsync(client, "Kinds");
        Assert.Equal("song", created.GetProperty("currentVersion").GetProperty("kind").GetString());
        var edited = await EditAsync(client, VersionId(created), """{"inputs":{"kind":"speech"}}""");

        // A Version says its kind too, in the edit's answer and in the tree's list.
        Assert.Equal("speech", edited.GetProperty("kind").GetString());
        var versions = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/songs/n8-1/versions", UriKind.Relative)));
        Assert.Equal("speech", versions.GetProperty("items")[0].GetProperty("kind").GetString());

        using var one = await client.GetAsync(SongApi.Song("n8-1"));
        Assert.Equal("speech", (await SetupApi.JsonAsync(one)).GetProperty("currentVersion").GetProperty("kind").GetString());
        await EditAsync(client, VersionId(created), """{"inputs":{"kind":"sound"}}""");
        var list = await SongApi.ListAsync(client);
        Assert.Equal("sound", list.GetProperty("items")[0].GetProperty("currentVersion").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task UpgradingGivesEveryExistingVersionTheSpeechAndSoundDefaultsFrozenOnesIncluded()
    {
        using var directory = new TemporaryDirectory();
        SqliteDatabase.CreateIfMissing(TestDatabase.FilePath(directory.Path));
        var builder = new DbContextOptionsBuilder<N8TracksDbContext>();
        builder.UseN8TracksSqlite(TestDatabase.FilePath(directory.Path));
        var options = builder.Options;
        await using (var context = new N8TracksDbContext(options))
        {
            var before = context.Database.GetMigrations().Single(static id => id.EndsWith("_AddVersionInputs", StringComparison.Ordinal));
            await context.GetService<IMigrator>().MigrateAsync(before);
        }

        // Two Versions as the previous schema stored them, the second frozen.
        var song = Upper(Guid.CreateVersion7());
        TestDatabase.Execute(
            directory.Path,
            $$"""
            INSERT INTO songs (id, shortcode_number, title, title_sort_key, workflow_state_id, created_utc, updated_utc, revision)
            VALUES ('{{song}}', 1, 'One', 'one', '{{Upper(DefaultWorkflowStates.Idea.Id)}}', '2026-10-01T09:00:00.000Z', '2026-10-01T09:00:00.000Z', 1);
            INSERT INTO versions (id, song_id, number, number_sort_key, visibility, lyrics, styles, created_utc, updated_utc, revision, inputs)
            VALUES ('{{Upper(Guid.CreateVersion7())}}', '{{song}}', '1', '{{VersionNumbers.SortKey("1")}}', 'active', '', '', '2026-10-01T09:00:00.000Z', '2026-10-01T09:00:00.000Z', 1,
                    '{"songMode":"simple","speechMode":"advanced","simplePrompt":"Kept","simpleLyricsAdded":false,"simpleStylesAdded":false,"excludeStyles":"","vocalGender":"male","durationMode":"auto","durationSeconds":180,"maxMode":false,"weirdness":70,"styleInfluence":50,"variety":"normal","personalize":false,"title":"One"}');
            INSERT INTO versions (id, song_id, number, number_sort_key, visibility, lyrics, styles, created_utc, updated_utc, revision)
            VALUES ('{{Upper(Guid.CreateVersion7())}}', '{{song}}', '2', '{{VersionNumbers.SortKey("2")}}', 'active', '', '', '2026-10-01T09:00:00.000Z', '2026-10-01T09:00:00.000Z', 1);
            UPDATE versions SET is_frozen = 1, last_generation_ordinal = 1 WHERE number = '2';
            """);

        await using (var context = new N8TracksDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        var rows = TestDatabase.Rows(directory.Path, "SELECT inputs FROM versions ORDER BY number;");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.EndsWith(SpeechAndSoundDefaults + "}", row, StringComparison.Ordinal));
        Assert.Contains("\"simplePrompt\":\"Kept\"", rows[0], StringComparison.Ordinal);
        Assert.Contains("\"weirdness\":70", rows[0], StringComparison.Ordinal);

        // The freeze trigger is back, unchanged.
        var error = Assert.ThrowsAny<Exception>(() => TestDatabase.Execute(directory.Path, "UPDATE versions SET inputs = '{}' WHERE number = '2';"));
        Assert.Contains("A frozen Version's inputs never change.", error.Message, StringComparison.Ordinal);

        // And the upgraded Versions read through the API.
        using var factory = TestDatabase.Host(directory.Path);
        using var client = await SessionApi.SignedInClientAsync(factory);
        foreach (var version in TestDatabase.Rows(directory.Path, "SELECT id FROM versions ORDER BY number;"))
        {
            var detail = await GetAsync(client, Guid.Parse(version));
            Assert.Equal("any", detail.GetProperty("inputs").GetProperty("soundKey").GetString());
        }
    }

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    private static Guid VersionId(JsonElement song) => song.GetProperty("currentVersion").GetProperty("id").GetGuid();

    private static Uri Version(Guid id) => new($"/api/v1/versions/{id}", UriKind.Relative);

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(Version(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<string> IfMatchAsync(HttpClient client, Guid id) =>
        SongApi.Quoted((await GetAsync(client, id)).GetProperty("revision").GetInt32());

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, string ifMatch, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, Version(id))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        return await client.SendAsync(request);
    }

    /// <summary>Edits a Version at its current revision and returns it, asserting 200.</summary>
    private static async Task<JsonElement> EditAsync(HttpClient client, Guid id, string json)
    {
        using var response = await PatchAsync(client, id, await IfMatchAsync(client, id), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }
}
