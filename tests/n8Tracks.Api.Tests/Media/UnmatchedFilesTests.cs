using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The Unmatched Files list (#209) through the API: <c>GET /api/v1/audio-files?association=none</c>
/// sorted, filtered by text, and with <c>include=suggestions</c>. Listing and suggesting never write:
/// every test compares the whole <c>audio_files</c> table before and after.
/// </summary>
public sealed class UnmatchedFilesTests
{
    private const string Deleted = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string Kept = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";
    private const string Suggested = "?association=none&include=suggestions";

    [Fact]
    public async Task AFileNamedForASongIsListedWithThatSongSuggestedAndStaysUnassociatedOnEveryScan()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = (await SongApi.CreateAsync(client, "My Song Title")).GetProperty("id").GetGuid();
        await SongApi.CreateAsync(client, "Something Else");
        MediaApi.Place(factory, "My Song Title.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var before = Table(factory);

        var file = Assert.Single((await MediaApi.ListAsync(client, Suggested)).Items);
        Assert.Equal("My Song Title.mp3", file.GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("song").ValueKind);
        var suggestion = Assert.Single(file.GetProperty("suggestions").EnumerateArray());
        Assert.Equal(song, suggestion.GetProperty("song").GetProperty("id").GetGuid());
        Assert.Equal("n8-1", suggestion.GetProperty("song").GetProperty("shortcode").GetString());
        Assert.Equal("My Song Title", suggestion.GetProperty("song").GetProperty("title").GetString());
        Assert.Equal(100, suggestion.GetProperty("score").GetInt32());
        Assert.Equal(JsonValueKind.Null, suggestion.GetProperty("generation").ValueKind);
        var reason = Assert.Single(suggestion.GetProperty("reasons").EnumerateArray());
        Assert.Equal("title_equals_file_name", reason.GetProperty("code").GetString());
        Assert.False(reason.TryGetProperty("folder", out _));

        // Listing and suggesting wrote nothing; a scan after leaves the file listed and unassociated.
        Assert.Equal(before, Table(factory));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var again = Assert.Single((await MediaApi.ListAsync(client, Suggested)).Items);
        Assert.Equal(JsonValueKind.Null, again.GetProperty("song").ValueKind);
        Assert.Single(again.GetProperty("suggestions").EnumerateArray());
    }

    [Fact]
    public async Task WithoutIncludeThereAreNoSuggestionsAndAnAssociatedFileGetsNone()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Night Drive");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(Kept, "Night Drive", 1.9));
        MediaApi.Place(factory, $"Night Drive (suno-{Kept}).mp3", "mp3");
        MediaApi.Place(factory, "Night Drive.wav", "wav");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        var (plain, body) = await MediaApi.ListAsync(client);
        Assert.Equal(2, plain.Count);
        Assert.DoesNotContain("suggestions", body, StringComparison.Ordinal);

        var (all, _) = await MediaApi.ListAsync(client, "?include=suggestions");
        Assert.Empty(MediaApi.ByPath(all, $"Night Drive (suno-{Kept}).mp3").GetProperty("suggestions").EnumerateArray());
        Assert.Single(MediaApi.ByPath(all, "Night Drive.wav").GetProperty("suggestions").EnumerateArray());
    }

    [Fact]
    public async Task AFileWhoseGenerationWasDeletedShowsTheReasonAndIsNeverPointedAtTheDeletedGeneration()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Night Drive");
        var deleted = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(Deleted, "Night Drive", 1.87));
        var kept = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clip(Kept, "Night Drive", 2.5));
        MediaApi.Place(factory, $"Night Drive (suno-{Deleted}).mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        await DeleteGenerationAsync(client, "n8-1-v1-g1");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var before = Table(factory);

        var file = Assert.Single((await MediaApi.ListAsync(client, Suggested)).Items);
        Assert.Equal("generation_deleted", file.GetProperty("unmatchedReason").GetString());
        var suggestion = Assert.Single(file.GetProperty("suggestions").EnumerateArray());
        Assert.Equal(deleted.Generation.SongId, suggestion.GetProperty("song").GetProperty("id").GetGuid());
        Assert.Equal(kept.Generation.Id, suggestion.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.DoesNotContain(deleted.Generation.Id.ToString(), file.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Table(factory));
    }

    [Fact]
    public async Task AtMostThreeSuggestionsAreGivenAndAnArchivedSongIsNeverSuggested()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        for (var index = 0; index < 5; index++)
        {
            await SongApi.CreateAsync(client, "Same Title");
        }

        // n8-5 is archived; n8-4, n8-3, n8-2 are the three most recently updated of the rest.
        Sql(factory, $"UPDATE songs SET workflow_state_id = '{DefaultWorkflowStates.Archived.Id.ToString().ToUpperInvariant()}' WHERE shortcode_number = 5;");
        MediaApi.Place(factory, "Same Title.flac", "flac");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        var file = Assert.Single((await MediaApi.ListAsync(client, Suggested)).Items);
        Assert.Equal(
            ["n8-4", "n8-3", "n8-2"],
            file.GetProperty("suggestions").EnumerateArray().Select(static suggestion => suggestion.GetProperty("song").GetProperty("shortcode").GetString()));
    }

    [Fact]
    public async Task AMissingFileStaysListedMarkedMissingWithItsSuggestions()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Gone Song");
        var path = MediaApi.Place(factory, "Gone Song.ogg", "ogg");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        File.Delete(path);
        MediaApi.Result(await MediaApi.ScanAsync(client));

        var file = Assert.Single((await MediaApi.ListAsync(client, Suggested)).Items);
        Assert.Equal("missing", file.GetProperty("status").GetString());
        Assert.Single(file.GetProperty("suggestions").EnumerateArray());
    }

    [Fact]
    public async Task TheListSortsByNameFolderAndFirstSeenAndFiltersByTextInTheNameOrFolder()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "b/zeta.wav", "wav");
        MediaApi.Place(factory, "a/Mid.mp3", "mp3");
        MediaApi.Place(factory, "c/alpha.flac", "flac");
        MediaApi.Place(factory, "top.ogg", "ogg");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        Sql(factory, "UPDATE audio_files SET first_seen_utc = '2026-10-01T00:00:00.000Z' WHERE file_name = 'alpha.flac';");
        Sql(factory, "UPDATE audio_files SET first_seen_utc = '2026-10-03T00:00:00.000Z' WHERE file_name = 'zeta.wav';");
        Sql(factory, "UPDATE audio_files SET first_seen_utc = '2026-10-02T00:00:00.000Z' WHERE file_name IN ('Mid.mp3', 'top.ogg');");

        Assert.Equal(["a/Mid.mp3", "b/zeta.wav", "c/alpha.flac", "top.ogg"], await PathsAsync(client, ""));
        Assert.Equal(["a/Mid.mp3", "c/alpha.flac", "top.ogg", "b/zeta.wav"], await PathsAsync(client, "&sort=name"));
        Assert.Equal(["b/zeta.wav", "top.ogg", "c/alpha.flac", "a/Mid.mp3"], await PathsAsync(client, "&sort=name&direction=desc"));
        Assert.Equal(["top.ogg", "a/Mid.mp3", "b/zeta.wav", "c/alpha.flac"], await PathsAsync(client, "&sort=folder"));
        Assert.Equal(["c/alpha.flac", "b/zeta.wav", "a/Mid.mp3", "top.ogg"], await PathsAsync(client, "&sort=folder&direction=desc"));

        // First seen reads newest first by default; equal times fall back to the path.
        Assert.Equal(["b/zeta.wav", "top.ogg", "a/Mid.mp3", "c/alpha.flac"], await PathsAsync(client, "&sort=firstSeen"));
        Assert.Equal(["c/alpha.flac", "a/Mid.mp3", "top.ogg", "b/zeta.wav"], await PathsAsync(client, "&sort=firstSeen&direction=asc"));

        // Text in the name or the folder, in any letter case; the total counts the matches.
        Assert.Equal(["a/Mid.mp3"], await PathsAsync(client, "&q=mid"));
        Assert.Equal(["b/zeta.wav"], await PathsAsync(client, "&q=B%2F"));
        Assert.Equal(["c/alpha.flac"], await PathsAsync(client, "&q=%20ALPHA%20"));
        Assert.Empty(await PathsAsync(client, "&q=nothing"));
        using var counted = await client.GetAsync(new Uri("/api/v1/audio-files?q=a", UriKind.Relative));
        Assert.Equal(3, (await SetupApi.JsonAsync(counted)).GetProperty("total").GetInt32());

        // Paging follows the order asked for.
        Assert.Equal(["c/alpha.flac", "top.ogg"], await PathsAsync(client, "&sort=name&offset=1&limit=2"));
    }

    [Theory]
    [InlineData("sort=size", "sort")]
    [InlineData("sort=name&sort=folder", "sort")]
    [InlineData("direction=up", "direction")]
    [InlineData("include=everything", "include")]
    [InlineData("include=suggestions&limit=101", "limit")]
    public async Task AWrongSortDirectionIncludeOrLimitIsRefused(string query, string field)
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(new Uri($"/api/v1/audio-files?{query}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await SetupApi.JsonAsync(response);
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task ASearchOverTwoHundredCharactersIsRefusedAndAHundredWithSuggestionsIsAllowed()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var tooLong = await client.GetAsync(new Uri($"/api/v1/audio-files?q={new string('x', 201)}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.True((await SetupApi.JsonAsync(tooLong)).GetProperty("errors").TryGetProperty("q", out _));

        using var hundred = await client.GetAsync(new Uri("/api/v1/audio-files?include=suggestions&limit=100", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, hundred.StatusCode);
        Assert.Equal(100, (await SetupApi.JsonAsync(hundred)).GetProperty("limit").GetInt32());
        using var defaulted = await client.GetAsync(new Uri("/api/v1/audio-files?include=suggestions", UriKind.Relative));
        Assert.Equal(100, (await SetupApi.JsonAsync(defaulted)).GetProperty("limit").GetInt32());
    }

    private static async Task<List<string?>> PathsAsync(HttpClient client, string query) =>
        [.. (await MediaApi.ListAsync(client, "?association=none" + query)).Items.Select(static item => item.GetProperty("path").GetString())];

    private static string Clip(string sunoId, string title, double seconds) =>
        new JsonObject
        {
            ["id"] = sunoId,
            ["status"] = "complete",
            ["title"] = title,
            ["metadata"] = new JsonObject { ["duration"] = seconds },
        }.ToJsonString();

    private static async Task DeleteGenerationAsync(HttpClient client, string shortcode)
    {
        var generation = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative)));
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative));
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(generation.GetProperty("revision").GetInt32())));
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Every row of <c>audio_files</c>, every column, in path order: what "nothing was written" compares.</summary>
    private static List<string> Table(N8TracksApiFactory factory)
    {
        using var connection = Open(factory);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM audio_files ORDER BY path;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(index => $"{reader.GetName(index)}={reader.GetValue(index)}")));
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static void Sql(N8TracksApiFactory factory, string sql)
    {
        using var connection = Open(factory);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.True(command.ExecuteNonQuery() > 0, sql);
    }

    private static SqliteConnection Open(N8TracksApiFactory factory)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = TestDatabase.FilePath(factory.DataPath), Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        return connection;
    }
}
