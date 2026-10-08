using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;
using n8Tracks.Application.Retention;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Preferred audio files and playback (#212) through the API, over real scans of the host's temporary
/// media folder: choosing and clearing a Generation's and a Song's file, the two playback reads, the
/// marks on the Song's list, a chosen file that goes Missing and comes back (its choice kept byte for
/// byte), the Selected Generation changing nothing, an association change or a deletion clearing the
/// choice, the database's own refusals, and the API's refusals. Nothing in the media folder changes.
/// </summary>
public sealed class PreferredAudioFilesTests
{
    private const string A = "0c90d621-e30c-4c76-814a-e1fdeb500582";
    private const string B = "6f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9";
    private const string D = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d";

    private static readonly string Wav = $"take (suno-{A}).wav";
    private static readonly string Mp3 = $"take (suno-{A}).mp3";

    [Fact]
    public async Task TheDemoAChosenFilePlaysFallsBackWhileMissingAndPlaysAgainOnceBackWithItsChoiceUntouched()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        var before = MediaApi.Listing(factory.MediaPath);

        // 1. With no choice, the WAV plays now.
        var playback = await PlaybackAsync(client, "generations/n8-1-v1-g1");
        Assert.Equal("local", playback.GetProperty("source").GetString());
        Assert.Equal(Wav, playback.GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.Equal("format_order", playback.GetProperty("reason").GetString());
        var files = await SongFilesAsync(client, "n8-1");
        AssertMarks(files[Wav], preferred: false, forGeneration: true);
        AssertMarks(files[Mp3], preferred: false, forGeneration: false);

        // 2. The MP3 chosen: it is preferred and plays now; the Generation's revision rises.
        var revision = await GenerationRevisionAsync(client, "n8-1-v1-g1");
        using (var response = await SendAsync(client, HttpMethod.Put, "generations/n8-1-v1-g1/preferred-audio-file", revision, Body(files[Mp3])))
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            Assert.Equal(SongApi.Quoted(revision + 1), response.Headers.ETag?.Tag);
            Assert.Equal("n8-1-v1-g1", JsonDocument.Parse(body).RootElement.GetProperty("shortcode").GetString());
        }

        files = await SongFilesAsync(client, "n8-1");
        AssertMarks(files[Mp3], preferred: true, forGeneration: true);
        AssertMarks(files[Wav], preferred: false, forGeneration: false);
        playback = await PlaybackAsync(client, "generations/n8-1-v1-g1");
        Assert.Equal(Mp3, playback.GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.Equal("generation_preferred", playback.GetProperty("reason").GetString());
        Assert.Equal("mp3", playback.GetProperty("audioFile").GetProperty("format").GetString());
        Assert.True(playback.GetProperty("audioFile").GetProperty("durationSeconds").GetDecimal() > 1m);
        Assert.Equal(
            $"/api/v1/audio-files/{files[Mp3].GetProperty("id").GetGuid()}/content",
            playback.GetProperty("audioFile").GetProperty("contentUrl").GetString());
        var stored = Preferences(factory);
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));

        // 3. The MP3 moved away: the WAV plays now, the MP3 is still preferred, Missing, and the choice
        // is stored byte for byte as it was.
        File.Move(MediaApi.FullPath(factory, Mp3), Path.Combine(catalog.Outside, "away.mp3"));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        files = await SongFilesAsync(client, "n8-1");
        AssertMarks(files[Mp3], preferred: true, forGeneration: false);
        Assert.Equal("missing", files[Mp3].GetProperty("status").GetString());
        AssertMarks(files[Wav], preferred: false, forGeneration: true);
        playback = await PlaybackAsync(client, "generations/n8-1-v1-g1");
        Assert.Equal(Wav, playback.GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.Equal("preferred_missing_fallback", playback.GetProperty("reason").GetString());
        Assert.Equal(stored, Preferences(factory));
        Assert.Equal(revision + 1, await GenerationRevisionAsync(client, "n8-1-v1-g1"));

        // While the media folder cannot be read, nothing local plays.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaAvailability>().RecordAsync(readable: false, CancellationToken.None);
        }

        playback = await PlaybackAsync(client, "generations/n8-1-v1-g1");
        Assert.Equal("none", playback.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, playback.GetProperty("audioFile").ValueKind);
        Assert.Equal("nothing_available", playback.GetProperty("reason").GetString());

        // 4. Back, and scanned: the MP3 plays again with no action, the choice unchanged.
        File.Move(Path.Combine(catalog.Outside, "away.mp3"), MediaApi.FullPath(factory, Mp3));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        playback = await PlaybackAsync(client, "generations/n8-1-v1-g1");
        Assert.Equal(Mp3, playback.GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.Equal("generation_preferred", playback.GetProperty("reason").GetString());
        Assert.Equal(stored, Preferences(factory));
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
    }

    [Fact]
    public async Task ChoosingTheSameFileAgainOrClearingNothingStoresNothingAndAStaleRevisionIsAConflict()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        var files = await SongFilesAsync(client, "n8-1");
        var revision = await GenerationRevisionAsync(client, "n8-1-v1-g1");

        // Clearing a Generation with no choice stores nothing.
        using (var response = await SendAsync(client, HttpMethod.Delete, "generations/n8-1-v1-g1/preferred-audio-file", revision))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(SongApi.Quoted(revision), response.Headers.ETag?.Tag);
        }

        await SetGenerationAsync(client, "n8-1-v1-g1", files[Mp3], revision);
        using (var again = await SendAsync(client, HttpMethod.Put, "generations/n8-1-v1-g1/preferred-audio-file", revision + 1, Body(files[Mp3])))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(SongApi.Quoted(revision + 1), again.Headers.ETag?.Tag);
        }

        using (var stale = await SendAsync(client, HttpMethod.Put, "generations/n8-1-v1-g1/preferred-audio-file", revision, Body(files[Wav])))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(revision + 1, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var stale = await SendAsync(client, HttpMethod.Delete, "generations/n8-1-v1-g1/preferred-audio-file", revision))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        Assert.True((await SongFilesAsync(client, "n8-1"))[Mp3].GetProperty("isPreferred").GetBoolean());

        // Cleared: the automatic rule decides again.
        using (var cleared = await SendAsync(client, HttpMethod.Delete, "generations/n8-1-v1-g1/preferred-audio-file", revision + 1))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            Assert.Equal(SongApi.Quoted(revision + 2), cleared.Headers.ETag?.Tag);
        }

        Assert.Equal("format_order", (await PlaybackAsync(client, "generations/n8-1-v1-g1")).GetProperty("reason").GetString());
        Assert.Empty(Preferences(factory));
    }

    [Fact]
    public async Task TheSongPlaysItsPreferredSongLevelFileElseItsSelectedGenerationsAndSelectingChangesNoFileOrChoice()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);

        // No Selected Generation and no choice: nothing local plays.
        var playback = await PlaybackAsync(client, "songs/n8-1");
        Assert.Equal("none", playback.GetProperty("source").GetString());
        Assert.Equal("no_selected_generation", playback.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, playback.GetProperty("generation").ValueKind);

        // g1 selected: its WAV plays for the Song.
        await SelectAsync(client, "n8-1-v1-g1");
        playback = await PlaybackAsync(client, "songs/n8-1");
        Assert.Equal(Wav, playback.GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.Equal("format_order", playback.GetProperty("reason").GetString());
        Assert.Equal("n8-1-v1-g1", playback.GetProperty("generation").GetProperty("shortcode").GetString());

        // The Song-level master chosen: it outranks the Selected Generation.
        var files = await SongFilesAsync(client, "n8-1");
        var revision = await SongRevisionAsync(client, "n8-1");
        using (var response = await SendAsync(client, HttpMethod.Put, "songs/n8-1/preferred-audio-file", revision, Body(files["Loose/master.mp3"])))
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            Assert.Equal(SongApi.Quoted(revision + 1), response.Headers.ETag?.Tag);
            Assert.Equal(revision + 1, JsonDocument.Parse(body).RootElement.GetProperty("revision").GetInt32());
        }

        playback = await PlaybackAsync(client, "songs/n8-1");
        Assert.Equal("master.mp3", playback.GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.Equal("song_preferred", playback.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, playback.GetProperty("generation").ValueKind);
        files = await SongFilesAsync(client, "n8-1");
        Assert.True(files["Loose/master.mp3"].GetProperty("isPreferred").GetBoolean());
        Assert.True(files["Loose/master.mp3"].GetProperty("playsForSong").GetBoolean());
        Assert.False(files["Loose/master.mp3"].GetProperty("playsForGeneration").GetBoolean());
        Assert.False(files[Wav].GetProperty("playsForSong").GetBoolean());
        Assert.True(files[Wav].GetProperty("playsForGeneration").GetBoolean());

        // Complement: selecting another Generation leaves every audio file row and both choice tables as they were.
        var table = AudioFiles(factory);
        var choices = Preferences(factory);
        await SelectAsync(client, "n8-1-v1-g2");
        Assert.Equal(table, AudioFiles(factory));
        Assert.Equal(choices, Preferences(factory));

        // The master away: g2's file plays for the Song, and the master's choice is kept.
        File.Move(MediaApi.FullPath(factory, "Loose/master.mp3"), Path.Combine(catalog.Outside, "master.mp3"));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        playback = await PlaybackAsync(client, "songs/n8-1");
        Assert.Equal($"second (suno-{B}).m4a", playback.GetProperty("audioFile").GetProperty("fileName").GetString());
        Assert.Equal("preferred_missing_fallback", playback.GetProperty("reason").GetString());
        Assert.Equal("n8-1-v1-g2", playback.GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal(choices, Preferences(factory));

        // Cleared, the Selected Generation's file plays for its own reason.
        revision = await SongRevisionAsync(client, "n8-1");
        using (var cleared = await SendAsync(client, HttpMethod.Delete, "songs/n8-1/preferred-audio-file", revision))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            Assert.Equal(SongApi.Quoted(revision + 1), cleared.Headers.ETag?.Tag);
        }

        Assert.Equal("format_order", (await PlaybackAsync(client, "songs/n8-1")).GetProperty("reason").GetString());
        Assert.Empty(Preferences(factory));
    }

    [Fact]
    public async Task AnotherOwnersFileIsRefusedAndTheDatabaseRefusesItToo()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await CatalogAsync(factory, client);
        var files = await SongFilesAsync(client, "n8-1");
        var other = MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, $"other (suno-{D}).wav");
        var loose = MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, "unrelated.mp3");
        var generation = await GenerationRevisionAsync(client, "n8-1-v1-g1");
        var song = await SongRevisionAsync(client, "n8-1");
        var table = AudioFiles(factory);

        // A Generation's choice: a file of another Generation, of the Song only, of another Song, or of none.
        foreach (var file in new[] { files[$"second (suno-{B}).m4a"], files["Loose/master.mp3"], other, loose })
        {
            using var refused = await SendAsync(client, HttpMethod.Put, "generations/n8-1-v1-g1/preferred-audio-file", generation, Body(file));
            await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "audio_file_not_in_generation");
        }

        // A Song's choice: one of its Generations' files, or a file of another Song or of none.
        using (var refused = await SendAsync(client, HttpMethod.Put, "songs/n8-1/preferred-audio-file", song, Body(files[Wav])))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "audio_file_not_song_level");
        }

        foreach (var file in new[] { other, loose })
        {
            using var refused = await SendAsync(client, HttpMethod.Put, "songs/n8-1/preferred-audio-file", song, Body(file));
            await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "audio_file_not_in_song");
        }

        // An unknown file, Generation, or Song; a body that names no file; no revision.
        using (var response = await SendAsync(client, HttpMethod.Put, "generations/n8-1-v1-g1/preferred-audio-file", generation, $$"""{"audioFile":"{{Guid.CreateVersion7()}}"}"""))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        using (var response = await SendAsync(client, HttpMethod.Put, "generations/n8-1-v9-g1/preferred-audio-file", 1, Body(files[Wav])))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        using (var response = await SendAsync(client, HttpMethod.Put, "songs/n8-99/preferred-audio-file", 1, Body(files["Loose/master.mp3"])))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        foreach (var json in new[] { "{}", """{"audioFile":12}""", """{"audioFile":"take.mp3"}""" })
        {
            using var response = await SendAsync(client, HttpMethod.Put, "songs/n8-1/preferred-audio-file", song, json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("audioFile", out _));
        }

        using (var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/songs/n8-1/preferred-audio-file", UriKind.Relative)))
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        }

        Assert.Equal(generation, await GenerationRevisionAsync(client, "n8-1-v1-g1"));
        Assert.Equal(song, await SongRevisionAsync(client, "n8-1"));
        Assert.Empty(Preferences(factory));
        Assert.Equal(table, AudioFiles(factory));

        // The database's own keys: a choice of another Generation's file, and an association change that
        // would leave a choice behind.
        var wav = Upper(files[Wav].GetProperty("id").GetGuid());
        Assert.Throws<SqliteException>(() => Sql(factory, $"INSERT INTO generation_preferred_audio_files (generation_id, audio_file_id) VALUES ('{Upper(catalog.Second)}', '{wav}');"));
        Assert.Throws<SqliteException>(() => Sql(factory, $"INSERT INTO song_preferred_audio_files (song_id, audio_file_id) VALUES ('{Upper(catalog.OtherSong)}', '{wav}');"));
        await SetGenerationAsync(client, "n8-1-v1-g1", files[Wav], generation);
        Assert.Throws<SqliteException>(() => Sql(factory, $"UPDATE audio_files SET generation_id = NULL, association_origin = 'user' WHERE id = '{wav}';"));
        Assert.Throws<SqliteException>(() => Sql(factory, $"UPDATE audio_files SET generation_id = '{Upper(catalog.Second)}' WHERE id = '{wav}';"));
    }

    [Fact]
    public async Task ChangingOrRemovingAChosenFilesAssociationClearsTheChoiceAndRaisesItsOwnersRevision()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        var files = await SongFilesAsync(client, "n8-1");

        // A Generation's choice, its file moved to another Generation of the same Song.
        var generation = await GenerationRevisionAsync(client, "n8-1-v1-g1");
        await SetGenerationAsync(client, "n8-1-v1-g1", files[Mp3], generation);
        await AssociateAsync(client, (await SongFilesAsync(client, "n8-1"))[Mp3], """{"song":"n8-1","generation":"n8-1-v1-g2"}""");
        Assert.Equal(generation + 2, await GenerationRevisionAsync(client, "n8-1-v1-g1"));
        Assert.Empty(Preferences(factory));
        Assert.False((await SongFilesAsync(client, "n8-1"))[Mp3].GetProperty("isPreferred").GetBoolean());

        // A Generation's choice, its file made Song-level.
        generation = await GenerationRevisionAsync(client, "n8-1-v1-g1");
        await SetGenerationAsync(client, "n8-1-v1-g1", files[Wav], generation);
        await AssociateAsync(client, (await SongFilesAsync(client, "n8-1"))[Wav], """{"song":"n8-1"}""");
        Assert.Equal(generation + 2, await GenerationRevisionAsync(client, "n8-1-v1-g1"));
        Assert.Empty(Preferences(factory));

        // The Song's choice, its file's association removed; then another, its file given a Generation.
        var song = await SongRevisionAsync(client, "n8-1");
        await SetSongAsync(client, "n8-1", (await SongFilesAsync(client, "n8-1"))["Loose/master.mp3"], song);
        var master = (await SongFilesAsync(client, "n8-1"))["Loose/master.mp3"];
        Assert.True(master.GetProperty("isPreferred").GetBoolean());
        using (var removed = await SendAsync(client, HttpMethod.Delete, $"audio-files/{master.GetProperty("id").GetGuid()}/association", master.GetProperty("revision").GetInt32()))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(song + 2, await SongRevisionAsync(client, "n8-1"));
        Assert.Empty(Preferences(factory));

        song = await SongRevisionAsync(client, "n8-1");
        await SetSongAsync(client, "n8-1", (await SongFilesAsync(client, "n8-1"))[Wav], song);
        await AssociateAsync(client, (await SongFilesAsync(client, "n8-1"))[Wav], """{"song":"n8-1","generation":"n8-1-v1-g1"}""");
        Assert.Equal(song + 2, await SongRevisionAsync(client, "n8-1"));
        Assert.Empty(Preferences(factory));

        // Naming the association a chosen file already has changes nothing, the choice included.
        generation = await GenerationRevisionAsync(client, "n8-1-v1-g1");
        await SetGenerationAsync(client, "n8-1-v1-g1", (await SongFilesAsync(client, "n8-1"))[Wav], generation);
        await AssociateAsync(client, (await SongFilesAsync(client, "n8-1"))[Wav], """{"song":"n8-1","generation":"n8-1-v1-g1"}""");
        Assert.Single(Preferences(factory));
        Assert.Equal(generation + 1, await GenerationRevisionAsync(client, "n8-1-v1-g1"));
    }

    [Fact]
    public async Task DeletingAnOwnerRemovesItsChoiceAndARestoreDoesNotBringItBack()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        var files = await SongFilesAsync(client, "n8-1");
        await SetGenerationAsync(client, "n8-1-v1-g1", files[Mp3], await GenerationRevisionAsync(client, "n8-1-v1-g1"));
        await SetSongAsync(client, "n8-1", files["Loose/master.mp3"], await SongRevisionAsync(client, "n8-1"));

        // The Generation deleted: its choice goes with it, the Song's stays.
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "generations/n8-1-v1-g1", await GenerationRevisionAsync(client, "n8-1-v1-g1")))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Equal([$"song|{files["Loose/master.mp3"].GetProperty("id").GetGuid().ToString().ToUpperInvariant()}"], Preferences(factory));
        using (var scope = factory.Services.CreateScope())
        {
            Assert.IsType<DeletedItemRestoreOutcome.Restored>(
                await scope.ServiceProvider.GetRequiredService<DeletedItemsService>().RestoreAsync("n8-1-v1-g1", CancellationToken.None));
        }

        MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal("format_order", (await PlaybackAsync(client, "generations/n8-1-v1-g1")).GetProperty("reason").GetString());

        // The Song deleted with its choice and its Generations' choices.
        await SetGenerationAsync(client, "n8-1-v1-g1", (await SongFilesAsync(client, "n8-1"))[Mp3], await GenerationRevisionAsync(client, "n8-1-v1-g1"));
        Assert.Equal(2, Preferences(factory).Count);
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-1", await SongRevisionAsync(client, "n8-1"), """{"confirmTitle":"Origin"}"""))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Empty(Preferences(factory));
    }

    [Fact]
    public async Task PlaybackReadsNeedCatalogReadAndChoicesNeedSongsWrite()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CatalogAsync(factory, client);
        var files = await SongFilesAsync(client, "n8-1");
        using var anonymous = factory.CreateClient();
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);

        foreach (var path in new[] { "generations/n8-1-v1-g1/playback", "songs/n8-1/playback" })
        {
            var uri = new Uri($"/api/v1/{path}", UriKind.Relative);
            using (var read = await CredentialApi.SendAsync(anonymous, HttpMethod.Get, uri, reader))
            {
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
            }

            using (var refused = await CredentialApi.SendAsync(anonymous, HttpMethod.Get, uri, writer))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            using var signedOut = await anonymous.GetAsync(uri);
            Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        }

        var generation = await GenerationRevisionAsync(client, "n8-1-v1-g1");
        using (var refused = await TokenSendAsync(anonymous, reader, HttpMethod.Put, "generations/n8-1-v1-g1/preferred-audio-file", generation, Body(files[Mp3])))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var allowed = await TokenSendAsync(anonymous, writer, HttpMethod.Put, "generations/n8-1-v1-g1/preferred-audio-file", generation, Body(files[Mp3])))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        // An unknown Generation and a deleted Song answer as their other reads do.
        using (var response = await client.GetAsync(new Uri("/api/v1/generations/n8-1-v9-g1/playback", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        await SongApi.CreateAsync(client, "Gone");
        using (var deleted = await SendAsync(client, HttpMethod.Delete, "songs/n8-3", 1, """{"confirmTitle":"Gone"}"""))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }

        using (var response = await client.GetAsync(new Uri("/api/v1/songs/n8-3/playback", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "song_deleted");
        }
    }

    /// <summary>
    /// Song n8-1 (Version 1 with g1 and g2) and n8-2 (one Generation); g1 has a WAV and an MP3 matched by
    /// Suno ID, g2 an M4A; <c>Loose/master.mp3</c> is associated with n8-1 alone; n8-2's g1 has a WAV;
    /// <c>unrelated.mp3</c> is associated with nothing. A folder outside the media folder takes files moved away.
    /// </summary>
    private static async Task<Catalog> CatalogAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.CreateAsync(client, "Other");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(A));
        var second = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal(B));
        var other = await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal(D));

        MediaApi.Place(factory, Mp3, "mp3");
        MediaApi.Place(factory, Wav, "wav");
        MediaApi.Place(factory, $"second (suno-{B}).m4a", "m4a");
        MediaApi.Place(factory, $"other (suno-{D}).wav", "wav");
        MediaApi.Place(factory, "Loose/master.mp3", "mp3");
        MediaApi.Place(factory, "unrelated.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));

        var master = MediaApi.ByPath((await MediaApi.ListAsync(client)).Items, "Loose/master.mp3");
        await AssociateAsync(client, master, """{"song":"n8-1"}""");

        var outside = Path.Combine(factory.DataPath, "outside");
        Directory.CreateDirectory(outside);
        return new Catalog(second.Generation.Id, other.Generation.SongId, outside);
    }

    private static async Task AssociateAsync(HttpClient client, JsonElement file, string json)
    {
        using var response = await SendAsync(client, HttpMethod.Put, $"audio-files/{file.GetProperty("id").GetGuid()}/association", file.GetProperty("revision").GetInt32(), json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task SetGenerationAsync(HttpClient client, string generation, JsonElement file, int revision)
    {
        using var response = await SendAsync(client, HttpMethod.Put, $"generations/{generation}/preferred-audio-file", revision, Body(file));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task SetSongAsync(HttpClient client, string song, JsonElement file, int revision)
    {
        using var response = await SendAsync(client, HttpMethod.Put, $"songs/{song}/preferred-audio-file", revision, Body(file));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task SelectAsync(HttpClient client, string generation)
    {
        using var response = await SendAsync(client, HttpMethod.Put, "songs/n8-1/selected-generation", await SongRevisionAsync(client, "n8-1"), $$"""{"generation":"{{generation}}"}""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static string Body(JsonElement file) => $$"""{"audioFile":"{{file.GetProperty("id").GetGuid()}}"}""";

    private static async Task<JsonElement> PlaybackAsync(HttpClient client, string owner)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/{owner}/playback", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The Song's list by path (folder and file name).</summary>
    private static async Task<Dictionary<string, JsonElement>> SongFilesAsync(HttpClient client, string song)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/songs/{song}/audio-files", UriKind.Relative));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray().ToDictionary(static item => item.GetProperty("path").GetString()!, static item => item.Clone());
    }

    private static void AssertMarks(JsonElement file, bool preferred, bool forGeneration)
    {
        Assert.Equal(preferred, file.GetProperty("isPreferred").GetBoolean());
        Assert.Equal(forGeneration, file.GetProperty("playsForGeneration").GetBoolean());
    }

    private static async Task<int> GenerationRevisionAsync(HttpClient client, string generation) =>
        (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/generations/{generation}", UriKind.Relative)))).GetProperty("revision").GetInt32();

    private static async Task<int> SongRevisionAsync(HttpClient client, string song) =>
        (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(song)))).GetProperty("revision").GetInt32();

    /// <summary>Both choice tables, row by row as stored (kind, file ID).</summary>
    private static List<string> Preferences(N8TracksApiFactory factory) =>
        TestDatabase.Rows(
            factory.DataPath,
            "SELECT 'generation|' || generation_id || '|' || audio_file_id FROM generation_preferred_audio_files "
            + "UNION ALL SELECT 'song|' || audio_file_id FROM song_preferred_audio_files ORDER BY 1;");

    /// <summary>Every audio_files row, every column, as stored.</summary>
    private static List<string> AudioFiles(N8TracksApiFactory factory) =>
        TestDatabase.Rows(factory.DataPath, "SELECT quote(id) || quote(path) || quote(song_id) || quote(generation_id) || quote(association_origin) || quote(unmatched_reason) || quote(revision) || quote(status) || quote(last_seen_utc) || quote(auto_match_blocked) FROM audio_files ORDER BY path;");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri($"/api/v1/{path}", UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> TokenSendAsync(HttpClient client, string token, HttpMethod method, string path, int revision, string json)
    {
        using var request = new HttpRequestMessage(method, new Uri($"/api/v1/{path}", UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
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

    private sealed record Catalog(Guid Second, Guid OtherSong, string Outside);
}
