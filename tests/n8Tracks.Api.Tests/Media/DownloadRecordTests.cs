using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Download records (#222): the extension reports each file it downloaded from Suno
/// (<c>POST /api/v1/suno/downloads</c>, <c>suno.sync</c>), n8Tracks keeps it by Suno ID whether or not
/// the clip is a Generation, and the Generation panel reads a Generation's records
/// (<c>GET /api/v1/generations/{reference}/downloads</c>) with whether a scanned file of each name is
/// attached to it. The clip lookup (#215) answers the formats already downloaded.
/// </summary>
public sealed class DownloadRecordTests
{
    private const string ClipA = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string ClipB = "1b2c3d4e-5f6a-4b7c-9d8e-0f1a2b3c4d5e";
    private const string Unknown = "2c3d4e5f-6a7b-4c8d-ae9f-1a2b3c4d5e6f";

    private static readonly Uri Downloads = new("/api/v1/suno/downloads", UriKind.Relative);

    [Fact]
    public async Task ARecordForAnUnknownClipIsStoredAndCreatesNoGeneration()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        var generations = TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;");
        var id = Guid.NewGuid();

        using var response = await ReportAsync(client, token, Report(id, Unknown.ToUpperInvariant(), "wav", "Song (suno-x).wav", size: 1234, spent: true));

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await SetupApi.JsonAsync(response);
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(Unknown, body.GetProperty("sunoId").GetString());
        Assert.Equal("wav", body.GetProperty("format").GetString());
        Assert.Equal("Song (suno-x).wav", body.GetProperty("fileName").GetString());
        Assert.Equal(1234, body.GetProperty("sizeBytes").GetInt64());
        Assert.True(body.GetProperty("spentUnlock").GetBoolean());
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM download_records;"));
        Assert.Equal(Unknown, TestDatabase.Scalar(factory.DataPath, "SELECT suno_id FROM download_records;"));

        // Complement: no Generation, Song, or tombstone came of it.
        Assert.Equal(generations, TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM generations;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM songs;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM provider_tombstones;"));
    }

    [Fact]
    public async Task DownloadingAgainAddsARecordAndAReportSentAgainAddsNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        var first = Guid.NewGuid();

        using var once = await ReportAsync(client, token, Report(first, ClipA, "mp3", "Song.mp3"));
        using var again = await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "mp3", "Song (1).mp3"));
        Assert.Equal(HttpStatusCode.Created, once.StatusCode);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM download_records;"));

        // The same report ID, even with another body, changes nothing and answers the stored record.
        var stored = TestDatabase.Rows(factory.DataPath, "SELECT id || '|' || suno_id || '|' || format || '|' || file_name || '|' || completed_utc || '|' || received_utc || '|' || coalesce(size_bytes, '-') || '|' || spent_unlock FROM download_records ORDER BY id;");
        using var repeated = await ReportAsync(client, token, Report(first, ClipB, "wav", "Other.wav", spent: true));
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var body = await SetupApi.JsonAsync(repeated);
        Assert.Equal(ClipA, body.GetProperty("sunoId").GetString());
        Assert.Equal("Song.mp3", body.GetProperty("fileName").GetString());
        Assert.False(body.GetProperty("spentUnlock").GetBoolean());
        Assert.Equal(stored, TestDatabase.Rows(factory.DataPath, "SELECT id || '|' || suno_id || '|' || format || '|' || file_name || '|' || completed_utc || '|' || received_utc || '|' || coalesce(size_bytes, '-') || '|' || spent_unlock FROM download_records ORDER BY id;"));
    }

    [Fact]
    public async Task ACompletionTimeLaterThanReceiptIsTakenAsReceipt()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);

        using var response = await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "m4a-stream", "Song stream.m4a", completed: DateTimeOffset.UtcNow.AddDays(3)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await SetupApi.JsonAsync(response);
        Assert.Equal(body.GetProperty("receivedAt").GetDateTimeOffset(), body.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sizeBytes").ValueKind);
    }

    [Theory]
    [InlineData("format", "flac")]
    [InlineData("format", "M4A")]
    [InlineData("sunoId", "clip-held")]
    [InlineData("sunoId", "")]
    [InlineData("fileName", "")]
    [InlineData("fileName", "folder/Song.mp3")]
    [InlineData("fileName", "folder\\Song.mp3")]
    [InlineData("fileName", "LONG")]
    [InlineData("id", "not-a-uuid")]
    [InlineData("id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("completedAt", "yesterday")]
    [InlineData("sizeBytes", "-1")]
    [InlineData("sizeBytes", "\"12\"")]
    [InlineData("spentUnlock", "null")]
    public async Task RefusesAReportThatIsNotValid(string field, string value)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        var report = Report(Guid.NewGuid(), ClipA, "wav", "Song.wav");
        report[field] = field switch
        {
            "sizeBytes" or "spentUnlock" => JsonNode.Parse(value),
            "fileName" when value == "LONG" => new string('a', 252) + ".wav",
            _ => value,
        };

        using var response = await ReportAsync(client, token, report);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await SetupApi.JsonAsync(response);
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM download_records;"));
    }

    [Fact]
    public async Task TakesANameOf255CharactersAndADeletedClipsId()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO provider_tombstones (suno_id, kind, deleted_utc, title) VALUES ('{ClipB}', 'clip', '2026-10-01T00:00:00.000Z', 'Gone');");

        using var response = await ReportAsync(client, token, Report(Guid.NewGuid(), ClipB, "wav", new string('a', 251) + ".wav"));

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReportingNeedsSunoSync()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var generateOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoGenerate);
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using var anonymous = factory.CreateClient();

        using var refusedGenerate = await ReportAsync(client, generateOnly, Report(Guid.NewGuid(), ClipA, "wav", "Song.wav"));
        using var refusedReader = await ReportAsync(client, reader, Report(Guid.NewGuid(), ClipA, "wav", "Song.wav"));
        using var noToken = await anonymous.PostAsync(Downloads, new StringContent(Report(Guid.NewGuid(), ClipA, "wav", "Song.wav").ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, refusedGenerate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refusedReader.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM download_records;"));
    }

    [Fact]
    public async Task AGenerationListsItsRecordsNewestFirstIncludingThoseMadeBeforeItWasAttached()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        var older = DateTimeOffset.Parse("2026-10-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "wav", "First.wav", completed: older)))
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "mp3", "Second.mp3", completed: older.AddHours(1), size: 99, spent: true)))
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipB, "wav", "Other.wav", completed: older)))
        {
        }

        // The clip is imported after it was downloaded.
        var song = await SongApi.CreateAsync(client, "Downloaded first");
        var songRef = song.GetProperty("shortcode").GetString()!;
        var generation = await SongApi.AttachGenerationAsync(factory, $"{songRef}-v1", Clips.Minimal(ClipA));
        var plain = await SongApi.AttachGenerationAsync(factory, $"{songRef}-v1");

        var items = await ListAsync(client, generation.Shortcode);

        Assert.Equal(["Second.mp3", "First.wav"], items.Select(static item => item.GetProperty("fileName").GetString()));
        Assert.Equal("mp3", items[0].GetProperty("format").GetString());
        Assert.Equal(99, items[0].GetProperty("sizeBytes").GetInt64());
        Assert.True(items[0].GetProperty("spentUnlock").GetBoolean());
        Assert.Equal(older.AddHours(1), items[0].GetProperty("completedAt").GetDateTimeOffset());
        Assert.All(items, static item => Assert.Equal("not-found", item.GetProperty("mediaFolder").GetProperty("match").GetString()));
        Assert.All(items, static item => Assert.Equal(JsonValueKind.Null, item.GetProperty("mediaFolder").GetProperty("audioFile").ValueKind));

        // By ID too; a Generation without a Suno ID has none; an unknown reference is 404.
        Assert.Equal(2, (await ListAsync(client, generation.Generation.Id.ToString())).Count);
        Assert.Empty(await ListAsync(client, plain.Shortcode));
        using var missing = await client.GetAsync(new Uri($"/api/v1/generations/{Guid.NewGuid()}/downloads", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task TheListHoldsAtMost100Records()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Many downloads");
        var generation = await SongApi.AttachGenerationAsync(factory, $"{song.GetProperty("shortcode").GetString()}-v1", Clips.Minimal(ClipA));
        var rows = string.Join(
            ", ",
            Enumerable.Range(0, 101).Select(static i =>
                $"('{Guid.NewGuid().ToString("D").ToUpperInvariant()}', '{ClipA}', 'wav', 'File {i}.wav', '2026-10-01T00:{i / 60:00}:{i % 60:00}.000Z', '2026-10-02T00:00:00.000Z', NULL, 0)"));
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO download_records (id, suno_id, format, file_name, completed_utc, received_utc, size_bytes, spent_unlock) VALUES {rows};");

        var items = await ListAsync(client, generation.Shortcode);

        Assert.Equal(100, items.Count);
        Assert.Equal("File 100.wav", items[0].GetProperty("fileName").GetString());
        Assert.DoesNotContain(items, static item => item.GetProperty("fileName").GetString() == "File 0.wav");
    }

    [Fact]
    public async Task EachRecordSaysWhetherAScannedFileOfItsNameIsAttachedElsewhereOrNotFound()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        var song = await SongApi.CreateAsync(client, "Matched");
        var generation = await SongApi.AttachGenerationAsync(factory, $"{song.GetProperty("shortcode").GetString()}-v1", Clips.Minimal(ClipA));
        var at = DateTimeOffset.Parse("2026-10-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "mp3", $"Song (suno-{ClipA}).mp3", completed: at.AddMinutes(3))))
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "wav", "Loose (2).wav", completed: at.AddMinutes(2))))
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "m4a", "Gone.m4a", completed: at.AddMinutes(1))))
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "wav", "Loose_.wav", completed: at)))
        {
        }

        // Another case, the browser's numbering, and a folder: the scan attaches it by its Suno ID.
        MediaApi.Place(factory, $"Album/SONG (suno-{ClipA.ToUpperInvariant()}) (1).mp3", "mp3");
        MediaApi.Place(factory, "loose.wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        var items = (await ListAsync(client, generation.Shortcode)).ToDictionary(static item => item.GetProperty("fileName").GetString()!);

        var attached = items[$"Song (suno-{ClipA}).mp3"].GetProperty("mediaFolder");
        Assert.Equal("attached", attached.GetProperty("match").GetString());
        Assert.Equal("available", attached.GetProperty("audioFile").GetProperty("status").GetString());
        var elsewhere = items["Loose (2).wav"].GetProperty("mediaFolder");
        Assert.Equal("elsewhere", elsewhere.GetProperty("match").GetString());
        Assert.Equal("not-found", items["Gone.m4a"].GetProperty("mediaFolder").GetProperty("match").GetString());

        // "_" is matched as itself, not as any character.
        Assert.Equal("not-found", items["Loose_.wav"].GetProperty("mediaFolder").GetProperty("match").GetString());

        // A Missing file still counts as found, with its state.
        File.Delete(MediaApi.FullPath(factory, "loose.wav"));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var missing = (await ListAsync(client, generation.Shortcode)).Single(static item => item.GetProperty("fileName").GetString() == "Loose (2).wav").GetProperty("mediaFolder");
        Assert.Equal("elsewhere", missing.GetProperty("match").GetString());
        Assert.Equal("missing", missing.GetProperty("audioFile").GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheClipLookupAnswersTheFormatsAlreadyDownloaded()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        var song = await SongApi.CreateAsync(client, "Looked up");
        await SongApi.AttachGenerationAsync(factory, $"{song.GetProperty("shortcode").GetString()}-v1", Clips.Minimal(ClipA));
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "m4a-stream", "A stream.m4a")))
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "wav", "A.wav")))
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), ClipA, "wav", "A (1).wav")))
        using (await ReportAsync(client, token, Report(Guid.NewGuid(), Unknown, "mp3", "U.mp3")))
        {
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/suno/clips/lookup", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { sunoIds = new[] { ClipA, Unknown.ToUpperInvariant(), ClipB, "clip-text" } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rows = (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()
            .ToDictionary(static row => row.GetProperty("sunoId").GetString()!, static row => row.GetProperty("downloadedFormats").EnumerateArray().Select(static format => format.GetString()).ToList());

        // In the order the formats are offered, each once; an ID in another case finds its records.
        Assert.Equal(["wav", "m4a-stream"], rows[ClipA]);
        Assert.Equal(["mp3"], rows[Unknown.ToUpperInvariant()]);
        Assert.Empty(rows[ClipB]);
        Assert.Empty(rows["clip-text"]);
    }

    private static JsonObject Report(Guid id, string sunoId, string format, string fileName, DateTimeOffset? completed = null, long? size = null, bool spent = false)
    {
        var report = new JsonObject
        {
            ["id"] = id.ToString("D"),
            ["sunoId"] = sunoId,
            ["format"] = format,
            ["fileName"] = fileName,
            ["completedAt"] = (completed ?? DateTimeOffset.UtcNow.AddMinutes(-1)).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["spentUnlock"] = spent,
        };
        if (size is not null)
        {
            report["sizeBytes"] = size;
        }

        return report;
    }

    private static async Task<HttpResponseMessage> ReportAsync(HttpClient client, string token, JsonObject report)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Downloads)
        {
            Content = new StringContent(report.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/generations/{reference}/downloads", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }
}
