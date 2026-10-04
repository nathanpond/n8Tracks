using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace n8Tracks.Api.Tests.Logging;

/// <summary>
/// Guard for invariant 6: sensitive data is never logged. Sentinel values are sent in every place a
/// secret travels (an Authorization header, a Cookie header, a query-string token, the JSON body
/// fields <c>lyrics</c> and <c>prompt</c>) and logged as properties with sensitive names; none may
/// reach the captured log, while a non-sensitive sentinel logged in the same call must.
/// <para>
/// Not covered: sensitive text written into a message by hand (<c>$"lyrics: {text}"</c>) and objects
/// logged without the destructuring operator (<c>{Song}</c> instead of <c>{@Song}</c>), which are
/// code-review and audit concerns. A story that adds a sensitive field adds its name to
/// <c>RedactionPolicy.SensitiveNames</c>.
/// </para>
/// </summary>
public sealed class LogRedactionGuardTests
{
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
    ];

    [Fact]
    public async Task SensitiveSentinelsNeverReachTheLogWhileTheTitleDoes()
    {
        // Debug, so that everything the app can log about the request is in the capture.
        using var factory = new LoggingApiFactory("Debug").WithProbe("/api/probe/song", LogEverythingAboutTheRequest);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/probe/song?token={QueryTokenSentinel}&page=2", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AuthorizationSentinel}");
        request.Headers.TryAddWithoutValidation("Cookie", $"session={CookieSentinel}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { title = "A song", lyrics = BodyLyricsSentinel, prompt = BodyPromptSentinel }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requestId = LoggingApiFactory.RequestId(response);
        var completion = await factory.CompletionLine(requestId);
        var captured = factory.CapturedText;

        // Complement: the probe's lines were captured, so the absences below mean something.
        Assert.Contains(TitleSentinel, captured, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", captured, StringComparison.Ordinal);
        Assert.Equal("/api/probe/song", completion.GetProperty("properties").GetProperty("path").GetString());

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
