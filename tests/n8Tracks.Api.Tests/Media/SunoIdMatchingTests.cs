using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Retention;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Automatic association by Suno ID (#206), through real scans of the host's temporary media folder:
/// a file whose name carries the complete Suno ID of exactly one live Generation is associated with it
/// and its Song; nothing else is ever matched; a user's association or removal is never overridden;
/// deleted Generations give a reason; and the database refuses an association that breaks the rules.
/// </summary>
public sealed class SunoIdMatchingTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string B = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";
    private const string C = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d";
    private const string Later = "11112222-3333-4444-5555-666677778888";
    private const string Nobody = "ffffeeee-dddd-4ccc-bbbb-aaaa99998888";

    [Fact]
    public async Task AScanAssociatesEachFileWhoseNameCarriesOneLiveGenerationsSunoIdAndNothingElse()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, $"Song Artist - Song Title (suno-{A}).wav", "wav");
        MediaApi.Place(factory, $"SUNO-{B.ToUpperInvariant()}.MP3", "mp3");
        MediaApi.Place(factory, $"Other/mix {C} final.flac", "flac");
        MediaApi.Place(factory, $"Album/Disc 1/Song Title (suno-{A}) (1).ogg", "ogg");
        MediaApi.Place(factory, $"{A} and {Nobody}.aac", "aac");
        MediaApi.Place(factory, $"suno-{A}/tone.m4a", "m4a");
        MediaApi.Place(factory, $"suno-{A[..^1]}.opus", "opus");
        MediaApi.Place(factory, $"{A} + {B}.wav", "wav");
        var before = MediaApi.Listing(factory.MediaPath);

        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(5, result.GetProperty("associated").GetInt32());
        Assert.Equal(3, result.GetProperty("unmatched").GetInt32());

        var (items, _) = await MediaApi.ListAsync(client);
        AssertAssociated(MediaApi.ByPath(items, $"Song Artist - Song Title (suno-{A}).wav"), catalog.First);
        AssertAssociated(MediaApi.ByPath(items, $"SUNO-{B.ToUpperInvariant()}.MP3"), catalog.Second);
        AssertAssociated(MediaApi.ByPath(items, $"Other/mix {C} final.flac"), catalog.OtherSong);

        // Several files may match one Generation; a UUID that matches nothing is ignored.
        AssertAssociated(MediaApi.ByPath(items, $"Album/Disc 1/Song Title (suno-{A}) (1).ogg"), catalog.First);
        AssertAssociated(MediaApi.ByPath(items, $"{A} and {Nobody}.aac"), catalog.First);

        // An ID only in a directory name, a partial ID, and two live IDs: unmatched.
        AssertUnmatched(MediaApi.ByPath(items, $"suno-{A}/tone.m4a"), null);
        AssertUnmatched(MediaApi.ByPath(items, $"suno-{A[..^1]}.opus"), null);
        AssertUnmatched(MediaApi.ByPath(items, $"{A} + {B}.wav"), "multiple_suno_ids");

        // The association filter is real, and one file reads the same.
        var (associated, _) = await MediaApi.ListAsync(client, "?association=associated");
        Assert.Equal(5, associated.Count);
        var (none, _) = await MediaApi.ListAsync(client, "?association=none");
        Assert.Equal(3, none.Count);
        var id = MediaApi.ByPath(items, $"SUNO-{B.ToUpperInvariant()}.MP3").GetProperty("id").GetGuid();
        using var one = await client.GetAsync(new Uri($"/api/v1/audio-files/{id}", UriKind.Relative));
        AssertAssociated(await SetupApi.JsonAsync(one), catalog.Second);

        // A second scan changes nothing, and the media folder was never written (invariant 2).
        var again = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(0, again.GetProperty("associated").GetInt32());
        Assert.Equal(3, again.GetProperty("unmatched").GetInt32());
        Assert.Equal(2, MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, $"Song Artist - Song Title (suno-{A}).wav").GetProperty("revision").GetInt32());
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
    }

    [Fact]
    public async Task NeitherASongTitleNorEmbeddedTitleAndDurationIsEverMatched()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // A Song and Generation named as the fixtures are tagged, 1.5 s long like them.
        await SongApi.CreateAsync(client, "Fixture Title");
        var clip = $$$"""{"id":"{{{A}}}","status":"complete","title":"Fixture Title","metadata":{"duration":1.5}}""";
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", clip);
        MediaApi.Place(factory, "Fixture Title.wav", "wav");
        MediaApi.Place(factory, "tagged.flac", "flac");

        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(0, result.GetProperty("associated").GetInt32());
        Assert.Equal(2, result.GetProperty("unmatched").GetInt32());
        var (items, _) = await MediaApi.ListAsync(client);
        var tagged = MediaApi.ByPath(items, "tagged.flac");
        Assert.Equal("Fixture Title", tagged.GetProperty("title").GetString());
        AssertUnmatched(tagged, null);
        AssertUnmatched(MediaApi.ByPath(items, "Fixture Title.wav"), null);
    }

    [Fact]
    public async Task AUsersAssociationOrRemovalIsNeverChangedByAScan()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, $"theirs (suno-{A}).wav", "wav");
        MediaApi.Place(factory, $"removed (suno-{A}).wav", "wav");
        MediaApi.Place(factory, "song level.wav", "wav");
        await MediaApi.ScanAsync(client);

        // The user associates the first with another Song's Generation, the third with a Song only, and
        // removes the second's association (#210 brings the endpoints; the rows are what they write).
        Sql(factory, $"UPDATE audio_files SET song_id = '{Upper(catalog.OtherSong.SongId)}', generation_id = '{Upper(catalog.OtherSong.GenerationId)}', association_origin = 'user', unmatched_reason = NULL WHERE file_name = 'theirs (suno-{A}).wav';");
        Sql(factory, $"UPDATE audio_files SET song_id = NULL, generation_id = NULL, association_origin = NULL, unmatched_reason = 'unassociated_by_user' WHERE file_name = 'removed (suno-{A}).wav';");
        Sql(factory, $"UPDATE audio_files SET song_id = '{Upper(catalog.First.SongId)}', association_origin = 'user' WHERE file_name = 'song level.wav';");

        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(0, result.GetProperty("associated").GetInt32());
        Assert.Equal(1, result.GetProperty("unmatched").GetInt32());

        var (items, _) = await MediaApi.ListAsync(client);
        AssertAssociated(MediaApi.ByPath(items, $"theirs (suno-{A}).wav"), catalog.OtherSong, "user");
        AssertUnmatched(MediaApi.ByPath(items, $"removed (suno-{A}).wav"), "unassociated_by_user");
        var songLevel = MediaApi.ByPath(items, "song level.wav");
        Assert.Equal("n8-1", songLevel.GetProperty("song").GetProperty("shortcode").GetString());
        Assert.Equal(JsonValueKind.Null, songLevel.GetProperty("generation").ValueKind);
        Assert.Equal("user", songLevel.GetProperty("associationOrigin").GetString());
    }

    [Fact]
    public async Task ADeletedGenerationsFilesAreUnmatchedWithTheReasonUntilItIsRestored()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, $"before (suno-{A}).wav", "wav");
        MediaApi.Place(factory, "song level.wav", "wav");
        await MediaApi.ScanAsync(client);
        Sql(factory, $"UPDATE audio_files SET song_id = '{Upper(catalog.First.SongId)}', association_origin = 'user' WHERE file_name = 'song level.wav';");

        // Deleting the Generation releases its file in the same transaction (the database would refuse
        // the deletion otherwise); the Song-level file stays with the Song.
        await DeleteGenerationAsync(client, catalog.First.Shortcode);
        var (items, _) = await MediaApi.ListAsync(client);
        var released = MediaApi.ByPath(items, $"before (suno-{A}).wav");
        AssertUnmatched(released, "generation_deleted");
        Assert.Equal(3, released.GetProperty("revision").GetInt32());
        Assert.Equal("n8-1", MediaApi.ByPath(items, "song level.wav").GetProperty("song").GetProperty("shortcode").GetString());

        // A file found later carries the reason too; one with a live ID as well matches the live one.
        MediaApi.Place(factory, $"after (suno-{A}).wav", "wav");
        MediaApi.Place(factory, $"{A} with {B}.wav", "wav");
        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(1, result.GetProperty("associated").GetInt32());
        (items, _) = await MediaApi.ListAsync(client);
        AssertUnmatched(MediaApi.ByPath(items, $"before (suno-{A}).wav"), "generation_deleted");
        AssertUnmatched(MediaApi.ByPath(items, $"after (suno-{A}).wav"), "generation_deleted");
        AssertAssociated(MediaApi.ByPath(items, $"{A} with {B}.wav"), catalog.Second);

        // Restored, the next scan matches its files again and clears the reason.
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            Assert.IsType<DeletedItemRestoreOutcome.Restored>(
                await scope.ServiceProvider.GetRequiredService<DeletedItemsService>().RestoreAsync(catalog.First.Shortcode, CancellationToken.None));
        }

        result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(2, result.GetProperty("associated").GetInt32());
        (items, _) = await MediaApi.ListAsync(client);
        AssertAssociated(MediaApi.ByPath(items, $"before (suno-{A}).wav"), catalog.First);
        AssertAssociated(MediaApi.ByPath(items, $"after (suno-{A}).wav"), catalog.First);
    }

    [Fact]
    public async Task DeletingAVersionOrASongWithAssociatedFilesWorksAndReleasesThem()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        SongApi.AddVersionDirectly(factory.DataPath, 1, "2");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v2", Clips.Minimal(Later));
        MediaApi.Place(factory, $"v2 (suno-{Later}).wav", "wav");
        MediaApi.Place(factory, $"other (suno-{C}).wav", "wav");
        MediaApi.Place(factory, "other song level.wav", "wav");
        Assert.Equal(2, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("associated").GetInt32());
        Sql(factory, $"UPDATE audio_files SET song_id = '{Upper(catalog.OtherSong.SongId)}', association_origin = 'user' WHERE file_name = 'other song level.wav';");

        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v2", UriKind.Relative)));
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v2", version.GetProperty("revision").GetInt32()))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        }

        var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-2")));
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-2", song.GetProperty("revision").GetInt32(), """{"confirmTitle":"Other"}"""))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        var (items, _) = await MediaApi.ListAsync(client);
        AssertUnmatched(MediaApi.ByPath(items, $"v2 (suno-{Later}).wav"), "generation_deleted");
        AssertUnmatched(MediaApi.ByPath(items, $"other (suno-{C}).wav"), "song_deleted");
        AssertUnmatched(MediaApi.ByPath(items, "other song level.wav"), "song_deleted");

        // A scan keeps the Song's reason for its files (its Generations' IDs are deleted too).
        await MediaApi.ScanAsync(client);
        (items, _) = await MediaApi.ListAsync(client);
        AssertUnmatched(MediaApi.ByPath(items, $"other (suno-{C}).wav"), "song_deleted");
        AssertUnmatched(MediaApi.ByPath(items, $"v2 (suno-{Later}).wav"), "generation_deleted");
    }

    [Fact]
    public async Task AMovedGenerationsFilesFollowItToItsNewSong()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, $"moving (suno-{B}).wav", "wav");
        await MediaApi.ScanAsync(client);

        var generation = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/generations/{catalog.Second.Shortcode}", UriKind.Relative)));
        using (var moved = await SendAsync(client, HttpMethod.Post, $"generations/{catalog.Second.Shortcode}/move-to-new-song", generation.GetProperty("revision").GetInt32(), """{"title":"Split out"}"""))
        {
            Assert.True(moved.StatusCode == HttpStatusCode.Created, await moved.Content.ReadAsStringAsync());
        }

        var file = MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, $"moving (suno-{B}).wav");
        Assert.Equal("n8-3", file.GetProperty("song").GetProperty("shortcode").GetString());
        Assert.Equal(catalog.Second.GenerationId, file.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal("n8-3-v1-g1", file.GetProperty("generation").GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task AGenerationAttachedAfterAScanGainsItsFileAtTheNext()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        MediaApi.Place(factory, $"early (suno-{Later}).wav", "wav");

        var first = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(0, first.GetProperty("associated").GetInt32());
        AssertUnmatched(MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, $"early (suno-{Later}).wav"), null);

        var attached = await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal(Later));
        var second = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(1, second.GetProperty("associated").GetInt32());
        Assert.Equal(0, second.GetProperty("unmatched").GetInt32());
        AssertAssociated(
            MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, $"early (suno-{Later}).wav"),
            new Attached(attached.Generation.SongId, attached.Generation.Id, "n8-2", "n8-2-v1-g2"));
    }

    [Fact]
    public async Task TheDatabaseRefusesAnythingButOneSongAndAtMostOneGenerationOfThatSong()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        MediaApi.Place(factory, "file.wav", "wav");
        await MediaApi.ScanAsync(client);
        const string Where = " WHERE file_name = 'file.wav';";
        var first = Upper(catalog.First.SongId);

        // A Generation of another Song, a Song that does not exist, a Generation with no Song.
        Assert.Throws<SqliteException>(() => Sql(factory, $"UPDATE audio_files SET song_id = '{first}', generation_id = '{Upper(catalog.OtherSong.GenerationId)}', association_origin = 'user'" + Where));
        Assert.Throws<SqliteException>(() => Sql(factory, $"UPDATE audio_files SET song_id = '{Upper(Guid.NewGuid())}', association_origin = 'user'" + Where));
        Assert.Throws<SqliteException>(() => Sql(factory, $"UPDATE audio_files SET generation_id = '{Upper(catalog.First.GenerationId)}'" + Where));

        // An association without its origin, an origin without a Song, a Suno ID association with no
        // Generation, a reason while associated, and unknown codes.
        Assert.Throws<SqliteException>(() => Sql(factory, $"UPDATE audio_files SET song_id = '{first}'" + Where));
        Assert.Throws<SqliteException>(() => Sql(factory, "UPDATE audio_files SET association_origin = 'user'" + Where));
        Assert.Throws<SqliteException>(() => Sql(factory, $"UPDATE audio_files SET song_id = '{first}', association_origin = 'suno-id'" + Where));
        Assert.Throws<SqliteException>(() => Sql(factory, $"UPDATE audio_files SET song_id = '{first}', association_origin = 'user', unmatched_reason = 'song_deleted'" + Where));
        Assert.Throws<SqliteException>(() => Sql(factory, $"UPDATE audio_files SET song_id = '{first}', association_origin = 'guess'" + Where));
        Assert.Throws<SqliteException>(() => Sql(factory, "UPDATE audio_files SET unmatched_reason = 'title_match'" + Where));

        // A Song and a Generation of a Song is one column each: one file cannot hold two Songs.
        Assert.Equal(
            ["generation_id", "song_id"],
            TestDatabase.Rows(factory.DataPath, "SELECT name FROM pragma_table_info('audio_files') WHERE name IN ('song_id', 'generation_id') ORDER BY name;"));

        // Complement: what the rules allow is accepted (and every reason code is known).
        Sql(factory, $"UPDATE audio_files SET song_id = '{first}', generation_id = '{Upper(catalog.First.GenerationId)}', association_origin = 'suno-id'" + Where);
        Sql(factory, $"UPDATE audio_files SET generation_id = NULL, association_origin = 'user'" + Where);
        foreach (var reason in new[] { "generation_deleted", "multiple_suno_ids", "unassociated_by_user", "song_deleted" })
        {
            Sql(factory, $"UPDATE audio_files SET song_id = NULL, generation_id = NULL, association_origin = NULL, unmatched_reason = '{reason}'" + Where);
        }
    }

    /// <summary>
    /// Song n8-1 ("Origin") with Generations g1 (Suno ID <see cref="A"/>) and g2 (<see cref="B"/>) on
    /// its Version 1; Song n8-2 ("Other") with g1 (<see cref="C"/>).
    /// </summary>
    private static async Task<Catalog> CatalogAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.CreateAsync(client, "Other");
        var first = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(A));
        var second = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(B));
        var other = await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal(C));
        return new Catalog(
            new Attached(first.Generation.SongId, first.Generation.Id, "n8-1", "n8-1-v1-g1"),
            new Attached(second.Generation.SongId, second.Generation.Id, "n8-1", "n8-1-v1-g2"),
            new Attached(other.Generation.SongId, other.Generation.Id, "n8-2", "n8-2-v1-g1"));
    }

    private static void AssertAssociated(JsonElement file, Attached expected, string origin = "suno-id")
    {
        var song = file.GetProperty("song");
        Assert.True(song.ValueKind == JsonValueKind.Object, file.ToString());
        Assert.Equal(expected.SongId, song.GetProperty("id").GetGuid());
        Assert.Equal(expected.SongShortcode, song.GetProperty("shortcode").GetString());
        Assert.Equal(expected.GenerationId, file.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal(expected.Shortcode, file.GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal(origin, file.GetProperty("associationOrigin").GetString());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("unmatchedReason").ValueKind);
    }

    private static void AssertUnmatched(JsonElement file, string? reason)
    {
        Assert.True(file.GetProperty("song").ValueKind == JsonValueKind.Null, file.ToString());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("generation").ValueKind);
        Assert.Equal(JsonValueKind.Null, file.GetProperty("associationOrigin").ValueKind);
        Assert.Equal(reason, file.GetProperty("unmatchedReason").GetString());
    }

    private static async Task DeleteGenerationAsync(HttpClient client, string shortcode)
    {
        var generation = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative)));
        using var response = await SendAsync(client, HttpMethod.Delete, $"generations/{shortcode}", generation.GetProperty("revision").GetInt32());
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

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

    /// <summary>Runs <paramref name="sql"/> on the host's database with foreign keys enforced, as the app's connections are.</summary>
    private static void Sql(N8TracksApiFactory factory, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = TestDatabase.FilePath(factory.DataPath), Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    private sealed record Attached(Guid SongId, Guid GenerationId, string SongShortcode, string Shortcode);

    private sealed record Catalog(Attached First, Attached Second, Attached OtherSong);
}
