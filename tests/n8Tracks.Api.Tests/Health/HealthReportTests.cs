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
            new HealthComponent(media, "d"));

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

        Assert.Equal(7, details.Count);
        Assert.All(details, detail => Assert.Matches("^[a-z ]{1,20}$", detail));
    }
}
