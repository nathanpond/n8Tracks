using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;
using n8Tracks.Infrastructure.Persistence.Migrations;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The Suno fallback (#221) through the API: a Generation with no local file answers its stored Suno
/// address (<c>source: "suno"</c>) and its Suno page; a local file always wins; an address on an unlisted
/// host, an unfinished clip, and one Suno no longer lists are not streamed, each with its reason; a Song
/// follows its Selected Generation to Suno and never borrows another Generation's file; the stream
/// counts as playable on Generation and Song answers and is a source to compare. The migration that
/// re-derives the stored addresses agrees with the clip reader, and a sync right after it proposes no
/// change. Reading writes nothing. Every response carries the Content Security Policy and the
/// Referrer Policy.
/// </summary>
public sealed class SunoStreamPlaybackTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string B = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";

    private static readonly string Stream = $"https://d2lwuy8qc234o3.cloudfront.net/1/clip/{A}.m4a";

    [Fact]
    public async Task WithNoLocalFileTheGenerationStreamsItsStoredAddressUntilALocalFileArrives()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(A, Stream));
        var rows = AllRows(factory);

        var playback = await ReadAsync(client, "generations/n8-1-v1-g1/playback");
        Assert.Equal("suno", playback.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, playback.GetProperty("audioFile").ValueKind);
        Assert.Equal(Stream, playback.GetProperty("sunoAudioUrl").GetString());
        Assert.Equal($"https://suno.com/song/{A}", playback.GetProperty("sunoPageUrl").GetString());
        Assert.Equal("suno_stream", playback.GetProperty("reason").GetString());
        AssertPlayable(await ReadAsync(client, "generations/n8-1-v1-g1"), true, "suno_stream");
        Assert.Equal(rows, AllRows(factory));

        // A local file for that Generation always wins over Suno.
        MediaApi.Place(factory, $"take (suno-{A}).mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        playback = await ReadAsync(client, "generations/n8-1-v1-g1/playback");
        Assert.Equal("local", playback.GetProperty("source").GetString());
        Assert.Equal("mp3", playback.GetProperty("audioFile").GetProperty("format").GetString());
        Assert.Equal(JsonValueKind.Null, playback.GetProperty("sunoAudioUrl").ValueKind);
        Assert.Equal($"https://suno.com/song/{A}", playback.GetProperty("sunoPageUrl").GetString());
        AssertPlayable(await ReadAsync(client, "generations/n8-1-v1-g1"), true, null);

        // Missing again: back to Suno.
        File.Delete(MediaApi.FullPath(factory, $"take (suno-{A}).mp3"));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal("suno", (await ReadAsync(client, "generations/n8-1-v1-g1/playback")).GetProperty("source").GetString());
    }

    [Fact]
    public async Task NoAddressAnUnlistedHostAnUnfinishedClipAndAClipSunoNoLongerListsAreNotStreamed()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(A));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(B, "https://studio-api.prod.suno.com/api/forbidden"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip("c-streaming", "https://d2lwuy8qc234o3.cloudfront.net/1/clip/c.m4a", status: "streaming"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip("d-trashed", "https://d2lwuy8qc234o3.cloudfront.net/1/clip/d.m4a"));
        Sql(factory, "UPDATE generations SET remote_state = 'trashed' WHERE suno_id = 'd-trashed';");

        foreach (var (generation, reason, page) in new[]
        {
            ("n8-1-v1-g1", "nothing_available", $"https://suno.com/song/{A}"),
            ("n8-1-v1-g2", "nothing_available", $"https://suno.com/song/{B}"),
            ("n8-1-v1-g3", "suno_not_complete", "https://suno.com/song/c-streaming"),
            ("n8-1-v1-g4", "suno_not_present", "https://suno.com/song/d-trashed"),
        })
        {
            var playback = await ReadAsync(client, $"generations/{generation}/playback");
            Assert.Equal("none", playback.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, playback.GetProperty("sunoAudioUrl").ValueKind);
            Assert.Equal(reason, playback.GetProperty("reason").GetString());

            // Open in Suno stays possible: the page link is answered whenever there is Suno data.
            Assert.Equal(page, playback.GetProperty("sunoPageUrl").GetString());
            AssertPlayable(await ReadAsync(client, $"generations/{generation}"), false, reason);
        }

        // Nothing to offer on the Song either.
        Assert.Equal("none", (await ReadAsync(client, "songs/n8-1")).GetProperty("playback").GetProperty("state").GetString());
        Assert.Empty((await ReadAsync(client, "songs/n8-1/playback-sources")).GetProperty("generations").EnumerateArray());
    }

    [Fact]
    public async Task ASongStreamsItsSelectedGenerationRatherThanPlayAnotherGenerationsLocalFile()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(A, Stream));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(B, null));
        MediaApi.Place(factory, $"other (suno-{B}).wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        // Nothing selected: the stream-only Generation is a playable candidate, and a source to compare.
        var asked = await ReadAsync(client, "songs/n8-1/playback");
        Assert.Equal("needs-choice", asked.GetProperty("state").GetString());
        var candidates = asked.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal([true, true], candidates.Select(static candidate => candidate.GetProperty("playable").GetBoolean()));
        Assert.Equal(["suno_stream", null], candidates.Select(static candidate => candidate.GetProperty("reason").GetString()));
        var sources = (await ReadAsync(client, "songs/n8-1/playback-sources")).GetProperty("generations").EnumerateArray().ToList();
        Assert.Equal(["n8-1-v1-g1", "n8-1-v1-g2"], sources.Select(static source => source.GetProperty("generation").GetProperty("shortcode").GetString()));
        Assert.Empty(sources[0].GetProperty("files").EnumerateArray());
        Assert.Equal(Stream, sources[0].GetProperty("sunoAudioUrl").GetString());
        Assert.Equal($"https://suno.com/song/{A}", sources[0].GetProperty("sunoPageUrl").GetString());
        Assert.Equal(JsonValueKind.Null, sources[1].GetProperty("sunoAudioUrl").ValueKind);
        Assert.Single(sources[1].GetProperty("files").EnumerateArray());

        // "Selected one, from Suno".
        await SendOkAsync(client, HttpMethod.Put, "songs/n8-1/selected-generation", await RevisionAsync(client, "songs/n8-1"), """{"generation":"n8-1-v1-g1"}""");
        var rows = AllRows(factory);
        var playback = await ReadAsync(client, "songs/n8-1/playback");
        Assert.Equal("ready", playback.GetProperty("state").GetString());
        Assert.Equal("suno", playback.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, playback.GetProperty("audioFile").ValueKind);
        Assert.Equal(Stream, playback.GetProperty("sunoAudioUrl").GetString());
        Assert.Equal($"https://suno.com/song/{A}", playback.GetProperty("sunoPageUrl").GetString());
        Assert.Equal("suno_stream", playback.GetProperty("reason").GetString());
        Assert.Equal("n8-1-v1-g1", playback.GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal("ready", (await ReadAsync(client, "songs/n8-1")).GetProperty("playback").GetProperty("state").GetString());
        var listed = (await ReadAsync(client, "songs")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("ready", listed.GetProperty("playback").GetProperty("state").GetString());
        Assert.Equal(rows, AllRows(factory));
    }

    [Fact]
    public async Task EveryResponseCarriesAPolicyThatAllowsAudioOnlyFromTheAppAndTheListedSunoHostsAndNoReferrerPath()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var path in new[] { "/", "/songs", "/api/v1/health", "/api/v1/songs", "/api/v1/no-such-route" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

            Assert.Equal("media-src 'self' https://d2lwuy8qc234o3.cloudfront.net", policy);

            // Complement: exactly one directive, no wildcard, no scheme or host beyond the list.
            var directives = policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var sources = Assert.Single(directives).Split(' ').Skip(1).ToList();
            Assert.Equal(["'self'", .. SunoAudioHosts.Hosts.Select(static host => "https://" + host)], sources);
            Assert.DoesNotContain(sources, static source => source.Contains('*', StringComparison.Ordinal) || source.EndsWith(':'));

            // A request to Suno's host names at most n8Tracks' origin, never a path.
            Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        }
    }

    public static TheoryData<string> Payloads => new()
    {
        Clips.FixtureClip("feed-v3.completed-clip.response.json"),
        Clips.FixtureClip("feed-v3.songs-advanced.response.json"),
        Clips.FixtureClip("feed-v3.sounds.response.json"),
        Clips.FixtureClip("generate-v2-web.response.json"),
        """{"id":"p1","media_urls":[{"url":"https://h/x.wav","content_type":"wav"},{"url":"https://h/a.ogg"},{"url":"https://h/b.m4a?x=.mp3","content_type":"m4a-opus"},{"url":"https://h/c.MP3#frag"}],"audio_url":"https://h/au"}""",
        """{"id":"p2","media_urls":[{"url":"https://h/x.flac?ext=.mp3"},{"url":"  "},{"url":7},"loose",{"url":"https://h/ok.ogg","content_type":"OGG"}]}""",
        """{"id":"p3","media_urls":{"url":"https://h/x.mp3"},"audio_url":"https://h/fallback"}""",
        """{"id":"p4","media_urls":[],"audio_url":"   "}""",
        """{"id":"p5"}""",
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public void TheMigrationReDerivesTheStoredAddressAsTheClipReaderReadsIt(string payload)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE generations (id TEXT PRIMARY KEY, audio_url TEXT); CREATE TABLE provider_records (generation_id TEXT PRIMARY KEY, payload TEXT);");
        Execute(connection, "INSERT INTO generations VALUES ('g', 'https://studio-api.prod.suno.com/api/forbidden'), ('no-record', 'kept');");
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO provider_records VALUES ('g', $payload);";
            insert.Parameters.AddWithValue("$payload", payload);
            insert.ExecuteNonQuery();
        }

        Execute(connection, RederiveGenerationAudioUrls.Sql);

        var expected = Assert.IsType<ClipReading.Read>(ClipReader.Read(payload)).Fields.AudioUrl;
        Assert.Equal(expected, Scalar(connection, "SELECT audio_url FROM generations WHERE id = 'g';"));
        Assert.Equal("kept", Scalar(connection, "SELECT audio_url FROM generations WHERE id = 'no-record';"));
    }

    [Fact]
    public void ASyncRightAfterTheReDerivationSeesNoChange()
    {
        // The stored address is not a compared field: a Generation re-derived by the migration is not
        // Changed at the next sync, whatever its address was before.
        var incoming = Assert.IsType<ClipReading.Read>(ClipReader.Read(Clips.FixtureClip("feed-v3.completed-clip.response.json"))).Fields;
        Assert.Empty(SunoExportRules.ChangedFields(incoming with { AudioUrl = "https://studio-api.prod.suno.com/api/forbidden" }, incoming));
        Assert.Empty(SunoExportRules.ChangedFields(incoming with { AudioUrl = null }, incoming));
    }

    private static string Clip(string sunoId, string? mediaUrl, string status = "complete")
    {
        var clip = new JsonObject
        {
            ["id"] = sunoId,
            ["status"] = status,
            ["title"] = "Clip",
            ["audio_url"] = "https://studio-api.prod.suno.com/api/forbidden",
        };
        if (mediaUrl is not null)
        {
            clip["media_urls"] = new JsonArray(new JsonObject { ["url"] = mediaUrl, ["content_type"] = "m4a-opus" });
        }

        return clip.ToJsonString();
    }

    private static void AssertPlayable(JsonElement generation, bool playable, string? reason)
    {
        var playback = generation.GetProperty("playback");
        Assert.Equal(playable, playback.GetProperty("playable").GetBoolean());
        Assert.Equal(reason, playback.GetProperty("reason").GetString());
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/{path}", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<int> RevisionAsync(HttpClient client, string path) =>
        (await ReadAsync(client, path)).GetProperty("revision").GetInt32();

    /// <summary>The Songs, Generations (with their stored addresses) and audio files, as stored.</summary>
    private static List<string> AllRows(N8TracksApiFactory factory) =>
        TestDatabase.Rows(
            factory.DataPath,
            "SELECT 'song|' || id || '|' || revision || '|' || quote(selected_generation_id) FROM songs "
            + "UNION ALL SELECT 'generation|' || id || '|' || revision || '|' || quote(audio_url) FROM generations "
            + "UNION ALL SELECT 'file|' || id || '|' || revision FROM audio_files ORDER BY 1;");

    private static void Sql(N8TracksApiFactory factory, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = TestDatabase.FilePath(factory.DataPath), Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        Execute(connection, sql);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static async Task SendOkAsync(HttpClient client, HttpMethod method, string path, int revision, string json)
    {
        using var request = new HttpRequestMessage(method, new Uri($"/api/v1/{path}", UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
