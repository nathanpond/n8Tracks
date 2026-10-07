using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Api.Tests.Suno;
using n8Tracks.Application.Suno;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Suno;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Invariants;

/// <summary>
/// Guard for invariant 3, Suno import (#140): imports never silently overwrite. Every row of every catalog
/// table (the list of #131's staging guard, <see cref="SunoExportStagingGuardTests.CatalogTables"/>, which
/// fails on a table in neither of its lists) is read, keyed by its primary key, before and after a commit
/// over a catalog of existing Songs, Versions, and Generations. Each added, changed, or removed row must
/// be one a confirmed choice names, worked out from the choices and checked against the job's result: the
/// new Songs, their Versions and Generations, a new Version of an existing Song, the Versions clips were
/// attached to (a mutable one is frozen), Generation Events of groups, provider records, the workspace of
/// a new Song, a model the clips report that no entry matched, the Reimport's restored Generation (its
/// retention group and tombstone go), the shortcode and number counters, and (#141) the one clip column a
/// Changed record's accepted field names, the move of a Conflict's Generation to a new child Version with
/// its alias and raw clip (its old Version and its rating untouched), and the remembered hash of a change
/// declined or a conflict kept, which is all such a record may change: never a clip column or the raw clip.
/// An ignored clip in Suno's Trash chosen for import (#143) comes in archived by sync. Every other row is
/// byte for byte the same. Following Suno (#142): an existing Generation may also change for a Suno state change left
/// selected at Confirm, and then only in its remote state, state, archiver, and revision. Complements: a
/// commit with every record (Changed and Conflict included) and every Suno state change set to Skip, and an export uploaded, classified,
/// proposed, and discarded, change nothing at all; and the guard bites when the commit retitles an
/// existing Generation, when it applies a Suno state change set to Skip, and when a declined change
/// writes a clip column. The one change no review confirms (#154): completing a Generation an observed
/// Create made, which may change only that Generation's clip columns and raw clip, and only while it was
/// never complete; the guard bites when a completion touches one that was. The guard does not cover
/// portable import (M8, #263).
/// </summary>
public sealed class ImportNeverOverwritesGuardTests
{
    private const string NewModel = "V9-GUARD";

    [Fact]
    public async Task ACommitChangesOnlyTheRowsTheConfirmedChoicesName()
    {
        using var factory = SongApi.Host();
        var scenario = await ScenarioAsync(factory);
        var result = await ImportCommitApi.CommitAsync(scenario.Client, scenario.ExportId);
        var after = Rows(factory.DataPath);

        Assert.Empty(Unexplained(factory, scenario, result, after));
        scenario.Client.Dispose();
    }

    [Fact]
    public async Task ACommitWithEveryRecordSetToSkipChangesNothing()
    {
        using var factory = SongApi.Host();
        var scenario = await ScenarioAsync(factory, everythingSkipped: true);
        var result = await ImportCommitApi.CommitAsync(scenario.Client, scenario.ExportId);

        Assert.Equal(0, result.GetProperty("created").GetProperty("generations").GetInt32());
        Assert.All(ImportCommitApi.Records(result).Values, static record => Assert.Equal("skipped", ImportCommitApi.Outcome(record)));
        Assert.All(RemoteStateApi.Results(result).Values, static row => Assert.Equal("skipped", row.GetProperty("outcome").GetString()));
        Assert.Empty(Differences(scenario.Before, Rows(factory.DataPath)));
        scenario.Client.Dispose();
    }

    [Fact]
    public async Task AnExportUploadedClassifiedProposedAndDiscardedChangesNothing()
    {
        using var factory = SongApi.Host();
        var scenario = await ScenarioAsync(factory);
        using (var discarded = await SunoExportApi.SendAsync(scenario.Client, HttpMethod.Post, SunoExportApi.Export(scenario.ExportId, "/discard"), scenario.Token))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, discarded.StatusCode);
        }

        Assert.Empty(Differences(scenario.Before, Rows(factory.DataPath)));
        scenario.Client.Dispose();
    }

    /// <summary>
    /// The ignore list (#143) is confirmed-choice data: a commit adds the Don't copy records, removes the
    /// ignored records imported, and refreshes the entries it saw (and, for a whole-library sync, the
    /// status of those it did not), and nothing else; receiving and discarding an export changes nothing,
    /// and a removal takes off exactly the items named.
    /// </summary>
    [Fact]
    public async Task TheIgnoreListChangesOnlyAsTheConfirmedChoicesSay()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        foreach (var (sunoId, title) in new[] { ("kept-1", "Old title"), ("wanted-1", "Wanted"), ("elsewhere-1", "Elsewhere") })
        {
            TestDatabase.Execute(factory.DataPath, $"INSERT INTO suno_ignored_items (suno_id, title, ignored_utc, last_status, last_seen_utc) VALUES ('{sunoId}', '{title}', '2026-09-01T00:00:00.000Z', 'present', '2026-09-01T00:00:00.000Z');");
        }

        var at = ProposalApi.At;
        JsonNode[] clips =
        [
            ProposalApi.Clip("dont-1", null, at, 0, "Dont words"),
            ProposalApi.Clip("kept-1", null, at.AddHours(1), 0, "Kept words", "New title"),
            ProposalApi.Clip("wanted-1", null, at.AddHours(2), 0, "Wanted words"),
            ProposalApi.Clip("skip-1", null, at.AddHours(3), 0, "Skip words"),
        ];

        // Received, classified, and discarded: nothing changes.
        var start = Rows(factory.DataPath);
        var (discarded, _) = await ProposalApi.ExportAsync(client, token, clips);
        using (var response = await SunoExportApi.SendAsync(client, HttpMethod.Post, SunoExportApi.Export(discarded, "/discard"), token))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Empty(Differences(start, Rows(factory.DataPath)));

        var (exportId, records) = await ProposalApi.ExportAsync(client, token, clips);
        Assert.Equal(["new", "ignored", "ignored", "new"], new[] { "dont-1", "kept-1", "wanted-1", "skip-1" }.Select(id => records[id].GetProperty("class").GetString()));
        await ProposalApi.ChangedAsync(client, exportId, 1, ProposalApi.Change(new JsonObject { ["action"] = "ignore" }, "dont-1"));
        await ProposalApi.ChangedAsync(client, exportId, 2, ProposalApi.Change(new JsonObject { ["action"] = "skip" }, "skip-1"));
        await ProposalApi.ChangedAsync(client, exportId, 3, ProposalApi.Change(ProposalApi.Import(new JsonObject { ["kind"] = "newSong", ["key"] = "new:1", ["title"] = "Wanted" }), "wanted-1"));
        var before = Rows(factory.DataPath);

        await ImportCommitApi.CommitAsync(client, exportId);

        var changes = Differences(before, Rows(factory.DataPath));
        Assert.Equal(
            ["A dont-1", "C elsewhere-1", "C kept-1", "R wanted-1"],
            changes.Where(static change => change.Table == "suno_ignored_items").Select(static change => $"{change.Kind} {change.Key}").Order(StringComparer.Ordinal));
        var kept = changes.Single(static change => change.Key == "kept-1").Row;
        Assert.Equal(("New title", "present"), (kept["title"], kept["last_status"]));
        Assert.Equal("missing", changes.Single(static change => change.Key == "elsewhere-1").Row["last_status"]);
        string[] imported = ["songs", "versions", "generations", "provider_records", "used_version_numbers"];
        Assert.All(
            changes.Where(static change => change.Table != "suno_ignored_items"),
            change => Assert.True(
                (change.Kind == 'A' && imported.Contains(change.Table)) || (change.Kind == 'C' && change.Table == "shortcode_sequence"),
                $"{change.Table}: {change.Kind} {change.Key}"));

        // A removal takes off exactly the items named.
        var listed = Rows(factory.DataPath);
        await IgnoreListTests.RemoveAsync(client, "kept-1", "not-listed");
        Assert.Equal(["suno_ignored_items R kept-1"], Differences(listed, Rows(factory.DataPath)).Select(static change => $"{change.Table} {change.Kind} {change.Key}"));
    }

    /// <summary>
    /// The Suno playlists and personas the Sources pickers offer (#153) change only with what a commit
    /// imports: a commit that imports nothing (every record Skip or Don't copy) changes neither; one that
    /// imports a clip adds its Voice and the listed playlists that hold it, and touches no other row of
    /// either table, a stored persona the clip names included only in its last-seen time and name.
    /// </summary>
    [Fact]
    public async Task ThePlaylistsAndPersonasChangeOnlyWithTheClipsImported()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        TestDatabase.Execute(factory.DataPath, """
            INSERT INTO suno_playlists (suno_id, name, clip_ids, last_seen_utc) VALUES ('pl-other', 'Untouched', '["x"]', '2026-09-01T00:00:00.000Z');
            INSERT INTO suno_personas (suno_id, name, last_seen_utc) VALUES ('pe-other', 'Untouched', '2026-09-01T00:00:00.000Z');
            """);

        JsonNode Voiced(string id, int hour, string personaId)
        {
            var clip = ProposalApi.Clip(id, null, ProposalApi.At.AddHours(hour), 0, id + " words");
            clip["metadata"]!["persona_id"] = personaId;
            clip["persona"] = new JsonObject { ["id"] = personaId, ["name"] = personaId + " name" };
            return clip;
        }

        var header = SunoExportApi.Header(playlists:
        [
            SunoExportApi.Playlist("pl-imported", "Imported", "wanted-1"),
            SunoExportApi.Playlist("pl-skipped", "Skipped", "skip-1", "dont-1"),
            SunoExportApi.Playlist("pl-other", "Renamed elsewhere", "skip-1"),
        ]);
        JsonNode[] clips = [Voiced("wanted-1", 0, "pe-wanted"), Voiced("skip-1", 1, "pe-skip"), Voiced("dont-1", 2, "pe-other")];

        // Nothing imported: neither table changes.
        var (nothing, _) = await SunoExportApi.UploadAsync(client, token, header, SunoExportApi.Part(1, clips));
        await ProposalApi.ChangedAsync(client, nothing, 1, ProposalApi.Change(new JsonObject { ["action"] = "skip" }, "wanted-1", "skip-1"));
        await ProposalApi.ChangedAsync(client, nothing, 2, ProposalApi.Change(new JsonObject { ["action"] = "ignore" }, "dont-1"));
        var before = Rows(factory.DataPath);
        await ImportCommitApi.CommitAsync(client, nothing);
        Assert.DoesNotContain(Differences(before, Rows(factory.DataPath)), static change => change.Table is "suno_playlists" or "suno_personas");

        // One clip imported: its Voice and the listed playlist holding it, and nothing else.
        var (one, _) = await SunoExportApi.UploadAsync(client, token, header, SunoExportApi.Part(1, clips));
        await ProposalApi.ChangedAsync(client, one, 1, ProposalApi.Change(new JsonObject { ["action"] = "skip" }, "skip-1", "dont-1"));
        before = Rows(factory.DataPath);
        await ImportCommitApi.CommitAsync(client, one);
        Assert.Equal(
            ["suno_personas A pe-wanted", "suno_playlists A pl-imported"],
            Differences(before, Rows(factory.DataPath))
                .Where(static change => change.Table is "suno_playlists" or "suno_personas")
                .Select(static change => $"{change.Table} {change.Kind} {change.Key}")
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The guard bites: with the commit made to retitle an existing Generation that no choice names (as
    /// an import must never do), the same run reports that row as unexplained.
    /// </summary>
    [Fact]
    public async Task TheGuardFailsWhenTheCommitRetitlesAnExistingGeneration()
    {
        var bite = new BiteSwitch();
        using var factory = new N8TracksApiFactory
        {
            TestServices = services =>
            {
                services.AddSingleton(bite);
                services.RemoveAll<IProviderTombstoneStore>();
                services.AddScoped<IProviderTombstoneStore>(provider => new OverwritingTombstones(
                    ActivatorUtilities.CreateInstance<ProviderTombstoneStore>(provider),
                    provider.GetRequiredService<N8TracksDbContext>(),
                    provider.GetRequiredService<BiteSwitch>()));
            },
        };
        var scenario = await ScenarioAsync(factory);
        bite.GenerationId = scenario.Bystander;

        var result = await ImportCommitApi.CommitAsync(scenario.Client, scenario.ExportId);

        var unexplained = Unexplained(factory, scenario, result, Rows(factory.DataPath));
        Assert.Contains(unexplained, static line => line.StartsWith("generations: changed", StringComparison.Ordinal));
        scenario.Client.Dispose();
    }

    /// <summary>
    /// The guard bites on following Suno (#142): with the commit made to forget which Suno state changes
    /// were set to Skip, the bystander's Remote Missing, which the user skipped, is reported as unexplained.
    /// </summary>
    [Fact]
    public async Task TheGuardFailsWhenTheCommitAppliesASunoStateChangeSetToSkip()
    {
        var bite = new BiteSwitch();
        using var factory = new N8TracksApiFactory
        {
            TestServices = services =>
            {
                services.AddSingleton(bite);
                services.RemoveAll<IRemoteStateStore>();
                services.AddScoped<IRemoteStateStore>(provider => new ForgettingSkips(
                    ActivatorUtilities.CreateInstance<RemoteStateStore>(provider),
                    provider.GetRequiredService<BiteSwitch>()));
            },
        };
        var scenario = await ScenarioAsync(factory);
        bite.GenerationId = scenario.Bystander;

        var result = await ImportCommitApi.CommitAsync(scenario.Client, scenario.ExportId);

        var unexplained = Unexplained(factory, scenario, result, Rows(factory.DataPath), checkRemoteStates: false);
        Assert.Contains($"generations: changed {Upper(scenario.Bystander)}", unexplained);
        scenario.Client.Dispose();
    }

    /// <summary>
    /// The guard bites on a remembered decline (#141): with the database made to write Suno's title as
    /// well whenever the declined record's hash is remembered, that Generation is reported as unexplained.
    /// </summary>
    [Fact]
    public async Task TheGuardFailsWhenADeclinedChangeWritesAClipColumn()
    {
        using var factory = SongApi.Host();
        var scenario = await ScenarioAsync(factory);
        TestDatabase.Execute(
            factory.DataPath,
            """
            CREATE TRIGGER tr_test_declined_overwrites AFTER UPDATE OF declined_hash ON generations
            WHEN NEW.suno_id = 'declined-1' AND NEW.declined_hash IS NOT NULL
            BEGIN UPDATE generations SET suno_title = 'Declined after' WHERE id = NEW.id; END;
            """);

        var result = await ImportCommitApi.CommitAsync(scenario.Client, scenario.ExportId);

        var declined = ImportCommitApi.Records(result)["declined-1"].GetProperty("generation").GetProperty("id").GetGuid();
        Assert.Contains($"generations: changed {Upper(declined)}", Unexplained(factory, scenario, result, Rows(factory.DataPath)));
        scenario.Client.Dispose();
    }

    /// <summary>
    /// The one change to a Generation that no import review confirms (#154): completing a Generation an
    /// observed Create made once Suno finishes its clip. Over a catalog holding such Generations, one made by
    /// import (complete, and one still streaming), one already completed, and one an import review declined a
    /// change of, a finished clip is reported for every one of them: only the Generations that were never
    /// complete change, and then only in their clip columns and raw clip. Every other catalog row is byte for
    /// byte the same.
    /// </summary>
    [Fact]
    public async Task CompletionChangesOnlyAnObservedGenerationThatWasNeverComplete()
    {
        using var factory = SongApi.Host();
        var completion = await CompletionScenarioAsync(factory);

        await completion.ReportEveryClipAsync();

        Assert.Empty(CompletionUnexplained(completion.Before, Rows(factory.DataPath), completion.Provisional));
        completion.Client.Dispose();
    }

    /// <summary>
    /// The completion guard bites: with the database made to write a Generation that was complete whenever
    /// the provisional one is completed, that Generation is reported as unexplained.
    /// </summary>
    [Fact]
    public async Task TheGuardFailsWhenCompletionTouchesAGenerationThatWasEverComplete()
    {
        using var factory = SongApi.Host();
        var completion = await CompletionScenarioAsync(factory);
        TestDatabase.Execute(
            factory.DataPath,
            $"""
            CREATE TRIGGER tr_test_completion_overwrites AFTER UPDATE OF provider_status ON generations
            WHEN NEW.id = '{Upper(completion.Provisional.Single())}'
            BEGIN UPDATE generations SET duration_seconds = 1 WHERE id = '{Upper(completion.EverComplete)}'; END;
            """);

        await completion.ReportEveryClipAsync();

        Assert.Contains($"generations: changed {Upper(completion.EverComplete)}", CompletionUnexplained(completion.Before, Rows(factory.DataPath), completion.Provisional));
        completion.Client.Dispose();
    }

    /// <summary>
    /// The catalog the completion tests report on: an observed Create's three Generations (one to complete,
    /// one completed already, one whose change an import review declined), and two Generations made by
    /// import (one complete, one still streaming). The rows are read once the first completion is done.
    /// </summary>
    private static async Task<CompletionScenario> CompletionScenarioAsync(N8TracksApiFactory factory)
    {
        var client = await SessionApi.SignedInClientAsync(factory);
        var observed = await ProvisionalCompletionApi.ObservedAsync(factory, client, "Guard completion", "guard-open", "guard-done", "guard-declined");
        await ProvisionalCompletionApi.CompleteAsync(client, observed.Token, observed.RequestId, ProvisionalCompletionApi.Finished("guard-done"));
        TestDatabase.Execute(factory.DataPath, $"UPDATE generations SET declined_hash = 'declined' WHERE id = '{Upper(observed.Generations["guard-declined"].Id)}';");
        var imported = await SongApi.CreateAsync(client, "Guard imported");
        var version = imported.GetProperty("currentVersion").GetProperty("id").GetGuid().ToString();
        var complete = await SongApi.AttachGenerationAsync(factory, version, Clips.Minimal("guard-imported"));
        await SongApi.AttachGenerationAsync(factory, version, Clips.Minimal("guard-imported-streaming", "streaming"));
        return new CompletionScenario(client, observed, [observed.Generations["guard-open"].Id], complete.Generation.Id, Rows(factory.DataPath));
    }

    /// <summary>
    /// Every catalog row a completion changed that it may not: anything but the clip columns of the
    /// <paramref name="completed"/> Generations and their raw clips.
    /// </summary>
    private static List<string> CompletionUnexplained(
        Dictionary<string, Dictionary<string, Row>> before,
        Dictionary<string, Dictionary<string, Row>> after,
        IReadOnlyCollection<Guid> completed)
    {
        string[] clipColumns =
        [
            "provider_status", "suno_title", "duration_seconds", "model_version", "model_name", "model_label", "style_tags",
            "minimum_bpm", "maximum_bpm", "average_bpm", "musical_key", "suno_created_utc", "audio_url", "image_url", "workspace_id", "batch_index",
        ];
        var keys = completed.Select(Upper).ToHashSet(StringComparer.Ordinal);
        var unexplained = new List<string>();
        foreach (var (table, kind, key, row) in Differences(before, after))
        {
            var explained = kind == 'C' && keys.Contains(key) && table switch
            {
                "generations" => before[table][key].SameExcept(row, clipColumns),
                "provider_records" => before[table][key].SameExcept(row, "payload", "captured_utc"),
                _ => false,
            };
            if (!explained)
            {
                unexplained.Add($"{table}: {(kind == 'A' ? "added" : kind == 'C' ? "changed" : "removed")} {key}");
            }
        }

        return unexplained;
    }

    /// <summary>
    /// The catalog and the export the tests commit: a Song with a frozen Version (one Generation), a
    /// mutable Version holding a clip's inputs, a Generation deleted alone (tombstoned, retained), and a
    /// bystander Song with a Generation no choice names; then an export whose choices make a new Song (two
    /// clips of one group, in a workspace, with a staged image and a model the list lacks), a new Version
    /// of the existing Song, a clip for each existing Version, a Reimport, a Skip, and a Don't copy. It is a
    /// whole-library sync (#142) with the frozen Version's clip in Suno's Trash (a Suno state change left
    /// to apply) and the bystander's clip in neither list (Remote Missing, set to Skip). The rows are read
    /// once every choice is saved.
    /// </summary>
    private static async Task<Scenario> ScenarioAsync(N8TracksApiFactory factory, bool everythingSkipped = false)
    {
        var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("studio", "Studio"));

        var at = ProposalApi.At;
        var frozen = ProposalApi.Clip("frozen-1", null, at, 0, "Frozen words");
        var mutable = ProposalApi.Clip("mutable-1", null, at.AddHours(1), 0, "Mutable words");
        var deleted = ProposalApi.Clip("deleted-1", null, at.AddHours(2), 0, "Deleted words");

        var song = await SongApi.CreateAsync(client, "Already here");
        var songId = song.GetProperty("id").GetGuid();
        await ImportedVersions.AttachAsync(factory, song, "2", frozen);
        await ImportedVersions.AddAsync(factory, songId, "3", mutable);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", deleted.ToJsonString());
        await ProposalApi.DeleteGenerationAsync(client, "n8-1-v1-g1");
        var bystanderSong = await SongApi.CreateAsync(client, "Bystander");
        var bystander = await SongApi.AttachGenerationAsync(factory, "n8-2-v1", Clips.Minimal("bystander-1"));

        // #141: a Generation whose clip Suno retitled and retagged (Changed), rated and commented on, and
        // one whose clip now has other lyrics (Conflict).
        var changedClip = ProposalApi.Clip("changed-1", null, at.AddHours(9), 0, "Changed words", "Before");
        var conflictClip = ProposalApi.Clip("conflict-1", null, at.AddHours(10), 0, "Conflict words");
        await ImportedVersions.AttachAsync(factory, bystanderSong, "2", changedClip);
        await ImportedVersions.AttachAsync(factory, bystanderSong, "3", conflictClip);
        await ImportCommitApi.RateAndCommentAsync(client, "n8-2-v2-g1", 4, "Stays as it is");
        var changedNow = changedClip.DeepClone();
        changedNow["title"] = "After";
        changedNow["metadata"]!["tags"] = "retagged on Suno";
        var conflictNow = conflictClip.DeepClone();
        conflictNow["metadata"]!["prompt"] = "Other conflict words";

        // #141: a Generation whose clip Suno retitled, every field declined (Changed), and one whose clip
        // has other lyrics and a new title, kept where it is with the title declined (Conflict).
        var declinedClip = ProposalApi.Clip("declined-1", null, at.AddHours(12), 0, "Declined words", "Declined before");
        var keptClip = ProposalApi.Clip("kept-1", null, at.AddHours(13), 0, "Kept words", "Kept before");
        await ImportedVersions.AttachAsync(factory, bystanderSong, "4", declinedClip);
        await ImportedVersions.AttachAsync(factory, bystanderSong, "5", keptClip);
        var declinedNow = declinedClip.DeepClone();
        declinedNow["title"] = "Declined after";
        var keptNow = keptClip.DeepClone();
        keptNow["title"] = "Kept after";
        keptNow["metadata"]!["prompt"] = "Other kept words";

        // #143: a clip on the ignore list (as the sync below will refresh it) that is in Suno's Trash.
        var ignoredTrashed = ProposalApi.Clip("ignored-trashed", null, at.AddHours(11), 0, "Mutable words", "Trashed and ignored");
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO suno_ignored_items (suno_id, title, ignored_utc, last_status, last_seen_utc) VALUES ('ignored-trashed', 'Trashed and ignored', '2026-09-01T00:00:00.000Z', 'trashed', '{RemoteStateApi.CapturedLater}');");

        JsonNode NewSongClip(string id, int batch)
        {
            var clip = ProposalApi.Clip(id, "studio", at.AddHours(3), batch, "New Song words", "Brand new");
            clip["metadata"]!["model_badges"] = new JsonObject { ["songrow"] = new JsonObject { ["display_name"] = NewModel } };
            return clip;
        }

        var clips = new[]
        {
            NewSongClip("new-a1", 0),
            NewSongClip("new-a2", 1),
            ProposalApi.Clip("new-v", null, at.AddHours(4), 0, "Another Version words"),
            ProposalApi.Clip("same-mutable", null, at.AddHours(5), 0, "Mutable words"),
            ProposalApi.Clip("same-frozen", null, at.AddHours(6), 0, "Frozen words"),
            ProposalApi.Clip("skipped-1", null, at.AddHours(7), 0, "Skipped words"),
            ProposalApi.Clip("ignored-1", null, at.AddHours(8), 0, "Ignored words"),
            deleted,
            changedNow,
            conflictNow,
            declinedNow,
            keptNow,
        };
        var (exportId, _) = await SunoExportApi.UploadAsync(client, token, RemoteStateApi.Header(), SunoExportApi.Part(1, clips, trashed: [frozen, ignoredTrashed]));
        var records = await SunoExportApi.RecordsByIdAsync(client, exportId);
        Assert.Equal("deleted", records["deleted-1"].GetProperty("class").GetString());
        Assert.Equal(("changed", "conflict", "ignored"), (records["declined-1"].GetProperty("class").GetString(), records["kept-1"].GetProperty("class").GetString(), records["ignored-trashed"].GetProperty("class").GetString()));
        await ImportCommitApi.StageImageAsync(client, token, exportId, "new-a1");

        var revision = 1;
        async Task ChooseAsync(JsonNode choice, params string[] sunoIds) =>
            await ProposalApi.ChangedAsync(client, exportId, revision++, ProposalApi.Change(choice, sunoIds));

        if (everythingSkipped)
        {
            await ChooseAsync(new JsonObject { ["action"] = "skip" }, [.. records.Keys]);
            await RemoteStateApi.SetAsync(client, exportId, false, [.. (await RemoteStateApi.RowsAsync(client, exportId)).Keys]);
        }
        else
        {
            await ChooseAsync(
                ProposalApi.Import(new JsonObject { ["kind"] = "newVersion", ["key"] = "new:90", ["song"] = "n8-1", ["parentVersion"] = null, ["number"] = "4" }),
                "new-v");
            await ChooseAsync(ProposalApi.Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v3" }), "same-mutable");
            await ChooseAsync(ProposalApi.Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v2" }), "same-frozen");
            await ChooseAsync(new JsonObject { ["action"] = "skip" }, "skipped-1");
            await ChooseAsync(new JsonObject { ["action"] = "ignore" }, "ignored-1");
            await ChooseAsync(ProposalApi.Import(new JsonObject { ["kind"] = "newSong", ["key"] = "new:91", ["title"] = "Reimported" }), "deleted-1");
            await ChooseAsync(new JsonObject { ["action"] = "apply", ["acceptFields"] = new JsonArray("title") }, "changed-1");
            await ChooseAsync(new JsonObject { ["action"] = "moveToNewVersion" }, "conflict-1");
            await ChooseAsync(new JsonObject { ["action"] = "apply", ["acceptFields"] = new JsonArray() }, "declined-1");
            await ChooseAsync(new JsonObject { ["action"] = "keep" }, "kept-1");
            await ChooseAsync(ProposalApi.Import(new JsonObject { ["kind"] = "version", ["version"] = "n8-1-v3" }), "ignored-trashed");
            Assert.Equal("newSong", ProposalApi.Text(ProposalApi.Target(records["new-a1"]), "kind"));
            await RemoteStateApi.SetAsync(client, exportId, false, "bystander-1");
        }

        // The Suno state changes left to apply at Confirm.
        var remote = await RemoteStateApi.RowsAsync(client, exportId);
        Assert.Equal(["bystander-1", "frozen-1"], remote.Keys.Order(StringComparer.Ordinal));
        var applied = remote.Where(static row => row.Value.GetProperty("apply").GetBoolean()).Select(static row => Upper(row.Value.GetProperty("generation").GetProperty("id").GetGuid())).ToHashSet(StringComparer.Ordinal);

        return new Scenario(client, token, exportId, songId, bystander.Generation.Id, Rows(factory.DataPath), applied);
    }

    /// <summary>
    /// Every catalog row change the confirmed choices do not account for, as text (<c>table: kind key</c>);
    /// empty when the commit did exactly what was confirmed. The choices of <see cref="ScenarioAsync"/> are
    /// checked against the result first: each created record went where its choice named.
    /// </summary>
    private static List<string> Unexplained(N8TracksApiFactory factory, Scenario scenario, JsonElement result, Dictionary<string, Dictionary<string, Row>> after, bool checkRemoteStates = true)
    {
        var records = ImportCommitApi.Records(result);
        Assert.Equal(["created", "created", "created", "created", "created", "skipped", "ignored", "created", "created"], new[] { "new-a1", "new-a2", "new-v", "same-mutable", "same-frozen", "skipped-1", "ignored-1", "deleted-1", "ignored-trashed" }.Select(id => ImportCommitApi.Outcome(records[id])));

        string GenerationOf(string sunoId) => Upper(records[sunoId].GetProperty("generation").GetProperty("id").GetGuid());
        string VersionIdOf(string generationId) => after["generations"][generationId]["version_id"]!;
        string Number(string versionId) => after["versions"][versionId]["number"]!;
        var before = scenario.Before;
        var songA = Upper(scenario.SongId);
        var versionTwo = before["versions"].Values.Single(row => row["song_id"] == songA && row["number"] == "2")["id"]!;
        var versionThree = before["versions"].Values.Single(row => row["song_id"] == songA && row["number"] == "3")["id"]!;

        // Where each record went is what its choice named.
        var newSongVersion = VersionIdOf(GenerationOf("new-a1"));
        Assert.Equal(newSongVersion, VersionIdOf(GenerationOf("new-a2")));
        Assert.Equal("1", Number(newSongVersion));
        var newSong = after["versions"][newSongVersion]["song_id"]!;
        Assert.DoesNotContain(newSong, before["songs"].Keys);
        Assert.Equal("Brand new", after["songs"][newSong]["title"]);
        var newVersion = VersionIdOf(GenerationOf("new-v"));
        Assert.Equal((songA, "4"), (after["versions"][newVersion]["song_id"], Number(newVersion)));
        Assert.Equal(versionThree, VersionIdOf(GenerationOf("same-mutable")));
        Assert.Equal(versionTwo, VersionIdOf(GenerationOf("same-frozen")));

        // #143: the ignored clip from Suno's Trash went where its choice named, archived by sync.
        var trashedImport = GenerationOf("ignored-trashed");
        Assert.Equal(versionThree, VersionIdOf(trashedImport));
        Assert.Equal(("archived", "trashed", "sync"), (after["generations"][trashedImport]["state"], after["generations"][trashedImport]["remote_state"], after["generations"][trashedImport]["archived_by"]));
        var restored = GenerationOf("deleted-1");
        Assert.True(records["deleted-1"].GetProperty("restored").GetBoolean());
        Assert.Contains(before["retention_records"].Values, row => row["original_id"] == restored);
        var restoredVersion = VersionIdOf(restored);
        Assert.Equal(1, ImportCommitApi.GenerationCount(factory, "deleted-1"));
        Assert.Equal(0, ImportCommitApi.GenerationCount(factory, "skipped-1") + ImportCommitApi.GenerationCount(factory, "ignored-1"));

        // #141: the Changed record took only its accepted title, remembering the tags it declined; the
        // Conflict's Generation moved to a new child Version of its own, leaving its Version (not named
        // below) as it was. The declined record and the kept conflict changed only what they remember.
        Assert.Equal(
            ("updated", "moved", "declined", "kept"),
            (ImportCommitApi.Outcome(records["changed-1"]), ImportCommitApi.Outcome(records["conflict-1"]), ImportCommitApi.Outcome(records["declined-1"]), ImportCommitApi.Outcome(records["kept-1"])));
        var changedGeneration = GenerationOf("changed-1");
        var movedGeneration = GenerationOf("conflict-1");
        var declinedGeneration = GenerationOf("declined-1");
        var keptGeneration = GenerationOf("kept-1");
        Assert.All(
            new[] { (changedGeneration, "declined_hash"), (declinedGeneration, "declined_hash"), (keptGeneration, "declined_hash"), (keptGeneration, "kept_inputs_hash") },
            remembered => Assert.Matches("^[0-9a-f]{64}$", after["generations"][remembered.Item1][remembered.Item2]));
        var childVersion = VersionIdOf(movedGeneration);
        Assert.Equal("3.1", Number(childVersion));
        var songB = after["versions"][childVersion]["song_id"]!;
        var resolvedColumns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [changedGeneration] = ["suno_title", "declined_hash"],
            [movedGeneration] = ["version_id", "ordinal", "revision"],
            [declinedGeneration] = ["declined_hash"],
            [keptGeneration] = ["declined_hash", "kept_inputs_hash"],
        };
        var replacedClips = Set(changedGeneration, movedGeneration);

        var createdSongs = Set(newSong);
        var createdVersions = Set(newSongVersion, newVersion, childVersion);
        var createdGenerations = Set([.. new[] { "new-a1", "new-a2", "new-v", "same-mutable", "same-frozen", "deleted-1", "ignored-trashed" }.Select(GenerationOf)]);
        var namedSongs = Set(songA, newSong, songB);
        var namedVersions = Set(versionTwo, versionThree, restoredVersion);
        var restoredGroups = Set([.. before["retention_records"].Values.Where(row => row["original_id"] == restored).Select(static row => row["group_id"]!)]);

        bool In(HashSet<string> set, string? value) => value is not null && set.Contains(value.ToUpperInvariant());

        // A resolved Generation (#141) may change only in the columns its choice names: never its rating,
        // and, declined or kept, never a clip column (only what it remembers).
        bool OnlyResolvedColumns(Row row) =>
            resolvedColumns.TryGetValue(row["id"]!, out var allowed)
            && before["generations"][row["id"]!].Columns.Where(column => before["generations"][row["id"]!][column] != row[column]).All(allowed.Contains);

        // Following Suno (#142): the result applied exactly the Suno state changes left selected, and an
        // existing Generation changes only for one of them, only in its remote state, state, archiver, and revision.
        var remoteApplied = RemoteStateApi.Results(result).Values
            .Where(static row => row.GetProperty("outcome").GetString() == "applied")
            .Select(static row => Upper(row.GetProperty("generation").GetProperty("id").GetGuid()))
            .ToHashSet(StringComparer.Ordinal);
        if (checkRemoteStates)
        {
            Assert.Equal(scenario.RemoteApplied.Order(StringComparer.Ordinal), remoteApplied.Order(StringComparer.Ordinal));
            var frozenGeneration = Assert.Single(scenario.RemoteApplied);
            Assert.Equal(("archived", "trashed", "sync"), (after["generations"][frozenGeneration]["state"], after["generations"][frozenGeneration]["remote_state"], after["generations"][frozenGeneration]["archived_by"]));
        }

        bool FollowsSuno(Row row) =>
            scenario.RemoteApplied.Contains(row["id"]!.ToUpperInvariant())
            && before["generations"][row["id"]!].SameExcept(row, "remote_state", "state", "archived_by", "revision");

        // What each table may have had added (A), changed (C), or removed (R), and only that.
        var rules = new Dictionary<string, Func<char, Row, bool>>(StringComparer.Ordinal)
        {
            ["songs"] = (kind, row) => kind == 'A' ? In(createdSongs, row["id"]) : kind == 'C' && In(namedSongs, row["id"]),
            ["versions"] = (kind, row) => kind == 'A' ? In(createdVersions, row["id"]) : kind == 'C' && In(namedVersions, row["id"]),
            ["generations"] = (kind, row) => kind == 'A' ? In(createdGenerations, row["id"]) : kind == 'C' && (OnlyResolvedColumns(row) || FollowsSuno(row)),
            ["provider_records"] = (kind, row) => kind == 'A' ? In(createdGenerations, row["generation_id"]) : kind == 'C' && In(replacedClips, row["generation_id"]),
            ["shortcode_aliases"] = (kind, row) => kind == 'A' && row["generation_id"] == movedGeneration,
            ["generation_comments"] = (kind, row) => kind == 'A' && row["generation_id"] == restored,
            ["generation_events"] = static (kind, _) => kind == 'A',
            ["generation_event_links"] = (kind, row) => kind == 'A' && In(createdGenerations, row["generation_id"]),
            ["used_version_numbers"] = (kind, row) => kind == 'A' && In(namedSongs, row["song_id"]),
            ["shortcode_sequence"] = static (kind, _) => kind == 'C',
            ["suno_models"] = static (kind, row) => kind == 'A' && row["name"] == NewModel,
            ["settings"] = static (kind, row) => kind is 'A' or 'C' && row["key"] == "suno.models",
            ["provider_tombstones"] = static (kind, row) => kind == 'R' && row["suno_id"] == "deleted-1",
            ["retention_groups"] = (kind, row) => kind == 'R' && In(restoredGroups, row["id"]),
            ["retention_records"] = (kind, row) => kind == 'R' && In(restoredGroups, row["group_id"]),
            ["suno_ignored_items"] = static (kind, row) => (kind == 'A' && row["suno_id"] == "ignored-1") || (kind == 'R' && row["suno_id"] == "ignored-trashed"),
        };

        var unexplained = new List<string>();
        foreach (var (table, kind, key, row) in Differences(before, after))
        {
            if (!rules.TryGetValue(table, out var allowed) || !allowed(kind, row))
            {
                unexplained.Add($"{table}: {(kind == 'A' ? "added" : kind == 'C' ? "changed" : "removed")} {key}");
            }
        }

        // The group's event, and the counted changes the result reports.
        Assert.Equal(1, Differences(before, after).Count(static change => change.Table == "generation_events"));
        var created = result.GetProperty("created");
        Assert.Equal((1, 3, 7), (created.GetProperty("songs").GetInt32(), created.GetProperty("versions").GetInt32(), created.GetProperty("generations").GetInt32()));
        return unexplained;
    }

    /// <summary>Every added (A), changed (C, the row after), and removed (R) catalog row.</summary>
    private static List<(string Table, char Kind, string Key, Row Row)> Differences(
        Dictionary<string, Dictionary<string, Row>> before,
        Dictionary<string, Dictionary<string, Row>> after)
    {
        var changes = new List<(string, char, string, Row)>();
        foreach (var (table, rows) in after)
        {
            var old = before[table];
            foreach (var (key, row) in rows)
            {
                if (!old.TryGetValue(key, out var was))
                {
                    changes.Add((table, 'A', key, row));
                }
                else if (!string.Equals(was.Text, row.Text, StringComparison.Ordinal))
                {
                    changes.Add((table, 'C', key, row));
                }
            }

            changes.AddRange(old.Where(pair => !rows.ContainsKey(pair.Key)).Select(pair => (table, 'R', pair.Key, pair.Value)));
        }

        return changes;
    }

    /// <summary>
    /// Every row of every catalog table, whole, by table and then by primary key (every column of a table
    /// with none); a GUID as SQLite holds it, upper-case text.
    /// </summary>
    internal static Dictionary<string, Dictionary<string, Row>> Rows(string dataPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = TestDatabase.FilePath(dataPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        var tables = new Dictionary<string, Dictionary<string, Row>>(StringComparer.Ordinal);
        foreach (var table in SunoExportStagingGuardTests.CatalogTables)
        {
            var key = new List<(int Position, string Name)>();
            using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info(\"{table}\");";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetInt32(5) > 0)
                    {
                        key.Add((reader.GetInt32(5), reader.GetString(1)));
                    }
                }
            }

            var keyColumns = key.OrderBy(static column => column.Position).Select(static column => column.Name).ToList();
            var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
            using var select = connection.CreateCommand();
            select.CommandText = $"SELECT * FROM \"{table}\";";
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        values[reader.GetName(column)] = reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);
                    }

                    var row = new Row(values);
                    rows[keyColumns.Count == 0 ? row.Text : string.Join('/', keyColumns.Select(name => values[name]))] = row;
                }
            }

            tables[table] = rows;
        }

        return tables;
    }

    private static HashSet<string> Set(params string[] values) => [.. values.Select(static value => value.ToUpperInvariant())];

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    /// <summary>One row: its values by column, and all of them as one text.</summary>
    /// <summary>The completion guard's catalog: the client, the observed Create, the Generations a completion may change, one that was complete, and the rows before.</summary>
    private sealed record CompletionScenario(
        HttpClient Client,
        ProvisionalCompletionApi.Observed Observed,
        IReadOnlyCollection<Guid> Provisional,
        Guid EverComplete,
        Dictionary<string, Dictionary<string, Row>> Before)
    {
        /// <summary>Reports a finished clip for every Generation of the scenario; only the provisional one is completed.</summary>
        public async Task ReportEveryClipAsync()
        {
            foreach (var sunoId in new[] { "guard-open", "guard-done", "guard-declined", "guard-imported", "guard-imported-streaming" })
            {
                using var _ = await ProvisionalCompletionApi.PostAsync(Client, Observed.Token, Observed.RequestId, ProvisionalCompletionApi.Finished(sunoId));
            }
        }
    }

    internal sealed class Row(Dictionary<string, string?> values)
    {
        public string Text { get; } = string.Join('|', values.Select(static pair => $"{pair.Key}={pair.Value ?? "<null>"}"));

        /// <summary>The row's column names.</summary>
        public IEnumerable<string> Columns => values.Keys;

        public string? this[string column] => values.TryGetValue(column, out var value)
            ? value
            : throw new KeyNotFoundException($"No column {column}: {string.Join(", ", values.Keys)}.");

        /// <summary>Whether <paramref name="other"/> has the same values in every column but <paramref name="columns"/>.</summary>
        public bool SameExcept(Row other, params string[] columns) =>
            values.All(pair => columns.Contains(pair.Key, StringComparer.Ordinal) || string.Equals(pair.Value, other[pair.Key], StringComparison.Ordinal));
    }

    private sealed record Scenario(HttpClient Client, string Token, Guid ExportId, Guid SongId, Guid Bystander, Dictionary<string, Dictionary<string, Row>> Before, HashSet<string> RemoteApplied);

    /// <summary>Which existing Generation the overwriting store retitles; none until the scenario is built.</summary>
    private sealed class BiteSwitch
    {
        public Guid? GenerationId { get; set; }
    }

    /// <summary>
    /// The tombstone store, made to retitle an existing Generation (inside the commit's own transaction)
    /// whenever it is asked about a Suno ID, as an import must never do.
    /// </summary>
    private sealed class OverwritingTombstones(IProviderTombstoneStore inner, N8TracksDbContext context, BiteSwitch bite) : IProviderTombstoneStore
    {
        public async Task<ProviderTombstone?> FindAsync(string sunoId, CancellationToken cancellationToken)
        {
            if (bite.GenerationId is { } id)
            {
                await context.Database.ExecuteSqlAsync($"UPDATE generations SET suno_title = 'Overwritten by an import' WHERE id = {id.ToString().ToUpperInvariant()};", cancellationToken);
            }

            return await inner.FindAsync(sunoId, cancellationToken);
        }

        public Task<IReadOnlySet<string>> TombstonedAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken) => inner.TombstonedAsync(sunoIds, cancellationToken);

        public Task<IReadOnlyDictionary<string, string?>> SunoClipsOfAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken) => inner.SunoClipsOfAsync(generationIds, cancellationToken);

        public Task SaveAsync(IReadOnlyCollection<ProviderTombstone> tombstones, CancellationToken cancellationToken) => inner.SaveAsync(tombstones, cancellationToken);

        public Task<bool> RemoveAsync(string sunoId, CancellationToken cancellationToken) => inner.RemoveAsync(sunoId, cancellationToken);
    }

    /// <summary>The remote-state store, made to forget the Suno state changes set to Skip once the bite is armed, as the commit must never do.</summary>
    private sealed class ForgettingSkips(IRemoteStateStore inner, BiteSwitch bite) : IRemoteStateStore
    {
        public Task<IReadOnlyList<RemoteStateCandidate>> ListedAsync(Guid exportId, CancellationToken cancellationToken) => inner.ListedAsync(exportId, cancellationToken);

        public Task<IReadOnlyList<RemoteStateCandidate>> UnlistedAsync(Guid exportId, CancellationToken cancellationToken) => inner.UnlistedAsync(exportId, cancellationToken);

        public async Task<IReadOnlyList<string>> SkipsAsync(Guid exportId, CancellationToken cancellationToken) =>
            bite.GenerationId is null ? await inner.SkipsAsync(exportId, cancellationToken) : [];

        public Task<bool> TrySetSkipsAsync(Guid exportId, int revision, IReadOnlyCollection<string> skips, CancellationToken cancellationToken) =>
            inner.TrySetSkipsAsync(exportId, revision, skips, cancellationToken);

        public Task<bool> TryApplyAsync(Guid generationId, int revision, RemoteStateTransition transition, CancellationToken cancellationToken) =>
            inner.TryApplyAsync(generationId, revision, transition, cancellationToken);
    }
}
