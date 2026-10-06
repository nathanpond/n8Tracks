using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Application.Jobs;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;
using n8Tracks.Infrastructure.Persistence;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Retention;

/// <summary>
/// The retention foundation (#95): retaining moves records, and everything the database would remove
/// with them, out of the live tables into one group, dated, with a prune time 30 days later; the
/// records are then invisible to every read. A group restores as a whole, each record as it was except
/// its revision, provided its parents are there and no live row holds its ID or unique keys. The first
/// retained type is the editor snapshot; a test-only type covers revisions, cascades, and files.
/// </summary>
public sealed class RetentionServiceTests
{
    [Fact]
    public async Task ARetainedSnapshotIsGoneFromEveryReadAndRestoresAsItWas()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, shortcode, snapshots) = await VersionWithSnapshotsAsync(client, "Kept for later", "First words", "Second words", "Third words");
        var before = Dump(factory, "editor_revisions");
        var target = snapshots[1];

        clock.Advance(TimeSpan.FromMinutes(5));
        var group = await RetainAsync(factory, Snapshot(target, shortcode) with { Shortcode = shortcode });

        Assert.Equal(RetainedRecordTypes.EditorSnapshot, group.Kind);
        Assert.Equal($"History entry of {shortcode}", group.Label);
        Assert.Equal(clock.GetUtcNow(), group.DeletedUtc);
        Assert.Equal(clock.GetUtcNow().AddDays(30), group.PruneAfterUtc);
        Assert.Equal([new RetainedRecord(RetainedRecordTypes.EditorSnapshot, Upper(target), 1)], group.Records);

        // Gone from the list, the read, and the table itself; the others are untouched.
        Assert.Equal([snapshots[2], snapshots[0]], await HistoryAsync(client, version));
        Assert.Equal(HttpStatusCode.NotFound, await ReadSnapshotAsync(client, version, target));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM editor_revisions WHERE id = '{Upper(target)}';"));
        Assert.Equal([before[0], before[2]], Dump(factory, "editor_revisions"));
        Assert.Equivalent(group, await WithServiceAsync(factory, service => service.FindByShortcodeAsync(shortcode, CancellationToken.None)), strict: true);
        Assert.Equivalent(new[] { group }, await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)), strict: true);

        var outcome = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        Assert.Empty(outcome.Notes);
        Assert.Equivalent(group, outcome.Group, strict: true);

        // Back exactly as it was (a snapshot has no revision to increment), and the group is gone.
        Assert.Equal(before.Order(StringComparer.Ordinal), Dump(factory, "editor_revisions").Order(StringComparer.Ordinal));
        Assert.Equal(snapshots.AsEnumerable().Reverse(), await HistoryAsync(client, version));
        Assert.Equal(HttpStatusCode.OK, await ReadSnapshotAsync(client, version, target));
        Assert.Equal("0|0", TestDatabase.Scalar(factory.DataPath, "SELECT (SELECT count(*) FROM retention_groups) || '|' || (SELECT count(*) FROM retention_records);"));
        Assert.Null(await WithServiceAsync(factory, service => service.FindAsync(group.Id, CancellationToken.None)));
        Assert.IsType<RetentionRestoreOutcome.NotFound>(await RestoreAsync(factory, group.Id));
    }

    [Fact]
    public async Task RestoreIncrementsEachRevisionAndPutsBackEverythingRetainedWithTheRoot()
    {
        var clock = new TestClock();
        using var factory = Host(clock, withNotes: true);
        using var client = await SessionApi.SignedInClientAsync(factory);
        CreateTestTables(factory);
        var (version, _, _) = await VersionWithSnapshotsAsync(client, "Art");
        var artwork = AddTestArtwork(factory, version, "art/cover.png", revision: 7);
        var other = AddTestArtwork(factory, version, "art/other.png");
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO test_artwork_notes (artwork_id, position, text) VALUES ('{Upper(artwork)}', 0, 'first'), ('{Upper(artwork)}', 1, 'second'), ('{Upper(other)}', 0, 'theirs');");
        var artworkBefore = Dump(factory, "test_artwork");
        var notesBefore = Dump(factory, "test_artwork_notes");

        var group = await RetainAsync(factory, new RetentionRequest("test-artwork", "Cover", null, [new RetainedRoot("test-artwork", artwork)], ["art/cover.png"]));

        // The root first, then what cascades from it; another artwork's note stays.
        Assert.Equal(["test-artwork", "test-artwork-note", "test-artwork-note"], group.Records.Select(static record => record.RecordType));
        Assert.Equal([Upper(artwork) + "/0", Upper(artwork) + "/1"], group.Records.Skip(1).Select(static record => record.OriginalId));
        Assert.Equal(["art/cover.png"], group.Files);
        Assert.Equal([artworkBefore[1]], Dump(factory, "test_artwork"));
        Assert.Equal([notesBefore[2]], Dump(factory, "test_artwork_notes"));

        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));

        // Every column as it was, except the revision, which is one more.
        var restored = Dump(factory, "test_artwork");
        Assert.Contains(artworkBefore[0].Replace("|7", "|8", StringComparison.Ordinal), restored);
        Assert.Contains(artworkBefore[1], restored);
        Assert.Equal(notesBefore.Order(StringComparer.Ordinal), Dump(factory, "test_artwork_notes").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RowsThatReferToEachOtherBothWaysAreRetainedAndRestoredTogether()
    {
        // As a Song names its current Version and the Version names its Song (both restricting).
        var parent = new Infrastructure.Retention.RetainedType("test-parent", "test_parent", "test parent", 1);
        var child = new Infrastructure.Retention.RetainedType("test-child", "test_child", "test child", 1);
        using var factory = new N8TracksApiFactory
        {
            TestServices = services =>
            {
                services.AddSingleton(parent);
                services.AddSingleton(child);
            },
        };
        using var client = await SessionApi.SignedInClientAsync(factory);
        TestDatabase.Execute(
            factory.DataPath,
            """
            CREATE TABLE test_parent (id TEXT NOT NULL PRIMARY KEY, current_child_id TEXT NULL REFERENCES test_child (id) ON DELETE RESTRICT, revision INTEGER NOT NULL);
            CREATE TABLE test_child (id TEXT NOT NULL PRIMARY KEY, parent_id TEXT NOT NULL REFERENCES test_parent (id) ON DELETE RESTRICT, revision INTEGER NOT NULL);
            """);
        var (one, two, other) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        TestDatabase.Execute(
            factory.DataPath,
            $"""
            PRAGMA foreign_keys = OFF;
            INSERT INTO test_parent (id, current_child_id, revision) VALUES ('{Upper(one)}', '{Upper(two)}', 4), ('{Upper(other)}', NULL, 1);
            INSERT INTO test_child (id, parent_id, revision) VALUES ('{Upper(two)}', '{Upper(one)}', 2);
            """);
        var parentsBefore = Dump(factory, "test_parent");

        var group = await RetainAsync(factory, new RetentionRequest("test-parent", "Both ways", null, [new RetainedRoot("test-parent", one), new RetainedRoot("test-child", two)], []));

        Assert.Equal(["test-parent", "test-child"], group.Records.Select(static record => record.RecordType));
        Assert.Equal([parentsBefore[1]], Dump(factory, "test_parent"));
        Assert.Empty(Dump(factory, "test_child"));

        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        Assert.Contains($"'{Upper(one)}'|'{Upper(two)}'|5", Dump(factory, "test_parent"));
        Assert.Equal([$"'{Upper(two)}'|'{Upper(one)}'|3"], Dump(factory, "test_child"));
        Assert.Empty(TestDatabase.Rows(factory.DataPath, "SELECT * FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task RestoringAGroupWhoseParentIsGoneIsRefusedNamingItAndChangesNothing()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(client, "Orphaned", "Words");
        var group = await RetainAsync(factory, Snapshot(snapshots[0]));

        // The Version goes (straight in the database, so it is not in retention either).
        TestDatabase.Execute(factory.DataPath, $"UPDATE songs SET current_version_id = NULL; DELETE FROM versions WHERE id = '{Upper(version)}';");
        var retainedBefore = Dump(factory, "retention_records");

        var refused = Assert.IsType<RetentionRestoreOutcome.MissingParent>(await RestoreAsync(factory, group.Id));
        Assert.Equal("The Version this history entry belongs to no longer exists.", refused.Message);
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM editor_revisions;"));
        Assert.Equal(retainedBefore, Dump(factory, "retention_records"));
        Assert.NotNull(await WithServiceAsync(factory, service => service.FindAsync(group.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task RestoringARecordWhoseIdOrUniqueKeyIsHeldByALiveRowIsRefusedAndChangesNothing()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(client, "Clashing", "Words");
        var group = await RetainAsync(factory, Snapshot(snapshots[0]));

        // A live row takes the retained ID.
        TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO editor_revisions (id, version_id, sequence, lyrics, styles, created_utc) VALUES ('{Upper(snapshots[0])}', '{Upper(version)}', 99, 'Interloper', '', '2026-10-01T09:30:00.000Z');");
        var before = Dump(factory, "editor_revisions");

        var refused = Assert.IsType<RetentionRestoreOutcome.Clash>(await RestoreAsync(factory, group.Id));
        Assert.Equal($"A live history entry already has the ID {Upper(snapshots[0])}.", refused.Message);
        Assert.Equal(before, Dump(factory, "editor_revisions"));
        Assert.NotNull(await WithServiceAsync(factory, service => service.FindAsync(group.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task ATestTypeWhoseUniqueKeyIsTakenIsRefusedNamingTheKey()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        CreateTestTables(factory);
        TestDatabase.Execute(factory.DataPath, "CREATE UNIQUE INDEX ux_test_artwork_file ON test_artwork (file);");
        var (version, _, _) = await VersionWithSnapshotsAsync(client, "Unique art");
        var artwork = AddTestArtwork(factory, version, "art/one.png");
        var group = await RetainAsync(factory, new RetentionRequest("test-artwork", "One", null, [new RetainedRoot("test-artwork", artwork)], []));
        AddTestArtwork(factory, version, "art/one.png");

        var refused = Assert.IsType<RetentionRestoreOutcome.Clash>(await RestoreAsync(factory, group.Id));
        Assert.Equal("A live test artwork already holds its file (art/one.png).", refused.Message);
        Assert.Single(Dump(factory, "test_artwork"));
    }

    [Fact]
    public async Task ASnapshotWhoseSequenceWasTakenIsRestoredWithTheNextOneAndSaysSo()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(client, "Ordered", "One", "Two");
        var group = await RetainAsync(factory, Snapshot(snapshots[1]));

        // The next snapshot takes the retained one's sequence number.
        clock.Advance(TimeSpan.FromMinutes(1));
        var newer = await SnapshotAsync(client, version, "Three");
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, $"SELECT sequence FROM editor_revisions WHERE id = '{Upper(newer)}';"));

        var restored = Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        Assert.Equal(["A history entry was given a new sequence, as its own was taken."], restored.Notes);
        Assert.Equal("3|Two", TestDatabase.Scalar(factory.DataPath, $"SELECT sequence || '|' || lyrics FROM editor_revisions WHERE id = '{Upper(snapshots[1])}';"));
        Assert.Equal([newer, snapshots[1], snapshots[0]], await HistoryAsync(client, version));
    }

    [Fact]
    public async Task RestoringIntoAFullHistoryLetsTheFiftyEntryCapTrimTheOldest()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(client, "Full", "Oldest", "Second");
        var group = await RetainAsync(factory, Snapshot(snapshots[1]));
        for (var index = 0; index < EditorRevisionService.MaximumKept - 1; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await SnapshotAsync(client, version, $"Newer {index}");
        }

        Assert.Equal(EditorRevisionService.MaximumKept, (await HistoryAsync(client, version)).Count);
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));

        // The restored entry is the second oldest, so the oldest is trimmed, as a new one would trim it.
        var history = await HistoryAsync(client, version);
        Assert.Equal(EditorRevisionService.MaximumKept, history.Count);
        Assert.Equal(snapshots[1], history[^1]);
        Assert.DoesNotContain(snapshots[0], history);
    }

    [Fact]
    public async Task EntriesTheFiftyEntryCapRemovesArePlainDeletionsAndAreNotRetained()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(client, "Capped", "Oldest");
        for (var index = 0; index < EditorRevisionService.MaximumKept; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await SnapshotAsync(client, version, $"Newer {index}");
        }

        Assert.DoesNotContain(snapshots[0], await HistoryAsync(client, version));
        Assert.Equal("0|0", TestDatabase.Scalar(factory.DataPath, "SELECT (SELECT count(*) FROM retention_groups) || '|' || (SELECT count(*) FROM editor_revisions WHERE lyrics = 'Oldest');"));
    }

    [Fact]
    public async Task RetainingSomethingThatWouldTakeUnretainedRowsWithItIsRefusedAndChangesNothing()
    {
        using var factory = Host(new TestClock(), withNotes: false);
        using var client = await SessionApi.SignedInClientAsync(factory);
        CreateTestTables(factory);
        var (version, _, _) = await VersionWithSnapshotsAsync(client, "Notes not retained");
        var artwork = AddTestArtwork(factory, version, "art/noted.png");
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO test_artwork_notes (artwork_id, position, text) VALUES ('{Upper(artwork)}', 0, 'kept');");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RetainAsync(factory, new RetentionRequest("test-artwork", "Noted", null, [new RetainedRoot("test-artwork", artwork)], [])));
        Assert.Contains("test_artwork_notes, which is not a retained type", refused.Message, StringComparison.Ordinal);
        Assert.Single(Dump(factory, "test_artwork"));
        Assert.Single(Dump(factory, "test_artwork_notes"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
    }

    [Fact]
    public async Task RetainingSomethingAnotherRowRestrictsIsRefusedAndChangesNothing()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(client, "Restricted", "Words");
        TestDatabase.Execute(factory.DataPath, "CREATE TABLE test_restricting (id TEXT PRIMARY KEY, snapshot_id TEXT NOT NULL REFERENCES editor_revisions (id) ON DELETE RESTRICT);");
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO test_restricting (id, snapshot_id) VALUES ('x', '{Upper(snapshots[0])}');");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RetainAsync(factory, Snapshot(snapshots[0])));
        Assert.Contains("Rows of test_restricting refer to the history entry being deleted (RESTRICT)", refused.Message, StringComparison.Ordinal);
        Assert.Equal([snapshots[0]], await HistoryAsync(client, version));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
    }

    [Fact]
    public async Task RetainingNeedsARealRecordAndManagedPathsAndInsideAStoryTransactionTheCallersOne()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (_, _, snapshots) = await VersionWithSnapshotsAsync(client, "Checked", "Words");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RetainAsync(factory, Snapshot(Guid.CreateVersion7())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RetainAsync(factory, new RetentionRequest("x", "x", null, [new RetainedRoot("not-a-type", snapshots[0])], [])));
        foreach (var path in new[] { "../outside.png", "/etc/passwd", "a/../../b", "a//b", "a\\b", "./a", "C:/a", string.Empty })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => RetainAsync(factory, Snapshot(snapshots[0], "n8-1-v1", path)));
        }

        // Without a transaction of the caller's, the store refuses to change anything.
        await Assert.ThrowsAsync<InvalidOperationException>(() => WithServiceAsync(factory, service => service.RetainWithinAsync(Snapshot(snapshots[0]), CancellationToken.None)));

        // Inside one, the retention commits or rolls back with the caller's other changes.
        var scope = factory.Services.CreateAsyncScope();
        await using (scope)
        {
            var transaction = scope.ServiceProvider.GetRequiredService<IExclusiveTransaction>();
            var service = scope.ServiceProvider.GetRequiredService<RetentionService>();
            await Assert.ThrowsAsync<TimeoutException>(() => transaction.RunAsync<bool>(
                async token =>
                {
                    await service.RetainWithinAsync(Snapshot(snapshots[0]), token);
                    throw new TimeoutException("The caller's later step failed.");
                },
                CancellationToken.None));
        }

        Assert.Equal("1|0", TestDatabase.Scalar(factory.DataPath, "SELECT (SELECT count(*) FROM editor_revisions) || '|' || (SELECT count(*) FROM retention_groups);"));
    }

    [Fact]
    public void OnlyManagedPathsAreAcceptedAsGroupFiles()
    {
        Assert.True(RetentionService.IsManagedFilePath("ab/cd/ef.webp"));
        Assert.True(RetentionService.IsManagedFilePath("cover.png"));
        foreach (var path in new[] { null, string.Empty, "/abs", "../up", "a/./b", "a/", "a\\b", "C:x", "a\0b", new string('a', 1025) })
        {
            Assert.False(RetentionService.IsManagedFilePath(path), path);
        }
    }

    [Fact]
    public async Task TheRetainedSnapshotsTextIsKeptOnlyInTheRetentionDocument()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (_, _, snapshots) = await VersionWithSnapshotsAsync(client, "Private", "Secret lyric line");
        var group = await RetainAsync(factory, Snapshot(snapshots[0]));

        // The model the service hands out carries no document.
        Assert.DoesNotContain("Secret lyric line", group.ToString(), StringComparison.Ordinal);
        Assert.Contains("Secret lyric line", TestDatabase.Scalar(factory.DataPath, "SELECT document FROM retention_records;"), StringComparison.Ordinal);

        var scope = factory.Services.CreateAsyncScope();
        await using (scope)
        {
            var model = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>().Model;
            Assert.Equal("document", model.FindEntityType(typeof(Infrastructure.Retention.RetentionRecordRecord))!.FindProperty("Document")!.GetColumnName());
        }

        Assert.True(Infrastructure.Logging.RedactionPolicy.IsSensitive("document"));
    }

    [Fact]
    public async Task RetainedTextNeverReachesTheLogThroughRetainRestoreOrPrune()
    {
        const string Sentinel = "RETAINED-LYRIC-SENTINEL-95";
        var clock = new TestClock();
        using var factory = new Logging.LoggingApiFactory("Debug")
        {
            TestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            },
        };
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (version, _, snapshots) = await VersionWithSnapshotsAsync(client, "Logged", Sentinel);
        var group = await RetainAsync(factory, Snapshot(snapshots[0]));

        // A refused restore (its ID taken), a successful one, and a prune run as the daily job.
        TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO editor_revisions (id, version_id, sequence, lyrics, styles, created_utc) VALUES ('{Upper(snapshots[0])}', '{Upper(version)}', 99, 'Other', '', '2026-10-01T09:30:00.000Z');");
        Assert.IsType<RetentionRestoreOutcome.Clash>(await RestoreAsync(factory, group.Id));
        TestDatabase.Execute(factory.DataPath, "DELETE FROM editor_revisions WHERE sequence = 99;");
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        await RetainAsync(factory, Snapshot(snapshots[0]));
        clock.Advance(TimeSpan.FromDays(30));
        var job = await factory.Services.GetRequiredService<IJobQueue>().EnqueueAsync(RetentionPruneTask.JobType, null, CancellationToken.None);
        await Jobs.TestJobs.WaitUntilAsync(
            () => TestDatabase.Scalar(factory.DataPath, $"SELECT status FROM jobs WHERE id = '{Upper(job)}';") == "succeeded",
            "the prune job");

        // Complement: the prune logged, so the absence below means something.
        Assert.Contains("The retention prune removed 1 deleted groups", factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, factory.CapturedText, StringComparison.Ordinal);
    }
}
