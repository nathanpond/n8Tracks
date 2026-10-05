using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;

namespace n8Tracks.Api.Tests.Inventory;

/// <summary>
/// <c>GET /api/v1/suno/create-fields</c>: the embedded inventory's fields as captured, each with the
/// Version option it is stored in and Suno's explanation, and the model list, so the web app restates
/// no limit, range, list, or help text.
/// </summary>
public sealed class CreateFieldsEndpointTests
{
    private static readonly Uri CreateFields = new("/api/v1/suno/create-fields", UriKind.Relative);

    [Fact]
    public async Task ServesEveryInventoryFieldAsCapturedWithItsOptionAndHelp()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(CreateFields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var answer = await SetupApi.JsonAsync(response);

        var inventory = CreateFieldInventory.Embedded;
        var fields = answer.GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(inventory.Fields.Select(static field => field.Key), fields.Select(static field => field.GetProperty("key").GetString()));

        // Each field is the inventory's own record, untouched, plus option and help.
        using var captured = JsonDocument.Parse(inventory.Json);
        var records = captured.RootElement.GetProperty("fields").EnumerateArray().ToList();
        for (var i = 0; i < records.Count; i++)
        {
            foreach (var property in records[i].EnumerateObject())
            {
                Assert.True(JsonElement.DeepEquals(property.Value, fields[i].GetProperty(property.Name)), property.Name);
            }

            Assert.Equal(records[i].EnumerateObject().Count() + 2, fields[i].EnumerateObject().Count());
        }

        var byKey = fields.ToDictionary(static field => field.GetProperty("key").GetString()!, StringComparer.Ordinal);
        Assert.Equal(10, byKey["duration_seconds"].GetProperty("min").GetInt32());
        Assert.Equal(360, byKey["duration_seconds"].GetProperty("max").GetInt32());
        Assert.Equal("seconds", byKey["duration_seconds"].GetProperty("unit").GetString());
        Assert.Equal(1000, byKey["simple_prompt"].GetProperty("maxLength").GetInt32());

        // option: the key of the Version's inputs that stores the field, as the API spells it.
        Assert.Equal("excludeStyles", byKey["exclude_styles"].GetProperty("option").GetString());
        Assert.Equal("simpleLyricsAdded", byKey["simple_add_lyrics"].GetProperty("option").GetString());
        Assert.Equal("durationSeconds", byKey["duration_seconds"].GetProperty("option").GetString());
        foreach (var key in VersionInputRules.Keys.Where(static key => VersionInputRules.InventoryKey(key) is not null))
        {
            Assert.Equal(key, byKey[VersionInputRules.InventoryKey(key)!].GetProperty("option").GetString());
        }

        // Complement: a field no option stores, and lyrics and styles (the Version's own fields), have none.
        Assert.Equal(JsonValueKind.Null, byKey["workspace"].GetProperty("option").ValueKind);
        Assert.Equal(JsonValueKind.Null, byKey["lyrics"].GetProperty("option").ValueKind);

        // help: Suno's tooltip, quoted in the notes; none where the capture recorded none.
        Assert.Equal("Turn it up for wild, unexpected results", byKey["weirdness"].GetProperty("help").GetString());
        Assert.Equal("Change the gender of the generated vocals", byKey["vocal_gender"].GetProperty("help").GetString());
        Assert.Equal("At Max: Clips may differ significantly from your style input.", byKey["variety"].GetProperty("help").GetString());
        Assert.Equal(JsonValueKind.Null, byKey["exclude_styles"].GetProperty("help").ValueKind);
        Assert.Equal(JsonValueKind.Null, byKey["model"].GetProperty("help").ValueKind);

        // models: the models offered for a new choice, which the shipped list seeds from the
        // inventory's own model field, in its order.
        Assert.Equal(
            await new ServiceModelList(factory).OfferedAsync(),
            answer.GetProperty("models").EnumerateArray().Select(static model => model.GetString()!));
        Assert.Equal(CreateFieldInventory.Embedded.Get("model").Values, answer.GetProperty("models").EnumerateArray().Select(static model => model.GetString()!));
    }

    [Fact]
    public async Task NeedsCatalogRead()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using var read = await CredentialApi.SendAsync(client, HttpMethod.Get, CreateFields, reader);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // Complement: every other scope is refused, naming the one needed.
        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, CreateFields, token);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        // Complement: signed out, nothing is served.
        using var signedOut = factory.CreateClient();
        await SetupApi.ProblemAsync(await signedOut.GetAsync(CreateFields), HttpStatusCode.Unauthorized, "not_authenticated");
    }

    [Theory]
    [InlineData("Tooltip: 'Make Variety match your taste'.", "Make Variety match your taste")]
    [InlineData("A five-step slider. Tooltip at Max: 'Clips may differ.' Default taken elsewhere.", "At Max: Clips may differ.")]
    [InlineData("Tooltip: 'One'. Tooltip: 'Two'.", "One Two")]
    [InlineData("'Add audio - Browse, upload, or record audio'. Mechanics belong to TS-002.", null)]
    [InlineData(null, null)]
    public void HelpIsTheTooltipTheNotesQuote(string? notes, string? help)
    {
        var field = CreateFieldInventory.Embedded.Get("weirdness") with { Notes = notes };

        Assert.Equal(help, field.Help);
    }

    /// <summary>The host's model list, read the way the endpoint reads it.</summary>
    private sealed class ServiceModelList(N8TracksApiFactory factory)
    {
        public async Task<IReadOnlyList<string>> OfferedAsync()
        {
            var scope = factory.Services.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                return await scope.ServiceProvider.GetRequiredService<ISunoModelList>().OfferedAsync(CancellationToken.None);
            }
        }
    }
}
