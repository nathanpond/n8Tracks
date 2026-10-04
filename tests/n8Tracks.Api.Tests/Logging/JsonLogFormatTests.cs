using System.Text.Json;
using n8Tracks.Infrastructure.Logging;
using Serilog;
using Serilog.Context;
using Serilog.Events;

namespace n8Tracks.Api.Tests.Logging;

/// <summary>The JSON shape and the masking rules, exercised on a logger built like the application's.</summary>
public sealed class JsonLogFormatTests : IDisposable
{
    private readonly StringWriter output = new();
    private readonly Serilog.Core.Logger logger;

    public JsonLogFormatTests()
    {
        logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new JsonLinesSink(output))
            .Enrich.FromLogContext()
            .WithRedaction()
            .CreateLogger();
    }

    public void Dispose()
    {
        logger.Dispose();
        output.Dispose();
    }

    [Fact]
    public void ALineHasTheAgreedKeysAndIsOneLine()
    {
        logger.Information("Imported {Count} songs from {Source}\nsecond line", 3, "suno");

        var text = output.ToString();
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Count(character => character == '\n'));

        var line = SingleLine();
        LogLineAssert.HasTheLogShape(line);
        Assert.False(line.TryGetProperty("exception", out _));
        Assert.Equal("Information", line.GetProperty("level").GetString());
        Assert.Equal("Imported 3 songs from suno\nsecond line", line.GetProperty("message").GetString());
        Assert.Equal(3, line.GetProperty("properties").GetProperty("count").GetInt32());
        Assert.Equal("suno", line.GetProperty("properties").GetProperty("source").GetString());
    }

    [Fact]
    public void TheTimestampIsUtcWithMillisecondsAndAZSuffix()
    {
        var local = new DateTimeOffset(2026, 3, 4, 23, 30, 15, 987, TimeSpan.FromHours(5)).AddTicks(6543);
        logger.Write(new LogEvent(local, LogEventLevel.Information, null, new Serilog.Parsing.MessageTemplateParser().Parse("A line"), []));

        Assert.Equal("2026-03-04T18:30:15.987Z", SingleLine().GetProperty("timestamp").GetString());
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, "Trace")]
    [InlineData(LogEventLevel.Debug, "Debug")]
    [InlineData(LogEventLevel.Information, "Information")]
    [InlineData(LogEventLevel.Warning, "Warning")]
    [InlineData(LogEventLevel.Error, "Error")]
    [InlineData(LogEventLevel.Fatal, "Critical")]
    public void LevelsUseMicrosoftsNames(LogEventLevel level, string expected)
    {
        logger.Write(level, "A line");

        Assert.Equal(expected, SingleLine().GetProperty("level").GetString());
    }

    [Fact]
    public void AnExceptionIsWrittenWithTypeScrubbedMessageAndFramesOnly()
    {
        Exception thrown;
        try
        {
            try
            {
                throw new FormatException("inner failed: token=inner-secret-value");
            }
            catch (FormatException cause)
            {
                throw new InvalidOperationException("""outer failed {"password":"outer-secret-value","id":7}""", cause);
            }
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }

        logger.Error(thrown, "Import failed");

        var text = output.ToString();
        Assert.DoesNotContain("inner-secret-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("outer-secret-value", text, StringComparison.Ordinal);

        var line = SingleLine();
        LogLineAssert.HasTheLogShape(line);
        var exceptionJson = line.GetProperty("exception");
        Assert.Equal("System.InvalidOperationException", exceptionJson.GetProperty("type").GetString());
        Assert.Equal("""outer failed {"password":"[REDACTED]","id":7}""", exceptionJson.GetProperty("message").GetString());
        Assert.Contains(nameof(AnExceptionIsWrittenWithTypeScrubbedMessageAndFramesOnly), exceptionJson.GetProperty("stackTrace").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("failed", exceptionJson.GetProperty("stackTrace").GetString(), StringComparison.Ordinal);

        var inner = exceptionJson.GetProperty("innerException");
        Assert.Equal("System.FormatException", inner.GetProperty("type").GetString());
        Assert.Equal("inner failed: token=[REDACTED]", inner.GetProperty("message").GetString());
        Assert.False(inner.TryGetProperty("innerException", out _));
    }

    [Fact]
    public void SensitivePropertiesAreMaskedAtEveryDepthAndInEveryContainer()
    {
        var logged = new
        {
            Title = "visible-title",
            Password = "s3cr3t-1",
            Nested = new { ApiKey = "s3cr3t-2", Name = "visible-name" },
            List = new[] { new { Lyrics = "s3cr3t-3", Index = 1 } },
            Map = new Dictionary<string, object> { ["refresh_token"] = "s3cr3t-4", ["kept"] = "visible-map" },
            Json = JsonSerializer.SerializeToElement(new { prompt = "s3cr3t-5", items = new[] { new { style = "s3cr3t-6", id = 9 } } }),
        };

        logger.Information("Logged {@Thing} with {ProviderPayload} and {RawPayload}", logged, new { A = "s3cr3t-7" }, "s3cr3t-8");

        var text = output.ToString();
        Assert.DoesNotContain("s3cr3t", text, StringComparison.Ordinal);

        var properties = SingleLine().GetProperty("properties");
        var thing = properties.GetProperty("thing");
        Assert.Equal("visible-title", thing.GetProperty("title").GetString());
        Assert.Equal("[REDACTED]", thing.GetProperty("password").GetString());
        Assert.Equal("[REDACTED]", thing.GetProperty("nested").GetProperty("apiKey").GetString());
        Assert.Equal("visible-name", thing.GetProperty("nested").GetProperty("name").GetString());
        Assert.Equal("[REDACTED]", thing.GetProperty("list")[0].GetProperty("lyrics").GetString());
        Assert.Equal(1, thing.GetProperty("list")[0].GetProperty("index").GetInt32());
        Assert.Equal("[REDACTED]", thing.GetProperty("map").GetProperty("refresh_token").GetString());
        Assert.Equal("visible-map", thing.GetProperty("map").GetProperty("kept").GetString());
        Assert.Equal("[REDACTED]", thing.GetProperty("json").GetProperty("prompt").GetString());
        Assert.Equal("[REDACTED]", thing.GetProperty("json").GetProperty("items")[0].GetProperty("style").GetString());
        Assert.Equal(9, thing.GetProperty("json").GetProperty("items")[0].GetProperty("id").GetInt32());
        Assert.Equal("[REDACTED]", properties.GetProperty("providerPayload").GetString());
        Assert.Equal("[REDACTED]", properties.GetProperty("rawPayload").GetString());
    }

    [Fact]
    public void ASensitiveNullStaysNullAndACancellationTokenIsNotMasked()
    {
        logger.Information("Logged {@Thing} {CancellationToken}", new { Password = (string?)null, Lyrics = "words" }, "not-a-secret");

        var properties = SingleLine().GetProperty("properties");
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("thing").GetProperty("password").ValueKind);
        Assert.Equal("[REDACTED]", properties.GetProperty("thing").GetProperty("lyrics").GetString());
        Assert.Equal("not-a-secret", properties.GetProperty("cancellationToken").GetString());
    }

    [Fact]
    public void LogContextPropertiesAreMasked()
    {
        using (LogContext.PushProperty("SessionCookie", "s3cr3t-context"))
        using (LogContext.PushProperty("Workspace", "visible-workspace"))
        {
            logger.Information("Inside the context");
        }

        Assert.DoesNotContain("s3cr3t", output.ToString(), StringComparison.Ordinal);
        var properties = SingleLine().GetProperty("properties");
        Assert.Equal("[REDACTED]", properties.GetProperty("sessionCookie").GetString());
        Assert.Equal("visible-workspace", properties.GetProperty("workspace").GetString());
    }

    [Fact]
    public void DeepLongAndLargeValuesAreCut()
    {
        var deep = new { L1 = new { L2 = new { L3 = new { L4 = new { L5 = new { L6 = "too-deep" } } } } } };
        var json = JsonSerializer.SerializeToElement(new
        {
            text = new string('x', 5000),
            many = Enumerable.Range(0, 200).ToArray(),
            deep,
        });

        logger.Information("Logged {@Deep} {@Json} {@Many}", deep, json, Enumerable.Range(0, 200).ToArray());

        Assert.DoesNotContain("too-deep", output.ToString(), StringComparison.Ordinal);
        var properties = SingleLine().GetProperty("properties");
        Assert.Equal(RedactionPolicy.MaximumStringLength, properties.GetProperty("json").GetProperty("text").GetString()!.Length);
        Assert.Equal(RedactionPolicy.MaximumCollectionCount, properties.GetProperty("json").GetProperty("many").GetArrayLength());
        Assert.Equal(RedactionPolicy.MaximumCollectionCount, properties.GetProperty("many").GetArrayLength());
    }

    private JsonElement SingleLine() =>
        JsonSerializer.Deserialize<JsonElement>(Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)));
}
