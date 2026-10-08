using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// A Song's audio files (#211) through the API, over real scans of the host's temporary media folder:
/// the Song's complete list (Song-level files and its Generations' files, never another Song's), in
/// order; each Generation's counts and formats on every Generation answer; each Song's count on the
/// Song answers, and the Songs table's sort by it; Missing and Unavailable files; and the refusals.
/// Reading never writes the media folder (invariant 2).
/// </summary>
public sealed class SongAudioFilesTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string B = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";
    private const string C = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
    private const string D = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d";

    [Fact]
    public async Task TheSongsListHoldsItsSongLevelAndGenerationFilesInOrderAndNoOtherSongsFiles()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        var before = MediaApi.Listing(factory.MediaPath);

        using var response = await client.GetAsync(SongFiles("n8-1"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var items = Items(body);

        // Song-level first, then Version 1's g1 (WAV before MP3), its g2, then Version 2's g1; never
        // the other Song's file or the unassociated one.
        Assert.Equal(
            ["Loose/song.mp3", $"take (suno-{A}).wav", $"take (suno-{A}).mp3", $"second (suno-{B}).m4a", $"Later/third (suno-{C}).flac"],
            items.Select(static item => item.GetProperty("path").GetString()));
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("generation").ValueKind);
        Assert.Equal("user", items[0].GetProperty("associationOrigin").GetString());
        Assert.Equal("n8-1-v1-g1", items[1].GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal("suno-id", items[1].GetProperty("associationOrigin").GetString());
        Assert.Equal("n8-1-v2-g1", items[4].GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.All(items, static item => Assert.Equal("available", item.GetProperty("status").GetString()));
        Assert.All(items, item => Assert.Equal(catalog.SongId, item.GetProperty("song").GetProperty("id").GetGuid()));
        Assert.All(items, static item => Assert.False(item.TryGetProperty("suggestions", out _)));

        // By the Song's ID too; the other Song has only its own file.
        Assert.Equal(body, await (await client.GetAsync(SongFiles(catalog.SongId.ToString()))).Content.ReadAsStringAsync());
        var other = Items(await (await client.GetAsync(SongFiles("n8-2"))).Content.ReadAsStringAsync());
        Assert.Equal([$"other (suno-{D}).wav"], other.Select(static item => item.GetProperty("path").GetString()));

        // A Song with no files answers an empty list.
        Assert.Empty(Items(await (await client.GetAsync(SongFiles("n8-3"))).Content.ReadAsStringAsync()));

        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
    }

    [Fact]
    public async Task EveryGenerationAnswerCountsItsFilesAndFormatsAndEverySongAnswerItsCount()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);

        var generations = await GenerationsAsync(client, "n8-1");
        AssertTally(generations["n8-1-v1-g1"], 2, 0, 0, "wav", "mp3");
        AssertTally(generations["n8-1-v1-g2"], 1, 0, 0, "m4a");
        AssertTally(generations["n8-1-v2-g1"], 1, 0, 0, "flac");
        AssertTally(generations["n8-1-v2-g2"], 0, 0, 0);
        AssertTally((await GenerationsAsync(client, "n8-2"))["n8-2-v1-g1"], 1, 0, 0, "wav");

        // Whether each has a file to play (#218): every one with an available file; not the one with none.
        AssertPlayable(generations["n8-1-v1-g1"], true);
        AssertPlayable(generations["n8-1-v1-g2"], true);
        AssertPlayable(generations["n8-1-v2-g2"], false);

        // One Generation, read alone, says the same.
        AssertTally(await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/generations/n8-1-v1-g1", UriKind.Relative))), 2, 0, 0, "wav", "mp3");

        // The Song counts every file, Song-level ones included; the list's rows too.
        Assert.Equal(5, (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")))).GetProperty("audioFileCount").GetInt32());
        var list = await SongApi.ListAsync(client);
        Assert.Equal(
            new Dictionary<string, int> { ["n8-1"] = 5, ["n8-2"] = 1, ["n8-3"] = 0 },
            list.GetProperty("items").EnumerateArray().ToDictionary(static song => song.GetProperty("shortcode").GetString()!, static song => song.GetProperty("audioFileCount").GetInt32()));
    }

    [Fact]
    public async Task TheSongsTableSortsByTheCountMostFirstByDefaultAndRefusesAnUnknownSort()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);

        Assert.Equal(["n8-1", "n8-2", "n8-3"], SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=audioFiles")));
        Assert.Equal(["n8-3", "n8-2", "n8-1"], SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=audioFiles&direction=asc")));

        using var refused = await client.GetAsync(new Uri("/api/v1/songs?sort=files", UriKind.Relative));
        var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Contains("audioFiles", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAndUnavailableFilesAreListedMarkedAndCounted()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);

        // The matched MP3 is renamed on disk: the next scan marks it Missing, and it is still listed.
        File.Move(MediaApi.FullPath(factory, $"take (suno-{A}).mp3"), MediaApi.FullPath(factory, "renamed.mp3"));
        MediaApi.Result(await MediaApi.ScanAsync(client));

        var items = Items(await (await client.GetAsync(SongFiles("n8-1"))).Content.ReadAsStringAsync());
        Assert.Equal(5, items.Count);
        Assert.Equal("missing", MediaApi.ByPath(items, $"take (suno-{A}).mp3").GetProperty("status").GetString());
        AssertTally((await GenerationsAsync(client, "n8-1"))["n8-1-v1-g1"], 2, 1, 0, "wav", "mp3");
        AssertPlayable((await GenerationsAsync(client, "n8-1"))["n8-1-v1-g1"], true);

        // A Generation whose only file is Missing has nothing to play (#218).
        File.Move(MediaApi.FullPath(factory, $"second (suno-{B}).m4a"), MediaApi.FullPath(factory, "renamed.m4a"));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        AssertTally((await GenerationsAsync(client, "n8-1"))["n8-1-v1-g2"], 1, 1, 0, "m4a");
        AssertPlayable((await GenerationsAsync(client, "n8-1"))["n8-1-v1-g2"], false);
        Assert.Equal(5, (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")))).GetProperty("audioFileCount").GetInt32());

        // While the media folder is unavailable, every file says so, and every count with it.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaAvailability>().RecordAsync(readable: false, CancellationToken.None);
        }

        items = Items(await (await client.GetAsync(SongFiles("n8-1"))).Content.ReadAsStringAsync());
        Assert.All(items, static item => Assert.Equal("unavailable", item.GetProperty("status").GetString()));
        Assert.Equal("missing", MediaApi.ByPath(items, $"take (suno-{A}).mp3").GetProperty("storedStatus").GetString());
        AssertTally((await GenerationsAsync(client, "n8-1"))["n8-1-v1-g1"], 2, 0, 2, "wav", "mp3");
        Assert.All((await GenerationsAsync(client, "n8-1")).Values, static generation => AssertPlayable(generation, false));
        Assert.Equal(5, (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-1")))).GetProperty("audioFileCount").GetInt32());
    }

    [Fact]
    public async Task AnUnknownSongIs404ADeletedOneSaysSoAndATokenNeedsCatalogRead()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);

        using (var response = await client.GetAsync(SongFiles("n8-99")))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        // The Song with no files (n8-3) is deleted: its list says it was.
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-3", 1, """{"confirmTitle":"None"}"""))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }

        using (var response = await client.GetAsync(SongFiles("n8-3")))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "song_deleted");
        }

        using var anonymous = factory.CreateClient();
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var response = await CredentialApi.SendAsync(anonymous, HttpMethod.Get, SongFiles("n8-1"), reader))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(5, Items(await response.Content.ReadAsStringAsync()).Count);
        }

        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using (var response = await CredentialApi.SendAsync(anonymous, HttpMethod.Get, SongFiles("n8-1"), writer))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var response = await anonymous.GetAsync(SongFiles("n8-1")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static Uri SongFiles(string reference) => new($"/api/v1/songs/{reference}/audio-files", UriKind.Relative);

    /// <summary>
    /// Songs n8-1 (Versions 1 and 2, two Generations each) and n8-2 (one Generation); files matched by
    /// Suno ID to n8-1's g1 (WAV and MP3), its g2, its v2 g1, and n8-2's g1; one file the user
    /// associated with n8-1 alone; and one associated with nothing. Then a third Song, n8-3, with none.
    /// </summary>
    private static async Task<Catalog> CatalogAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.CreateAsync(client, "Other");
        SongApi.AddVersionDirectly(factory.DataPath, 1, "2");
        var first = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(A));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(B));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", Clips.Minimal(C));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", Clips.Minimal("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e"));
        await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal(D));
        await SongApi.CreateAsync(client, "None");

        MediaApi.Place(factory, $"take (suno-{A}).mp3", "mp3");
        MediaApi.Place(factory, $"take (suno-{A}).wav", "wav");
        MediaApi.Place(factory, $"second (suno-{B}).m4a", "m4a");
        MediaApi.Place(factory, $"Later/third (suno-{C}).flac", "flac");
        MediaApi.Place(factory, $"other (suno-{D}).wav", "wav");
        MediaApi.Place(factory, "Loose/song.mp3", "mp3");
        MediaApi.Place(factory, "unrelated.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        var loose = MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, "Loose/song.mp3");
        using (var response = await SendAsync(client, HttpMethod.Put, $"audio-files/{loose.GetProperty("id").GetGuid()}/association", loose.GetProperty("revision").GetInt32(), """{"song":"n8-1"}"""))
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        return new Catalog(first.Generation.SongId);
    }

    private static List<JsonElement> Items(string body)
    {
        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.GetProperty("items").EnumerateArray().Select(static item => item.Clone())];
    }

    private static async Task<Dictionary<string, JsonElement>> GenerationsAsync(HttpClient client, string song)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/songs/{song}/generations", UriKind.Relative));
        return Items(await response.Content.ReadAsStringAsync()).ToDictionary(static generation => generation.GetProperty("shortcode").GetString()!);
    }

    private static void AssertTally(JsonElement generation, int count, int missing, int unavailable, params string[] formats)
    {
        var tally = generation.GetProperty("audioFiles");
        Assert.Equal(count, tally.GetProperty("count").GetInt32());
        Assert.Equal(missing, tally.GetProperty("missing").GetInt32());
        Assert.Equal(unavailable, tally.GetProperty("unavailable").GetInt32());
        Assert.Equal(formats, tally.GetProperty("formats").EnumerateArray().Select(static format => format.GetString()));
    }

    /// <summary>The Generation's <c>playback</c>: playable with no reason, or not with <c>nothing_available</c>.</summary>
    private static void AssertPlayable(JsonElement generation, bool playable)
    {
        var playback = generation.GetProperty("playback");
        Assert.Equal(playable, playback.GetProperty("playable").GetBoolean());
        if (playable)
        {
            Assert.Equal(JsonValueKind.Null, playback.GetProperty("reason").ValueKind);
        }
        else
        {
            Assert.Equal("nothing_available", playback.GetProperty("reason").GetString());
        }
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

    private sealed record Catalog(Guid SongId);
}
