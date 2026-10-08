using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The sources a Song's player compares (#220) through the API, over real scans of the host's
/// temporary media folder: the Song-level files first, then the Generations in Version tree order
/// with their rating, revision and duration, each with its playback file first and its other files
/// after; Missing files and Generations with nothing to play left out; the refusals; and reading it
/// writes nothing.
/// </summary>
public sealed class PlaybackSourcesTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string B = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";
    private const string C = "1d2c3b4a-5f6e-4d7c-8b9a-a1b2c3d4e5f6";

    [Fact]
    public async Task TheSourcesAreNestedByGenerationInComparisonOrderAndReadingWritesNothing()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(A, 93.5));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(B, 61));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(C, null));
        MediaApi.Place(factory, $"take (suno-{A}).mp3", "mp3");
        MediaApi.Place(factory, $"take (suno-{A}).wav", "wav");
        MediaApi.Place(factory, $"second (suno-{B}).m4a", "m4a");
        MediaApi.Place(factory, $"third (suno-{C}).wav", "wav");
        MediaApi.Place(factory, "Loose/master.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var listed = (await MediaApi.ListAsync(client)).Items;
        var master = MediaApi.ByPath(listed, "Loose/master.mp3");
        await SendOkAsync(client, HttpMethod.Put, $"audio-files/{master.GetProperty("id").GetGuid()}/association", master.GetProperty("revision").GetInt32(), """{"song":"n8-1"}""");
        await SendOkAsync(client, HttpMethod.Patch, "generations/n8-1-v1-g1", await RevisionAsync(client, "generations/n8-1-v1-g1"), """{"rating":4}""");
        var mp3 = MediaApi.ByPath(listed, $"take (suno-{A}).mp3");
        await SendOkAsync(client, HttpMethod.Put, "generations/n8-1-v1-g1/preferred-audio-file", await RevisionAsync(client, "generations/n8-1-v1-g1"), $$"""{"audioFile":"{{mp3.GetProperty("id").GetGuid()}}"}""");

        // G3's only file goes Missing: G3 is left out.
        var outside = Path.Combine(factory.DataPath, "outside");
        Directory.CreateDirectory(outside);
        File.Move(MediaApi.FullPath(factory, $"third (suno-{C}).wav"), Path.Combine(outside, "away.wav"));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var before = MediaApi.Listing(factory.MediaPath);
        var rows = AllRows(factory);
        var g1Revision = await RevisionAsync(client, "generations/n8-1-v1-g1");

        var sources = await SourcesAsync(client, "n8-1");

        Assert.Equal("n8-1", sources.GetProperty("song").GetProperty("shortcode").GetString());
        Assert.Equal("Origin", sources.GetProperty("song").GetProperty("title").GetString());
        var songFiles = sources.GetProperty("songFiles").EnumerateArray().ToList();
        Assert.Equal(["master.mp3"], songFiles.Select(static file => file.GetProperty("audioFile").GetProperty("fileName").GetString()));
        Assert.False(songFiles[0].GetProperty("isPlaybackFile").GetBoolean());
        Assert.Equal($"/api/v1/audio-files/{master.GetProperty("id").GetGuid()}/content", songFiles[0].GetProperty("audioFile").GetProperty("contentUrl").GetString());

        var generations = sources.GetProperty("generations").EnumerateArray().ToList();
        Assert.Equal(["n8-1-v1-g1", "n8-1-v1-g2"], generations.Select(static generation => generation.GetProperty("generation").GetProperty("shortcode").GetString()));
        var g1 = generations[0];
        Assert.Equal("1", g1.GetProperty("versionNumber").GetString());
        Assert.Equal(4, g1.GetProperty("rating").GetInt32());
        Assert.Equal(g1Revision, g1.GetProperty("revision").GetInt32());
        Assert.Equal(93.5, g1.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("active", g1.GetProperty("state").GetString());
        Assert.Equal("present", g1.GetProperty("remoteState").GetString());

        // Its chosen MP3 first (the one it plays), then the WAV.
        var g1Files = g1.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["mp3", "wav"], g1Files.Select(static file => file.GetProperty("audioFile").GetProperty("format").GetString()));
        Assert.Equal([true, false], g1Files.Select(static file => file.GetProperty("isPlaybackFile").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, generations[1].GetProperty("rating").ValueKind);
        Assert.Equal(["m4a"], generations[1].GetProperty("files").EnumerateArray().Select(static file => file.GetProperty("audioFile").GetProperty("format").GetString()));

        // Reading wrote nothing: no revision, no choice, no file.
        Assert.Equal(rows, AllRows(factory));
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
    }

    [Fact]
    public async Task AnUnknownSongIsNotFoundAndADeletedOneSaysSo()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Origin");

        var empty = await SourcesAsync(client, "n8-1");
        Assert.Empty(empty.GetProperty("songFiles").EnumerateArray());
        Assert.Empty(empty.GetProperty("generations").EnumerateArray());

        using (var response = await client.GetAsync(new Uri("/api/v1/songs/n8-99/playback-sources", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-1", await RevisionAsync(client, "songs/n8-1"), """{"confirmTitle":"Origin"}"""))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }

        using (var response = await client.GetAsync(new Uri("/api/v1/songs/n8-1/playback-sources", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "song_deleted");
        }
    }

    /// <summary>A clip with a duration (or none).</summary>
    private static string Clip(string sunoId, double? duration)
    {
        var clip = new JsonObject { ["id"] = sunoId, ["status"] = "complete", ["title"] = "Clip" };
        if (duration is { } seconds)
        {
            clip["metadata"] = new JsonObject { ["duration"] = seconds };
        }

        return clip.ToJsonString();
    }

    private static async Task<JsonElement> SourcesAsync(HttpClient client, string song)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/songs/{song}/playback-sources", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<int> RevisionAsync(HttpClient client, string path) =>
        (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/{path}", UriKind.Relative)))).GetProperty("revision").GetInt32();

    /// <summary>The Songs, Generations (with ratings), audio files and choices, as stored.</summary>
    private static List<string> AllRows(N8TracksApiFactory factory) =>
        TestDatabase.Rows(
            factory.DataPath,
            "SELECT 'song|' || id || '|' || revision || '|' || quote(selected_generation_id) FROM songs "
            + "UNION ALL SELECT 'generation|' || id || '|' || revision || '|' || quote(rating) FROM generations "
            + "UNION ALL SELECT 'file|' || id || '|' || revision || '|' || quote(song_id) || '|' || quote(generation_id) FROM audio_files "
            + "UNION ALL SELECT 'generation-choice|' || audio_file_id FROM generation_preferred_audio_files "
            + "UNION ALL SELECT 'song-choice|' || audio_file_id FROM song_preferred_audio_files ORDER BY 1;");

    private static async Task SendOkAsync(HttpClient client, HttpMethod method, string path, int revision, string json)
    {
        using var response = await SendAsync(client, method, path, revision, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int revision, string json)
    {
        using var request = new HttpRequestMessage(method, new Uri($"/api/v1/{path}", UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }
}
