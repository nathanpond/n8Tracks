using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Extension;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Extension;

/// <summary>
/// <c>GET /api/v1/extension/handshake</c>: any valid token may call it, whatever its scopes. It
/// answers the application version, the credential's name and scopes, and whether the extension's
/// version is compatible, and records the reported versions on the credential, where the
/// Credentials screen shows them.
/// </summary>
public sealed class HandshakeEndpointTests
{
    private static readonly Uri Handshake = new(HandshakeEndpoint.Path, UriKind.Relative);
    private static readonly Uri Credentials = new("/api/v1/credentials", UriKind.Relative);

    [Fact]
    public async Task AValidTokenGetsTheApplicationVersionItsCredentialAndCompatibility()
    {
        var clock = new TestClock();
        using var factory = Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await ExtensionTokenAsync(factory, "Chrome at home", CredentialScopes.CatalogRead, CredentialScopes.SunoSync, CredentialScopes.SunoGenerate);
        var sameMinor = SameMinorAs(ProductVersion.Current);

        using var response = await SendAsync(client, token, sameMinor, "7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var body = await SetupApi.JsonAsync(response);
        Assert.Equal(["applicationVersion", "compatible", "credentialName", "scopes"], body.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ProductVersion.Current, body.GetProperty("applicationVersion").GetString());
        Assert.Equal("Chrome at home", body.GetProperty("credentialName").GetString());
        Assert.Equal(["catalog.read", "suno.sync", "suno.generate"], Strings(body.GetProperty("scopes")));
        Assert.True(body.GetProperty("compatible").GetBoolean());

        // Complement: the versions the headers carried are stored on the credential, with the time, and its revision is unchanged.
        var shown = await CredentialAsync(client, "Chrome at home");
        Assert.Equal(sameMinor, shown.GetProperty("lastExtensionVersion").GetString());
        Assert.Equal("7", shown.GetProperty("lastAdapterVersion").GetString());
        Assert.Equal(TestClock.Start.UtcDateTime, shown.GetProperty("lastSeenAt").GetDateTime());
        Assert.Equal(1, shown.GetProperty("revision").GetInt32());

        // A later handshake replaces the sighting.
        clock.Advance(TimeSpan.FromMinutes(5));
        using (var again = await SendAsync(client, token, "999.0.0", "8"))
        {
            var answer = await SetupApi.JsonAsync(again);
            Assert.False(answer.GetProperty("compatible").GetBoolean());
        }

        shown = await CredentialAsync(client, "Chrome at home");
        Assert.Equal("999.0.0", shown.GetProperty("lastExtensionVersion").GetString());
        Assert.Equal("8", shown.GetProperty("lastAdapterVersion").GetString());
        Assert.Equal(TestClock.Start.AddMinutes(5).UtcDateTime, shown.GetProperty("lastSeenAt").GetDateTime());
    }

    [Fact]
    public async Task ATokenWithoutTheSunoScopesStillGetsAnAnswerNamingTheScopesItHas()
    {
        using var factory = Host(new TestClock());
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await ExtensionTokenAsync(factory, "read only", CredentialScopes.CatalogRead);

        using var response = await SendAsync(client, token, ProductVersion.Current, "1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await SetupApi.JsonAsync(response);
        Assert.Equal(["catalog.read"], Strings(body.GetProperty("scopes")));
        Assert.Equal("read only", body.GetProperty("credentialName").GetString());
    }

    [Fact]
    public async Task ARevokedTokenIsRefusedWith401AndNothingIsRecorded()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var created = Assert.IsType<CredentialOutcome.Created>(await CredentialApi.CreateAsync(
            factory,
            new CredentialRequest("revoked one", CredentialKinds.Extension, [CredentialScopes.SunoSync])));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<CredentialService>().RevokeAsync(created.Id, CancellationToken.None);
        }

        using var response = await SendAsync(client, created.Token, ProductVersion.Current, "1");

        await SetupApi.ProblemAsync(response, HttpStatusCode.Unauthorized, BearerAuthenticationHandler.InvalidTokenCode);
        var shown = await CredentialAsync(client, "revoked one");
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("lastExtensionVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("lastSeenAt").ValueKind);

        // Complement: an unknown token is refused the same way.
        using var unknown = await SendAsync(client, CredentialToken.Create(), ProductVersion.Current, "1");
        await SetupApi.ProblemAsync(unknown, HttpStatusCode.Unauthorized, BearerAuthenticationHandler.InvalidTokenCode);
    }

    [Fact]
    public async Task MissingOrUnreadableHeadersAreStoredAsNullAndAreNeverCompatible()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await ExtensionTokenAsync(factory, "no headers", CredentialScopes.SunoGenerate);

        using (var response = await SendAsync(client, token, extensionVersion: null, adapterVersion: null))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False((await SetupApi.JsonAsync(response)).GetProperty("compatible").GetBoolean());
        }

        var shown = await CredentialAsync(client, "no headers");
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("lastExtensionVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("lastAdapterVersion").ValueKind);
        Assert.Equal(JsonValueKind.String, shown.GetProperty("lastSeenAt").ValueKind);

        // Too long to keep: stored as null, and not compatible.
        using (var response = await SendAsync(client, token, new string('1', 65), "2"))
        {
            Assert.False((await SetupApi.JsonAsync(response)).GetProperty("compatible").GetBoolean());
        }

        shown = await CredentialAsync(client, "no headers");
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("lastExtensionVersion").ValueKind);
        Assert.Equal("2", shown.GetProperty("lastAdapterVersion").GetString());
    }

    [Fact]
    public async Task ABrowserSessionIsRefusedBecauseItHasNoCredential()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(Handshake);

        await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, HandshakeEndpoint.CredentialRequiredCode);

        // Complement: without any sign-in it is the usual 401.
        using var anonymous = factory.CreateClient();
        using var refused = await anonymous.GetAsync(Handshake);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task EveryScopeOnItsOwnMayMakeTheHandshake()
    {
        using var factory = Host(new TestClock());
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        foreach (var scope in CredentialScopes.All)
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var response = await SendAsync(client, token, ProductVersion.Current, "1");
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{scope}: {response.StatusCode}");
        }
    }

    [Theory]
    [InlineData("1.4.0", "1.4.7", true)]
    [InlineData("1.4.7", "1.4.0", true)]
    [InlineData("1.4", "1.4.2", true)]
    [InlineData("1.4", "1.5", false)]
    [InlineData("1.4.2", "1.5.0", false)]
    [InlineData("1.9.9", "2.9.9", false)]
    [InlineData("2.0.0", "1.0.0", false)]
    [InlineData("1.4.0-rc.1", "1.4.3", true)]
    [InlineData("1.4.0", "1.4.0-edge.abc1234", true)]
    [InlineData("1.5.0-rc.1", "1.4.0", false)]
    [InlineData("v1.4.0", "1.4.0", false)]
    [InlineData("1", "1.0.0", false)]
    [InlineData("", "1.0.0", false)]
    [InlineData(null, "1.0.0", false)]
    [InlineData("1.4.0", "0.0.0-dev", false)]
    public void CompatibleWhenMajorAndMinorAreEqual(string? extension, string application, bool compatible) =>
        Assert.Equal(compatible, ExtensionCompatibility.IsCompatible(extension, application));

    [Theory]
    [InlineData(" 1.2.3 ", "1.2.3")]
    [InlineData("7", "7")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("1.2.3é", null)]
    [InlineData("1.2 3", null)]
    public void AReportedVersionIsKeptOnlyWhenItIsShortVisibleAscii(string? header, string? kept) =>
        Assert.Equal(kept, ExtensionHandshakeService.Reported(header));

    private static N8TracksApiFactory Host(TestClock clock) =>
        new()
        {
            TestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            },
        };

    private static async Task<string> ExtensionTokenAsync(N8TracksApiFactory factory, string name, params string[] scopes)
    {
        var outcome = await CredentialApi.CreateAsync(factory, new CredentialRequest(name, CredentialKinds.Extension, scopes));
        return Assert.IsType<CredentialOutcome.Created>(outcome).Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string? extensionVersion, string? adapterVersion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Handshake);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (extensionVersion is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(HandshakeEndpoint.ExtensionVersionHeader, extensionVersion));
        }

        if (adapterVersion is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(HandshakeEndpoint.AdapterVersionHeader, adapterVersion));
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> CredentialAsync(HttpClient client, string name)
    {
        using var list = await client.GetAsync(Credentials);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await SetupApi.JsonAsync(list);
        return Assert.Single(body.EnumerateArray(), credential => credential.GetProperty("name").GetString() == name);
    }

    /// <summary>A different patch of the same major.minor, so compatibility is not mere string equality.</summary>
    private static string SameMinorAs(string version)
    {
        var parts = version.Split('-')[0].Split('.');
        return $"{parts[0]}.{parts[1]}.4321";
    }

    private static List<string?> Strings(JsonElement array) => [.. array.EnumerateArray().Select(static item => item.GetString())];
}
