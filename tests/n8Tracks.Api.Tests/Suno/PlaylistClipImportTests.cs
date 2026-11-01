using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// A playlist sync's songs (#452): the extension reads them from the data Suno embeds in the playlist's
/// page, which leaves out what a library or workspace feed carries (the workspace, Suno's BPM and key,
/// options Suno omits), and marks each clip with <see cref="ClipReader.SourceMember"/>. The review offers
/// them as a workspace sync's are, Confirm imports them and records the membership, and a song n8Tracks
/// has is left alone on what the playlist does not carry (invariant 3).
/// </summary>
public sealed class PlaylistClipImportTests
{
    /// <summary>A library fixture clip as a playlist leg sends it: no workspace, no BPM or key, no sliders, marked.</summary>
    private static JsonNode FromPlaylist(JsonNode clip, bool marked = true)
    {
        var lacking = clip.DeepClone().AsObject();
        lacking.Remove("project");
        var metadata = lacking["metadata"]!.AsObject();
        foreach (var name in new[] { "key", "min_bpm", "max_bpm", "avg_bpm", "control_sliders", "model_badges", "negative_tags", "is_max_mode" })
        {
            metadata.Remove(name);
        }

        if (marked)
        {
            lacking[ClipReader.SourceMember] = "playlist";
        }

        return lacking;
    }

    [Fact]
    public async Task APlaylistSyncsSongsAreOfferedAsNewAndConfirmImportsThemWithTheMembership()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var clips = SunoExportApi.LibraryClips().Take(2).Select(clip => FromPlaylist(clip)).ToList();
        var ids = clips.Select(SunoExportApi.IdOf).ToArray();

        var exportId = await PlaylistMembershipTests.PlaylistExportAsync(client, token, clips, PlaylistMembershipTests.Membership("pl-1", "Night Drive", 2, ids));

        var export = await SunoExportApi.GetAsync(client, null, exportId);
        Assert.Equal(2, SunoExportApi.Count(export, "new"));
        var row = Assert.Single(await PlaylistMembershipTests.RowsAsync(client, exportId));
        Assert.Equal((0, 2), (row.GetProperty("inCatalog").GetInt32(), row.GetProperty("inExport").GetInt32()));
        Assert.Empty(row.GetProperty("notImported").EnumerateArray());

        var result = await ImportCommitApi.CommitAsync(client, exportId);

        var records = ImportCommitApi.Records(result);
        Assert.All(ids, id => Assert.Equal("created", ImportCommitApi.Outcome(records[id])));
        Assert.All(ids, id => Assert.Equal(1, ImportCommitApi.GenerationCount(factory, id)));
        Assert.Equal("""[{"sunoId":"pl-1","outcome":"applied"}]""", result.GetProperty("playlists").GetRawText());
        Assert.Equal(
            $"[\"{ids[0]}\",\"{ids[1]}\"]",
            TestDatabase.Scalar(factory.DataPath, "SELECT clip_ids FROM suno_playlists WHERE suno_id = 'pl-1';"));
    }

    [Fact]
    public async Task ASongNTracksHasIsLeftAloneOnWhatThePlaylistDoesNotCarry()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        // The Advanced clip of TS-003: its sliders off their defaults, Suno's BPM and key analysed.
        var clip = SunoExportApi.FixtureClips("feed-v3.songs-advanced.response.json")[0];
        var song = await SongApi.CreateAsync(client, "Linked");
        await ImportedVersions.AttachAsync(factory, song, "2", clip);
        var id = SunoExportApi.IdOf(clip);

        var marked = await PlaylistMembershipTests.PlaylistExportAsync(client, token, [FromPlaylist(clip)], PlaylistMembershipTests.Membership("pl-1", "Night Drive", 1, id));
        var record = (await SunoExportApi.RecordsByIdAsync(client, marked))[id];
        Assert.Equal("linked", record.GetProperty("class").GetString());
        Assert.Empty(record.GetProperty("changedFields").EnumerateArray());

        // It bites: the same clip without the mark reads its absent members as Suno's defaults and as cleared.
        var unmarked = await PlaylistMembershipTests.PlaylistExportAsync(client, token, [FromPlaylist(clip, marked: false)], PlaylistMembershipTests.Membership("pl-1", "Night Drive", 1, id));
        Assert.NotEqual("linked", (await SunoExportApi.RecordsByIdAsync(client, unmarked))[id].GetProperty("class").GetString());
    }
}
