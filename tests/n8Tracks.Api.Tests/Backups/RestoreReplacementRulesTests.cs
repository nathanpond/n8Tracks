using n8Tracks.Application.Backups;
using n8Tracks.Application.Maintenance;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Api.Tests.Backups;

/// <summary>The pure rules of a restore's replacement: where an archive's files may be unpacked, and which safety backups are kept.</summary>
public sealed class RestoreReplacementRulesTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "n8tracks-staging-rules");

    [Fact]
    public void TheDatabaseHasAFixedNameAndAssetsStayUnderTheAssetsFolder()
    {
        Assert.Equal(Path.Combine(Folder, "n8tracks.db"), RestoreArchives.StagedPath(Folder, "n8tracks.db"));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Folder, "assets", "art", "cover.png")),
            RestoreArchives.StagedPath(Folder, "assets/art/cover.png"));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Folder, "assets", "..cover", "a..b.png")),
            RestoreArchives.StagedPath(Folder, "assets/..cover/a..b.png"));

        // What the database already holds, and anything else, is not unpacked.
        Assert.Null(RestoreArchives.StagedPath(Folder, "settings.json"));
        Assert.Null(RestoreArchives.StagedPath(Folder, "manifest.json"));
        Assert.Null(RestoreArchives.StagedPath(Folder, "../n8tracks.db"));
    }

    [Theory]
    [InlineData("assets/")]
    [InlineData("assets/../n8tracks.db")]
    [InlineData("assets/art/../../../escape.txt")]
    [InlineData("assets/./cover.png")]
    [InlineData("assets//cover.png")]
    [InlineData("assets/art/")]
    [InlineData("assets/..\\..\\escape.txt")]
    [InlineData("assets/C:/escape.txt")]
    [InlineData("assets/line\nbreak.txt")]
    public void AnAssetPathThatIsNotPlainlyRelativeIsRefused(string entry) =>
        Assert.Throws<RestoreStagingException>(() => RestoreArchives.StagedPath(Folder, entry));

    [Fact]
    public void TheThreeNewestSafetyBackupsAreKeptAndNothingElseIsCounted()
    {
        var start = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        BackupArchive Archive(string name, int hours, string? kind, BackupValidity validity = BackupValidity.Valid) =>
            new(BackupLocation.Data, name, 100, start.AddHours(hours), "0.1.0", kind, validity);
        var archives = new[]
        {
            Archive("s1", 1, "safety"),
            Archive("s2", 2, "safety"),
            Archive("s3", 3, "safety"),
            Archive("s4", 4, "safety"),
            Archive("s5", 5, "safety"),
            Archive("m1", 0, "manual"),
            Archive("sc1", 0, "scheduled"),
            Archive("bad", 0, "safety", BackupValidity.Invalid),
            Archive("newer", 0, null, BackupValidity.Newer),
        };

        Assert.Equal(["s1", "s2"], BackupRetention.SelectSafety(archives).Select(static archive => archive.Name));

        // The archive a restore reads is never selected.
        Assert.Equal(["s2"], BackupRetention.SelectSafety(archives, archives[0]).Select(static archive => archive.Name));

        // Three or fewer: none.
        Assert.Empty(BackupRetention.SelectSafety(archives.Skip(2)));

        // Scheduled retention does not count safety backups either.
        Assert.Empty(BackupRetention.Select(archives, keep: 1));
    }

    [Fact]
    public void ARollbackFailureKeepsMaintenanceOnAcrossARestart()
    {
        var store = new MemoryStore();
        var mode = new MaintenanceMode(store);
        Assert.True(mode.TryBegin(MaintenanceStage.Validating));
        mode.Report(MaintenanceStage.Migrating, 40);
        mode.Stall();

        Assert.Equal(new MaintenanceSnapshot(true, MaintenanceStage.Migrating, 40, MaintenanceOutcome.RollbackFailed), mode.Current);
        Assert.False(mode.TryBegin(MaintenanceStage.Validating));
        Assert.Equal("rollback-failed", MaintenanceSnapshot.OutcomeText(MaintenanceOutcome.RollbackFailed));
        Assert.Equal(MaintenanceOutcome.RollbackFailed, MaintenanceSnapshot.ParseOutcome("rollback-failed"));

        // A new process reads the same state and stays closed.
        var restarted = new MaintenanceMode(store);
        Assert.True(restarted.IsActive);
        Assert.False(restarted.RecoveredAtStart);
        Assert.Equal(MaintenanceOutcome.RollbackFailed, restarted.Current.Outcome);
    }

    private sealed class MemoryStore : IMaintenanceStateStore
    {
        private MaintenanceSnapshot? stored;

        public MaintenanceSnapshot? Read() => stored;

        public void Write(MaintenanceSnapshot snapshot) => stored = snapshot;
    }
}
