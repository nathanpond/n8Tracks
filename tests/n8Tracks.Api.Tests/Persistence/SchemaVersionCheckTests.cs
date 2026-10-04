using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Persistence;

public sealed class SchemaVersionCheckTests
{
    private static readonly string[] Known = ["2026_A", "2026_B", "2026_C"];

    [Theory]
    [InlineData("", false)]
    [InlineData("2026_A", true)]
    [InlineData("2026_A,2026_B", true)]
    [InlineData("2026_A,2026_B,2026_C", true)]
    public void AHistoryThatIsTheStartOfTheKnownMigrationsIsAccepted(string applied, bool hasOtherTables)
    {
        Assert.Null(SchemaVersionCheck.Refusal(Known, Split(applied), hasOtherTables));
    }

    [Theory]
    [InlineData("2026_B", "2026_A")]
    [InlineData("2026_A,2026_C", "2026_B")]
    [InlineData("2026_C", "2026_A, 2026_B")]
    public void AGapBeforeTheLastAppliedMigrationIsRefused(string applied, string missing)
    {
        var refusal = SchemaVersionCheck.Refusal(Known, Split(applied), hasOtherTables: true);

        Assert.NotNull(refusal);
        Assert.Contains("not a database this application can upgrade", refusal, StringComparison.Ordinal);
        Assert.Contains(missing, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TablesWithoutAnyHistoryAreRefused()
    {
        var refusal = SchemaVersionCheck.Refusal(Known, [], hasOtherTables: true);

        Assert.NotNull(refusal);
        Assert.Contains("not a database this application can upgrade", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026_A,2026_B,2026_C,2027_D")]
    [InlineData("2026_A,2027_D")]
    public void AnAppliedMigrationThisBuildDoesNotKnowMeansTheDatabaseIsNewer(string applied)
    {
        var refusal = SchemaVersionCheck.Refusal(Known, Split(applied), hasOtherTables: true);

        Assert.NotNull(refusal);
        Assert.Contains("newer than the application", refusal, StringComparison.Ordinal);
        Assert.Contains("2027_D", refusal, StringComparison.Ordinal);
        Assert.Contains("2026_C", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AppMetadata", "app_metadata")]
    [InlineData("Key", "key")]
    [InlineData("LastAppliedMigrationId", "last_applied_migration_id")]
    [InlineData("SunoURLPath", "suno_url_path")]
    [InlineData("PK_app_metadata", "pk_app_metadata")]
    [InlineData("IX_Song_ArtistId", "ix_song_artist_id")]
    [InlineData("Track2Id", "track2_id")]
    [InlineData("already_snake", "already_snake")]
    public void NamesAreConvertedToSnakeCase(string name, string expected)
    {
        Assert.Equal(expected, SnakeCaseNamingConvention.ToSnakeCase(name));
    }

    private static string[] Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
