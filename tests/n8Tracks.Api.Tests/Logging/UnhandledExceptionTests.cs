using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace n8Tracks.Api.Tests.Logging;

public sealed class UnhandledExceptionTests
{
    private const string MessageSentinel = "sentinel-exception-token-42aa";
    private const string InnerSentinel = "sentinel-inner-lyrics-97cd";

    [Fact]
    public async Task AnUnhandledExceptionIsLoggedOnceAtErrorAndAnsweredWithAProblemDetails500()
    {
        using var factory = new LoggingApiFactory().WithProbe("/probe/throw", _ => throw new InvalidOperationException(
            $"Provider call failed: token={MessageSentinel}",
            new FormatException($$"""Bad payload {"lyrics":"{{InnerSentinel}}"}""")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/probe/throw?token=abc", UriKind.Relative));

        // The response: Problem Details, the request ID, and nothing about the exception.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var requestId = LoggingApiFactory.RequestId(response);
        var bodyText = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(bodyText);
        Assert.Equal(500, body.GetProperty("status").GetInt32());
        Assert.Equal("internal_error", body.GetProperty("code").GetString());
        Assert.Equal(requestId, body.GetProperty("requestId").GetString());
        Assert.False(body.TryGetProperty("detail", out _));
        Assert.DoesNotContain("Provider call failed", bodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", bodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", bodyText, StringComparison.Ordinal);
        Assert.DoesNotContain(MessageSentinel, bodyText, StringComparison.Ordinal);

        // The log: the exception once, at Error, with type and stack trace, its messages scrubbed.
        var completion = await factory.CompletionLine(requestId);
        var withException = Assert.Single(factory.Lines(), line => line.TryGetProperty("exception", out _));
        LogLineAssert.HasTheLogShape(withException);
        Assert.Equal("Error", withException.GetProperty("level").GetString());
        Assert.Equal(requestId, LoggingApiFactory.Property(withException, "requestId"));
        Assert.Equal(
            ["requestId", "sourceContext"],
            withException.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        var exception = withException.GetProperty("exception");
        Assert.Equal("System.InvalidOperationException", exception.GetProperty("type").GetString());
        Assert.Equal("Provider call failed: token=[REDACTED]", exception.GetProperty("message").GetString());
        Assert.Contains("   at ", exception.GetProperty("stackTrace").GetString(), StringComparison.Ordinal);
        Assert.Equal("System.FormatException", exception.GetProperty("innerException").GetProperty("type").GetString());
        Assert.Equal("""Bad payload {"lyrics":"[REDACTED]"}""", exception.GetProperty("innerException").GetProperty("message").GetString());

        var captured = factory.CapturedText;
        Assert.DoesNotContain(MessageSentinel, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(InnerSentinel, captured, StringComparison.Ordinal);
        Assert.Contains("Provider call failed", captured, StringComparison.Ordinal);

        // The completion line is still written, at Error, without the exception.
        Assert.Equal("Error", completion.GetProperty("level").GetString());
        Assert.Equal(500, completion.GetProperty("properties").GetProperty("status").GetInt32());
        Assert.Equal("/probe/throw", completion.GetProperty("properties").GetProperty("path").GetString());
        Assert.False(completion.TryGetProperty("exception", out _));
        Assert.Equal(2, factory.LinesOf(requestId).Count);
    }

    [Fact]
    public async Task ARequestTheClientAbortsIsLoggedAtDebugAndIsNotAnError()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new LoggingApiFactory("Debug").WithProbe("/probe/wait", async context =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        using var client = factory.CreateClient();
        using var abort = new CancellationTokenSource();

        var pending = client.GetAsync(new Uri("/probe/wait", UriKind.Relative), abort.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var completion = await factory.WaitForLine(line =>
            LoggingApiFactory.IsCompletion(line) && line.GetProperty("properties").GetProperty("path").GetString() == "/probe/wait");

        Assert.Equal("Debug", completion.GetProperty("level").GetString());
        Assert.Equal(499, completion.GetProperty("properties").GetProperty("status").GetInt32());

        var lines = factory.LinesOf(LoggingApiFactory.Property(completion, "requestId")!);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal("Debug", line.GetProperty("level").GetString()));
        Assert.All(lines, line => Assert.False(line.TryGetProperty("exception", out _)));
        Assert.Contains(lines, line => line.GetProperty("message").GetString() == "The client aborted the request");
        Assert.DoesNotContain(factory.Lines(), line => line.GetProperty("level").GetString() is "Error" or "Critical" or "Warning");
    }

    [Fact]
    public async Task AnExceptionAfterTheResponseStartedIsStillLoggedOnce()
    {
        using var factory = new LoggingApiFactory().WithProbe("/probe/late", async context =>
        {
            await context.Response.WriteAsync("partial");
            await context.Response.Body.FlushAsync();
            throw new InvalidOperationException("late failure");
        });
        using var client = factory.CreateClient();

        try
        {
            using var response = await client.GetAsync(new Uri("/probe/late", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead);
            await response.Content.ReadAsStringAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            // The connection is cut: the client must not take the partial body for a whole one.
        }

        var withException = await factory.WaitForLine(line => line.TryGetProperty("exception", out _));
        Assert.Equal("Error", withException.GetProperty("level").GetString());
        Assert.Equal("late failure", withException.GetProperty("exception").GetProperty("message").GetString());

        var completion = await factory.CompletionLine(LoggingApiFactory.Property(withException, "requestId")!);
        Assert.Equal("Error", completion.GetProperty("level").GetString());
        Assert.Single(factory.Lines(), line => line.TryGetProperty("exception", out _));
    }
}
