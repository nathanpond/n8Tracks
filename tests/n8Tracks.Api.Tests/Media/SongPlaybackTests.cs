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
/// Play on a Song (#219) through the API, over real scans of the host's temporary media folder: the
/// Song's playback answer in each state of the one rule (needs a choice, with the chooser's candidates
/// in order; ready by the Selected Generation and by the Song-level preferred file; the Selected
/// Generation with nothing to play, with its Suno ID; nothing at all), the same state on the Song,
/// the Songs list, Album tracks and Playlist songs, and the refusals. Reading it writes nothing.
/// </summary>
public sealed class SongPlaybackTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string B = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";
    private const string C = "1d2c3b4a-5f6e-4d7c-8b9a-a1b2c3d4e5f6";

    private static readonly string Wav = $"take (suno-{A}).wav";

    [Fact]
    public async Task EachStateOfTheRuleIsAnsweredOnThePlaybackReadAndOnEverySongRow()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.CreateAsync(client, "Empty");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(A, 93.5));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(B, 61));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(C, null));
        MediaApi.Place(factory, Wav, "wav");
        MediaApi.Place(factory, $"second (suno-{B}).m4a", "m4a");
        MediaApi.Place(factory, "Loose/master.mp3", "mp3");
        MediaApi.Place(factory, "Loose/alternate.wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var listed = (await MediaApi.ListAsync(client)).Items;
        var master = MediaApi.ByPath(listed, "Loose/master.mp3");
        await SendOkAsync(client, HttpMethod.Put, $"audio-files/{master.GetProperty("id").GetGuid()}/association", master.GetProperty("revision").GetInt32(), """{"song":"n8-1"}""");
        var alternate = MediaApi.ByPath(listed, "Loose/alternate.wav");
        await SendOkAsync(client, HttpMethod.Put, $"audio-files/{alternate.GetProperty("id").GetGuid()}/association", alternate.GetProperty("revision").GetInt32(), """{"song":"n8-1"}""");
        await SendOkAsync(client, HttpMethod.Patch, "generations/n8-1-v1-g1", await RevisionAsync(client, "generations/n8-1-v1-g1"), """{"rating":4}""");
        await SendOkAsync(client, HttpMethod.Patch, "generations/n8-1-v1-g2", await RevisionAsync(client, "generations/n8-1-v1-g2"), """{"state":"archived"}""");
        var before = MediaApi.Listing(factory.MediaPath);
        var songRevision = await RevisionAsync(client, "songs/n8-1");
        var rows = AllRows(factory);

        // Nothing selected: Play asks. Nothing is picked; the Song-level files come first (WAV before
        // MP3), then the Active Generations, then the Archived one, each with whether it plays.
        var playback = await PlaybackAsync(client, "n8-1");
        Assert.Equal("needs-choice", playback.GetProperty("state").GetString());
        Assert.Equal("none", playback.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, playback.GetProperty("audioFile").ValueKind);
        Assert.Equal("no_selected_generation", playback.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, playback.GetProperty("generation").ValueKind);
        var candidates = playback.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(
            ["file alternate.wav", "file master.mp3", "generation n8-1-v1-g1", "generation n8-1-v1-g3", "generation n8-1-v1-g2"],
            candidates.Select(static candidate => candidate.GetProperty("kind").GetString() == "file"
                ? $"file {candidate.GetProperty("audioFile").GetProperty("fileName").GetString()}"
                : $"generation {candidate.GetProperty("generation").GetProperty("shortcode").GetString()}"));
        Assert.Equal([true, true, true, false, true], candidates.Select(static candidate => candidate.GetProperty("playable").GetBoolean()));
        var g1 = candidates[2];
        Assert.Equal("1", g1.GetProperty("versionNumber").GetString());
        Assert.Equal(4, g1.GetProperty("rating").GetInt32());
        Assert.Equal(93.5, g1.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("active", g1.GetProperty("state").GetString());
        Assert.Equal("present", g1.GetProperty("remoteState").GetString());
        Assert.Equal(JsonValueKind.Null, g1.GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, g1.GetProperty("audioFile").ValueKind);
        Assert.Equal("nothing_available", candidates[3].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, candidates[3].GetProperty("durationSeconds").ValueKind);
        Assert.Equal("archived", candidates[4].GetProperty("state").GetString());
        var file = candidates[0];
        Assert.Equal("wav", file.GetProperty("audioFile").GetProperty("format").GetString());
        Assert.Equal($"/api/v1/audio-files/{alternate.GetProperty("id").GetGuid()}/content", file.GetProperty("audioFile").GetProperty("contentUrl").GetString());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("generation").ValueKind);
        await AssertRowsAsync(client, "needs-choice", "no_selected_generation");

        // Reading wrote nothing: no selection, no revision, no file.
        Assert.Equal(songRevision, await RevisionAsync(client, "songs/n8-1"));
        Assert.Equal(rows, AllRows(factory));
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));

        // A Song with no Generations and no files: nothing to offer.
        var empty = await PlaybackAsync(client, "n8-2");
        Assert.Equal("none", empty.GetProperty("state").GetString());
        Assert.Equal("no_generations", empty.GetProperty("reason").GetString());
        Assert.Empty(empty.GetProperty("candidates").EnumerateArray());
        var emptySong = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-2")));
        AssertPlayback(emptySong, "none", "no_generations");

        // The Selected Generation plays, named with its Suno ID; nothing to choose.
        await SendOkAsync(client, HttpMethod.Put, "songs/n8-1/selected-generation", await RevisionAsync(client, "songs/n8-1"), """{"generation":"n8-1-v1-g1"}""");
        playback = await PlaybackAsync(client, "n8-1");
        Assert.Equal("ready", playback.GetProperty("state").GetString());
        Assert.Equal(Wav, playback.GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.Equal("format_order", playback.GetProperty("reason").GetString());
        Assert.Equal("n8-1-v1-g1", playback.GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal(A, playback.GetProperty("generation").GetProperty("sunoId").GetString());
        Assert.Empty(playback.GetProperty("candidates").EnumerateArray());
        await AssertRowsAsync(client, "ready", null);

        // Its file gone: the Selected Generation has nothing to play; nothing else is offered.
        var outside = Path.Combine(factory.DataPath, "outside");
        Directory.CreateDirectory(outside);
        File.Move(MediaApi.FullPath(factory, Wav), Path.Combine(outside, "away.wav"));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        playback = await PlaybackAsync(client, "n8-1");
        Assert.Equal("selected-unplayable", playback.GetProperty("state").GetString());
        Assert.Equal("none", playback.GetProperty("source").GetString());
        Assert.Equal("nothing_available", playback.GetProperty("reason").GetString());
        Assert.Equal(A, playback.GetProperty("generation").GetProperty("sunoId").GetString());
        Assert.Empty(playback.GetProperty("candidates").EnumerateArray());
        await AssertRowsAsync(client, "selected-unplayable", "nothing_available");

        // The Song-level preferred file plays over it.
        await SendOkAsync(client, HttpMethod.Put, "songs/n8-1/preferred-audio-file", await RevisionAsync(client, "songs/n8-1"), $$"""{"audioFile":"{{master.GetProperty("id").GetGuid()}}"}""");
        playback = await PlaybackAsync(client, "n8-1");
        Assert.Equal("ready", playback.GetProperty("state").GetString());
        Assert.Equal("master.mp3", playback.GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.Equal("song_preferred", playback.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, playback.GetProperty("generation").ValueKind);
        await AssertRowsAsync(client, "ready", null);
        Assert.Equal(before.Where(static entry => !entry.StartsWith($"f {Wav} ", StringComparison.Ordinal)), MediaApi.Listing(factory.MediaPath));
    }

    [Fact]
    public async Task AnUnknownSongIsNotFoundAndADeletedOneSaysSo()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Origin");

        using (var response = await client.GetAsync(new Uri("/api/v1/songs/n8-99/playback", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-1", await RevisionAsync(client, "songs/n8-1"), """{"confirmTitle":"Origin"}"""))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }
        using (var response = await client.GetAsync(new Uri("/api/v1/songs/n8-1/playback", UriKind.Relative)))
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

    /// <summary>n8-1's <c>playback</c> on the Song, the Songs list, an Album's track, and a Playlist's song.</summary>
    private static async Task AssertRowsAsync(HttpClient client, string state, string? reason)
    {
        AssertPlayback(await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1"))), state, reason);
        var list = await SongApi.ListAsync(client);
        AssertPlayback(list.GetProperty("items").EnumerateArray().Single(static song => song.GetProperty("shortcode").GetString() == "n8-1"), state, reason);

        var album = await CollectionAsync(client, "albums", "tracks");
        AssertPlayback(album.GetProperty("tracks").EnumerateArray().Single(static track => track.GetProperty("shortcode").GetString() == "n8-1"), state, reason);
        var playlist = await CollectionAsync(client, "playlists", "songs");
        AssertPlayback(playlist.GetProperty("songs").EnumerateArray().Single(static song => song.GetProperty("shortcode").GetString() == "n8-1"), state, reason);
    }

    /// <summary>The one Album (or Playlist) holding n8-1 and n8-2, made the first time it is asked for.</summary>
    private static async Task<JsonElement> CollectionAsync(HttpClient client, string collection, string member)
    {
        var list = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/{collection}", UriKind.Relative)));
        Guid id;
        if (list.GetProperty("items").GetArrayLength() == 0)
        {
            using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/{collection}", UriKind.Relative), JsonSerializer.Serialize(new { title = "Set" }));
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            var record = await SetupApi.JsonAsync(created);
            id = record.GetProperty("id").GetGuid();
            var revision = record.GetProperty("revision").GetInt32();
            foreach (var song in new[] { "n8-1", "n8-2" })
            {
                using var added = await SendAsync(client, HttpMethod.Post, $"{collection}/{id}/{member}", revision, $$"""{"songId":"{{song}}"}""");
                Assert.True(added.StatusCode == HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
                revision = (await SetupApi.JsonAsync(added)).GetProperty("revision").GetInt32();
            }
        }
        else
        {
            id = list.GetProperty("items")[0].GetProperty("id").GetGuid();
        }

        var collected = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/{collection}/{id}", UriKind.Relative)));
        AssertPlayback(collected.GetProperty(member).EnumerateArray().Single(static song => song.GetProperty("shortcode").GetString() == "n8-2"), "none", "no_generations");
        return collected;
    }

    private static void AssertPlayback(JsonElement owner, string state, string? reason)
    {
        var playback = owner.GetProperty("playback");
        Assert.Equal(state, playback.GetProperty("state").GetString());
        Assert.Equal(reason, playback.GetProperty("reason").GetString());
    }

    private static async Task<JsonElement> PlaybackAsync(HttpClient client, string song)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/songs/{song}/playback", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<int> RevisionAsync(HttpClient client, string path) =>
        (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/{path}", UriKind.Relative)))).GetProperty("revision").GetInt32();

    /// <summary>The Songs, Generations, audio files and choices, as stored.</summary>
    private static List<string> AllRows(N8TracksApiFactory factory) =>
        TestDatabase.Rows(
            factory.DataPath,
            "SELECT 'song|' || id || '|' || revision || '|' || quote(selected_generation_id) FROM songs "
            + "UNION ALL SELECT 'generation|' || id || '|' || revision FROM generations "
            + "UNION ALL SELECT 'file|' || id || '|' || revision || '|' || quote(song_id) || '|' || quote(generation_id) FROM audio_files "
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
