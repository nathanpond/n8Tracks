using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Songs;
using static n8Tracks.Api.Tests.Suno.ProposalApi;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Proposals (#138): when an export becomes ready, each new clip is proposed a place, grouped by Create
/// request (TS-001) and split by inputs, and each record's choice starts as its proposal. Proposing
/// changes nothing in the catalog.
/// </summary>
public sealed class ImportProposalTests
{
    /// <summary>
    /// Two clips of one Create request (1 ms apart, <c>batch_index</c> 0 and 1) are one proposal: one new
    /// Song titled with the first clip's title and in their workspace. A request 17 seconds later is
    /// another new Song. The catalog is untouched.
    /// </summary>
    [Fact]
    public async Task EachCreateRequestThatMatchesNothingIsProposedAsOneNewSong()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var before = SunoExportStagingGuardTests.Snapshot(factory.DataPath);

        var (_, records) = await ExportAsync(
            client,
            token,
            Clip("b", "studio", At.AddMilliseconds(1), 1, title: "Second title"),
            Clip("a", "studio", At, 0, title: "First title"),
            Clip("c", "studio", At.AddSeconds(17), 0, title: "Later"));

        var (a, b, c) = (records["a"], records["b"], records["c"]);
        Assert.Equal("import", Text(Proposed(a), "action"));
        Assert.Equal("newSong", Text(Target(a), "kind"));
        Assert.Equal("First title", Text(Target(a), "title"));
        Assert.Equal("studio", Text(Target(a), "workspaceId"));
        Assert.Equal("newSong", Text(a.GetProperty("proposal"), "basis"));
        Assert.False(a.GetProperty("proposal").GetProperty("freezesVersion").GetBoolean());
        Assert.Equal(Proposed(a).GetRawText(), Proposed(b).GetRawText());
        Assert.Equal(a.GetProperty("proposal").GetProperty("group").GetInt32(), b.GetProperty("proposal").GetProperty("group").GetInt32());

        Assert.Equal("newSong", Text(Target(c), "kind"));
        Assert.Equal("Later", Text(Target(c), "title"));
        Assert.NotEqual(Text(Target(a), "key"), Text(Target(c), "key"));
        Assert.Equal(JsonValueKind.Null, c.GetProperty("proposal").GetProperty("group").ValueKind);

        // The choice starts as the proposal.
        Assert.All(records.Values, static record => Assert.Equal(Proposed(record).GetRawText(), record.GetProperty("choice").GetRawText()));
        Assert.Equal(before, SunoExportStagingGuardTests.Snapshot(factory.DataPath));
    }

    /// <summary>Clips of one request with different inputs are split: Version 1 of the new Song, and a new Version 2 of it, in <c>batch_index</c> order.</summary>
    [Fact]
    public async Task ClipsOfOneRequestWithDifferentInputsAreSplit()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);

        var (_, records) = await ExportAsync(
            client,
            token,
            Clip("a", "studio", At, 0, lyrics: "[Verse]\nOne"),
            Clip("b", "studio", At.AddMilliseconds(1), 1, lyrics: "[Verse]\nTwo"));

        var song = Target(records["a"]);
        Assert.Equal("newSong", Text(song, "kind"));
        var version = Target(records["b"]);
        Assert.Equal("newVersion", Text(version, "kind"));
        Assert.Equal(Text(song, "key"), Text(version, "song"));
        Assert.Equal(JsonValueKind.Null, version.GetProperty("parentVersion").ValueKind);
        Assert.Equal("2", Text(version, "number"));
        Assert.NotEqual(Text(song, "key"), Text(version, "key"));
        Assert.Equal(records["a"].GetProperty("proposal").GetProperty("group").GetInt32(), records["b"].GetProperty("proposal").GetProperty("group").GetInt32());
    }

    /// <summary>
    /// A workspace associated with exactly one Song: the clips go to its Version with the same inputs,
    /// one that has Generations before one that has none; a mutable match is proposed with the note that
    /// importing freezes it.
    /// </summary>
    [Fact]
    public async Task AWorkspaceWithOneSongProposesItsVersionWithTheSameInputs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("studio", "Studio"), SunoWorkspaceApi.Project("demos", "Demos"));
        var studioSong = await SongApi.CreateAsync(client, "Studio song");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "studio");
        var mutable = await ImportedVersions.AddAsync(factory, studioSong.GetProperty("id").GetGuid(), "2", Clip("template-1", "studio", At, 0));
        await ImportedVersions.AttachAsync(factory, studioSong, "3", Clip("held", "studio", At.AddDays(-1), 0));
        var demoSong = await SongApi.CreateAsync(client, "Demo song");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-2", "demos");
        var onlyMutable = await ImportedVersions.AddAsync(factory, demoSong.GetProperty("id").GetGuid(), "2", Clip("template-2", "demos", At, 0));

        var (_, records) = await ExportAsync(
            client,
            token,
            Clip("a", "studio", At, 0),
            Clip("b", "studio", At.AddMilliseconds(1), 1),
            Clip("c", "demos", At, 0));

        var frozen = await VersionIdAsync(client, "n8-1-v3");
        Assert.All([records["a"], records["b"]], record =>
        {
            Assert.Equal("version", Text(Target(record), "kind"));
            Assert.Equal(frozen, Target(record).GetProperty("version").GetGuid());
            Assert.Equal("workspaceSong", Text(record.GetProperty("proposal"), "basis"));
            Assert.False(record.GetProperty("proposal").GetProperty("freezesVersion").GetBoolean());
        });
        Assert.NotEqual(mutable, Target(records["a"]).GetProperty("version").GetGuid());

        Assert.Equal(onlyMutable, Target(records["c"]).GetProperty("version").GetGuid());
        Assert.True(records["c"].GetProperty("proposal").GetProperty("freezesVersion").GetBoolean());
    }

    /// <summary>
    /// A workspace with one Song and no Version holding the clips' inputs: a new top-level Version of that
    /// Song, numbered by #61 (the next top-level number), one for each set of inputs; a later request with
    /// the same inputs as an earlier one is proposed for the same new Version.
    /// </summary>
    [Fact]
    public async Task AWorkspaceWithOneSongAndNoMatchProposesANewVersionNumberedByTheRules()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("studio", "Studio"));
        var song = await SongApi.CreateAsync(client, "Studio song");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "studio");

        var (_, records) = await ExportAsync(
            client,
            token,
            Clip("a", "studio", At, 0, lyrics: "[Verse]\nOne"),
            Clip("b", "studio", At.AddSeconds(30), 0, lyrics: "[Verse]\nTwo"),
            Clip("c", "studio", At.AddSeconds(60), 0, lyrics: "[Verse]\nOne"));

        var (a, b, c) = (Target(records["a"]), Target(records["b"]), Target(records["c"]));
        Assert.All([a, b, c], target =>
        {
            Assert.Equal("newVersion", Text(target, "kind"));
            Assert.Equal(song.GetProperty("id").GetGuid(), target.GetProperty("song").GetGuid());
            Assert.Equal(JsonValueKind.Null, target.GetProperty("parentVersion").ValueKind);
        });
        Assert.Equal("2", Text(a, "number"));
        Assert.Equal("3", Text(b, "number"));
        Assert.Equal(a.GetRawText(), c.GetRawText());
        Assert.Equal("workspaceSong", Text(records["b"].GetProperty("proposal"), "basis"));
    }

    /// <summary>A workspace with two Songs, or with none, gives no Song to propose: a new Song.</summary>
    [Fact]
    public async Task AWorkspaceWithTwoSongsOrNoneProposesANewSong()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: false, SunoWorkspaceApi.Project("studio", "Studio"));
        await SongApi.CreateAsync(client, "One");
        await SongApi.CreateAsync(client, "Two");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "studio");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-2", "studio");

        var (_, records) = await ExportAsync(
            client,
            token,
            Clip("shared", "studio", At, 0),
            Clip("elsewhere", "nobody", At, 0),
            Clip("nowhere", null, At, 0));

        Assert.All(records.Values, static record =>
        {
            Assert.Equal("newSong", Text(Target(record), "kind"));
            Assert.Equal("newSong", Text(record.GetProperty("proposal"), "basis"));
        });
        Assert.Equal(3, records.Values.Select(static record => Text(Target(record), "key")).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(JsonValueKind.Null, Target(records["nowhere"]).GetProperty("workspaceId").ValueKind);
    }

    /// <summary>
    /// Clips made for a Song n8Tracks has: a new clip whose group-mate is already a Generation goes to that
    /// Generation's Version when the inputs are the same, and otherwise to a new Version of its Song.
    /// </summary>
    [Fact]
    public async Task ANewClipFollowsAGroupMateThatIsAlreadyAGeneration()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var song = await SongApi.CreateAsync(client, "Sent from n8Tracks");
        var mate = Clip("mate", "studio", At, 0);
        var otherMate = Clip("other-mate", "studio", At.AddMinutes(5), 0, lyrics: "[Verse]\nThree");
        await ImportedVersions.AttachAsync(factory, song, "2", mate);
        await ImportedVersions.AttachAsync(factory, song, "3", otherMate);

        var (_, records) = await ExportAsync(
            client,
            token,
            mate,
            Clip("same", "studio", At.AddMilliseconds(1), 1),
            otherMate,
            Clip("different", "studio", At.AddMinutes(5).AddMilliseconds(1), 1, lyrics: "[Verse]\nFour"));

        Assert.Equal("linked", records["mate"].GetProperty("class").GetString());
        Assert.Equal("skip", Text(Proposed(records["mate"]), "action"));
        Assert.Equal(await VersionIdAsync(client, "n8-1-v2"), Target(records["same"]).GetProperty("version").GetGuid());
        Assert.Equal("groupMateVersion", Text(records["same"].GetProperty("proposal"), "basis"));

        var different = Target(records["different"]);
        Assert.Equal("newVersion", Text(different, "kind"));
        Assert.Equal(song.GetProperty("id").GetGuid(), different.GetProperty("song").GetGuid());
        Assert.Equal("4", Text(different, "number"));
        Assert.Equal("groupMateSong", Text(records["different"].GetProperty("proposal"), "basis"));
    }

    /// <summary>Ignored and deleted records are proposed Skip, with the reason.</summary>
    [Fact]
    public async Task IgnoredAndDeletedRecordsAreProposedSkip()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var deleted = Clip("deleted", "studio", At, 0);
        await SongApi.CreateAsync(client, "Song");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", deleted.ToJsonString());
        await DeleteGenerationAsync(client, "n8-1-v1-g1");
        SunoExportApi.Ignore(factory, "ignored");

        var (_, records) = await ExportAsync(client, token, deleted, Clip("ignored", "studio", At.AddMinutes(1), 0));

        Assert.Equal("deleted", records["deleted"].GetProperty("class").GetString());
        Assert.Equal("skip", Text(Proposed(records["deleted"]), "action"));
        Assert.Equal("deleted", Text(records["deleted"].GetProperty("proposal"), "basis"));
        Assert.Equal("skip", Text(Proposed(records["ignored"]), "action"));
        Assert.Equal("ignored", Text(records["ignored"].GetProperty("proposal"), "basis"));
    }
}
