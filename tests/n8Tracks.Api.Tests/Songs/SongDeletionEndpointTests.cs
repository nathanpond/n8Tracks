using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Retention;
using n8Tracks.Domain.Catalog;
using SkiaSharp;
using static n8Tracks.Api.Tests.Assets.ArtworkApi;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// Deleting a Song (#102): <c>GET /api/v1/songs/{reference}/deletion-impact</c> and
/// <c>DELETE /api/v1/songs/{reference}</c>, both session only. The Song, its Versions, Generations,
/// history, credits, assignments, release details, and artwork go into one retention group; its Album
/// and Playlist memberships and relationships go with it, leaving the Albums, Playlists, and other
/// Songs otherwise as they were; its shortcode is never reused and resolves as deleted; and restoring
/// the group puts it all back, memberships and relationships where the other side still exists.
/// </summary>
public sealed class SongDeletionEndpointTests
{
    private const string Isrc = "USRC17607839";

    [Fact]
    public async Task TheImpactCountsEverythingThatGoesWithTheSongAndOnlyThat()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var fixture = await RichSongAsync(factory, client, clock);

        var impact = await ImpactAsync(client, "n8-1");
        Assert.Equal(fixture.SongId, impact.GetProperty("id").GetGuid());
        Assert.Equal("n8-1", impact.GetProperty("shortcode").GetString());
        Assert.Equal("Doomed", impact.GetProperty("title").GetString());

        // Versions 1, 2 (with the Generation), and 3 (archived); 4 was deleted on its own and is a placeholder, not counted.
        Assert.Equal(3, impact.GetProperty("versionCount").GetInt32());
        Assert.Equal(1, impact.GetProperty("generationCount").GetInt32());
        Assert.Equal(1, impact.GetProperty("artworkCount").GetInt32());
        Assert.Equal(1, impact.GetProperty("albumCount").GetInt32());
        Assert.Equal(2, impact.GetProperty("playlistCount").GetInt32());
        Assert.Equal(2, impact.GetProperty("relationshipCount").GetInt32());
        Assert.Equal(0, impact.GetProperty("audioFileCount").GetInt32());
        Assert.True(impact.GetProperty("titleRequired").GetBoolean());
        Assert.Equal((await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32(), impact.GetProperty("revision").GetInt32());

        // By ID too, and session only, no-store.
        using var byId = await client.GetAsync(new Uri($"/api/v1/songs/{fixture.SongId}/deletion-impact", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Contains("no-store", byId.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        // Complement: a new Song with one empty Version needs no typed title, and counts nothing else.
        await SongApi.CreateAsync(client, "Plain");
        var plain = await ImpactAsync(client, "n8-5");
        Assert.False(plain.GetProperty("titleRequired").GetBoolean());
        Assert.Equal(1, plain.GetProperty("versionCount").GetInt32());
        foreach (var count in (string[])["generationCount", "artworkCount", "albumCount", "playlistCount", "relationshipCount", "audioFileCount"])
        {
            Assert.Equal(0, plain.GetProperty(count).GetInt32());
        }

        using var missing = await client.GetAsync(new Uri("/api/v1/songs/n8-99/deletion-impact", UriKind.Relative));
        await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task DeletingASongRetainsItAsOneGroupAndLeavesItsNeighboursOtherwiseUntouched()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var fixture = await RichSongAsync(factory, client, clock);
        var albumBefore = await AlbumAsync(client, fixture.AlbumId);
        var playlistBefore = await PlaylistAsync(client, fixture.PlaylistIds[0]);
        var neighbourBefore = await SongAsync(client, "n8-2");
        var otherBefore = await SongAsync(client, "n8-3");
        var groupsBefore = (await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None))).Count;
        var revision = (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32();

        clock.Advance(TimeSpan.FromMinutes(1));
        using (var deleted = await DeleteAsync(client, "n8-1", revision, """{"confirmTitle":"  Doomed  "}"""))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
            Assert.Contains("no-store", deleted.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        }

        // One new group for the Song, under its shortcode; Version 4's own group from before is kept.
        var groups = await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None));
        Assert.Equal(groupsBefore + 1, groups.Count);
        var group = groups[0];
        Assert.Equal(RetainedRecordTypes.Song, group.Kind);
        Assert.Equal("Song n8-1 (Doomed)", group.Label);
        Assert.Equal("n8-1", group.Shortcode);
        Assert.Contains(groups, static other => other.Shortcode == "n8-1-v4" && other.Kind == RetainedRecordTypes.Version);
        Assert.Equal(Upper(fixture.SongId), group.Records[0].OriginalId);
        var types = group.Records.Select(static record => record.RecordType).ToHashSet(StringComparer.Ordinal);
        foreach (var type in (string[])[
            RetainedRecordTypes.Song, RetainedRecordTypes.Version, RetainedRecordTypes.Generation, RetainedRecordTypes.EditorSnapshot,
            RetainedRecordTypes.ArtworkAttachment, RetainedRecordTypes.UsedVersionNumber, RetainedRecordTypes.SongLink, RetainedRecordTypes.SongGenre,
            RetainedRecordTypes.SongTag, RetainedRecordTypes.SongCredit, RetainedRecordTypes.AlbumTrack, RetainedRecordTypes.PlaylistEntry,
            RetainedRecordTypes.SongRelationship])
        {
            Assert.True(types.Contains(type), $"The group holds no {type}.");
        }

        Assert.Equal(3, group.Records.Count(static record => record.RecordType == RetainedRecordTypes.Version));
        Assert.Equal(4, group.Records.Count(static record => record.RecordType == RetainedRecordTypes.UsedVersionNumber));
        Assert.Equal(2, group.Records.Count(static record => record.RecordType == RetainedRecordTypes.PlaylistEntry));
        Assert.NotEmpty(group.Files);

        // Nothing of the Song is live.
        var id = Upper(fixture.SongId);
        foreach (var (table, column) in OwnTables)
        {
            Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM {table} WHERE {column} = '{id}';"));
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM song_relationships WHERE to_song_id = '{id}';"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM artwork_attachments WHERE owner_id = '{id}';"));

        // The Album keeps its other tracks with their numbers (2 is a gap now), one revision on.
        var album = await AlbumAsync(client, fixture.AlbumId);
        Assert.Equal([("n8-2", 1, 1), ("n8-3", 1, 3)], Tracks(album));
        Assert.Equal(albumBefore.GetProperty("revision").GetInt32() + 1, album.GetProperty("revision").GetInt32());
        Assert.Equal(albumBefore.GetProperty("title").GetString(), album.GetProperty("title").GetString());
        Assert.Equal(clock.GetUtcNow().UtcDateTime, album.GetProperty("updatedAt").GetDateTime());

        // Each Playlist keeps its other Songs in order, one revision on; the one that held only it is empty.
        var playlist = await PlaylistAsync(client, fixture.PlaylistIds[0]);
        Assert.Equal(["n8-2", "n8-3"], PlaylistSongs(playlist));
        Assert.Equal(playlistBefore.GetProperty("revision").GetInt32() + 1, playlist.GetProperty("revision").GetInt32());
        Assert.Empty(PlaylistSongs(await PlaylistAsync(client, fixture.PlaylistIds[1])));

        // The related Songs lose only that relationship (theirs with each other stays), one revision on.
        var neighbour = await SongAsync(client, "n8-2");
        var other = await SongAsync(client, "n8-3");
        Assert.Equal(neighbourBefore.GetProperty("revision").GetInt32() + 1, neighbour.GetProperty("revision").GetInt32());
        Assert.Equal(otherBefore.GetProperty("revision").GetInt32() + 1, other.GetProperty("revision").GetInt32());
        Assert.Equal(["n8-3"], RelatedShortcodes(neighbour));
        Assert.Equal(["n8-2"], RelatedShortcodes(other));
        Assert.Equal(neighbourBefore.GetProperty("title").GetString(), neighbour.GetProperty("title").GetString());

        // No read shows it: lists, search, the same-title and same-ISRC checks, Genre, Tag, and Artist counts.
        Assert.Equal(["n8-2", "n8-3", "n8-4"], SongApi.Shortcodes(await SongApi.ListAsync(client)).Order(StringComparer.Ordinal));
        Assert.Empty(SongApi.Shortcodes(await SongApi.ListAsync(client, "q=Doomed")));
        Assert.Empty(SongApi.Shortcodes(await SongApi.ListAsync(client, "title=doomed")));
        Assert.Empty(neighbour.GetProperty("warnings").EnumerateArray());
        Assert.Equal(0, await CountAsync(client, "genres", fixture.GenreId));
        Assert.Equal(0, await CountAsync(client, "tags", fixture.TagId));
        var artist = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/artists/{fixture.ArtistId}", UriKind.Relative)));
        Assert.Equal(0, artist.GetProperty("songCount").GetInt32());

        // Its Genre and Tag are used by no Song now: each is deleted without a choice about Songs.
        foreach (var (collection, itemId) in (ValueTuple<string, Guid>[])[("genres", fixture.GenreId), ("tags", fixture.TagId)])
        {
            var revisionOf = (await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/{collection}", UriKind.Relative))))
                .GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == itemId).GetProperty("revision").GetInt32();
            using var unused = await SendAsync(client, HttpMethod.Delete, $"/api/v1/{collection}/{itemId}", revisionOf);
            Assert.True(unused.IsSuccessStatusCode, $"{collection}: {await unused.Content.ReadAsStringAsync()}");
        }

        // Reads of it say it was deleted, by shortcode and by ID.
        foreach (var reference in (string[])["n8-1", "N8-1", fixture.SongId.ToString()])
        {
            using var read = await client.GetAsync(SongApi.Song(reference));
            var problem = await SetupApi.ProblemAsync(read, HttpStatusCode.NotFound, SongDeletionEndpoints.DeletedCode);
            Assert.Equal("Doomed", problem.GetProperty("title").GetString());
            Assert.Equal("n8-1", problem.GetProperty("shortcode").GetString());
            Assert.Equal(fixture.SongId, problem.GetProperty("songId").GetGuid());
            Assert.Equal(clock.GetUtcNow().UtcDateTime, problem.GetProperty("deletedAt").GetDateTime());
        }

        using (var impact = await client.GetAsync(new Uri("/api/v1/songs/n8-1/deletion-impact", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(impact, HttpStatusCode.NotFound, SongDeletionEndpoints.DeletedCode);
        }

        using (var again = await DeleteAsync(client, "n8-1", revision))
        {
            await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, SongDeletionEndpoints.DeletedCode);
        }

        // The shortcode is never given to another Song.
        var next = await SongApi.CreateAsync(client, "Afterwards");
        Assert.Equal("n8-5", next.GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task ItsShortcodeAndItsVersionsAndGenerationsShortcodesResolveAsDeletedForThirtyDays()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var fixture = await RichSongAsync(factory, client, clock);
        var version2 = await ResolveAsync(client, "n8-1-v2");
        var generation = await ResolveAsync(client, "n8-1-v2-g1");
        await DeleteAsync(client, "n8-1", "Doomed");

        var song = await ResolveAsync(client, "n8-1");
        Assert.Equal("song", song.GetProperty("entityType").GetString());
        Assert.Equal("deleted", song.GetProperty("status").GetString());
        Assert.Equal(fixture.SongId, song.GetProperty("id").GetGuid());
        Assert.Equal("deleted", (await ResolveAsync(client, fixture.SongId.ToString())).GetProperty("status").GetString());

        // A Version deleted with it, by shortcode and by ID.
        foreach (var reference in (string[])["n8-1-v2", "N8-1-V2", version2.GetProperty("id").GetGuid().ToString()])
        {
            var version = await ResolveAsync(client, reference);
            Assert.Equal("version", version.GetProperty("entityType").GetString());
            Assert.Equal("deleted", version.GetProperty("status").GetString());
            Assert.Equal("n8-1-v2", version.GetProperty("shortcode").GetString());
            Assert.Equal(version2.GetProperty("id").GetGuid(), version.GetProperty("id").GetGuid());
            Assert.Equal("n8-1", version.GetProperty("song").GetProperty("shortcode").GetString());
            Assert.Equal(fixture.SongId, version.GetProperty("song").GetProperty("id").GetGuid());
        }

        // An archived one, and one deleted on its own before its Song.
        Assert.Equal("deleted", (await ResolveAsync(client, "n8-1-v3")).GetProperty("status").GetString());
        Assert.Equal("deleted", (await ResolveAsync(client, "n8-1-v4")).GetProperty("status").GetString());

        // Its Generation, by shortcode and by ID.
        foreach (var reference in (string[])["n8-1-v2-g1", generation.GetProperty("id").GetGuid().ToString()])
        {
            var resolved = await ResolveAsync(client, reference);
            Assert.Equal("generation", resolved.GetProperty("entityType").GetString());
            Assert.Equal("deleted", resolved.GetProperty("status").GetString());
            Assert.Equal("n8-1-v2-g1", resolved.GetProperty("shortcode").GetString());
            Assert.Equal("n8-1-v2", resolved.GetProperty("version").GetProperty("shortcode").GetString());
            Assert.Equal("n8-1", resolved.GetProperty("song").GetProperty("shortcode").GetString());
        }

        // Complement: numbers it never had name nothing.
        await ExpectNoReferenceAsync(client, "n8-1-v9");
        await ExpectNoReferenceAsync(client, "n8-1-v2-g2");

        // After 30 days (before the prune has run) it is plain not found.
        clock.Advance(RetentionService.RetentionPeriod + TimeSpan.FromSeconds(1));
        using (var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signIn.StatusCode);
        }

        await ExpectNoReferenceAsync(client, "n8-1");
        await ExpectNoReferenceAsync(client, "n8-1-v2");
        await ExpectNoReferenceAsync(client, "n8-1-v2-g1");
        using var read = await client.GetAsync(SongApi.Song("n8-1"));
        await SetupApi.ProblemAsync(read, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task ASongWithOneVersionAndNothingElseIsDeletedWithAPlainConfirmation()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Simple");

        using (var deleted = await DeleteAsync(client, "n8-1", song.GetProperty("revision").GetInt32()))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.Empty(SongApi.Shortcodes(await SongApi.ListAsync(client)));

        // A title sent anyway is not checked when none is required.
        var other = await SongApi.CreateAsync(client, "Other");
        using var withTitle = await DeleteAsync(client, "n8-2", other.GetProperty("revision").GetInt32(), """{"confirmTitle":"not its title"}""");
        Assert.Equal(HttpStatusCode.NoContent, withTitle.StatusCode);
    }

    [Fact]
    public async Task AMissingOrWrongTitleIsRefusedWhenRequiredWithTheImpactAsItIsNow()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Exact Title");
        var shortcode = song.GetProperty("shortcode").GetString()!;

        // The dialog opened on a plain confirmation; then a second Version was made elsewhere.
        Assert.False((await ImpactAsync(client, shortcode)).GetProperty("titleRequired").GetBoolean());
        await AddVersionAsync(client, shortcode, "2");
        var revision = (await SongAsync(client, shortcode)).GetProperty("revision").GetInt32();

        foreach (var body in (string?[])[null, "{}", """{"confirmTitle":null}""", """{"confirmTitle":"exact title"}""", """{"confirmTitle":"Exact  Title"}""", """{"confirmTitle":""}"""])
        {
            using var refused = await DeleteAsync(client, shortcode, revision, body);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, SongDeletionEndpoints.ConfirmationRequiredCode);
            var impact = problem.GetProperty("impact");
            Assert.True(impact.GetProperty("titleRequired").GetBoolean());
            Assert.Equal(2, impact.GetProperty("versionCount").GetInt32());
            Assert.Equal(revision, impact.GetProperty("revision").GetInt32());
        }

        // A stale revision is reported before the title.
        using (var stale = await DeleteAsync(client, shortcode, revision + 1, """{"confirmTitle":"wrong"}"""))
        {
            var conflict = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
            Assert.Equal(revision, conflict.GetProperty("current").GetProperty("revision").GetInt32());
        }

        // Nothing was deleted by any of them.
        Assert.Equal(2, (await SongAsync(client, shortcode)).GetProperty("versionCount").GetInt32());
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));

        using var confirmed = await DeleteAsync(client, shortcode, revision, """{"confirmTitle":"Exact Title"}""");
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Fact]
    public async Task MalformedDeletesAreRefusedAndChangeNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Kept");
        var revision = song.GetProperty("revision").GetInt32();

        using (var noRevision = await DeleteAsync(client, "n8-1", revision: null))
        {
            await SetupApi.ProblemAsync(noRevision, HttpStatusCode.PreconditionRequired, Revisions.RequiredCode);
        }

        using (var notJson = await DeleteAsync(client, "n8-1", revision, "confirm"))
        {
            await SetupApi.ProblemAsync(notJson, HttpStatusCode.BadRequest, ApiProblem.InvalidRequestCode);
        }

        using (var notObject = await DeleteAsync(client, "n8-1", revision, "[]"))
        {
            await SetupApi.ProblemAsync(notObject, HttpStatusCode.BadRequest, ApiProblem.InvalidRequestCode);
        }

        using (var notText = await DeleteAsync(client, "n8-1", revision, """{"confirmTitle":7}"""))
        {
            var problem = await SetupApi.ProblemAsync(notText, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty(SongDeletionEndpoints.ConfirmTitleField, out _));
        }

        foreach (var reference in (string[])["n8-99", "n8-1-v1", Guid.CreateVersion7().ToString(), "nothing"])
        {
            using var missing = await DeleteAsync(client, reference, revision);
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // Session only: a token with every scope is refused both endpoints.
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        using (var byToken = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Delete, SongApi.Song("n8-1"), token))
        {
            await SetupApi.ProblemAsync(byToken, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        using (var impactByToken = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Get, new Uri("/api/v1/songs/n8-1/deletion-impact", UriKind.Relative), token))
        {
            await SetupApi.ProblemAsync(impactByToken, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        Assert.Equal(revision, (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32());
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
    }

    [Fact]
    public async Task RestoringTheGroupPutsEverythingBackWithMembershipsAtTheEnd()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var fixture = await RichSongAsync(factory, client, clock);
        var id = Upper(fixture.SongId);
        var before = OwnRows(factory, id);
        var songBefore = await SongAsync(client, "n8-1");
        var versionsBefore = await VersionListAsync(client, "n8-1");
        var historyBefore = TestDatabase.Rows(factory.DataPath, $"SELECT id || '|' || lyrics FROM editor_revisions WHERE version_id IN (SELECT id FROM versions WHERE song_id = '{id}') ORDER BY id;");
        await DeleteAsync(client, "n8-1", "Doomed");

        // Meanwhile the Album and the first Playlist move on: a new Song joins each at the end.
        clock.Advance(TimeSpan.FromMinutes(1));
        await SongApi.CreateAsync(client, "Newcomer");
        var album = await AlbumAsync(client, fixture.AlbumId);
        await AddTrackAsync(client, fixture.AlbumId, album.GetProperty("revision").GetInt32(), "n8-5");
        var playlist = await PlaylistAsync(client, fixture.PlaylistIds[0]);
        await AddToPlaylistAsync(client, fixture.PlaylistIds[0], playlist.GetProperty("revision").GetInt32(), "n8-5");
        var albumRevision = (await AlbumAsync(client, fixture.AlbumId)).GetProperty("revision").GetInt32();
        var playlistRevision = (await PlaylistAsync(client, fixture.PlaylistIds[0])).GetProperty("revision").GetInt32();
        var neighbourRevision = (await SongAsync(client, "n8-2")).GetProperty("revision").GetInt32();

        clock.Advance(TimeSpan.FromMinutes(1));
        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, (await SongGroupAsync(factory, "n8-1")).Id));
        Assert.Empty(restored.Notes);

        // The Song and everything of its own are back as they were, revisions aside.
        Assert.Equal(before, OwnRows(factory, id));
        var song = await SongAsync(client, "n8-1");
        Assert.Equal(songBefore.GetProperty("title").GetString(), song.GetProperty("title").GetString());
        Assert.True(song.GetProperty("revision").GetInt32() > songBefore.GetProperty("revision").GetInt32());
        Assert.Equal(songBefore.GetProperty("credits").ToString(), song.GetProperty("credits").ToString());
        Assert.Equal(songBefore.GetProperty("genres").ToString(), song.GetProperty("genres").ToString());
        Assert.Equal(songBefore.GetProperty("tags").ToString(), song.GetProperty("tags").ToString());
        Assert.Equal(songBefore.GetProperty("release").ToString(), song.GetProperty("release").ToString());
        Assert.Equal(songBefore.GetProperty("artwork").ToString(), song.GetProperty("artwork").ToString());
        Assert.Equal(songBefore.GetProperty("currentVersion").ToString(), song.GetProperty("currentVersion").ToString());
        Assert.Equal(songBefore.GetProperty("state").ToString(), song.GetProperty("state").ToString());
        Assert.Equal(Numbers(versionsBefore), Numbers(await VersionListAsync(client, "n8-1")));
        Assert.Equal(historyBefore, TestDatabase.Rows(factory.DataPath, $"SELECT id || '|' || lyrics FROM editor_revisions WHERE version_id IN (SELECT id FROM versions WHERE song_id = '{id}') ORDER BY id;"));
        Assert.True((await VersionAsync(client, "n8-1-v2")).GetProperty("isFrozen").GetBoolean());
        Assert.Equal("active", (await ResolveAsync(client, "n8-1-v2-g1")).GetProperty("status").GetString());
        using (var served = await GetAsync(client, UrlOf(song.GetProperty("artwork"), "320")))
        {
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        }

        // Used numbers are the same rows: 4 (deleted on its own) is still never offered.
        Assert.Equal(["1", "2", "3", "4"], TestDatabase.Rows(factory.DataPath, $"SELECT number FROM used_version_numbers WHERE song_id = '{id}' ORDER BY number;"));
        var options = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/versions/n8-1-v1/next-numbers", UriKind.Relative)));
        Assert.DoesNotContain("4", options.GetProperty("options").EnumerateArray().Select(static option => option.GetProperty("number").GetString()));

        // Memberships come back at the end of the Album's last disc and of each Playlist, raising their revisions.
        Assert.Equal([("n8-2", 1, 1), ("n8-3", 1, 3), ("n8-5", 1, 4), ("n8-1", 1, 5)], Tracks(await AlbumAsync(client, fixture.AlbumId)));
        Assert.Equal(albumRevision + 1, (await AlbumAsync(client, fixture.AlbumId)).GetProperty("revision").GetInt32());
        Assert.Equal(["n8-2", "n8-3", "n8-5", "n8-1"], PlaylistSongs(await PlaylistAsync(client, fixture.PlaylistIds[0])));
        Assert.Equal(playlistRevision + 1, (await PlaylistAsync(client, fixture.PlaylistIds[0])).GetProperty("revision").GetInt32());
        Assert.Equal(["n8-1"], PlaylistSongs(await PlaylistAsync(client, fixture.PlaylistIds[1])));
        Assert.Equal(
            songBefore.GetProperty("albums").EnumerateArray().Select(static album => album.GetProperty("id").GetGuid()),
            song.GetProperty("albums").EnumerateArray().Select(static album => album.GetProperty("id").GetGuid()));

        // Relationships are back on both sides, and the related Songs' revisions move again.
        Assert.Equal(RelatedShortcodes(songBefore), RelatedShortcodes(song));
        Assert.Equal(["n8-1", "n8-3"], RelatedShortcodes(await SongAsync(client, "n8-2")));
        Assert.True((await SongAsync(client, "n8-2")).GetProperty("revision").GetInt32() > neighbourRevision);

        // The shortcode is the Song's again, and its earlier Version deletion can now be restored too.
        Assert.Equal("active", (await ResolveAsync(client, "n8-1")).GetProperty("status").GetString());
        var version4 = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, version4.Id));
        Assert.Equal(["1", "2", "3", "4"], Numbers(await VersionListAsync(client, "n8-1")));
    }

    [Fact]
    public async Task RestoringLeavesOutWhatIsGoneAndKeepsTheSongInAStateThatExists()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var fixture = await RichSongAsync(factory, client, clock);
        var id = Upper(fixture.SongId);

        // A state of its own that is deleted while the Song is.
        var state = Upper(Guid.CreateVersion7());
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO workflow_states (id, name, name_key, colour, position, hidden) VALUES ('{state}', 'Doomed state', 'DOOMED STATE', 'red', 99, 0);");
        var revision = (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32();
        await SongApi.EditAsync(client, fixture.SongId.ToString(), revision, $$"""{"stateId":"{{state}}"}""");
        await DeleteAsync(client, "n8-1", "Doomed");

        // Meanwhile: its state, its Genre, the Album, and the second Playlist are deleted, and so is n8-3.
        TestDatabase.Execute(
            factory.DataPath,
            $"""
            DELETE FROM workflow_states WHERE id = '{state}';
            DELETE FROM song_genres WHERE genre_id = '{Upper(fixture.GenreId)}';
            DELETE FROM genres WHERE id = '{Upper(fixture.GenreId)}';
            DELETE FROM albums WHERE id = '{Upper(fixture.AlbumId)}';
            DELETE FROM playlists WHERE id = '{Upper(fixture.PlaylistIds[1])}';
            """);
        await DeleteAsync(client, "n8-3", "Other");

        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, (await SongGroupAsync(factory, "n8-1")).Id));
        Assert.Contains(restored.Notes, static note => note.Contains("first visible state", StringComparison.Ordinal));
        Assert.Contains(restored.Notes, static note => note.StartsWith("A Genre assignment was not restored", StringComparison.Ordinal));
        Assert.Contains(restored.Notes, static note => note.StartsWith("A membership of an Album was not restored", StringComparison.Ordinal));
        Assert.Contains(restored.Notes, static note => note.StartsWith("A membership of a Playlist was not restored", StringComparison.Ordinal));
        Assert.Contains(restored.Notes, static note => note.StartsWith("A relationship was not restored", StringComparison.Ordinal));

        var song = await SongAsync(client, "n8-1");
        var first = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM workflow_states WHERE hidden = 0 ORDER BY position LIMIT 1;");
        Assert.Equal(first, Upper(song.GetProperty("state").GetProperty("id").GetGuid()));
        Assert.Empty(song.GetProperty("genres").EnumerateArray());
        Assert.Single(song.GetProperty("tags").EnumerateArray());
        Assert.Empty(song.GetProperty("albums").EnumerateArray());
        Assert.Equal([fixture.PlaylistIds[0]], song.GetProperty("playlists").EnumerateArray().Select(static playlist => playlist.GetProperty("id").GetGuid()));
        Assert.Equal(["n8-2"], RelatedShortcodes(song));
        Assert.Equal(3, song.GetProperty("versionCount").GetInt32());
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM artwork_attachments WHERE owner_id = '{id}';"));
    }

    [Fact]
    public async Task AVersionCreatedAtTheMomentItsSongIsDeletedComesBackWithIt()
    {
        // The clock does not move: Version 1 is created at the very time of the deletion, which is
        // how restoring a Version's own group recognises a blank it made; a Song's group must not.
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Instant");
        var before = OwnRows(factory, Upper(song.GetProperty("id").GetGuid()));
        await DeleteAsync(client, "n8-1", "Instant");

        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, (await SongGroupAsync(factory, "n8-1")).Id));
        Assert.Equal(before, OwnRows(factory, Upper(song.GetProperty("id").GetGuid())));
        Assert.Equal(["1"], Numbers(await VersionListAsync(client, "n8-1")));
    }

    [Fact]
    public async Task ASongAloneOnItsDiscLeavesNoGapInTheDiscs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        foreach (var title in (string[])["One", "Two", "Three", "Four"])
        {
            await SongApi.CreateAsync(client, title);
        }

        var albumId = await CreateAlbumAsync(client, "Discs");
        var revision = 1;
        foreach (var member in (string[])["n8-1", "n8-2", "n8-3", "n8-4"])
        {
            revision = await AddTrackAsync(client, albumId, revision, member);
        }

        using (var put = await SendAsync(
            client,
            HttpMethod.Put,
            $"/api/v1/albums/{albumId}/tracks",
            revision,
            """{"tracks":[{"songId":"n8-1","disc":1,"track":1},{"songId":"n8-4","disc":1,"track":3},{"songId":"n8-2","disc":2,"track":1},{"songId":"n8-3","disc":3,"track":5}]}"""))
        {
            Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        }

        await DeleteAsync(client, "n8-2", "Two");

        // Disc 3 closes up to disc 2; every track number stays as it was.
        Assert.Equal([("n8-1", 1, 1), ("n8-4", 1, 3), ("n8-3", 2, 5)], Tracks(await AlbumAsync(client, albumId)));

        // Restored, it goes to the end of the last disc.
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, (await SongGroupAsync(factory, "n8-2")).Id));
        Assert.Equal([("n8-1", 1, 1), ("n8-4", 1, 3), ("n8-3", 2, 5), ("n8-2", 2, 6)], Tracks(await AlbumAsync(client, albumId)));
    }

    [Fact]
    public async Task AVersionDeletedOnItsOwnCannotBeRestoredWhileItsSongIsDeleted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Parent");
        await AddVersionAsync(client, "n8-1", "2");
        await DeleteVersionAsync(client, "n8-1-v2");
        var versionGroup = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        await DeleteAsync(client, "n8-1", "Parent");

        Assert.IsType<RetentionRestoreOutcome.MissingParent>(await RestoreAsync(factory, versionGroup.Id));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM versions WHERE song_id = '{Upper(song.GetProperty("id").GetGuid())}';"));
    }

    /// <summary>The tables holding a Song's own rows, by the column naming it.</summary>
    private static readonly (string Table, string Column)[] OwnTables =
    [
        ("songs", "id"), ("versions", "song_id"), ("generations", "song_id"), ("used_version_numbers", "song_id"), ("song_links", "song_id"),
        ("song_genres", "song_id"), ("song_tags", "song_id"), ("song_artist_credits", "song_id"), ("album_songs", "song_id"),
        ("playlist_songs", "song_id"), ("song_relationships", "from_song_id"),
    ];

    /// <summary>
    /// The Song's own rows, every column but <c>revision</c> (a restore raises it) and, for the
    /// memberships, their place (a restore puts them at the end), sorted: what a restore must give back.
    /// </summary>
    private static List<string> OwnRows(N8TracksApiFactory factory, string songId)
    {
        var rows = new List<string>();
        foreach (var (table, column) in OwnTables.Append(("artwork_attachments", "owner_id")))
        {
            var columns = TestDatabase.Rows(factory.DataPath, $"SELECT name FROM pragma_table_info('{table}') WHERE name NOT IN ('revision', 'disc', 'track', 'position') ORDER BY name;");
            rows.AddRange(TestDatabase.Rows(
                factory.DataPath,
                $"SELECT '{table}|' || {string.Join(" || '|' || ", columns.Select(static name => $"COALESCE(quote({name}), '∅')"))} FROM {table} WHERE {column} = '{songId}';"));
        }

        rows.AddRange(TestDatabase.Rows(factory.DataPath, $"SELECT 'history|' || id || '|' || sequence || '|' || lyrics FROM editor_revisions WHERE version_id IN (SELECT id FROM versions WHERE song_id = '{songId}');"));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>
    /// Song n8-1 "Doomed" with everything a deletion takes: Versions 1, 2 (with a Generation, so
    /// frozen), and 3 (archived), and 4 deleted on its own; a history entry; notes, a Genre, a Tag,
    /// release details with a link, a primary and a featured Artist, and artwork; track 2 of an Album
    /// between n8-2 and n8-3; on two Playlists; related to n8-2 (Cover) and from n8-3 (Remix), which are
    /// related to each other too; and n8-2 shares its ISRC.
    /// </summary>
    private static async Task<Fixture> RichSongAsync(N8TracksApiFactory factory, HttpClient client, TestClock clock)
    {
        var song = await SongApi.CreateAsync(client, "Doomed");
        await SongApi.CreateAsync(client, "Neighbour");
        await SongApi.CreateAsync(client, "Other");
        var songId = song.GetProperty("id").GetGuid();
        clock.Advance(TimeSpan.FromSeconds(1));

        await AddVersionAsync(client, "n8-1", "2");
        await AddVersionAsync(client, "n8-1", "3");
        await AddVersionAsync(client, "n8-1", "4");
        await DeleteVersionAsync(client, "n8-1-v4");
        await SetCurrentAsync(client, "n8-1", "n8-1-v2");
        await PatchVersionAsync(client, "n8-1-v3", """{"archived":true}""");
        using (var snapshot = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/versions/n8-1-v1/snapshots", UriKind.Relative), """{"lyrics":"Kept in history","styles":"folk"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        }

        await SongApi.AttachGenerationAsync(factory, "n8-1-v2");

        var genreId = await CreateNamedAsync(client, "genres", "Doomed Folk");
        var tagId = await CreateNamedAsync(client, "tags", "Doomed Night");
        var asset = await StoreAsync(client, Assets.ArtworkImages.Halves(SKEncodedImageFormat.Png, 400, 200));
        var revision = (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32();
        await SongApi.EditAsync(
            client,
            songId.ToString(),
            revision,
            $$$"""{"notes":"Notes kept","genreIds":["{{{genreId}}}"],"tagIds":["{{{tagId}}}"],"artworkAssetId":"{{{IdOf(asset)}}}","release":{"isrc":"{{{Isrc}}}","links":[{"url":"https://example.com/doomed","label":"Listen"}]}}""");
        var neighbour = await SongAsync(client, "n8-2");
        await SongApi.EditAsync(client, neighbour.GetProperty("id").GetGuid().ToString(), neighbour.GetProperty("revision").GetInt32(), $$$"""{"release":{"isrc":"{{{Isrc}}}"}}""");

        var artistId = await CreateArtistAsync(client, "Doomed Primary");
        var guestId = await CreateArtistAsync(client, "Doomed Guest");
        revision = (await SongAsync(client, "n8-1")).GetProperty("revision").GetInt32();
        using (var credits = await SendAsync(client, HttpMethod.Put, "/api/v1/songs/n8-1/credits", revision, $$"""{"primaryArtistId":"{{artistId}}","featuredArtistIds":["{{guestId}}"]}"""))
        {
            Assert.True(credits.StatusCode == HttpStatusCode.OK, await credits.Content.ReadAsStringAsync());
        }

        var albumId = await CreateAlbumAsync(client, "Record");
        var albumRevision = 1;
        foreach (var member in (string[])["n8-2", "n8-1", "n8-3"])
        {
            albumRevision = await AddTrackAsync(client, albumId, albumRevision, member);
        }

        var first = await CreatePlaylistAsync(client, "Mix");
        var playlistRevision = 1;
        foreach (var member in (string[])["n8-2", "n8-1", "n8-3"])
        {
            playlistRevision = await AddToPlaylistAsync(client, first, playlistRevision, member);
        }

        var second = await CreatePlaylistAsync(client, "Solo");
        await AddToPlaylistAsync(client, second, 1, "n8-1");

        await RelateAsync(client, "n8-1", SystemRelationshipTypes.Cover.Id, "n8-2");
        await RelateAsync(client, "n8-3", SystemRelationshipTypes.Remix.Id, "n8-1");
        await RelateAsync(client, "n8-2", SystemRelationshipTypes.Mashup.Id, "n8-3");

        // A bystander Song, untouched by any of it.
        await SongApi.CreateAsync(client, "Bystander");
        clock.Advance(TimeSpan.FromSeconds(1));
        return new Fixture(songId, albumId, [first, second], genreId, tagId, artistId);
    }

    private static async Task<JsonElement> SongAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(SongApi.Song(reference));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> ImpactAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/songs/{reference}/deletion-impact", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Deletes the Song at its current revision, confirming with <paramref name="title"/>.</summary>
    private static async Task DeleteAsync(HttpClient client, string reference, string title)
    {
        var revision = (await SongAsync(client, reference)).GetProperty("revision").GetInt32();
        using var response = await DeleteAsync(client, reference, revision, JsonSerializer.Serialize(new { confirmTitle = title }));
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string reference, int? revision, string? json = null) =>
        SendAsync(client, HttpMethod.Delete, $"/api/v1/songs/{reference}", revision, json);

    private static async Task<RetentionGroup> SongGroupAsync(N8TracksApiFactory factory, string shortcode)
    {
        var group = await WithServiceAsync(factory, service => service.FindByShortcodeAsync(shortcode, CancellationToken.None));
        Assert.NotNull(group);
        return group;
    }

    private static async Task<JsonElement> ResolveAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/resolve/{reference}", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{reference}: {await response.Content.ReadAsStringAsync()}");
        return await SetupApi.JsonAsync(response);
    }

    private static async Task ExpectNoReferenceAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/resolve/{reference}", UriKind.Relative));
        await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, ResolveEndpoints.ReferenceNotFoundCode);
    }

    private static async Task<JsonElement> VersionAsync(HttpClient client, string reference) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/versions/{reference}", UriKind.Relative)));

    private static async Task<JsonElement> VersionListAsync(HttpClient client, string song) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/songs/{song}/versions", UriKind.Relative)));

    private static List<string?> Numbers(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("number").GetString())];

    private static async Task AddVersionAsync(HttpClient client, string song, string number)
    {
        using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/songs/{song}/versions", UriKind.Relative), $$"""{"sourceVersionId":"{{song}}-v1","number":"{{number}}"}""");
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    private static async Task DeleteVersionAsync(HttpClient client, string version)
    {
        var revision = (await VersionAsync(client, version)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Delete, $"/api/v1/versions/{version}", revision);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task SetCurrentAsync(HttpClient client, string song, string version)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Put, new Uri($"/api/v1/songs/{song}/current-version", UriKind.Relative), $$"""{"versionId":"{{version}}"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task PatchVersionAsync(HttpClient client, string version, string json)
    {
        var revision = (await VersionAsync(client, version)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/versions/{version}", revision, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> CreateNamedAsync(HttpClient client, string collection, string name)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/{collection}", UriKind.Relative), JsonSerializer.Serialize(new { name }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateArtistAsync(HttpClient client, string name)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/artists", UriKind.Relative), JsonSerializer.Serialize(new { name, confirmDuplicate = true }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>How many Songs the Genre or Tag list counts for <paramref name="id"/>.</summary>
    private static async Task<int> CountAsync(HttpClient client, string collection, Guid id)
    {
        var list = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/{collection}", UriKind.Relative)));
        return list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id).GetProperty("songCount").GetInt32();
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient client, string title)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/albums", UriKind.Relative), JsonSerializer.Serialize(new { title }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Adds the Song to the end of the Album; the Album's revision afterwards.</summary>
    private static async Task<int> AddTrackAsync(HttpClient client, Guid albumId, int revision, string song)
    {
        using var response = await SendAsync(client, HttpMethod.Post, $"/api/v1/albums/{albumId}/tracks", revision, $$"""{"songId":"{{song}}"}""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("revision").GetInt32();
    }

    private static async Task<JsonElement> AlbumAsync(HttpClient client, Guid id) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/albums/{id}", UriKind.Relative)));

    private static List<(string, int, int)> Tracks(JsonElement album) =>
        [.. album.GetProperty("tracks").EnumerateArray().Select(static track => (track.GetProperty("shortcode").GetString()!, track.GetProperty("disc").GetInt32(), track.GetProperty("track").GetInt32()))];

    private static async Task<Guid> CreatePlaylistAsync(HttpClient client, string title)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/playlists", UriKind.Relative), JsonSerializer.Serialize(new { title }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Adds the Song to the end of the Playlist; the Playlist's revision afterwards.</summary>
    private static async Task<int> AddToPlaylistAsync(HttpClient client, Guid playlistId, int revision, string song)
    {
        using var response = await SendAsync(client, HttpMethod.Post, $"/api/v1/playlists/{playlistId}/songs", revision, $$"""{"songId":"{{song}}"}""");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("revision").GetInt32();
    }

    private static async Task<JsonElement> PlaylistAsync(HttpClient client, Guid id) =>
        await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/playlists/{id}", UriKind.Relative)));

    private static List<string> PlaylistSongs(JsonElement playlist) =>
        [.. playlist.GetProperty("songs").EnumerateArray().Select(static song => song.GetProperty("shortcode").GetString()!)];

    private static async Task RelateAsync(HttpClient client, string from, Guid typeId, string other)
    {
        using var response = await SongApi.SendJsonAsync(
            client,
            HttpMethod.Post,
            new Uri($"/api/v1/songs/{from}/relationships", UriKind.Relative),
            $$"""{"typeId":"{{typeId}}","direction":"forward","otherSong":"{{other}}"}""");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static List<string> RelatedShortcodes(JsonElement song) =>
        [.. song.GetProperty("relationships").EnumerateArray().Select(static relation => relation.GetProperty("song").GetProperty("shortcode").GetString()!).Order(StringComparer.Ordinal)];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int? revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (revision is { } value)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
        }

        return await client.SendAsync(request);
    }

    private sealed record Fixture(Guid SongId, Guid AlbumId, IReadOnlyList<Guid> PlaylistIds, Guid GenreId, Guid TagId, Guid ArtistId);
}
