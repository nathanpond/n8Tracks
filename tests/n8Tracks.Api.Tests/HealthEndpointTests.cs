using System.Net;

namespace n8Tracks.Api.Tests;

public class HealthEndpointTests(N8TracksApiFactory factory) : IClassFixture<N8TracksApiFactory>
{
    [Fact]
    public async Task HealthReturnsOk()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
