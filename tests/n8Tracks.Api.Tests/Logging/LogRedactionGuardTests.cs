using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Telemetry;
using n8Tracks.TestSupport;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace n8Tracks.Api.Tests.Logging;

/// <summary>
/// Guard for invariant 6: sensitive data is never logged. Sentinel values are sent in every place a
/// secret travels (an Authorization header, a Cookie header, a query-string token, the JSON body
/// fields <c>lyrics</c> and <c>prompt</c>) and logged as properties with sensitive names; none may
/// reach the captured log, while a non-sensitive sentinel logged in the same call must. With
/// telemetry export on, the same holds for everything the OpenTelemetry collector receives.
/// <para>
/// Not covered: sensitive text written into a message by hand (<c>$"lyrics: {text}"</c>) and objects
/// logged without the destructuring operator (<c>{Song}</c> instead of <c>{@Song}</c>), which are
/// code-review and audit concerns. A story that adds a sensitive field adds its name to
/// <c>RedactionPolicy.SensitiveNames</c>.
/// </para>
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class LogRedactionGuardTests
{
    private const string ProbePath = "/api/probe/song";
    private const string AuthorizationSentinel = "sentinel-authorization-7f3a";
    private const string CookieSentinel = "sentinel-cookie-91bc";
    private const string SetCookieSentinel = "sentinel-set-cookie-5d20";
    private const string QueryTokenSentinel = "sentinel-query-token-c4e8";
    private const string BodyLyricsSentinel = "sentinel-body-lyrics-0a6d";
    private const string BodyPromptSentinel = "sentinel-body-prompt-b719";
    private const string PasswordSentinel = "sentinel-password-3e55";
    private const string LyricsSentinel = "sentinel-lyrics-88f1";
    private const string RawPayloadSentinel = "sentinel-raw-payload-d2c7";
    private const string NestedSentinel = "sentinel-nested-api-key-64ab";
    private const string ScopeSentinel = "sentinel-scope-style-1f90";
    private const string TitleSentinel = "sentinel-title-visible-e3b4";
    private const string SetupPasswordSentinel = "sentinel-setup-password-90d1";
    private const string SetupConfirmationSentinel = "sentinel-setup-confirmation-2b7e";
    private const string SignInPasswordSentinel = "sentinel-sign-in-password-7c41";

    private static readonly string[] SensitiveSentinels =
    [
        AuthorizationSentinel,
        CookieSentinel,
        SetCookieSentinel,
        QueryTokenSentinel,
        BodyLyricsSentinel,
        BodyPromptSentinel,
        PasswordSentinel,
        LyricsSentinel,
        RawPayloadSentinel,
        NestedSentinel,
        ScopeSentinel,
        SetupPasswordSentinel,
        SetupConfirmationSentinel,
    ];

    [Fact]
    public async Task SensitiveSentinelsNeverReachTheLogWhileTheTitleDoes()
    {
        // Debug, so that everything the app can log about the request is in the capture.
        using var factory = new LoggingApiFactory("Debug").WithProbe(ProbePath, LogEverythingAboutTheRequest);

        var requestId = await SendTheSentinels(factory);
        var completion = await factory.CompletionLine(requestId);
        var captured = factory.CapturedText;

        // Complement: the probe's lines were captured, so the absences below mean something.
        Assert.Contains(TitleSentinel, captured, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", captured, StringComparison.Ordinal);
        Assert.Equal(ProbePath, completion.GetProperty("properties").GetProperty("path").GetString());

        Assert.All(SensitiveSentinels, sentinel => Assert.DoesNotContain(sentinel, captured, StringComparison.Ordinal));

        // The object logged with Title, Password, Lyrics, and RawPayload: one visible, three masked.
        var song = Assert.Single(factory.LinesOf(requestId), line => line.GetProperty("properties").TryGetProperty("song", out _))
            .GetProperty("properties").GetProperty("song");
        Assert.Equal(TitleSentinel, song.GetProperty("title").GetString());
        Assert.Equal("[REDACTED]", song.GetProperty("password").GetString());
        Assert.Equal("[REDACTED]", song.GetProperty("lyrics").GetString());
        Assert.Equal("[REDACTED]", song.GetProperty("rawPayload").GetString());
    }

    /// <summary>
    /// The same request with telemetry export on: what the collector receives, on any signal, holds
    /// none of the sensitive sentinels. The log records are the ones the OpenTelemetry sink sent, so
    /// this shows the sink sits behind the redaction policy; the trace and metric payloads show that
    /// the instrumentation exports no header, cookie, or query-string value either.
    /// </summary>
    [Fact]
    public async Task SensitiveSentinelsNeverReachTheCollectorWhileTheTitleDoes()
    {
        await using var collector = StubOtlpCollector.Start();
        string logs;
        string everything;

        using (var factory = new LoggingApiFactory("Debug") { OtlpEndpoint = collector.Endpoint }.WithProbe(ProbePath, LogEverythingAboutTheRequest))
        {
            var requestId = await SendTheSentinels(factory);
            await factory.CompletionLine(requestId);

            // The log sink sends in batches; the span and the metrics are sent when flushed.
            await StubOtlpCollector.Eventually(
                () => collector.ReceivedText(StubOtlpCollector.LogsPath).Contains(TitleSentinel, StringComparison.Ordinal)
                    && collector.ReceivedText(StubOtlpCollector.LogsPath).Contains(ProbePath, StringComparison.Ordinal)
                    && collector.Spans().Any(span => span.Kind == OtlpSpan.ServerKind)
                    && collector.Bodies(StubOtlpCollector.MetricsPath).Count > 0,
                () =>
                {
                    factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
                    factory.Services.GetRequiredService<MeterProvider>().ForceFlush();
                },
                "The collector did not receive the probe's log records, the request's span, and metrics.");
        }

        // The host is disposed: everything it had left to send has been sent.
        logs = collector.ReceivedText(StubOtlpCollector.LogsPath);
        everything = collector.ReceivedText(StubOtlpCollector.LogsPath, StubOtlpCollector.TracesPath, StubOtlpCollector.MetricsPath);

        // Complement: the probe's log records arrived, masked values included, and so did the request's span.
        Assert.Contains(TitleSentinel, logs, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", logs, StringComparison.Ordinal);
        Assert.Contains("Probe logged", logs, StringComparison.Ordinal);
        Assert.Contains(ProbePath, collector.ReceivedText(StubOtlpCollector.TracesPath), StringComparison.Ordinal);

        // The span keeps the query string's names and drops its values.
        Assert.Contains("token=Redacted", collector.ReceivedText(StubOtlpCollector.TracesPath), StringComparison.Ordinal);

        Assert.All(SensitiveSentinels, sentinel => Assert.DoesNotContain(sentinel, everything, StringComparison.Ordinal));
    }

    /// <summary>
    /// The setup submission carries the administrator's password twice. At Debug, whether it is
    /// refused (the two differ) or accepted, neither the password, the confirmation, nor the stored
    /// hash reaches the log, while the username does not need hiding and the request is logged.
    /// </summary>
    [Fact]
    public async Task TheSetupPasswordNeverReachesTheLog()
    {
        using var factory = new LoggingApiFactory("Debug");
        using var client = factory.CreateClient();

        using (var refused = await SetupApi.SubmitAsync(client, "owner", SetupPasswordSentinel, SetupConfirmationSentinel))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            await factory.CompletionLine(LoggingApiFactory.RequestId(refused));
        }

        using (var accepted = await SetupApi.SubmitAsync(client, "owner", SetupPasswordSentinel, SetupPasswordSentinel))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
            var completion = await factory.CompletionLine(LoggingApiFactory.RequestId(accepted));
            Assert.Equal("/api/v1/setup", completion.GetProperty("properties").GetProperty("path").GetString());
        }

        var hash = TestDatabase.Scalar(factory.DataPath, "SELECT password_hash FROM administrators;");
        var captured = factory.CapturedText;

        Assert.Contains("/api/v1/setup", captured, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupPasswordSentinel, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupConfirmationSentinel, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(hash, captured, StringComparison.Ordinal);
    }

    /// <summary>
    /// Signing in carries the password, and every later request carries the session identifier in
    /// a cookie. At Debug, through a refused sign-in, a successful one, a use of the session, and
    /// signing out, neither the password, the identifier, nor its stored hash reaches the log,
    /// while the requests themselves are logged.
    /// </summary>
    [Fact]
    public async Task TheSignInPasswordAndTheSessionIdentifierNeverReachTheLog()
    {
        using var factory = new LoggingApiFactory("Debug");
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        using (var refused = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SignInPasswordSentinel))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            await factory.CompletionLine(LoggingApiFactory.RequestId(refused));
        }

        string token;
        using (var signedIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword))
        {
            Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
            token = SessionApi.SessionToken(signedIn)!;
            await factory.CompletionLine(LoggingApiFactory.RequestId(signedIn));
        }

        using (var used = await client.GetAsync(SessionApi.Session))
        {
            await factory.CompletionLine(LoggingApiFactory.RequestId(used));
        }

        using (var signedOut = await SessionApi.SendAsync(client, HttpMethod.Delete, SessionApi.Session))
        {
            Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
            await factory.CompletionLine(LoggingApiFactory.RequestId(signedOut));
        }

        var captured = factory.CapturedText;
        Assert.Contains("/api/v1/session", captured, StringComparison.Ordinal);
        Assert.Contains("Signed in", captured, StringComparison.Ordinal);
        Assert.DoesNotContain(SignInPasswordSentinel, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupApi.TestPassword, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(token, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(n8Tracks.Application.Auth.SessionToken.Hash(token), captured, StringComparison.Ordinal);
    }

    /// <summary>
    /// A credential's token travels in the Authorization header of every call. At Debug, through a
    /// call the token's scope allows, one it refuses, and one with a token that does not exist,
    /// neither the token nor its stored hash reaches the log, while the requests themselves are logged.
    /// </summary>
    [Fact]
    public async Task AValidTokenAndItsHashNeverReachTheLog()
    {
        using var factory = new LoggingApiFactory("Debug") { TestServices = static services => TestEndpoints.Register(services) };
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, n8Tracks.Application.Credentials.CredentialScopes.CatalogRead);
        var unknown = n8Tracks.Application.Credentials.CredentialToken.Create();

        foreach (var (method, uri, bearer, status) in new[]
        {
            (HttpMethod.Get, TestEndpoints.Read, token, HttpStatusCode.OK),
            (HttpMethod.Post, TestEndpoints.Write, token, HttpStatusCode.Forbidden),
            (HttpMethod.Get, TestEndpoints.Read, unknown, HttpStatusCode.Unauthorized),
        })
        {
            using var response = await CredentialApi.SendAsync(client, method, uri, bearer);
            Assert.Equal(status, response.StatusCode);
            await factory.CompletionLine(LoggingApiFactory.RequestId(response));
        }

        var captured = factory.CapturedText;
        Assert.Contains("/api/v1/test/read", captured, StringComparison.Ordinal);
        Assert.Contains("/api/v1/test/songs", captured, StringComparison.Ordinal);
        Assert.DoesNotContain(token, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(token[4..], captured, StringComparison.Ordinal);
        Assert.DoesNotContain(n8Tracks.Application.Credentials.CredentialToken.Hash(token), captured, StringComparison.Ordinal);
        Assert.DoesNotContain(unknown[4..], captured, StringComparison.Ordinal);
    }

    /// <summary>
    /// The token is in one answer only, to the request that creates the credential. At Debug,
    /// through creating, listing, renaming, revoking, and a call with the token before and after
    /// revocation, neither the token nor its stored hash reaches the log, while the requests and the
    /// credential's ID are logged.
    /// </summary>
    [Fact]
    public async Task ACreatedTokenNeverReachesTheLogThroughTheCredentialScreens()
    {
        using var factory = new LoggingApiFactory("Debug") { TestServices = static services => TestEndpoints.Register(services) };
        using var client = await SessionApi.SignedInClientAsync(factory);
        var credentials = new Uri("/api/v1/credentials", UriKind.Relative);

        using var create = Antiforgery(new HttpRequestMessage(HttpMethod.Post, credentials)
        {
            Content = JsonContent.Create(new { name = "sentinel script", kind = "api", scopes = new[] { "catalog.read" } }),
        });
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await SetupApi.JsonAsync(created);
        var id = body.GetProperty("id").GetString()!;
        var token = body.GetProperty("token").GetString()!;
        await factory.CompletionLine(LoggingApiFactory.RequestId(created));

        using var rename = Antiforgery(new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/credentials/{id}", UriKind.Relative))
        {
            Content = JsonContent.Create(new { name = "renamed" }),
        });
        Assert.True(rename.Headers.TryAddWithoutValidation("If-Match", "\"1\""));
        var revoke = new Uri($"/api/v1/credentials/{id}/revoke", UriKind.Relative);

        foreach (var request in new Func<Task<HttpResponseMessage>>[]
        {
            () => client.GetAsync(credentials),
            () => CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token),
            () => client.SendAsync(rename),
            () => SessionApi.SendAsync(client, HttpMethod.Post, revoke),
            () => CredentialApi.SendAsync(client, HttpMethod.Get, TestEndpoints.Read, token),
        })
        {
            using var response = await request();
            await factory.CompletionLine(LoggingApiFactory.RequestId(response));
        }

        var captured = factory.CapturedText;
        Assert.Contains("Credential created", captured, StringComparison.Ordinal);
        Assert.Contains("Credential revoked", captured, StringComparison.Ordinal);
        Assert.Contains(id, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(token[4..], captured, StringComparison.Ordinal);
        Assert.DoesNotContain(n8Tracks.Application.Credentials.CredentialToken.Hash(token), captured, StringComparison.Ordinal);
    }

    private static HttpRequestMessage Antiforgery(HttpRequestMessage request)
    {
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        return request;
    }

    /// <summary>Sends the request that carries every sentinel and returns its request ID.</summary>
    private static async Task<string> SendTheSentinels(LoggingApiFactory factory)
    {
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{ProbePath}?token={QueryTokenSentinel}&page=2", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AuthorizationSentinel}");
        request.Headers.TryAddWithoutValidation("Cookie", $"session={CookieSentinel}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { title = "A song", lyrics = BodyLyricsSentinel, prompt = BodyPromptSentinel }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return LoggingApiFactory.RequestId(response);
    }

    /// <summary>
    /// Logs the way careless application code might: the object with sensitive properties, then the
    /// whole body, every header, and the query string, each destructured.
    /// </summary>
    private static async Task LogEverythingAboutTheRequest(HttpContext context)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("n8Tracks.Tests.Probe");

        var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted);
        var headers = context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.Ordinal);
        var query = context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);

        using (logger.BeginScope(new Dictionary<string, object> { ["Style"] = ScopeSentinel }))
        {
            logger.LogInformation(
                "Probe logged {@Song}",
                new ProbeSong(TitleSentinel, PasswordSentinel, LyricsSentinel, RawPayloadSentinel));

            logger.LogInformation("Probe logged the body {@Body}, headers {@Headers}, and query {@Query}", body, headers, query);

            logger.LogInformation(
                "Probe logged nested {@Outer} and a named value {AccessToken}",
                new { Provider = new { Name = "suno", Api_Key = NestedSentinel, Items = new[] { new { Prompt = BodyPromptSentinel } } } },
                QueryTokenSentinel);
        }

        context.Response.Headers.Append("Set-Cookie", $"session={SetCookieSentinel}; HttpOnly");
        await context.Response.WriteAsync("ok", context.RequestAborted);
    }

    private sealed record ProbeSong(string Title, string Password, string Lyrics, string RawPayload);
}
