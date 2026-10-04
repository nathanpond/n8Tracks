using System.Net;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Configuration;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Tests;

public class PathBaseTests
{
    [Theory]
    [InlineData("https://nas.example/n8tracks", "/n8tracks/health", HttpStatusCode.OK)]
    [InlineData("https://nas.example/n8tracks", "/health", HttpStatusCode.NotFound)]
    [InlineData("https://nas.example/n8tracks/", "/n8tracks/health", HttpStatusCode.OK)]
    [InlineData("https://nas.example/n8tracks", "/N8Tracks/health", HttpStatusCode.OK)]
    [InlineData("https://nas.example/n8tracks", "/n8tracksx/health", HttpStatusCode.NotFound)]
    [InlineData("https://nas.example/n8tracks", "/n8tracks", HttpStatusCode.NotFound)]
    [InlineData("https://nas.example/n8tracks", "/", HttpStatusCode.NotFound)]
    [InlineData("https://nas.example/apps/n8tracks", "/apps/n8tracks/health", HttpStatusCode.OK)]
    [InlineData("https://nas.example/apps/n8tracks", "/n8tracks/health", HttpStatusCode.NotFound)]
    [InlineData("https://nas.example", "/health", HttpStatusCode.OK)]
    [InlineData("https://nas.example", "/n8tracks/health", HttpStatusCode.NotFound)]
    [InlineData("https://nas.example/", "/health", HttpStatusCode.OK)]
    [InlineData(null, "/health", HttpStatusCode.OK)]
    [InlineData(null, "/n8tracks/health", HttpStatusCode.NotFound)]
    public async Task RoutesAreServedOnlyUnderTheBaseUrlPath(string? baseUrl, string requestPath, HttpStatusCode expected)
    {
        using var factory = CreateFactory(baseUrl);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(requestPath, UriKind.Relative));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ARequestOutsideThePrefixGetsAnEmpty404()
    {
        using var factory = CreateFactory("https://nas.example/n8tracks");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void TheOptionsObjectIsRegisteredOnceAndCarriesThePathBase()
    {
        using var factory = CreateFactory("https://nas.example/n8tracks/");

        var options = factory.Services.GetRequiredService<N8TracksOptions>();

        Assert.Same(options, factory.Services.GetRequiredService<N8TracksOptions>());
        Assert.Equal("/n8tracks", options.PathBase);
        Assert.Equal(new Uri("https://nas.example/n8tracks"), options.BaseUrl);
        Assert.Equal(factory.DataPath, options.DataPath);
    }

    private static N8TracksApiFactory CreateFactory(string? baseUrl)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (baseUrl is not null)
        {
            variables[EnvironmentOptionsLoader.BaseUrl] = baseUrl;
        }

        return new N8TracksApiFactory(variables);
    }
}
