using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Media;
using n8Tracks.Application.Retention;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Audio file associations when Generations, Versions, and Songs are deleted, moved, or restored
/// (#213), through the API and real scans of the host's temporary media folder: each deletion impact
/// counts the files it leaves unassociated (Missing ones included, hand-associated and Song-level ones
/// apart); each deletion unassociates them with its reason and removes the Preferred Audio File
/// choices; a deleted Generation's files are not matched again while it stays deleted; a restore puts
/// no association or choice back, says so, and the next scan matches the files carrying a restored
/// Suno ID; a move carries a Generation's files and choice to its new Song and leaves Song-level files.
/// The media folder is identical, file by file and byte for byte, before and after every path.
/// </summary>
public sealed class AudioFileLifecycleTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string B = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";
    private const string C = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d";
    private const string Later = "11112222-3333-4444-5555-666677778888";

    private static readonly string Wav = $"take (suno-{A}).wav";
    private static readonly string Mp3 = $"take (suno-{A}).mp3";
    private static readonly string Gone = $"gone (suno-{A}).mp3";
    private const string Hand = "Hand/g1 mix.wav";
    private const string Master = "Loose/master.mp3";
    private static readonly string Second = $"second (suno-{B}).m4a";
    private static readonly string Other = $"other (suno-{C}).wav";

    [Fact]
    public async Task DeletingAGenerationReleasesItsFilesWhichNoScanMatchesUntilARestoreAndTheNextScan()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        await SetGenerationPreferenceAsync(client, "n8-1-v1-g1", Mp3);
        await SetSongPreferenceAsync(client, "n8-1", Master);
        var before = MediaApi.Listing(factory.MediaPath);

        // The impact counts the Generation's four files: the Missing one, and one hand-associated
        // file with no Suno ID in its name. The Song-level file is not the Generation's.
        var impact = await JsonAsync(client, "generations/n8-1-v1-g1/deletion-impact");
        AssertCounts(impact, total: 4, hand: 1, songLevel: 0);
        Assert.Equal(4, (await JsonAsync(client, "generations/n8-1-v1-g1")).GetProperty("audioFiles").GetProperty("count").GetInt32());

        using (var deleted = await SendAsync(client, HttpMethod.Delete, "generations/n8-1-v1-g1", impact.GetProperty("revision").GetInt32()))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        }

        var files = await FilesAsync(client);
        foreach (var path in new[] { Wav, Mp3, Gone, Hand })
        {
            AssertUnmatched(files[path], "generation_deleted");
        }

        Assert.Equal("missing", files[Gone].GetProperty("status").GetString());
        AssertAssociated(files[Master], "n8-1", null, "user");
        AssertAssociated(files[Second], "n8-1", "n8-1-v1-g2");
        Assert.Equal([$"song|{Id(files[Master])}"], Preferences(factory));

        // Two scans later, the files carrying the deleted Generation's Suno ID are still unmatched.
        for (var scan = 0; scan < 2; scan++)
        {
            Assert.Equal(0, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("associated").GetInt32());
            files = await FilesAsync(client);
            foreach (var path in new[] { Wav, Mp3, Gone })
            {
                AssertUnmatched(files[path], "generation_deleted");
            }
        }

        // The restore puts back no association or choice, and says so; the files it released lose
        // their reason at once (#388).
        var restored = await RestoreAsync(factory, "n8-1-v1-g1");
        Assert.Contains(AudioFileLifecycle.RestoreNote, restored.Notes);
        files = await FilesAsync(client);
        Assert.All([Wav, Mp3, Gone, Hand], path => AssertUnmatched(files[path], null));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_released_audio_files;"));

        // The next scan matches the files carrying its Suno ID again (the Missing one too); the
        // hand-associated file without one stays unmatched, with no reason.
        Assert.Equal(3, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("associated").GetInt32());
        files = await FilesAsync(client);
        foreach (var path in new[] { Wav, Mp3, Gone })
        {
            AssertAssociated(files[path], "n8-1", "n8-1-v1-g1");
        }

        AssertUnmatched(files[Hand], null);
        Assert.Equal([$"song|{Id(files[Master])}"], Preferences(factory));
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
    }

    [Fact]
    public async Task DeletingAVersionReleasesItsGenerationsFilesAndCountsThem()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        SongApi.AddVersionDirectly(factory.DataPath, 1, "2");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", Clips.Minimal(Later));
        MediaApi.Place(factory, $"v2 (suno-{Later}).wav", "wav");
        MediaApi.Place(factory, "v2 by hand.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        await AssociateAsync(client, (await FilesAsync(client))["v2 by hand.mp3"], """{"song":"n8-1","generation":"n8-1-v2-g1"}""");
        await SetGenerationPreferenceAsync(client, "n8-1-v2-g1", "v2 by hand.mp3");
        var before = MediaApi.Listing(factory.MediaPath);

        // A Version whose Generations have no files counts none.
        await SongApi.CreateAsync(client, "Empty");
        await SongApi.AttachGenerationAsync(factory, "n8-3-v1", Clips.Minimal("99998888-7777-4666-a555-444433332222"));
        AssertCounts(await JsonAsync(client, "versions/n8-3-v1/deletion-impact"), total: 0, hand: 0, songLevel: 0);

        var impact = await JsonAsync(client, "versions/n8-1-v2/deletion-impact");
        AssertCounts(impact, total: 2, hand: 1, songLevel: 0);
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v2", impact.GetProperty("revision").GetInt32()))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        }

        var files = await FilesAsync(client);
        AssertUnmatched(files[$"v2 (suno-{Later}).wav"], "generation_deleted");
        AssertUnmatched(files["v2 by hand.mp3"], "generation_deleted");
        AssertAssociated(files[Wav], "n8-1", "n8-1-v1-g1");
        AssertAssociated(files[Master], "n8-1", null, "user");
        Assert.DoesNotContain(Preferences(factory), row => row.Contains(Id(files["v2 by hand.mp3"]), StringComparison.Ordinal));

        // Not matched again while the Version stays deleted; matched again once it is restored.
        Assert.Equal(0, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("associated").GetInt32());
        Assert.Contains(AudioFileLifecycle.RestoreNote, (await RestoreAsync(factory, "n8-1-v2")).Notes);
        AssertUnmatched((await FilesAsync(client))["v2 by hand.mp3"], null);
        Assert.Equal(1, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("associated").GetInt32());
        files = await FilesAsync(client);
        AssertAssociated(files[$"v2 (suno-{Later}).wav"], "n8-1", "n8-1-v2-g1");
        AssertUnmatched(files["v2 by hand.mp3"], null);
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
    }

    [Fact]
    public async Task DeletingASongReleasesEveryFileSongLevelOnesIncludedWithoutChangingItsConfirmation()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        await SetGenerationPreferenceAsync(client, "n8-1-v1-g1", Mp3);
        await SetSongPreferenceAsync(client, "n8-1", Master);

        // A Song with one Version and only a Song-level file still needs no typed title.
        await SongApi.CreateAsync(client, "Plain");
        MediaApi.Place(factory, "plain.wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        await AssociateAsync(client, (await FilesAsync(client))["plain.wav"], """{"song":"n8-3"}""");
        var plain = await JsonAsync(client, "songs/n8-3/deletion-impact");
        AssertCounts(plain, total: 1, hand: 1, songLevel: 1);
        Assert.Equal(1, plain.GetProperty("audioFileCount").GetInt32());
        Assert.False(plain.GetProperty("titleRequired").GetBoolean());
        var before = MediaApi.Listing(factory.MediaPath);

        // The user turned automatic matching off for the Song-level file (#210): no restore turns it back on.
        TestDatabase.Execute(factory.DataPath, $"UPDATE audio_files SET auto_match_blocked = 1 WHERE path = '{Master}';");

        var impact = await JsonAsync(client, "songs/n8-1/deletion-impact");
        AssertCounts(impact, total: 6, hand: 2, songLevel: 1);
        Assert.Equal(6, impact.GetProperty("audioFileCount").GetInt32());
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-1", impact.GetProperty("revision").GetInt32(), """{"confirmTitle":"Origin"}"""))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        var files = await FilesAsync(client);
        foreach (var path in new[] { Wav, Mp3, Gone, Hand, Master, Second })
        {
            AssertUnmatched(files[path], "song_deleted");
        }

        AssertAssociated(files[Other], "n8-2", "n8-2-v1-g1");
        Assert.Empty(Preferences(factory));

        // Another Song deleted as well, and left deleted: its file keeps its reason throughout.
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-3", plain.GetProperty("revision").GetInt32()))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        // Restored: no association comes back by itself, and every file the deletion released loses
        // its reason at once (#388): the Song is back, so "Its Song was deleted." would be untrue.
        Assert.Contains(AudioFileLifecycle.RestoreNote, (await RestoreAsync(factory, "n8-1")).Notes);
        files = await FilesAsync(client);
        foreach (var path in new[] { Wav, Mp3, Gone, Hand, Master, Second })
        {
            AssertUnmatched(files[path], null);
        }

        AssertUnmatched(files["plain.wav"], "song_deleted");
        Assert.True(files[Master].GetProperty("autoMatchBlocked").GetBoolean());

        // The next scan matches the files carrying its Generations' Suno IDs; the Song-level and
        // hand-associated ones stay plainly unmatched, the block on the Song-level one still on.
        Assert.Equal(4, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("associated").GetInt32());
        files = await FilesAsync(client);
        AssertAssociated(files[Wav], "n8-1", "n8-1-v1-g1");
        AssertAssociated(files[Second], "n8-1", "n8-1-v1-g2");
        AssertUnmatched(files[Master], null);
        AssertUnmatched(files[Hand], null);
        AssertUnmatched(files["plain.wav"], "song_deleted");
        Assert.True(files[Master].GetProperty("autoMatchBlocked").GetBoolean());
        Assert.Empty(Preferences(factory));
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
    }

    [Fact]
    public async Task AMovedGenerationTakesItsFilesAndItsChoiceAndLeavesSongLevelFiles()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        await SetGenerationPreferenceAsync(client, "n8-1-v1-g1", Mp3);
        await SetSongPreferenceAsync(client, "n8-1", Master);
        var preferences = Preferences(factory);
        var before = MediaApi.Listing(factory.MediaPath);

        var generation = await JsonAsync(client, "generations/n8-1-v1-g1");
        Assert.Equal(4, generation.GetProperty("audioFiles").GetProperty("count").GetInt32());
        using (var moved = await SendAsync(client, HttpMethod.Post, "generations/n8-1-v1-g1/move-to-new-song", generation.GetProperty("revision").GetInt32(), """{"title":"Split out"}"""))
        {
            Assert.True(moved.StatusCode == HttpStatusCode.Created, await moved.Content.ReadAsStringAsync());
        }

        var files = await FilesAsync(client);
        foreach (var path in new[] { Wav, Mp3, Gone })
        {
            AssertAssociated(files[path], "n8-3", "n8-3-v1-g1");
        }

        AssertAssociated(files[Hand], "n8-3", "n8-3-v1-g1", "user");
        AssertAssociated(files[Master], "n8-1", null, "user");
        AssertAssociated(files[Second], "n8-1", "n8-1-v1-g2");
        Assert.Equal(preferences, Preferences(factory));

        // The new Song's list holds them, with the choice; the old Song keeps its own file and choice.
        var newSong = await SongFilesAsync(client, "n8-3");
        Assert.Equal(new[] { Wav, Mp3, Gone, Hand }.Order(StringComparer.Ordinal), newSong.Keys.Order(StringComparer.Ordinal));
        Assert.True(newSong[Mp3].GetProperty("isPreferred").GetBoolean());
        Assert.Equal(Mp3, (await JsonAsync(client, "generations/n8-3-v1-g1/playback")).GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.True((await SongFilesAsync(client, "n8-1"))[Master].GetProperty("isPreferred").GetBoolean());
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
    }

    /// <summary>
    /// Song n8-1 ("Origin", Version 1 with g1 = <see cref="A"/> and g2 = <see cref="B"/>) and n8-2
    /// ("Other", g1 = <see cref="C"/>). g1 has a WAV and an MP3 matched by Suno ID, a Missing MP3, and
    /// <see cref="Hand"/> associated by the user; g2 has an M4A; <see cref="Master"/> is n8-1's own.
    /// </summary>
    private static async Task CatalogAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.CreateAsync(client, "Other");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(A));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(B));
        await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal(C));
        foreach (var (path, format) in new[] { (Wav, "wav"), (Mp3, "mp3"), (Gone, "mp3"), (Hand, "wav"), (Master, "mp3"), (Second, "m4a"), (Other, "wav") })
        {
            MediaApi.Place(factory, path, format);
        }

        Assert.Equal(5, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("associated").GetInt32());
        var files = await FilesAsync(client);
        await AssociateAsync(client, files[Hand], """{"song":"n8-1","generation":"n8-1-v1-g1"}""");
        await AssociateAsync(client, files[Master], """{"song":"n8-1"}""");

        // The test removes a file itself, so the scan marks it Missing.
        File.Delete(MediaApi.FullPath(factory, Gone));
        Assert.Equal(1, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("missing").GetInt32());
    }

    private static void AssertCounts(JsonElement impact, int total, int hand, int songLevel)
    {
        var files = impact.GetProperty("localAudioFiles");
        Assert.Equal(total, files.GetProperty("total").GetInt32());
        Assert.Equal(hand, files.GetProperty("handAssociated").GetInt32());
        Assert.Equal(songLevel, files.GetProperty("songLevel").GetInt32());
    }

    private static void AssertAssociated(JsonElement file, string song, string? generation, string? origin = null)
    {
        Assert.True(file.GetProperty("song").ValueKind == JsonValueKind.Object, file.ToString());
        Assert.Equal(song, file.GetProperty("song").GetProperty("shortcode").GetString());
        if (generation is null)
        {
            Assert.Equal(JsonValueKind.Null, file.GetProperty("generation").ValueKind);
        }
        else
        {
            Assert.Equal(generation, file.GetProperty("generation").GetProperty("shortcode").GetString());
        }

        if (origin is not null)
        {
            Assert.Equal(origin, file.GetProperty("associationOrigin").GetString());
        }

        Assert.Equal(JsonValueKind.Null, file.GetProperty("unmatchedReason").ValueKind);
    }

    private static void AssertUnmatched(JsonElement file, string? reason)
    {
        Assert.True(file.GetProperty("song").ValueKind == JsonValueKind.Null, file.ToString());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("generation").ValueKind);
        Assert.Equal(JsonValueKind.Null, file.GetProperty("associationOrigin").ValueKind);
        Assert.Equal(reason, file.GetProperty("unmatchedReason").GetString());
    }

    private static async Task<DeletedItemRestoreOutcome.Restored> RestoreAsync(N8TracksApiFactory factory, string reference)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return Assert.IsType<DeletedItemRestoreOutcome.Restored>(
                await scope.ServiceProvider.GetRequiredService<DeletedItemsService>().RestoreAsync(reference, CancellationToken.None));
        }
    }

    /// <summary>Every audio file, by path.</summary>
    private static async Task<Dictionary<string, JsonElement>> FilesAsync(HttpClient client) =>
        (await MediaApi.ListAsync(client, "?limit=200")).Items.ToDictionary(static item => item.GetProperty("path").GetString()!, static item => item);

    /// <summary>A Song's list, by path.</summary>
    private static async Task<Dictionary<string, JsonElement>> SongFilesAsync(HttpClient client, string song) =>
        (await JsonAsync(client, $"songs/{song}/audio-files")).GetProperty("items").EnumerateArray().ToDictionary(static item => item.GetProperty("path").GetString()!, static item => item.Clone());

    private static async Task AssociateAsync(HttpClient client, JsonElement file, string json)
    {
        using var response = await SendAsync(client, HttpMethod.Put, $"audio-files/{file.GetProperty("id").GetGuid()}/association", file.GetProperty("revision").GetInt32(), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task SetGenerationPreferenceAsync(HttpClient client, string generation, string path)
    {
        var file = (await FilesAsync(client))[path];
        var revision = (await JsonAsync(client, $"generations/{generation}")).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Put, $"generations/{generation}/preferred-audio-file", revision, Body(file));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task SetSongPreferenceAsync(HttpClient client, string song, string path)
    {
        var file = (await FilesAsync(client))[path];
        var revision = (await JsonAsync(client, $"songs/{song}")).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Put, $"songs/{song}/preferred-audio-file", revision, Body(file));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static string Body(JsonElement file) => $$"""{"audioFile":"{{file.GetProperty("id").GetGuid()}}"}""";

    private static string Id(JsonElement file) => file.GetProperty("id").GetGuid().ToString().ToUpperInvariant();

    private static async Task<JsonElement> JsonAsync(HttpClient client, string path) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/{path}", UriKind.Relative)));

    /// <summary>Both choice tables, row by row as stored (kind, file ID).</summary>
    private static List<string> Preferences(N8TracksApiFactory factory) =>
        TestDatabase.Rows(
            factory.DataPath,
            "SELECT 'generation|' || audio_file_id FROM generation_preferred_audio_files "
            + "UNION ALL SELECT 'song|' || audio_file_id FROM song_preferred_audio_files ORDER BY 1;");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri($"/api/v1/{path}", UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }
}
