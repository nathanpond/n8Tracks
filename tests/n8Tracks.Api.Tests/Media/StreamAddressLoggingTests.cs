using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Api.Tests.Telemetry;
using n8Tracks.TestSupport;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Invariant 6 for stored Suno stream addresses (#221, #390), by value rather than by log property
/// name: a Generation's stored address carries a signature no other text has, every answer that reads
/// a stored address is requested with the app logging at Trace and exporting telemetry, and neither
/// the captured log nor anything the collector receives holds the signature. The answers themselves
/// carry it, so its absence from the log is not an accident of a path that never read it.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class StreamAddressLoggingTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";

    [Fact]
    public async Task NoStoredStreamAddressReachesALogLineOrTheCollectorOnAnyPlaybackPath()
    {
        var signature = "sig-" + Guid.NewGuid().ToString("N");
        var address = $"https://d2lwuy8qc234o3.cloudfront.net/1/clip/{A}.m4a?Expires=1893456000&Signature={signature}&Key-Pair-Id=K2SENTINEL";
        await using var collector = StubOtlpCollector.Start();
        string captured;
        var answers = new List<string>();

        using (var factory = new LoggingApiFactory("Trace") { OtlpEndpoint = collector.Endpoint, TestServices = MediaApi.UseCountingMount })
        {
            using var client = await SessionApi.SignedInClientAsync(factory);
            await SongApi.CreateAsync(client, "Signed");
            await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(address));
            var requestIds = new List<string>();

            async Task<JsonElement> ReadAsync(string path)
            {
                using var response = await client.GetAsync(new Uri($"/api/v1/{path}", UriKind.Relative));
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {body}");
                requestIds.Add(Assert.Single(response.Headers.GetValues("X-Request-ID")));
                answers.Add(body);
                return JsonDocument.Parse(body).RootElement.Clone();
            }

            // With no Selected Generation: the Song asks for a choice, whose candidate is the stream.
            Assert.Equal("needs-choice", (await ReadAsync("songs/n8-1/playback")).GetProperty("state").GetString());

            using (var selected = await SendAsync(
                client,
                HttpMethod.Put,
                "songs/n8-1/selected-generation",
                """{"generation":"n8-1-v1-g1"}""",
                (await ReadAsync("songs/n8-1")).GetProperty("revision").GetInt32()))
            {
                Assert.True(selected.StatusCode == HttpStatusCode.OK, await selected.Content.ReadAsStringAsync());
            }

            var album = await CollectionAsync(client, "albums", "tracks");
            var playlist = await CollectionAsync(client, "playlists", "songs");

            // Every answer that reads a stored address: playback, the comparison sources, and the
            // Generation, Song, Songs, Album and Playlist answers that say whether it plays.
            Assert.Equal(address, (await ReadAsync("generations/n8-1-v1-g1/playback")).GetProperty("sunoAudioUrl").GetString());
            Assert.Equal(address, (await ReadAsync("songs/n8-1/playback")).GetProperty("sunoAudioUrl").GetString());
            Assert.Equal(address, (await ReadAsync("songs/n8-1/playback-sources")).GetProperty("generations")[0].GetProperty("sunoAudioUrl").GetString());
            Assert.True((await ReadAsync("generations/n8-1-v1-g1")).GetProperty("playback").GetProperty("playable").GetBoolean());
            Assert.Equal("ready", (await ReadAsync("songs/n8-1")).GetProperty("playback").GetProperty("state").GetString());
            await ReadAsync("songs");
            await ReadAsync("songs/n8-1/generations");
            await ReadAsync($"albums/{album}");
            await ReadAsync($"playlists/{playlist}");

            // Each request's completion line is written after its answer: wait for every one.
            foreach (var requestId in requestIds)
            {
                await factory.CompletionLine(requestId);
            }

            await StubOtlpCollector.Eventually(
                () => collector.ReceivedText(StubOtlpCollector.LogsPath).Contains("/playback-sources", StringComparison.Ordinal)
                    && collector.Spans().Any(span => span.Kind == OtlpSpan.ServerKind)
                    && collector.Bodies(StubOtlpCollector.MetricsPath).Count > 0,
                () =>
                {
                    factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
                    factory.Services.GetRequiredService<MeterProvider>().ForceFlush();
                },
                "The collector did not receive the requests' log records, spans, and metrics.");
            captured = factory.CapturedText;

            // Complement: the log runs below Information, and the playback requests' own lines were captured.
            var lines = factory.Lines();
            Assert.Contains(lines, static line => line.GetProperty("level").GetString() == "Debug");
            Assert.Contains(lines, static line => LoggingApiFactory.Property(line, "path") == "/api/v1/songs/n8-1/playback-sources");
        }

        // Complement: the playback answers carried the signature (asserted above), so it was read and sent.
        Assert.Contains(answers, answer => answer.Contains(signature, StringComparison.Ordinal));

        Assert.DoesNotContain(signature, captured, StringComparison.Ordinal);
        Assert.DoesNotContain("K2SENTINEL", captured, StringComparison.Ordinal);
        var exported = collector.ReceivedText(StubOtlpCollector.LogsPath, StubOtlpCollector.TracesPath, StubOtlpCollector.MetricsPath);
        Assert.DoesNotContain(signature, exported, StringComparison.Ordinal);
        Assert.DoesNotContain("K2SENTINEL", exported, StringComparison.Ordinal);
    }

    /// <summary>A complete clip whose only stream is <paramref name="address"/>.</summary>
    private static string Clip(string address) => new JsonObject
    {
        ["id"] = A,
        ["status"] = "complete",
        ["title"] = "Signed",
        ["media_urls"] = new JsonArray(new JsonObject { ["url"] = address, ["content_type"] = "m4a-opus" }),
    }.ToJsonString();

    /// <summary>A new Album (or Playlist) holding n8-1; its ID.</summary>
    private static async Task<Guid> CollectionAsync(HttpClient client, string collection, string member)
    {
        using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/{collection}", UriKind.Relative), """{"title":"Set"}""");
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var record = await SetupApi.JsonAsync(created);
        var id = record.GetProperty("id").GetGuid();
        using var added = await SendAsync(client, HttpMethod.Post, $"{collection}/{id}/{member}", """{"songId":"n8-1"}""", record.GetProperty("revision").GetInt32());
        Assert.True(added.StatusCode == HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        return id;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string json, int revision)
    {
        using var request = new HttpRequestMessage(method, new Uri($"/api/v1/{path}", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }
}
