using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Application.Health;
using n8Tracks.Infrastructure.Health;

namespace n8Tracks.Api.Tests.Health;

/// <summary>
/// The <c>migrations</c> component of <c>GET /health</c> is checked when asked (#237): the history
/// is changed under a running host, and the component follows within 30 seconds of the test clock.
/// </summary>
public sealed class MigrationsHealthTests
{
    private const string FutureMigration = "29991231235959_FromANewerVersion";

    private static readonly Uri Health = new("/health", UriKind.Relative);

    [Fact]
    public async Task ADatabaseMissingAMigrationIsPendingAndUnhealthyAndRecoversOnceItIsBack()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = factory.CreateClient();

        var before = Migrations(await Report(client, HttpStatusCode.OK));
        Assert.Equal("up to date", before.GetProperty("detail").GetString());

        var last = TestDatabase.History(factory.DataPath)[^1].Split('|');
        TestDatabase.Execute(factory.DataPath, $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{last[0]}';");

        // Read at most every 30 seconds: the change is not seen yet.
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal("up to date", Migrations(await Report(client, HttpStatusCode.OK)).GetProperty("detail").GetString());

        clock.Advance(TimeSpan.FromSeconds(2));
        var report = await Report(client, HttpStatusCode.ServiceUnavailable);
        Assert.Equal("unhealthy", report.GetProperty("status").GetString());
        var pending = Migrations(report);
        Assert.Equal("unhealthy", pending.GetProperty("status").GetString());
        Assert.Equal("pending", pending.GetProperty("detail").GetString());

        // The existing fields are kept, as captured at startup.
        Assert.Equal(before.GetProperty("lastApplied").GetString(), pending.GetProperty("lastApplied").GetString());
        Assert.Equal(before.GetProperty("lastOutcome").GetString(), pending.GetProperty("lastOutcome").GetString());
        Assert.Equal(
            ["detail", "lastApplied", "lastOutcome", "lastSafetyBackupAt", "status"],
            pending.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));

        TestDatabase.Execute(factory.DataPath, $"INSERT INTO \"__EFMigrationsHistory\" VALUES ('{last[0]}', '{last[1]}');");
        clock.Advance(TimeSpan.FromSeconds(31));
        var recovered = await Report(client, HttpStatusCode.OK);
        Assert.Equal("up to date", Migrations(recovered).GetProperty("detail").GetString());
        Assert.Equal("healthy", recovered.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ADatabaseStampedWithALaterVersionIsAheadAndUnhealthy()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = factory.CreateClient();

        await Report(client, HttpStatusCode.OK);
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO \"__EFMigrationsHistory\" VALUES ('{FutureMigration}', '99.0.0');");
        clock.Advance(TimeSpan.FromSeconds(31));

        var report = await Report(client, HttpStatusCode.ServiceUnavailable);
        var ahead = Migrations(report);
        Assert.Equal("unhealthy", ahead.GetProperty("status").GetString());
        Assert.Equal("ahead", ahead.GetProperty("detail").GetString());

        // The body names no migration but the one captured at startup.
        Assert.DoesNotContain(FutureMigration, report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADatabaseWithoutAnyHistoryIsPending()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = factory.CreateClient();

        await Report(client, HttpStatusCode.OK);
        TestDatabase.Execute(factory.DataPath, "DROP TABLE \"__EFMigrationsHistory\";");
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal("pending", Migrations(await Report(client, HttpStatusCode.ServiceUnavailable)).GetProperty("detail").GetString());
    }

    [Fact]
    public void TheComparisonTellsPendingFromAhead()
    {
        string[] known = ["1_A", "2_B", "3_C"];

        Assert.Equal(HealthDetails.MigrationsUpToDate, MigrationsHealthCheck.Compare(known, ["1_A", "2_B", "3_C"]));
        Assert.Equal(HealthDetails.MigrationsPending, MigrationsHealthCheck.Compare(known, ["1_A", "2_B"]));
        Assert.Equal(HealthDetails.MigrationsPending, MigrationsHealthCheck.Compare(known, []));
        Assert.Equal(HealthDetails.MigrationsPending, MigrationsHealthCheck.Compare(known, ["1_A", "3_C"]));
        Assert.Equal(HealthDetails.MigrationsAhead, MigrationsHealthCheck.Compare(known, ["1_A", "2_B", "3_C", "4_D"]));
        Assert.Equal(HealthDetails.MigrationsAhead, MigrationsHealthCheck.Compare(known, ["1_A", "4_D"]));
    }

    private static N8TracksApiFactory Host(TestClock clock) =>
        new()
        {
            TestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            },
        };

    private static async Task<JsonElement> Report(HttpClient client, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(Health);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(expected == response.StatusCode, $"Expected {expected}, got {response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static JsonElement Migrations(JsonElement report) => report.GetProperty("components").GetProperty("migrations");
}
