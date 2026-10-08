using System.Globalization;
using System.Net;
using System.Text.Json;
using n8Tracks.Api.Cli;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Media;
using n8Tracks.Application.Retention;
using SkiaSharp;
using static n8Tracks.Api.Tests.Assets.ArtworkApi;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Cli;

/// <summary>
/// <c>n8tracks list-deleted</c> and <c>n8tracks restore-deleted</c> (#105), run in-process through the
/// app binary's entry point against the database of an app that is running in the same test, as
/// <c>docker exec</c> runs them beside the server: the listing of every kind of deletion, restores of
/// a Song, a Version, an Album, an Artist, a history entry, and artwork, and the refusals, each of
/// which changes nothing. Nothing retained but labels, shortcodes, times, and counts is ever written.
/// </summary>
public sealed class DeletedCommandsTests
{
    /// <summary>Lyrics no output may ever show.</summary>
    private const string LyricsSentinel = "sentinel-lyrics-105-c4f2";

    private const string Oslo = "Europe/Oslo";

    [Fact]
    public async Task TheListingShowsEveryKindNewestFirstWithItsShortcodeAndTimesInTheConfiguredZone()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Listed");
        var versionId = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        await AddVersionAsync(client, "n8-1", "2");
        await SnapshotAsync(client, versionId, LyricsSentinel);
        var entry = await SnapshotAsync(client, versionId, "Another entry");
        await ReplaceArtworkAsync(client, song.GetProperty("id").GetGuid());

        await DeleteVersionAsync(client, "n8-1-v2");
        await DeleteEntryAsync(client, versionId, entry);
        await DeleteAlbumAsync(client, await CreateAsync(client, "albums", "title", "Gone Album"));
        await DeleteArtistAsync(client, await CreateAsync(client, "artists", "name", "Gone Artist"));
        await SongApi.CreateAsync(client, "Gone Song");
        await DeleteSongAsync(client, "n8-2", "Gone Song");

        var run = await RunAsync(factory.DataPath, ["list-deleted"], Oslo);

        Assert.Equal(0, run.ExitCode);
        var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Matches("^GROUP ID +KIND +LABEL +SHORTCODE +DELETED +PRUNES$", lines[0]);
        Assert.Equal(7, lines.Length);
        Assert.Equal(["Song", "Artist", "Album", "History entry", "Version", "Artwork"], lines[1..].Select(KindOf));
        Assert.Contains("Song n8-2 (Gone Song)", lines[1], StringComparison.Ordinal);
        Assert.Contains(" n8-2 ", lines[1], StringComparison.Ordinal);
        Assert.Contains("Version n8-1-v2", lines[5], StringComparison.Ordinal);
        Assert.Contains(" n8-1-v2 ", lines[5], StringComparison.Ordinal);
        Assert.Contains("Artwork of n8-1", lines[6], StringComparison.Ordinal);
        Assert.Contains("times in Europe/Oslo", run.Error, StringComparison.Ordinal);

        // Times to the minute in the configured zone; the ID shown is the start of the group's.
        var groups = await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Oslo);
        Assert.StartsWith(groups[0].Id.ToString()[..8], lines[1], StringComparison.Ordinal);
        Assert.Contains(TimeZoneInfo.ConvertTime(groups[0].DeletedUtc, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), lines[1], StringComparison.Ordinal);
        Assert.EndsWith(TimeZoneInfo.ConvertTime(groups[0].PruneAfterUtc, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), lines[1], StringComparison.Ordinal);

        // --json: the same names in camelCase, the whole ID, and the contents as counts.
        var json = await RunAsync(factory.DataPath, ["list-deleted", "--json"], Oslo);
        Assert.Equal(0, json.ExitCode);
        var items = JsonDocument.Parse(json.Output).RootElement.EnumerateArray().ToList();
        Assert.Equal(6, items.Count);
        Assert.Equal(groups.Select(static group => group.Id), items.Select(static item => item.GetProperty("groupId").GetGuid()));
        var deletedSong = items[0];
        Assert.Equal("song", deletedSong.GetProperty("kind").GetString());
        Assert.Equal("Song", deletedSong.GetProperty("kindName").GetString());
        Assert.Equal("n8-2", deletedSong.GetProperty("shortcode").GetString());
        Assert.Equal("Song n8-2 (Gone Song)", deletedSong.GetProperty("label").GetString());
        Assert.Equal(TimeZoneInfo.ConvertTime(groups[0].DeletedUtc, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), deletedSong.GetProperty("deleted").GetString());

        // Readable as printed (#304): the offset's '+' is itself, and nothing is \u-escaped.
        var deletedText = TimeZoneInfo.ConvertTime(groups[0].DeletedUtc, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        Assert.Contains('+', deletedText);
        Assert.Contains($"\"deleted\": \"{deletedText}\"", json.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json.Output, StringComparison.Ordinal);
        Assert.True(deletedSong.TryGetProperty("prunes", out _));
        var contents = deletedSong.GetProperty("contents").EnumerateArray().ToDictionary(static count => count.GetProperty("recordType").GetString()!, static count => count.GetProperty("count").GetInt32());
        Assert.Equal(1, contents[RetainedRecordTypes.Song]);
        Assert.Equal(1, contents[RetainedRecordTypes.Version]);
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("shortcode").ValueKind);

        // Nothing retained beyond the label is ever written: the history entry's lyrics included.
        foreach (var text in (string[])[run.Output, run.Error, json.Output, json.Error])
        {
            Assert.DoesNotContain(LyricsSentinel, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Another entry", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithNothingDeletedTheListingSaysSo()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var run = await RunAsync(factory.DataPath, ["list-deleted"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(DeletedCommands.NothingDeleted, run.Output.Trim());
        var json = await RunAsync(factory.DataPath, ["list-deleted", "--json"]);
        Assert.Equal("[]", json.Output.Trim());
    }

    [Fact]
    public async Task ASongRestoredByItsShortcodeIsBackInTheRunningAppWithItsVersionsAndShortcode()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Mistake");
        var versionId = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        await AddVersionAsync(client, "n8-1", "2");
        await SnapshotAsync(client, versionId, LyricsSentinel);
        var history = await HistoryAsync(client, versionId);
        var before = await SongAsync(client, "n8-1");
        await DeleteSongAsync(client, "n8-1", "Mistake");
        using (var gone = await client.GetAsync(SongApi.Song("n8-1")))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        var run = await RunAsync(factory.DataPath, ["restore-deleted", "n8-1"]);

        Assert.True(run.ExitCode == 0, run.Error);
        Assert.StartsWith("Restored Song n8-1 (Mistake) (group ", run.Output, StringComparison.Ordinal);
        Assert.Contains("Put back:\n  Song: 1\n", run.Output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("  Version: 2\n", run.Output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("  history entry: ", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Left out", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(LyricsSentinel, run.Output + run.Error, StringComparison.Ordinal);

        // The running app reads it at once: the same ID, shortcode, Versions, and history; a higher revision.
        var after = await SongAsync(client, "n8-1");
        Assert.Equal(before.GetProperty("id").GetGuid(), after.GetProperty("id").GetGuid());
        Assert.Equal("n8-1", after.GetProperty("shortcode").GetString());
        Assert.True(after.GetProperty("revision").GetInt32() > before.GetProperty("revision").GetInt32());
        Assert.Equal(["1", "2"], await VersionNumbersAsync(client, "n8-1"));
        Assert.Equal(history, await HistoryAsync(client, versionId));
        Assert.Empty(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));

        // Restoring it again finds nothing.
        var again = await RunAsync(factory.DataPath, ["restore-deleted", "n8-1"]);
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("Nothing deleted as n8-1 is in retention", again.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionRestoredByItsShortcodeKeepsItsNumber()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Versions");
        await AddVersionAsync(client, "n8-1", "2");
        await AddVersionAsync(client, "n8-1", "3");
        await DeleteVersionAsync(client, "n8-1-v2");
        Assert.Equal(["1", "3"], await VersionNumbersAsync(client, "n8-1"));

        var run = await RunAsync(factory.DataPath, ["restore-deleted", "N8-1-V2", "--json"]);

        Assert.True(run.ExitCode == 0, run.Error);
        var report = JsonDocument.Parse(run.Output).RootElement;
        Assert.Equal("n8-1-v2", report.GetProperty("restored").GetProperty("shortcode").GetString());
        Assert.Contains($"\"deleted\": \"{report.GetProperty("restored").GetProperty("deleted").GetString()}\"", run.Output, StringComparison.Ordinal);
        Assert.Contains("+00:00\"", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", run.Output, StringComparison.Ordinal);
        Assert.Contains(report.GetProperty("putBack").EnumerateArray(), static count => count.GetProperty("recordType").GetString() == RetainedRecordTypes.Version && count.GetProperty("count").GetInt32() == 1);
        Assert.Equal(["1", "2", "3"], await VersionNumbersAsync(client, "n8-1"));
        var version = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v2", UriKind.Relative)));
        Assert.Equal("n8-1-v2", version.GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task AnAlbumAnArtistAndAHistoryEntryAreRestoredByTheirGroupIds()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Keeper");
        var versionId = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        var entry = await SnapshotAsync(client, versionId, "Kept lyrics");
        var album = await CreateAsync(client, "albums", "title", "Back Album");
        var artist = await CreateAsync(client, "artists", "name", "Back Artist");
        await DeleteEntryAsync(client, versionId, entry);
        await DeleteAlbumAsync(client, album);
        await DeleteArtistAsync(client, artist);
        var groups = await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None));
        var listed = JsonDocument.Parse((await RunAsync(factory.DataPath, ["list-deleted", "--json"])).Output).RootElement.EnumerateArray()
            .ToDictionary(static item => item.GetProperty("kind").GetString()!, static item => item.GetProperty("shortId").GetString()!);

        // The Album by the start of its ID the listing shows, the Artist by its whole ID (any letter case).
        var albumRun = await RunAsync(factory.DataPath, ["restore-deleted", listed[RetainedRecordTypes.Album]]);
        Assert.True(albumRun.ExitCode == 0, albumRun.Error);
        Assert.StartsWith("Restored Album Back Album", albumRun.Output, StringComparison.Ordinal);
        using (var read = await client.GetAsync(new Uri($"/api/v1/albums/{album}", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        var artistGroup = groups.Single(static group => group.Kind == RetainedRecordTypes.Artist);
        var artistRun = await RunAsync(factory.DataPath, ["restore-deleted", artistGroup.Id.ToString().ToUpperInvariant()]);
        Assert.True(artistRun.ExitCode == 0, artistRun.Error);
        Assert.Contains("  Artist: 1", artistRun.Output, StringComparison.Ordinal);
        using (var read = await client.GetAsync(new Uri($"/api/v1/artists/{artist}", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        var entryRun = await RunAsync(factory.DataPath, ["restore-deleted", listed[RetainedRecordTypes.EditorSnapshot]]);
        Assert.True(entryRun.ExitCode == 0, entryRun.Error);
        Assert.Contains("  history entry: 1", entryRun.Output, StringComparison.Ordinal);
        Assert.Contains(entry, await HistoryAsync(client, versionId));
        Assert.DoesNotContain("Kept lyrics", entryRun.Output + entryRun.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionWhoseSongWasDeletedSinceIsRefusedNamingTheSongAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Parent");
        await AddVersionAsync(client, "n8-1", "2");
        await DeleteVersionAsync(client, "n8-1-v2");
        await DeleteSongAsync(client, "n8-1", "Parent");
        var before = Snapshot(factory);

        var run = await RunAsync(factory.DataPath, ["restore-deleted", "n8-1-v2"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("The Song this Version belongs to no longer exists.", run.Error, StringComparison.Ordinal);
        Assert.Contains("It was deleted as part of Song n8-1 (Parent): restore that first with n8tracks restore-deleted n8-1", run.Error, StringComparison.Ordinal);

        // The suggested command ends its line, so it can be copied whole; "Nothing was changed." is a line of its own (#303).
        Assert.Equal(["n8tracks restore-deleted n8-1", "Nothing was changed."], LastLines(run.Error));
        Assert.Equal(before, Snapshot(factory));
    }

    [Fact]
    public async Task AVersionDeletedWithItsSongIsRefusedAloneNamingTheGroupToRestore()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Whole");
        await AddVersionAsync(client, "n8-1", "2");
        await DeleteSongAsync(client, "n8-1", "Whole");
        var before = Snapshot(factory);

        var run = await RunAsync(factory.DataPath, ["restore-deleted", "n8-1-v2"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("n8-1-v2 was deleted as part of Song n8-1 (Whole), and comes back only with it. Restore that: n8tracks restore-deleted n8-1", run.Error, StringComparison.Ordinal);
        Assert.Equal(["n8tracks restore-deleted n8-1", "Nothing was changed."], LastLines(run.Error));
        Assert.Equal(before, Snapshot(factory));

        // A Generation of one of its Versions too.
        var generation = await RunAsync(factory.DataPath, ["restore-deleted", "n8-1-v1-g1"]);
        Assert.Equal(1, generation.ExitCode);
        Assert.Contains("n8-1-v1-g1 was deleted as part of Song n8-1 (Whole)", generation.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("n8-99", "Nothing deleted as n8-99 is in retention")]
    [InlineData("n8-x", "n8-x is not a shortcode")]
    [InlineData("0192abc", "at least its first 8 characters")]
    [InlineData("not an id at all", "Give a shortcode such as n8-3")]
    [InlineData("ffffffff", "No deleted group's ID starts with ffffffff")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", "No deleted group has the ID ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public async Task AnUnknownReferenceExitsOneAndChangesNothing(string reference, string message)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Bystander");
        await DeleteSongAsync(client, "n8-1", "Bystander");
        var before = Snapshot(factory);

        var run = await RunAsync(factory.DataPath, ["restore-deleted", reference]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(message, run.Error, StringComparison.Ordinal);
        Assert.Equal("Nothing was changed.", LastLines(run.Error)[1]);
        Assert.Equal(before, Snapshot(factory));
    }

    /// <summary>
    /// The end of the last two lines of <paramref name="error"/>: the last line whole, and the line
    /// before it from "n8tracks " on when it holds a command, otherwise whole.
    /// </summary>
    private static string[] LastLines(string error)
    {
        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line.TrimEnd('\r')).ToArray();
        Assert.True(lines.Length >= 2, error);
        var message = lines[^2];
        var command = message.LastIndexOf("n8tracks ", StringComparison.Ordinal);
        return [command < 0 ? message : message[command..], lines[^1]];
    }

    [Fact]
    public async Task RestoredArtworkGoesBackOnItsOwnerAndTheArtworkItHadGoesIntoRetention()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Pictured");
        var songId = song.GetProperty("id").GetGuid();
        var (first, second) = await ReplaceArtworkAsync(client, songId);
        var revision = (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32();
        var group = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        Assert.Contains("Artwork", (await RunAsync(factory.DataPath, ["list-deleted"])).Output, StringComparison.Ordinal);

        var run = await RunAsync(factory.DataPath, ["restore-deleted", group.Id.ToString()]);

        Assert.True(run.ExitCode == 0, run.Error);
        Assert.Contains("  artwork: 1", run.Output, StringComparison.Ordinal);
        Assert.Contains("The artwork the Song had until now was deleted in its place", run.Output, StringComparison.Ordinal);
        var after = await SongAsync(client, "n8-1");
        Assert.Equal(first, after.GetProperty("artwork").GetProperty("assetId").GetGuid());
        Assert.True(after.GetProperty("revision").GetInt32() > revision);
        var retired = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        Assert.Equal(RetainedRecordTypes.ArtworkAttachment, retired.Kind);
        Assert.Equal(group.Label, retired.Label);
        Assert.Contains(retired.Files, path => path.Contains(Hash(second.Content), StringComparison.Ordinal));

        // And back again: the second image returns the same way.
        var back = await RunAsync(factory.DataPath, ["restore-deleted", retired.Id.ToString()]);
        Assert.True(back.ExitCode == 0, back.Error);
        Assert.Equal(second.Id, (await SongAsync(client, "n8-1")).GetProperty("artwork").GetProperty("assetId").GetGuid());
    }

    [Fact]
    public async Task ArtworkWhoseOwnerWasDeletedIsRefusedNamingTheOwnersGroup()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Framed");
        await ReplaceArtworkAsync(client, song.GetProperty("id").GetGuid());
        var artwork = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        await DeleteSongAsync(client, "n8-1", "Framed");
        var before = Snapshot(factory);

        var run = await RunAsync(factory.DataPath, ["restore-deleted", artwork.Id.ToString()]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("The Song this artwork belongs to no longer exists.", run.Error, StringComparison.Ordinal);
        Assert.Contains("restore-deleted n8-1", run.Error, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(factory));
    }

    [Fact]
    public async Task WhatIsGoneIsLeftOutAndReportedAndASongWhoseStateWasDeletedGetsTheFirstVisibleOne()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Survivor");
        var songId = song.GetProperty("id").GetGuid();
        var state = Upper(Guid.CreateVersion7());
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO workflow_states (id, name, name_key, colour, position, hidden) VALUES ('{state}', 'Doomed state', 'DOOMED STATE', 'red', 99, 0);");
        await SongApi.EditAsync(client, songId.ToString(), (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32(), $$"""{"stateId":"{{state}}"}""");
        var playlist = await CreateAsync(client, "playlists", "title", "Short-lived");
        await SendExpectingAsync(client, HttpMethod.Post, $"/api/v1/playlists/{playlist}/songs", 1, $$"""{"songId":"{{songId}}"}""", HttpStatusCode.OK);

        await DeleteSongAsync(client, "n8-1", "Survivor");
        TestDatabase.Execute(factory.DataPath, $"DELETE FROM workflow_states WHERE id = '{state}'; DELETE FROM playlists WHERE id = '{Upper(playlist)}';");

        var run = await RunAsync(factory.DataPath, ["restore-deleted", "n8-1"]);

        Assert.True(run.ExitCode == 0, run.Error);
        var report = run.Output.ReplaceLineEndings("\n");
        Assert.Contains("Left out or changed:\n", report, StringComparison.Ordinal);
        Assert.Contains("  The Song's workflow state no longer exists, so it was restored in the first visible state.", report, StringComparison.Ordinal);
        Assert.Contains("  A membership of a Playlist was not restored: the Playlist it belongs to no longer exists.", report, StringComparison.Ordinal);
        Assert.DoesNotContain("membership of a Playlist: 1", report, StringComparison.Ordinal);
        var first = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM workflow_states WHERE hidden = 0 ORDER BY position LIMIT 1;");
        Assert.Equal(first, Upper((await SongAsync(client, "n8-1")).GetProperty("state").GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task ARestoreInALibraryWithLocalFilesSaysAssociationsAreNotRestored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Has files");
        var id = Upper(Guid.CreateVersion7());
        TestDatabase.Execute(
            factory.DataPath,
            "INSERT INTO audio_files (id, path, file_name, format, size_bytes, modified_utc, first_seen_utc, last_seen_utc, status, metadata_readable) "
            + $"VALUES ('{id}', 'a.mp3', 'a.mp3', 'mp3', 1, '2026-10-01T00:00:00.000Z', '2026-10-01T00:00:00.000Z', '2026-10-01T00:00:00.000Z', 'available', 0);");
        await DeleteSongAsync(client, "n8-1", "Has files");

        var run = await RunAsync(factory.DataPath, ["restore-deleted", "n8-1"]);

        Assert.True(run.ExitCode == 0, run.Error);
        var report = run.Output.ReplaceLineEndings("\n");
        Assert.Contains("Left out or changed:\n", report, StringComparison.Ordinal);
        Assert.Contains("  " + AudioFileLifecycle.RestoreNote + "\n", report, StringComparison.Ordinal);
        Assert.Contains("next media scan associates again", AudioFileLifecycle.RestoreNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupPastThirtyDaysIsListedOnlyWithAllAndRestoredOnlyByItsId()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Old");
        await DeleteSongAsync(client, "n8-1", "Old");
        TestDatabase.Execute(factory.DataPath, "UPDATE retention_groups SET deleted_utc = '2020-01-01T00:00:00.000Z', prune_after_utc = '2020-01-31T00:00:00.000Z';");
        var group = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));

        Assert.Equal(DeletedCommands.NothingDeleted, (await RunAsync(factory.DataPath, ["list-deleted"])).Output.Trim());
        var all = await RunAsync(factory.DataPath, ["list-deleted", "--all"]);
        Assert.Contains("Song n8-1 (Old)", all.Output, StringComparison.Ordinal);
        Assert.Contains("2020-01-31 00:00", all.Output, StringComparison.Ordinal);

        var byShortcode = await RunAsync(factory.DataPath, ["restore-deleted", "n8-1"]);
        Assert.Equal(1, byShortcode.ExitCode);
        Assert.Contains("list-deleted --all", byShortcode.Error, StringComparison.Ordinal);

        var byId = await RunAsync(factory.DataPath, ["restore-deleted", group.Id.ToString()]);
        Assert.True(byId.ExitCode == 0, byId.Error);
        Assert.Equal("Old", (await SongAsync(client, "n8-1")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task AShortcodeDeletedTwiceNamesTheNewestGroup()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Twice");
        await DeleteSongAsync(client, "n8-1", "Twice");
        Assert.Equal(0, (await RunAsync(factory.DataPath, ["restore-deleted", "n8-1"])).ExitCode);
        await SongApi.EditAsync(client, "n8-1", (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32(), """{"title":"Twice again"}""");
        await DeleteSongAsync(client, "n8-1", "Twice again");

        // An older group of the same shortcode, as a deletion before a restore left it.
        TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO retention_groups (id, kind, label, shortcode, deleted_utc, prune_after_utc, files) VALUES ('{Upper(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-1)))}', 'song', 'Song n8-1 (Older)', 'n8-1', '{DateTimeOffset.UtcNow.AddDays(-1).UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}', '{DateTimeOffset.UtcNow.AddDays(29).UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}', '[]');");

        var run = await RunAsync(factory.DataPath, ["restore-deleted", "n8-1"]);

        Assert.True(run.ExitCode == 0, run.Error);
        Assert.StartsWith("Restored Song n8-1 (Twice again)", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeforeSetupBothCommandsRefuse()
    {
        using var factory = SongApi.Host();
        using (var client = factory.CreateClient())
        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        foreach (var args in (string[][])[["list-deleted"], ["restore-deleted", "n8-1"]])
        {
            var run = await RunAsync(factory.DataPath, args);
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("Setup has never been completed", run.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ADatabaseOfAnotherSchemaVersionIsRefusedAndNeverMigrated()
    {
        using var data = new TemporaryDirectory();
        using (var factory = TestDatabase.Host(data.Path))
        using (var client = factory.CreateClient())
        {
            await SetupApi.CompleteAsync(client);
        }

        var last = TestDatabase.History(data.Path)[^1].Split('|')[0];
        TestDatabase.Execute(data.Path, $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{last}';");
        var history = TestDatabase.History(data.Path);

        var run = await RunAsync(data.Path, ["list-deleted"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("does not match this version of n8Tracks. Start the application first", run.Error, StringComparison.Ordinal);
        Assert.Equal(history, TestDatabase.History(data.Path));
    }

    [Fact]
    public async Task WithoutADatabaseItRefusesAndCreatesNothing()
    {
        using var data = new TemporaryDirectory();

        var run = await RunAsync(data.Path, ["restore-deleted", "n8-1"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Setup has never been completed", run.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    [Theory]
    [InlineData("list-deleted", "--recent")]
    [InlineData("list-deleted", "--all", "--all")]
    [InlineData("restore-deleted")]
    [InlineData("restore-deleted", "n8-1", "n8-2")]
    [InlineData("restore-deleted", "--force")]
    public async Task UnknownArgumentsAreRefusedWithTheUsage(params string[] args)
    {
        using var data = new TemporaryDirectory();

        var run = await RunAsync(data.Path, args);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains($"Usage: n8tracks {args[0]}", run.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    [Fact]
    public async Task AnUnknownTimeZoneIsReported()
    {
        using var data = new TemporaryDirectory();

        var run = await RunAsync(data.Path, ["list-deleted"], "Mars/Olympus");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Invalid configuration: TZ", run.Error, StringComparison.Ordinal);
    }

    private static string KindOf(string line) =>
        line.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[1];

    /// <summary>Everything a refused restore must leave as it was: the retention tables and the live catalog's IDs and revisions.</summary>
    private static List<string> Snapshot(N8TracksApiFactory factory)
    {
        var rows = new List<string>();
        foreach (var sql in (string[])[
            "SELECT id || '|' || kind || '|' || label FROM retention_groups ORDER BY id;",
            "SELECT group_id || '|' || position || '|' || record_type || '|' || original_id FROM retention_records ORDER BY group_id, position;",
            "SELECT id || '|' || revision FROM songs ORDER BY id;",
            "SELECT id || '|' || revision || '|' || number FROM versions ORDER BY id;",
            "SELECT id || '|' || owner_id || '|' || asset_id FROM artwork_attachments ORDER BY id;",
            "SELECT song_id || '|' || number FROM used_version_numbers ORDER BY song_id, number;"])
        {
            rows.AddRange(TestDatabase.Rows(factory.DataPath, sql));
        }

        return rows;
    }

    private static async Task<JsonElement> SongAsync(HttpClient client, string reference) =>
        await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(reference)));

    private static async Task<List<string>> VersionNumbersAsync(HttpClient client, string song)
    {
        var list = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/songs/{song}/versions", UriKind.Relative)));
        return [.. list.GetProperty("items").EnumerateArray().Select(static version => version.GetProperty("number").GetString()!).Order(StringComparer.Ordinal)];
    }

    private static async Task AddVersionAsync(HttpClient client, string song, string number)
    {
        using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/songs/{song}/versions", UriKind.Relative), $$"""{"sourceVersionId":"{{song}}-v1","number":"{{number}}"}""");
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    /// <summary>Creates a record of <paramref name="collection"/> through the API with one text field; its ID.</summary>
    private static async Task<Guid> CreateAsync(HttpClient client, string collection, string field, string value)
    {
        using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/{collection}", UriKind.Relative), JsonSerializer.Serialize(new Dictionary<string, string> { [field] = value }));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(created)).GetProperty("id").GetGuid();
    }

    /// <summary>Attaches one image to the Song and then replaces it with another, so the first is retained; both assets.</summary>
    private static async Task<(Guid First, (Guid Id, byte[] Content) Second)> ReplaceArtworkAsync(HttpClient client, Guid songId)
    {
        var firstImage = Assets.ArtworkImages.Solid(SKEncodedImageFormat.Png, 300, 300, SKColors.Red);
        var secondImage = Assets.ArtworkImages.Solid(SKEncodedImageFormat.Png, 300, 300, SKColors.Blue);
        var first = IdOf(await StoreAsync(client, firstImage));
        var second = IdOf(await StoreAsync(client, secondImage));
        foreach (var asset in (Guid[])[first, second])
        {
            var revision = (await SongAsync(client, songId.ToString())).GetProperty("revision").GetInt32();
            await SongApi.EditAsync(client, songId.ToString(), revision, $$"""{"artworkAssetId":"{{asset}}"}""");
        }

        return (first, (second, secondImage));
    }

    private static async Task DeleteSongAsync(HttpClient client, string reference, string title)
    {
        var revision = (await SongAsync(client, reference)).GetProperty("revision").GetInt32();
        await SendExpectingAsync(client, HttpMethod.Delete, $"/api/v1/songs/{reference}", revision, JsonSerializer.Serialize(new { confirmTitle = title }), HttpStatusCode.NoContent);
    }

    private static async Task DeleteVersionAsync(HttpClient client, string version)
    {
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/versions/{version}", UriKind.Relative)))).GetProperty("revision").GetInt32();
        await SendExpectingAsync(client, HttpMethod.Delete, $"/api/v1/versions/{version}", revision, null, HttpStatusCode.OK);
    }

    private static async Task DeleteEntryAsync(HttpClient client, Guid version, Guid entry)
    {
        using var response = await SessionApi.SendAsync(client, HttpMethod.Delete, new Uri($"/api/v1/versions/{version}/snapshots/{entry}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task DeleteAlbumAsync(HttpClient client, Guid album)
    {
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/albums/{album}", UriKind.Relative)))).GetProperty("revision").GetInt32();
        await SendExpectingAsync(client, HttpMethod.Delete, $"/api/v1/albums/{album}", revision, null, HttpStatusCode.NoContent);
    }

    private static async Task DeleteArtistAsync(HttpClient client, Guid artist)
    {
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/artists/{artist}", UriKind.Relative)))).GetProperty("revision").GetInt32();
        await SendExpectingAsync(client, HttpMethod.Delete, $"/api/v1/artists/{artist}", revision, null, HttpStatusCode.NoContent);
    }

    private static async Task SendExpectingAsync(HttpClient client, HttpMethod method, string path, int revision, string? json, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == expected, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<CommandRun> RunAsync(string dataPath, string[] args, string? timeZone = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.DataPath] = dataPath };
        if (timeZone is not null)
        {
            variables[EnvironmentOptionsLoader.TimeZone] = timeZone;
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await Program.RunAsync(args, new EnvironmentSnapshot(variables, Path.GetTempPath()), output, new CommandConsole(TextReader.Null, error, isTerminal: false), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        return new CommandRun(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CommandRun(int ExitCode, string Output, string Error);
}
