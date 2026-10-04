using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using n8Tracks.Gateway.Health;

namespace n8Tracks.Gateway.Tests;

/// <summary>The gateway's <c>GET /health</c> against a fake upstream put in place of the network.</summary>
public sealed class GatewayHealthTests : IDisposable
{
    private const string UpstreamHost = "n8tracks.test";

    private readonly StubUpstream upstream = new();
    private GatewayFactory? factory;

    public void Dispose()
    {
        factory?.Dispose();
        upstream.Dispose();
    }

    [Fact]
    public async Task AReachableUpstreamOfTheSameVersionIsHealthy()
    {
        upstream.RespondWith(() => StubUpstream.Health(ProductVersion.Current));

        using var response = await Gateway().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await Body(response);
        Assert.Equal(["status", "upstream", "version", "compatible"], body.EnumerateObject().Select(property => property.Name));
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal("reachable", body.GetProperty("upstream").GetString());
        Assert.Equal(ProductVersion.Current, body.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.True, body.GetProperty("compatible").ValueKind);

        var request = Assert.Single(upstream.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri($"http://{UpstreamHost}:8787/health"), request.RequestUri);
        Assert.Empty(factory!.GatewayLog);
    }

    [Theory]
    [InlineData("http://n8tracks.test/apps/n8tracks", "http://n8tracks.test/apps/n8tracks/health")]
    [InlineData("https://n8tracks.test/n8tracks/", "https://n8tracks.test/n8tracks/health")]
    public async Task TheProbeKeepsTheSubPathOfTheApiUrl(string apiUrl, string expected)
    {
        using var response = await Gateway(("N8TRACKS_API_URL", apiUrl)).GetAsync("/health");

        Assert.Equal(new Uri(expected), Assert.Single(upstream.Requests).RequestUri);
    }

    [Fact]
    public async Task AnUpstreamThatIsDownIsDegradedAndUnreachableButStillAnswers200()
    {
        upstream.Fail(new HttpRequestException(HttpRequestError.ConnectionError, $"Connection refused ({UpstreamHost}:8787)"));

        using var response = await Gateway().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await Body(response);
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal("unreachable", body.GetProperty("upstream").GetString());
        Assert.Equal(ProductVersion.Current, body.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("compatible").ValueKind);
    }

    [Fact]
    public async Task TheFailureReasonIsNeitherInTheResponseNorTheUrlInTheLog()
    {
        upstream.Fail(new HttpRequestException(HttpRequestError.ConnectionError, $"Connection refused ({UpstreamHost}:8787)"));

        using var response = await Gateway().GetAsync("/health");
        var text = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("refused", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UpstreamHost, text, StringComparison.OrdinalIgnoreCase);

        var line = Assert.Single(factory!.GatewayLog);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("unreachable", line.Message, StringComparison.Ordinal);
        Assert.All(factory.Log, entry => Assert.DoesNotContain(UpstreamHost, entry.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AFailedProbeIsLoggedOnceUntilTheUpstreamChanges()
    {
        upstream.Fail(new HttpRequestException(HttpRequestError.ConnectionError));
        var client = Gateway();

        await Health(client);
        await Health(client);
        await Health(client);

        Assert.Single(factory!.GatewayLog);

        upstream.RespondWith(() => StubUpstream.Health(ProductVersion.Current));
        await Health(client);
        upstream.Fail(new HttpRequestException(HttpRequestError.ConnectionError));
        await Health(client);

        Assert.Equal(
            [LogLevel.Warning, LogLevel.Information, LogLevel.Warning],
            factory.GatewayLog.Select(entry => entry.Level));
    }

    [Fact]
    public async Task TheUpstreamIsAskedAfreshOnEveryRequestAndNothingIsCached()
    {
        var client = Gateway();

        upstream.RespondWith(() => StubUpstream.Health(ProductVersion.Current));
        Assert.Equal("healthy", (await Health(client)).GetProperty("status").GetString());

        upstream.Fail(new HttpRequestException(HttpRequestError.ConnectionError));
        Assert.Equal("unreachable", (await Health(client)).GetProperty("upstream").GetString());

        upstream.RespondWith(() => StubUpstream.Health(OtherMinor()));
        Assert.Equal(JsonValueKind.False, (await Health(client)).GetProperty("compatible").ValueKind);

        upstream.RespondWith(() => StubUpstream.Health(ProductVersion.Current));
        Assert.Equal("healthy", (await Health(client)).GetProperty("status").GetString());
        Assert.Equal("healthy", (await Health(client)).GetProperty("status").GetString());

        Assert.Equal(5, upstream.Requests.Count);
    }

    [Fact]
    public async Task ADifferentPatchIsCompatible()
    {
        Assert.True(ProductVersion.TryReadMajorMinor(ProductVersion.Current, out var major, out var minor));
        upstream.RespondWith(() => StubUpstream.Health($"{major}.{minor}.987-edge.abc1234"));

        var body = await Health(Gateway());

        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.True, body.GetProperty("compatible").ValueKind);
        Assert.Empty(factory!.GatewayLog);
    }

    [Fact]
    public async Task ADifferentMinorOrMajorIsIncompatibleAndDegraded()
    {
        Assert.True(ProductVersion.TryReadMajorMinor(ProductVersion.Current, out var major, out var minor));
        var client = Gateway();

        foreach (var version in new[] { OtherMinor(), $"{major + 1}.{minor}.0" })
        {
            upstream.RespondWith(() => StubUpstream.Health(version));

            var body = await Health(client);

            Assert.Equal("degraded", body.GetProperty("status").GetString());
            Assert.Equal("reachable", body.GetProperty("upstream").GetString());
            Assert.Equal(ProductVersion.Current, body.GetProperty("version").GetString());
            Assert.Equal(JsonValueKind.False, body.GetProperty("compatible").ValueKind);
        }
    }

    [Fact]
    public async Task AMismatchWritesOneWarningWhenFirstDetectedAndAnInformationLineWhenItEnds()
    {
        var client = Gateway();
        upstream.RespondWith(() => StubUpstream.Health(OtherMinor()));

        await Health(client);
        await Health(client);
        await Health(client);

        var warning = Assert.Single(factory!.GatewayLog);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("Version mismatch", warning.Message, StringComparison.Ordinal);
        Assert.Contains(ProductVersion.Current, warning.Message, StringComparison.Ordinal);

        upstream.RespondWith(() => StubUpstream.Health(ProductVersion.Current));
        await Health(client);
        await Health(client);
        upstream.RespondWith(() => StubUpstream.Health(OtherMinor()));
        await Health(client);

        Assert.Equal(
            [LogLevel.Warning, LogLevel.Information, LogLevel.Warning],
            factory.GatewayLog.Select(entry => entry.Level));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AnyHttpStatusCountsAsReachable(HttpStatusCode status)
    {
        upstream.RespondWith(() => StubUpstream.Health(ProductVersion.Current, status));

        var body = await Health(Gateway());

        Assert.Equal("reachable", body.GetProperty("upstream").GetString());
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.True, body.GetProperty("compatible").ValueKind);
    }

    [Fact]
    public async Task ARedirectIsAnAnswerAndIsNotFollowed()
    {
        upstream.RespondWith(() => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("http://elsewhere.test/health") },
        });

        var body = await Health(Gateway());

        Assert.Equal("reachable", body.GetProperty("upstream").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("compatible").ValueKind);
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("[]")]
    [InlineData("\"0.1.0\"")]
    [InlineData("{}")]
    [InlineData("""{"status":"healthy"}""")]
    [InlineData("""{"version":null}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":{"major":0,"minor":1}}""")]
    [InlineData("""{"version":""}""")]
    [InlineData("""{"version":"latest"}""")]
    [InlineData("""{"version":"v0.1.0"}""")]
    [InlineData("""{"Version":"0.1.0"}""")]
    [InlineData("""{"version":"0.1.0" """)]
    public async Task AReachableUpstreamWithoutAReadableVersionIsDegradedWithCompatibleNull(string upstreamBody)
    {
        upstream.RespondWith(() => StubUpstream.Json(upstreamBody));
        var client = Gateway();

        var body = await Health(client);
        await Health(client);

        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal("reachable", body.GetProperty("upstream").GetString());
        Assert.Equal(ProductVersion.Current, body.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("compatible").ValueKind);

        var warning = Assert.Single(factory!.GatewayLog);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("readable version", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyOfExactlyTheLimitIsReadAndALongerOneIsNot()
    {
        var client = Gateway();
        var start = "{\"version\":\"" + ProductVersion.Current + "\",\"padding\":\"";
        const string end = "\"}";

        string BodyOfLength(int length) => start + new string('x', length - start.Length - end.Length) + end;

        upstream.RespondWith(() => StubUpstream.Json(BodyOfLength(UpstreamHealthClient.MaxBodyBytes)));
        Assert.Equal(JsonValueKind.True, (await Health(client)).GetProperty("compatible").ValueKind);

        upstream.RespondWith(() => StubUpstream.Json(BodyOfLength(UpstreamHealthClient.MaxBodyBytes + 1)));
        var tooLong = await Health(client);
        Assert.Equal("reachable", tooLong.GetProperty("upstream").GetString());
        Assert.Equal(JsonValueKind.Null, tooLong.GetProperty("compatible").ValueKind);

        Assert.Equal(64 * 1024, UpstreamHealthClient.MaxBodyBytes);
    }

    [Fact]
    public async Task AnUpstreamThatDoesNotAnswerWithinThreeSecondsIsUnreachable()
    {
        var gaveUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Respond = async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => gaveUp.TrySetResult());
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        };
        var client = Gateway();

        var watch = Stopwatch.StartNew();
        var body = await Health(client);
        watch.Stop();

        Assert.Equal(TimeSpan.FromSeconds(3), UpstreamHealthClient.Budget);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(15));
        Assert.True(gaveUp.Task.IsCompleted, "The gateway did not cancel its request.");
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.Equal("unreachable", body.GetProperty("upstream").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("compatible").ValueKind);
        Assert.Contains("timed out", Assert.Single(factory!.GatewayLog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheThreeSecondsCoverTheBodyToo()
    {
        upstream.RespondWith(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });

        var watch = Stopwatch.StartNew();
        var body = await Health(Gateway());
        watch.Stop();

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(15));
        Assert.Equal("unreachable", body.GetProperty("upstream").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("compatible").ValueKind);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    public async Task OnlyGetIsAllowed(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/health");

        using var response = await Gateway().SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task HealthIsTheOnlyEndpointAndThereIsNoMcpEndpoint()
    {
        var client = Gateway();

        var routes = factory!.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText);
        Assert.Equal(["/health"], routes);

        foreach (var path in new[] { "/", "/mcp", "/sse", "/message", "/openapi/v1.json", "/api/v1/health" })
        {
            using var get = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

            using var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");
            using var post = await client.PostAsync(path, content);
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        }

        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task TheLogLevelSettingHidesLowerLevels()
    {
        upstream.RespondWith(() => StubUpstream.Health(OtherMinor()));

        await Health(Gateway(("N8TRACKS_LOG_LEVEL", "Error")));

        Assert.Empty(factory!.Log);
    }

    [Fact]
    public async Task FrameworkCategoriesAreHeldAtWarningEvenAtTrace()
    {
        upstream.RespondWith(() => StubUpstream.Health(OtherMinor()));

        await Health(Gateway(("N8TRACKS_LOG_LEVEL", "Trace")));

        Assert.Contains(factory!.GatewayLog, entry => entry.Level == LogLevel.Warning);
        Assert.DoesNotContain(
            factory.Log,
            entry => entry.Level < LogLevel.Warning
                && (entry.Category.StartsWith("Microsoft", StringComparison.Ordinal) || entry.Category.StartsWith("System", StringComparison.Ordinal)));
    }

    private static string OtherMinor()
    {
        Assert.True(ProductVersion.TryReadMajorMinor(ProductVersion.Current, out var major, out var minor));
        return $"{major}.{minor + 1}.0";
    }

    private static async Task<JsonElement> Health(HttpClient client)
    {
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Body(response);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private HttpClient Gateway(params (string Name, string Value)[] variables)
    {
        factory = new GatewayFactory(upstream, variables);
        return factory.CreateClient();
    }

    /// <summary>A response body that never arrives.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
