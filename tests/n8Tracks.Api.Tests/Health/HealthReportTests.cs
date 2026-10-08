using n8Tracks.Application.Health;

namespace n8Tracks.Api.Tests.Health;

public class HealthReportTests
{
    private const HealthStatus H = HealthStatus.Healthy;
    private const HealthStatus D = HealthStatus.Degraded;
    private const HealthStatus U = HealthStatus.Unhealthy;

    [Theory]
    [InlineData(H, H, H, H, H)]
    [InlineData(H, H, H, D, D)]
    [InlineData(D, H, H, H, D)]
    [InlineData(D, D, D, D, D)]
    [InlineData(H, U, H, H, U)]
    [InlineData(H, H, U, H, U)]
    [InlineData(H, U, H, D, U)]
    [InlineData(D, D, D, U, U)]
    [InlineData(U, U, U, U, U)]
    public void TheOverallStatusIsTheWorstComponentStatus(
        HealthStatus application,
        HealthStatus database,
        HealthStatus migrations,
        HealthStatus media,
        HealthStatus expected)
    {
        var report = new HealthReport(
            new HealthComponent(application, "a"),
            new HealthComponent(database, "b"),
            new MigrationsHealthComponent(migrations, "c", null),
            new HealthComponent(media, "d"),
            new HealthComponent(H, "f"),
            new HealthComponent(H, "e"));

        Assert.Equal(expected, report.Status);
    }

    [Fact]
    public void MaintenanceIsDegradedNotUnhealthy()
    {
        var report = new HealthReport(
            new HealthComponent(H, HealthDetails.ApplicationRunning),
            new HealthComponent(D, HealthDetails.DatabaseInMaintenance),
            new MigrationsHealthComponent(H, HealthDetails.MigrationsUpToDate, null),
            new HealthComponent(H, HealthDetails.MediaAvailable),
            new HealthComponent(H, HealthDetails.JobsIdle),
            new HealthComponent(D, HealthDetails.MaintenanceRestoring));

        Assert.Equal(D, report.Status);
    }

    [Theory]
    [InlineData(HealthDetails.MigrationsPending)]
    [InlineData(HealthDetails.MigrationsAhead)]
    [InlineData(HealthDetails.MigrationsUnknown)]
    public void DuringMaintenanceAnUnhealthySchemaCountsAsDegraded(string detail)
    {
        var report = Report(new MigrationsHealthComponent(U, detail, null), maintenance: new HealthComponent(D, HealthDetails.MaintenanceRestoring));

        Assert.Equal(U, report.Migrations.Status);
        Assert.Equal(D, report.Status);

        // Complement: out of maintenance the same schema makes the instance unhealthy.
        Assert.Equal(U, Report(new MigrationsHealthComponent(U, detail, null), maintenance: new HealthComponent(H, HealthDetails.MaintenanceOff)).Status);
    }

    [Fact]
    public void DuringMaintenanceAStoppedJobWorkerIsStillUnhealthy()
    {
        var report = Report(
            new MigrationsHealthComponent(H, HealthDetails.MigrationsUpToDate, null),
            jobs: new HealthComponent(U, HealthDetails.JobsStopped),
            maintenance: new HealthComponent(D, HealthDetails.MaintenanceRestoring));

        Assert.Equal(U, report.Status);
    }

    [Theory]
    [InlineData(H, H)]
    [InlineData(D, D)]
    [InlineData(U, U)]
    public void TheJobsComponentCountsLikeAnyOther(HealthStatus jobs, HealthStatus expected)
    {
        var report = Report(new MigrationsHealthComponent(H, HealthDetails.MigrationsUpToDate, null), jobs: new HealthComponent(jobs, "x"));

        Assert.Equal(expected, report.Status);
    }

    [Fact]
    public void AggregationDoesNotDependOnTheOrderOfTheComponents()
    {
        Assert.Equal(U, HealthReport.Aggregate([U, D, H]));
        Assert.Equal(U, HealthReport.Aggregate([H, D, U]));
        Assert.Equal(D, HealthReport.Aggregate([D, H]));
        Assert.Equal(D, HealthReport.Aggregate([H, D]));
        Assert.Equal(H, HealthReport.Aggregate([]));
    }

    [Fact]
    public void EveryDetailIsAShortConstantWithoutPathOrPunctuation()
    {
        var details = typeof(HealthDetails).GetFields()
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(19, details.Count);
        Assert.All(details, detail => Assert.Matches("^[a-z ]{1,20}$", detail));
    }

    private static HealthReport Report(MigrationsHealthComponent migrations, HealthComponent? jobs = null, HealthComponent? maintenance = null) =>
        new(
            new HealthComponent(H, HealthDetails.ApplicationRunning),
            new HealthComponent(H, HealthDetails.DatabaseReachable),
            migrations,
            new HealthComponent(H, HealthDetails.MediaAvailable),
            jobs ?? new HealthComponent(H, HealthDetails.JobsIdle),
            maintenance ?? new HealthComponent(H, HealthDetails.MaintenanceOff));
}
