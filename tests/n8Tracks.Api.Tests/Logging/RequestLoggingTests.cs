using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace n8Tracks.Api.Tests.Logging;

public sealed class RequestLoggingTests
{
    private static readonly string[] AllLevels = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    [Fact]
    public async Task ARequestIsLoggedWithMethodPathStatusAndDurationAndNothingElse()
    {
        using var factory = new LoggingApiFactory().WithProbe("/probe/echo", context => context.Response.WriteAsync("response-body-text"));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/probe/echo?q=query-value&token=abc", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("User-Agent", "probe-agent/1.0");
        request.Headers.TryAddWithoutValidation("X-Custom", "custom-header-value");
        request.Content = new StringContent("request-body-text", Encoding.UTF8, "text/plain");
        using var response = await client.SendAsync(request);

        var line = await factory.CompletionLine(LoggingApiFactory.RequestId(response));

        LogLineAssert.HasTheLogShape(line);
        Assert.Equal("Information", line.GetProperty("level").GetString());

        var properties = line.GetProperty("properties");
        Assert.Equal("POST", properties.GetProperty("method").GetString());
        Assert.Equal("/probe/echo", properties.GetProperty("path").GetString());
        Assert.Equal(200, properties.GetProperty("status").GetInt32());
        Assert.True(properties.GetProperty("durationMs").GetDouble() >= 0);
        Assert.Equal(
            ["durationMs", "method", "path", "requestId", "sourceContext", "status"],
            properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.StartsWith("POST /probe/echo responded 200 in ", line.GetProperty("message").GetString(), StringComparison.Ordinal);

        var captured = factory.CapturedText;
        Assert.DoesNotContain("?", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("query-value", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("request-body-text", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("response-body-text", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("probe-agent", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("custom-header-value", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLoggedPathIsTheOneTheClientSentIncludingTheSubPath()
    {
        using var factory = new LoggingApiFactory("Debug", "https://nas.example/n8tracks");
        using var client = factory.CreateClient();

        using var inside = await client.GetAsync(new Uri("/n8tracks/health?probe=1", UriKind.Relative));
        using var outside = await client.GetAsync(new Uri("/elsewhere?x=1", UriKind.Relative));

        var insideLine = await factory.CompletionLine(LoggingApiFactory.RequestId(inside));
        var outsideLine = await factory.CompletionLine(LoggingApiFactory.RequestId(outside));

        Assert.Equal("/n8tracks/health", insideLine.GetProperty("properties").GetProperty("path").GetString());
        Assert.Equal(200, insideLine.GetProperty("properties").GetProperty("status").GetInt32());
        Assert.Equal("/elsewhere", outsideLine.GetProperty("properties").GetProperty("path").GetString());
        Assert.Equal(404, outsideLine.GetProperty("properties").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task HealthRequestsAreLoggedAtDebugAndOthersAtInformation()
    {
        using var factory = new LoggingApiFactory("Debug");
        using var client = factory.CreateClient();

        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative));
        using var missing = await client.GetAsync(new Uri("/missing", UriKind.Relative));

        var healthLine = await factory.CompletionLine(LoggingApiFactory.RequestId(health));
        var missingLine = await factory.CompletionLine(LoggingApiFactory.RequestId(missing));

        Assert.Equal("Debug", healthLine.GetProperty("level").GetString());
        Assert.Equal("Information", missingLine.GetProperty("level").GetString());
        Assert.Equal(404, missingLine.GetProperty("properties").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task AtTheDefaultLevelHealthRequestsAreNotLogged()
    {
        using var factory = new LoggingApiFactory();
        using var client = factory.CreateClient();

        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative));
        using var missing = await client.GetAsync(new Uri("/missing", UriKind.Relative));

        // The later request's line is there, so the earlier one's would be too.
        await factory.CompletionLine(LoggingApiFactory.RequestId(missing));
        Assert.Empty(factory.LinesOf(LoggingApiFactory.RequestId(health)));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task AServerErrorResponseIsLoggedAtError(int status)
    {
        using var factory = new LoggingApiFactory().WithProbe("/probe/status", context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/probe/status", UriKind.Relative));

        var line = await factory.CompletionLine(LoggingApiFactory.RequestId(response));
        Assert.Equal("Error", line.GetProperty("level").GetString());
        Assert.Equal(status, line.GetProperty("properties").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task EveryResponseCarriesAServerGeneratedRequestIdThatIsOnItsLogLines()
    {
        using var factory = new LoggingApiFactory("Debug", "https://nas.example/n8tracks").WithProbe("/probe/log", context =>
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("n8Tracks.Tests.Probe").LogInformation("Inside the request");
            return Task.CompletedTask;
        });
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/n8tracks/probe/log", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Request-ID", "client-chosen-id");
        using var first = await client.SendAsync(request);
        using var health = await client.GetAsync(new Uri("/n8tracks/health", UriKind.Relative));
        using var refused = await client.GetAsync(new Uri("/outside", UriKind.Relative));

        var ids = new[] { first, health, refused }.Select(LoggingApiFactory.RequestId).ToList();
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.True(Guid.TryParse(id, out _), $"'{id}' is not a server-generated ID."));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        foreach (var id in ids)
        {
            await factory.CompletionLine(id);
        }

        // Both the application's own line and the completion line carry the ID.
        var lines = factory.LinesOf(ids[0]);
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, line => line.GetProperty("message").GetString() == "Inside the request");
        Assert.DoesNotContain("client-chosen-id", factory.CapturedText, StringComparison.Ordinal);

        // Enrichment is the source and the request ID only.
        var inside = Assert.Single(lines, line => !LoggingApiFactory.IsCompletion(line));
        Assert.Equal(
            ["requestId", "sourceContext"],
            inside.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Trace", "Trace", "Warning")]
    [InlineData("Debug", "Debug", "Warning")]
    [InlineData("Information", "Information", "Warning")]
    [InlineData("Warning", "Warning", "Warning")]
    [InlineData("Error", "Error", "Error")]
    [InlineData("Critical", "Critical", "Critical")]
    public async Task TheMinimumLevelComesFromTheSettingAndFrameworkCategoriesAreHeldAtWarning(
        string configured,
        string lowestForTheApp,
        string lowestForTheFramework)
    {
        using var factory = new LoggingApiFactory(configured).WithProbe("/probe/levels", context =>
        {
            var loggers = context.RequestServices.GetRequiredService<ILoggerFactory>();
            foreach (var category in new[] { "n8Tracks.Probe", "Microsoft.Probe", "Microsoft", "System.Probe", "MicrosoftLookalike.Probe" })
            {
                var logger = loggers.CreateLogger(category);
                foreach (var level in Enum.GetValues<LogLevel>().Where(level => level != LogLevel.None))
                {
                    logger.Log(level, "level-probe");
                }
            }

            return Task.CompletedTask;
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/probe/levels", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The Critical line of the last category is written before the response is sent.
        var written = factory.Lines()
            .Where(line => line.GetProperty("message").GetString() == "level-probe")
            .ToLookup(
                line => line.GetProperty("properties").GetProperty("sourceContext").GetString()!,
                line => line.GetProperty("level").GetString()!);

        Assert.Equal(From(lowestForTheApp), written["n8Tracks.Probe"]);
        Assert.Equal(From(lowestForTheApp), written["MicrosoftLookalike.Probe"]);
        Assert.Equal(From(lowestForTheFramework), written["Microsoft.Probe"]);
        Assert.Equal(From(lowestForTheFramework), written["Microsoft"]);
        Assert.Equal(From(lowestForTheFramework), written["System.Probe"]);
    }

    [Fact]
    public async Task EveryCapturedLineHasTheLogShape()
    {
        using var factory = new LoggingApiFactory("Trace");
        using var client = factory.CreateClient();

        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative));
        await factory.CompletionLine(LoggingApiFactory.RequestId(health));

        var lines = factory.Lines();
        Assert.NotEmpty(lines);
        Assert.All(lines, LogLineAssert.HasTheLogShape);
        Assert.All(factory.CapturedText.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith("{", line, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Equal(JsonValueKind.Object, line.ValueKind));
    }

    private static IEnumerable<string> From(string lowest) => AllLevels.SkipWhile(level => level != lowest);
}
