using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Frontend;

namespace n8Tracks.Api.Tests.Frontend;

/// <summary>
/// The backend serving a built frontend, against a fixture web root: an <c>index.html</c> carrying the
/// placeholder and one file under <c>assets/</c>. Each case runs at the root of a hostname (base "")
/// and under a sub-path (base "/n8tracks").
/// </summary>
public class FrontendHostingTests
{
    private const string AssetBody = "console.log('n8Tracks');";

    private const string IndexHtml =
        """
        <!doctype html>
        <html lang="en">
          <head>
            <!--n8tracks-base-->
            <meta charset="utf-8">
            <script type="module" src="./assets/app-abc123.js"></script>
          </head>
          <body><div id="root"></div></body>
        </html>
        """;

    public static TheoryData<string> Bases => ["", "/n8tracks"];

    [Theory]
    [MemberData(nameof(Bases))]
    public async Task TheAppUrlReturnsTheShellWithTheBaseHref(string pathBase)
    {
        using var factory = BuiltFrontend(pathBase);
        using var client = Client(factory);

        using var response = await client.GetAsync(Relative(pathBase + "/"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(IndexHtml.Replace(FrontendHosting.BasePlaceholder, $"<base href=\"{pathBase}/\">", StringComparison.Ordinal), html);
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public async Task ADeepLinkReturnsTheSameShell(string pathBase)
    {
        using var factory = BuiltFrontend(pathBase);
        using var client = Client(factory);
        var shell = await client.GetStringAsync(Relative(pathBase + "/"));

        foreach (var deepLink in new[] { "/songs", "/songs/0198c0de/versions", "/apiary", "/healthy/x", "/index.html", "/songs?tab=lyrics", "/songs/n8-1/v/1.1", "/songs/n8-1/v/1.10.2" })
        {
            using var response = await client.GetAsync(Relative(pathBase + deepLink));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(shell, await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public async Task AHeadRequestGetsTheShellHeadersWithoutABody(string pathBase)
    {
        using var factory = BuiltFrontend(pathBase);
        using var client = Client(factory);
        var shell = await client.GetByteArrayAsync(Relative(pathBase + "/"));

        using var request = new HttpRequestMessage(HttpMethod.Head, Relative(pathBase + "/songs"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(shell.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public async Task AnAssetLoadsAndIsLongLivedImmutable(string pathBase)
    {
        using var factory = BuiltFrontend(pathBase);
        using var client = Client(factory);

        using var response = await client.GetAsync(Relative(pathBase + "/assets/app-abc123.js"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AssetBody, await response.Content.ReadAsStringAsync());
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FrontendHosting.AssetCacheControl, CacheControl(response));
        Assert.True(response.Headers.CacheControl?.MaxAge >= TimeSpan.FromDays(365));
        Assert.Contains("immutable", CacheControl(response), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public async Task TheShellAndOtherFilesMustBeRevalidated(string pathBase)
    {
        using var factory = BuiltFrontend(pathBase);
        using var client = Client(factory);

        foreach (var path in new[] { "/", "/songs", "/favicon.svg" })
        {
            using var response = await client.GetAsync(Relative(pathBase + path));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-cache", CacheControl(response));
        }
    }

    [Fact]
    public async Task TheBasePathWithoutASlashRedirectsToTheSlashedForm()
    {
        using var factory = BuiltFrontend("/n8tracks");
        using var client = Client(factory);

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var request = new HttpRequestMessage(method, Relative("/n8tracks?from=bookmark"));
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
            Assert.Equal("/n8tracks/?from=bookmark", response.Headers.Location?.OriginalString);
        }
    }

    [Fact]
    public async Task AFollowedRedirectEndsAtTheShell()
    {
        using var factory = BuiltFrontend("/n8tracks");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Relative("/n8tracks"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/n8tracks/", response.RequestMessage?.RequestUri?.AbsolutePath);
        Assert.Contains("<base href=\"/n8tracks/\">", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // Complement: what must never be answered with the shell.
    [Theory]
    [InlineData("", "/api/unknown")]
    [InlineData("", "/api")]
    [InlineData("", "/API/unknown")]
    [InlineData("", "/openapi/unknown")]
    [InlineData("", "/health/unknown")]
    [InlineData("", "/assets/missing.js")]
    [InlineData("", "/assets/missing")]
    [InlineData("", "/missing.css")]
    [InlineData("", "/songs/cover.png")]
    [InlineData("", "/songs/n8-1/v/cover.png")]
    [InlineData("", "/songs/n8-1/v/1.png")]
    [InlineData("/n8tracks", "/api/unknown")]
    [InlineData("/n8tracks", "/openapi/unknown")]
    [InlineData("/n8tracks", "/health/unknown")]
    [InlineData("/n8tracks", "/assets/missing.js")]
    [InlineData("/n8tracks", "/missing.css")]
    public async Task ReservedAndFileLikePathsAreNotFoundInsteadOfTheShell(string pathBase, string path)
    {
        using var factory = BuiltFrontend(pathBase);
        using var client = Client(factory);

        using var response = await client.GetAsync(Relative(pathBase + path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("", "POST", "/songs")]
    [InlineData("", "POST", "/")]
    [InlineData("", "PUT", "/songs")]
    [InlineData("", "DELETE", "/songs/1")]
    [InlineData("/n8tracks", "POST", "/songs")]
    [InlineData("/n8tracks", "POST", "/")]
    [InlineData("/n8tracks", "POST", "")]
    public async Task OnlyGetAndHeadGetTheShell(string pathBase, string method, string path)
    {
        using var factory = BuiltFrontend(pathBase);
        using var client = Client(factory);

        using var request = new HttpRequestMessage(new HttpMethod(method), Relative(pathBase + path));
        using var response = await client.SendAsync(request);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/songs")]
    [InlineData("/assets/app-abc123.js")]
    [InlineData("/index.html")]
    public async Task UnderASubPathNothingIsServedOutsideIt(string path)
    {
        using var factory = BuiltFrontend("/n8tracks");
        using var client = Client(factory);

        using var response = await client.GetAsync(Relative(path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public async Task TheHealthEndpointStillAnswersNextToABuiltFrontend(string pathBase)
    {
        using var factory = BuiltFrontend(pathBase);
        using var client = Client(factory);

        using var response = await client.GetAsync(Relative(pathBase + "/health"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [MemberData(nameof(Bases))]
    public async Task WithoutABuiltFrontendTheShellRoutesSaySoAndHealthStillWorks(string pathBase)
    {
        using var factory = Host(pathBase);
        using var client = Client(factory);

        foreach (var path in new[] { "/", "/songs", "/index.html" })
        {
            using var response = await client.GetAsync(Relative(pathBase + path));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(FrontendHosting.NotBuiltMessage, await response.Content.ReadAsStringAsync());
            Assert.Contains("not built", FrontendHosting.NotBuiltMessage, StringComparison.Ordinal);
        }

        using var health = await client.GetAsync(Relative(pathBase + "/health"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task ARequestAnEndpointClaimedIsNeverAnsweredWithTheShell()
    {
        using var factory = BuiltFrontend(string.Empty, services => services.AddSingleton<IStartupFilter>(new ClaimedEndpointFilter()));
        using var client = Client(factory);

        Assert.Equal(ClaimedEndpointFilter.Body, await client.GetStringAsync(Relative(ClaimedEndpointFilter.Path)));
        Assert.Contains("<base href=\"/\">", await client.GetStringAsync(Relative("/unclaimed")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuiltIndexWithoutThePlaceholderGetsTheTagRightAfterHead()
    {
        using var factory = Host("/n8tracks");
        await File.WriteAllTextAsync(
            Path.Combine(factory.WebRootPath, "index.html"),
            "<!doctype html><html><head lang=\"en\"><title>n8Tracks</title></head><body></body></html>");
        using var client = Client(factory);

        var html = await client.GetStringAsync(Relative("/n8tracks/"));

        Assert.Equal(
            "<!doctype html><html><head lang=\"en\"><base href=\"/n8tracks/\"><title>n8Tracks</title></head><body></body></html>",
            html);
    }

    [Fact]
    public async Task TheShellIsReadOnceAtStartup()
    {
        using var factory = BuiltFrontend(string.Empty);
        using var client = Client(factory);
        var shell = await client.GetStringAsync(Relative("/"));

        await File.WriteAllTextAsync(Path.Combine(factory.WebRootPath, "index.html"), "<html><head></head></html>");

        Assert.Equal(shell, await client.GetStringAsync(Relative("/")));
    }

    [Theory]
    [InlineData("<head><!--n8tracks-base--></head>", "", "<head><base href=\"/\"></head>")]
    [InlineData("<head><!--n8tracks-base--></head>", "/apps/n8tracks", "<head><base href=\"/apps/n8tracks/\"></head>")]
    [InlineData("<head><!--n8tracks-base--><!--n8tracks-base--></head>", "/a", "<head><base href=\"/a/\"><!--n8tracks-base--></head>")]
    [InlineData("<HEAD><title>x</title></HEAD>", "/a", "<HEAD><base href=\"/a/\"><title>x</title></HEAD>")]
    [InlineData("<header></header><head></head>", "/a", "<header></header><head><base href=\"/a/\"></head>")]
    [InlineData("<p>no head</p>", "/a", "<p>no head</p>")]
    [InlineData("<head><!--n8tracks-base--></head>", "/a\"b", "<head><base href=\"/a&quot;b/\"></head>")]
    public void TheBaseTagReplacesThePlaceholderOrFollowsHead(string html, string pathBase, string expected)
    {
        Assert.Equal(expected, FrontendHosting.InjectBase(html, pathBase));
    }

    private static N8TracksApiFactory Host(string pathBase, Action<IServiceCollection>? services = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pathBase.Length > 0)
        {
            variables[EnvironmentOptionsLoader.BaseUrl] = "https://nas.example" + pathBase;
        }

        return new N8TracksApiFactory(variables) { TestServices = services };
    }

    private static N8TracksApiFactory BuiltFrontend(string pathBase, Action<IServiceCollection>? services = null)
    {
        var factory = Host(pathBase, services);

        Directory.CreateDirectory(Path.Combine(factory.WebRootPath, "assets"));
        File.WriteAllText(Path.Combine(factory.WebRootPath, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(factory.WebRootPath, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        File.WriteAllText(Path.Combine(factory.WebRootPath, "assets", "app-abc123.js"), AssetBody);

        return factory;
    }

    /// <summary>A client that reports a redirect instead of following it.</summary>
    private static HttpClient Client(N8TracksApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static string CacheControl(HttpResponseMessage response) =>
        string.Join(", ", response.Headers.GetValues("Cache-Control"));

    /// <summary>
    /// Stands in for a future endpoint at a path that looks like a deep link: it selects an endpoint
    /// for the request ahead of the application's pipeline, as routing does for a mapped route.
    /// </summary>
    private sealed class ClaimedEndpointFilter : IStartupFilter
    {
        public const string Path = "/sign-in";

        public const string Body = "answered by an endpoint";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                if (context.Request.Path == Path)
                {
                    context.SetEndpoint(new Endpoint(claimed => claimed.Response.WriteAsync(Body), new EndpointMetadataCollection(new AllowAnonymousAttribute()), "claimed"));
                }

                return following(context);
            });

            next(app);
        };
    }
}
