using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Retention;

namespace n8Tracks.Api.Tests.Search;

/// <summary>
/// The index changes with the catalog (#223): after each kind of write, the old word no longer finds the
/// Song (or finds the right one) and the new one does, at once. Text in a Version's editing history or in
/// the retention store is never found.
/// </summary>
public sealed class SearchFreshnessTests
{
    [Fact]
    public async Task AnEditIsFoundAtOnceAndWhatItReplacedIsNot()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Oldword ballad");
        var id = song.GetProperty("id").GetString()!;
        var shortcode = song.GetProperty("shortcode").GetString()!;
        Assert.Equal([shortcode], await SearchApi.FoundAsync(client, "oldword"));

        _ = await SongApi.EditAsync(client, id, 1, """{"title":"Newword ballad","concept":"Conceptword"}""");
        Assert.Empty(await SearchApi.FoundAsync(client, "oldword"));
        Assert.Equal([shortcode], await SearchApi.FoundAsync(client, "newword"));
        Assert.Equal([shortcode], await SearchApi.FoundAsync(client, "conceptword"));

        await SearchApi.EditVersionAsync(client, $"{shortcode}-v1", """{"lyrics":"firstlyric","name":"firstname"}""");
        await SearchApi.EditVersionAsync(client, $"{shortcode}-v1", """{"lyrics":"secondlyric","name":null}""");
        Assert.Empty(await SearchApi.FoundAsync(client, "firstlyric"));
        Assert.Empty(await SearchApi.FoundAsync(client, "firstname"));
        Assert.Equal([shortcode], await SearchApi.FoundAsync(client, "secondlyric"));
    }

    [Fact]
    public async Task DeletingAVersionAGenerationOrACommentLeavesNothingOfItToFind()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = (await SongApi.CreateAsync(client, "Deletions")).GetProperty("shortcode").GetString()!;
        _ = SongApi.AddVersionDirectly(factory.DataPath, long.Parse(song[3..], System.Globalization.CultureInfo.InvariantCulture), "2", lyrics: "versionword");
        _ = await SongApi.AttachGenerationAsync(factory, $"{song}-v1", SearchApi.Clip("fresh-delete-1", title: "Generationword"));
        _ = await SongApi.AttachGenerationAsync(factory, $"{song}-v1", SearchApi.Clip("fresh-delete-2"));
        var comment = await SearchApi.CommentAsync(client, $"{song}-v1-g2", "Commentword here");
        Assert.Equal([song], await SearchApi.FoundAsync(client, "versionword"));
        Assert.Equal([song], await SearchApi.FoundAsync(client, "generationword"));
        Assert.Equal([song], await SearchApi.FoundAsync(client, "commentword"));

        using (var deleted = await SearchApi.SendAsync(client, HttpMethod.Delete, $"/api/v1/versions/{song}-v2", (await SearchApi.VersionAsync(client, $"{song}-v2")).GetProperty("revision").GetInt32()))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Empty(await SearchApi.FoundAsync(client, "versionword"));

        using (var deleted = await SearchApi.SendAsync(client, HttpMethod.Delete, $"/api/v1/generations/{song}-v1-g1", await SearchApi.RevisionAsync(client, $"/api/v1/generations/{song}-v1-g1")))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Empty(await SearchApi.FoundAsync(client, "generationword"));
        Assert.Empty(await SearchApi.FoundAsync(client, $"{song}-v1-g1"));

        using (var deleted = await SearchApi.SendAsync(client, HttpMethod.Delete, $"/api/v1/generations/{song}-v1-g2/comments/{comment.GetProperty("id").GetString()}", 1))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Empty(await SearchApi.FoundAsync(client, "commentword"));

        // Complement: the Song itself is still found.
        Assert.Equal([song], await SearchApi.FoundAsync(client, "deletions"));
    }

    [Fact]
    public async Task CollectionsAndTagsChangedAsAWholeAreFoundAsTheyAreNow()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Membership");
        var id = song.GetProperty("id").GetString()!;
        var shortcode = song.GetProperty("shortcode").GetString()!;
        var album = await SearchApi.CollectionAsync(client, "albums", "Albumword");
        var playlist = await SearchApi.CollectionAsync(client, "playlists", "Playlistword");
        var tag = await SearchApi.TagAsync(client, "tagword");
        await SearchApi.AddToAsync(client, "albums", album, id);
        await SearchApi.AddToAsync(client, "playlists", playlist, id);
        await SearchApi.TagSongAsync(client, id, tag);
        Assert.Equal([shortcode], await SearchApi.FoundAsync(client, "albumword playlistword tagword"));

        // Renaming a Tag, an Album, or a Playlist re-indexes every Song on it.
        using (var renamed = await SearchApi.SendAsync(client, HttpMethod.Patch, $"/api/v1/tags/{tag}", 1, """{"name":"renamedtag"}"""))
        {
            Assert.True(renamed.StatusCode == HttpStatusCode.OK, await renamed.Content.ReadAsStringAsync());
        }

        using (var renamed = await SearchApi.SendAsync(client, HttpMethod.Patch, $"/api/v1/albums/{album}", await SearchApi.RevisionAsync(client, $"/api/v1/albums/{album}"), """{"title":"Renamedalbum"}"""))
        {
            Assert.True(renamed.StatusCode == HttpStatusCode.OK, await renamed.Content.ReadAsStringAsync());
        }

        Assert.Empty(await SearchApi.FoundAsync(client, "tagword"));
        Assert.Empty(await SearchApi.FoundAsync(client, "albumword"));
        Assert.Equal([shortcode], await SearchApi.FoundAsync(client, "renamedtag renamedalbum playlistword"));

        // Taking it off the Album, and deleting the Playlist, leave neither to find.
        using (var removed = await SearchApi.SendAsync(client, HttpMethod.Delete, $"/api/v1/albums/{album}/tracks/{shortcode}", await SearchApi.RevisionAsync(client, $"/api/v1/albums/{album}")))
        {
            Assert.True(removed.StatusCode == HttpStatusCode.OK, await removed.Content.ReadAsStringAsync());
        }

        using (var deleted = await SearchApi.SendAsync(client, HttpMethod.Delete, $"/api/v1/playlists/{playlist}", await SearchApi.RevisionAsync(client, $"/api/v1/playlists/{playlist}")))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Empty(await SearchApi.FoundAsync(client, "renamedalbum"));
        Assert.Empty(await SearchApi.FoundAsync(client, "playlistword"));
        Assert.Equal([shortcode], await SearchApi.FoundAsync(client, "renamedtag"));
    }

    [Fact]
    public async Task AGenerationMovedToAnotherSongIsFoundOnThatSong()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = (await SongApi.CreateAsync(client, "Origin")).GetProperty("shortcode").GetString()!;
        _ = await SongApi.AttachGenerationAsync(factory, $"{song}-v1", SearchApi.Clip("fresh-move-1"));
        _ = await SongApi.AttachGenerationAsync(factory, $"{song}-v1", SearchApi.Clip("fresh-move-2", title: "Wanderword"));
        _ = await SearchApi.CommentAsync(client, $"{song}-v1-g2", "Travelword");
        Assert.Equal([song], await SearchApi.FoundAsync(client, "wanderword travelword"));

        string moved;
        using (var response = await SearchApi.SendAsync(client, HttpMethod.Post, $"/api/v1/generations/{song}-v1-g2/move-to-new-song", await SearchApi.RevisionAsync(client, $"/api/v1/generations/{song}-v1-g2"), """{"title":"Split out"}"""))
        {
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            moved = (await SetupApi.JsonAsync(response)).GetProperty("song").GetProperty("shortcode").GetString()!;
        }

        Assert.NotEqual(song, moved);
        Assert.Equal([moved], await SearchApi.FoundAsync(client, "wanderword travelword"));
        Assert.Equal([moved], await SearchApi.FoundAsync(client, $"{moved}-v1-g1"));
        // The old shortcode is no Generation's now: it is found only in the new Version's notes, which name it.
        var stale = await SearchApi.SearchAsync(client, $"{song}-v1-g2");
        Assert.Equal([moved], SongApi.Shortcodes(stale));
        Assert.Equal(["versionNotes"], SearchApi.Fields(SearchApi.Item(stale, moved)));
    }

    [Fact]
    public async Task ADeletedSongIsNotFoundAndARestoredOneIsAgain()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Phoenixword");
        var shortcode = song.GetProperty("shortcode").GetString()!;
        await SearchApi.EditVersionAsync(client, $"{shortcode}-v1", """{"lyrics":"ashword"}""");
        _ = await SongApi.AttachGenerationAsync(factory, $"{shortcode}-v1", SearchApi.Clip("fresh-restore-1", title: "Emberword"));
        _ = await SongApi.CreateAsync(client, "Bystander");

        using (var deleted = await SearchApi.SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/v1/songs/{shortcode}",
            (await SearchApi.SongAsync(client, shortcode)).GetProperty("revision").GetInt32(),
            JsonSerializer.Serialize(new { confirmTitle = "Phoenixword" })))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        // Its text is in the retention store now, and none of it is found.
        Assert.NotEqual("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_records;"));
        Assert.Empty(await SearchApi.FoundAsync(client, "phoenixword"));
        Assert.Empty(await SearchApi.FoundAsync(client, "ashword"));
        Assert.Empty(await SearchApi.FoundAsync(client, "emberword"));
        Assert.Empty(await SearchApi.FoundAsync(client, shortcode));
        Assert.Single(await SearchApi.FoundAsync(client, "bystander"));

        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            Assert.IsType<DeletedItemRestoreOutcome.Restored>(await scope.ServiceProvider.GetRequiredService<DeletedItemsService>().RestoreAsync(shortcode, CancellationToken.None));
        }

        Assert.Equal([shortcode], await SearchApi.FoundAsync(client, "phoenixword ashword emberword"));
    }

    [Fact]
    public async Task TextOnlyInAVersionsEditingHistoryIsNotFound()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var shortcode = (await SongApi.CreateAsync(client, "Drafts")).GetProperty("shortcode").GetString()!;
        var version = (await SearchApi.VersionAsync(client, $"{shortcode}-v1")).GetProperty("id").GetString()!;

        using (var snapshot = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/versions/{version}/snapshots", UriKind.Relative), """{"lyrics":"historyword","styles":"historystyle"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        }

        await SearchApi.EditVersionAsync(client, $"{shortcode}-v1", """{"lyrics":"currentword"}""");

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM editor_revisions WHERE lyrics = 'historyword';"));
        Assert.Empty(await SearchApi.FoundAsync(client, "historyword"));
        Assert.Empty(await SearchApi.FoundAsync(client, "historystyle"));
        Assert.Equal([shortcode], await SearchApi.FoundAsync(client, "currentword"));
    }
}
