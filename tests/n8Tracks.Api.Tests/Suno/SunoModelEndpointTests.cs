using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Managing the Suno model list through <c>/api/v1/suno/models</c>: add, rename, annotate, retire,
/// restore, reorder, and delete, each against the one list revision; never renaming or deleting a
/// model a Version names, and never leaving no model to offer. A Version's model is checked against
/// the whole list, retired models included, and a new Song's starts at the first one offered.
/// </summary>
public sealed class SunoModelEndpointTests
{
    private static readonly Uri Models = new("/api/v1/suno/models", UriKind.Relative);
    private static readonly Uri Order = new("/api/v1/suno/models/order", UriKind.Relative);
    private static readonly Uri CreateFields = new("/api/v1/suno/create-fields", UriKind.Relative);

    private static Uri Model(Guid id) => new($"/api/v1/suno/models/{id}", UriKind.Relative);

    [Fact]
    public async Task TheListIsSeededWithTheInventoryModelsAndCountsTheVersionsNamingEach()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(Models);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var list = await SetupApi.JsonAsync(response);
        Assert.Equal(1, list.GetProperty("revision").GetInt32());
        Assert.Equal(["v6|1|False|False|0", "v6-wild|2|False|False|0", "v6-mini|3|False|False|0"], Items(list).Select(Describe));
        Assert.All(Items(list), static model => Assert.Equal(JsonValueKind.Null, model.GetProperty("note").ValueKind));

        // A new Song's Version 1 takes the first model offered, as its Song's and its Sound's model;
        // a Version naming one model both ways counts once.
        var song = await SongApi.CreateAsync(client, "Seeded");
        var inputs = await InputsAsync(client, song);
        Assert.Equal("v6", inputs.GetProperty("model").GetString());
        Assert.Equal("v6", inputs.GetProperty("soundsModel").GetString());
        await EditVersionAsync(client, song, """{"inputs":{"soundsModel":"v6-mini"}}""");
        await SongApi.CreateAsync(client, "Second");

        Assert.Equal(["v6|2", "v6-wild|0", "v6-mini|1"], Items(await ListAsync(client)).Select(Usage));
    }

    [Fact]
    public async Task AModelIsAddedAtTheEndWithAnOptionalNote()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await SendAsync(client, HttpMethod.Post, Models, 1, """{"name":"  v7 ","note":"  Pro plan only  "}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var list = await SetupApi.JsonAsync(response);
        var added = Items(list)[^1];
        Assert.Equal("v7|4|False|False|0", Describe(added));
        Assert.Equal("Pro plan only", added.GetProperty("note").GetString());

        // Without a note, or with a blank one, there is none.
        var plain = Items(await ChangeAsync(client, HttpMethod.Post, Models, 2, """{"name":"v8","note":"   "}"""))[^1];
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("note").ValueKind);
        Assert.Equal(5, plain.GetProperty("order").GetInt32());

        // A new model can be a Version's model at once.
        var song = await SongApi.CreateAsync(client, "Uses v7");
        Assert.Equal("v7", (await EditVersionAsync(client, song, """{"inputs":{"model":"v7"}}""")).GetProperty("inputs").GetProperty("model").GetString());
    }

    [Fact]
    public async Task ABadOrTakenNameOrABadNoteIsRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var (body, field) in new[]
        {
            ("""{"name":"  V6  "}""", "name"),
            ("""{"name":"V6-MINI"}""", "name"),
            ("""{"name":"   "}""", "name"),
            ("""{}""", "name"),
            ($$"""{"name":"{{new string('a', 51)}}"}""", "name"),
            ("""{"name":"v7\nv8"}""", "name"),
            ($$"""{"name":"v7","note":"{{new string('n', 201)}}"}""", "note"),
            ("""{"name":"v7","note":"one\ntwo"}""", "note"),
        })
        {
            using var response = await SendAsync(client, HttpMethod.Post, Models, 1, body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        // Complement: the longest name and note are taken.
        var longest = Items(await ChangeAsync(client, HttpMethod.Post, Models, 1, $$"""{"name":"{{new string('a', 50)}}","note":"{{new string('n', 200)}}"}"""))[^1];
        Assert.Equal(50, longest.GetProperty("name").GetString()!.Length);

        // A rename is checked the same way, against every other model, and not against itself.
        using var taken = await SendAsync(client, HttpMethod.Patch, Model(DefaultSunoModels.V6Wild.Id), 2, """{"name":"v6-MINI"}""");
        await SetupApi.ProblemAsync(taken, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        using var wrongType = await SendAsync(client, HttpMethod.Patch, Model(DefaultSunoModels.V6Wild.Id), 2, """{"name":null,"note":3,"retired":"yes"}""");
        var typeProblem = await SetupApi.ProblemAsync(wrongType, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        Assert.Equal(["name", "note", "retired"], typeProblem.GetProperty("errors").EnumerateObject().Select(static error => error.Name).Order(StringComparer.Ordinal));

        var list = await ListAsync(client);
        Assert.Equal(2, list.GetProperty("revision").GetInt32());
        Assert.Equal(4, Items(list).Count);

        var renamed = await ChangeAsync(client, HttpMethod.Patch, Model(DefaultSunoModels.V6Wild.Id), 2, """{"name":"V6-WILD"}""");
        Assert.Equal("V6-WILD", Items(renamed)[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AnUnusedModelIsRenamedAnnotatedRetiredRestoredAndDeleted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var wild = Model(DefaultSunoModels.V6Wild.Id);

        var renamed = await ChangeAsync(client, HttpMethod.Patch, wild, 1, """{"name":" v6-wilder ","note":"Odd results"}""");
        Assert.Equal(2, renamed.GetProperty("revision").GetInt32());
        Assert.Equal("v6-wilder|2|False|False|0", Describe(Items(renamed)[1]));
        Assert.Equal("Odd results", Items(renamed)[1].GetProperty("note").GetString());

        // A null note removes it.
        var noteless = await ChangeAsync(client, HttpMethod.Patch, wild, 2, """{"note":null}""");
        Assert.Equal(JsonValueKind.Null, Items(noteless)[1].GetProperty("note").ValueKind);

        var retired = await ChangeAsync(client, HttpMethod.Patch, wild, 3, """{"retired":true}""");
        Assert.True(Items(retired)[1].GetProperty("retired").GetBoolean());
        var restored = await ChangeAsync(client, HttpMethod.Patch, wild, 4, """{"retired":false}""");
        Assert.False(Items(restored)[1].GetProperty("retired").GetBoolean());

        // An edit that changes nothing leaves the revision alone.
        var unchanged = await ChangeAsync(client, HttpMethod.Patch, wild, 5, """{"name":"v6-wilder","retired":false}""");
        Assert.Equal(5, unchanged.GetProperty("revision").GetInt32());

        var deleted = await ChangeAsync(client, HttpMethod.Delete, wild, 5, null);
        Assert.Equal(6, deleted.GetProperty("revision").GetInt32());
        Assert.Equal(["v6|1|False|False|0", "v6-mini|2|False|False|0"], Items(deleted).Select(Describe));

        using var gone = await SendAsync(client, HttpMethod.Patch, wild, 6, """{"retired":true}""");
        await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        using var goneDelete = await SendAsync(client, HttpMethod.Delete, wild, 6, null);
        await SetupApi.ProblemAsync(goneDelete, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task AModelAVersionNamesCannotBeRenamedOrDeletedButCanBeRetired()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Mini");
        await EditVersionAsync(client, song, """{"inputs":{"model":"v6-mini","soundsModel":"v6-wild"}}""");
        var mini = Model(DefaultSunoModels.V6Mini.Id);
        var wild = Model(DefaultSunoModels.V6Wild.Id);

        // As a Song's model and as a Sound's.
        foreach (var (method, uri, json) in new[]
        {
            (HttpMethod.Patch, mini, """{"name":"v6-tiny"}"""),
            (HttpMethod.Delete, mini, (string?)null),
            (HttpMethod.Patch, wild, """{"name":"v6-wilder","note":"kept apart"}"""),
            (HttpMethod.Delete, wild, null),
        })
        {
            using var response = await SendAsync(client, method, uri, 1, json);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "model_in_use");
            Assert.Equal(1, problem.GetProperty("versionCount").GetInt32());
        }

        Assert.Equal(1, (await ListAsync(client)).GetProperty("revision").GetInt32());

        // Retiring it stops it being offered; the Version keeps it, shows it, and may keep naming it.
        var retired = await ChangeAsync(client, HttpMethod.Patch, mini, 1, """{"retired":true,"note":"Free plan"}""");
        Assert.Equal("v6-mini|3|True|False|1", Describe(Items(retired)[2]));
        Assert.Equal(["v6", "v6-wild"], await OfferedAsync(client));
        var inputs = await InputsAsync(client, song);
        Assert.Equal("v6-mini", inputs.GetProperty("model").GetString());
        await EditVersionAsync(client, song, """{"inputs":{"model":"v6"}}""");
        Assert.Equal("v6-mini", (await EditVersionAsync(client, song, """{"inputs":{"model":"v6-mini"}}""")).GetProperty("inputs").GetProperty("model").GetString());

        // Complement: once no Version names it, it can be renamed and deleted.
        await EditVersionAsync(client, song, """{"inputs":{"model":"v6"}}""");
        var renamed = await ChangeAsync(client, HttpMethod.Patch, mini, 2, """{"name":"v6-tiny"}""");
        Assert.Equal("v6-tiny|3|True|False|0", Describe(Items(renamed)[2]));
        Assert.Equal(["v6|1|False|False|1", "v6-wild|2|False|False|1"], Items(await ChangeAsync(client, HttpMethod.Delete, mini, 3, null)).Select(Describe));

        // A model no longer on the list cannot be chosen.
        using var missing = await SendVersionEditAsync(client, song, """{"inputs":{"model":"v6-tiny"}}""");
        await SetupApi.ProblemAsync(missing, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
    }

    [Fact]
    public async Task TheLastModelNotRetiredCannotBeRetiredOrDeleted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ChangeAsync(client, HttpMethod.Patch, Model(DefaultSunoModels.V6Wild.Id), 1, """{"retired":true}""");
        await ChangeAsync(client, HttpMethod.Delete, Model(DefaultSunoModels.V6Mini.Id), 2, null);

        using var retire = await SendAsync(client, HttpMethod.Patch, Model(DefaultSunoModels.V6.Id), 3, """{"retired":true}""");
        await SetupApi.ProblemAsync(retire, HttpStatusCode.Conflict, "last_offered_model");
        using var delete = await SendAsync(client, HttpMethod.Delete, Model(DefaultSunoModels.V6.Id), 3, null);
        await SetupApi.ProblemAsync(delete, HttpStatusCode.Conflict, "last_offered_model");
        Assert.Equal(["v6"], await OfferedAsync(client));

        // Complement: once another is restored, it can be retired.
        await ChangeAsync(client, HttpMethod.Patch, Model(DefaultSunoModels.V6Wild.Id), 3, """{"retired":false}""");
        await ChangeAsync(client, HttpMethod.Patch, Model(DefaultSunoModels.V6.Id), 4, """{"retired":true}""");
        Assert.Equal(["v6-wild"], await OfferedAsync(client));
    }

    [Fact]
    public async Task ModelsAreReorderedAndANewSongTakesTheFirstOffered()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var v7 = Items(await ChangeAsync(client, HttpMethod.Post, Models, 1, """{"name":"v7"}"""))[^1].GetProperty("id").GetGuid();

        var ids = new[] { v7, DefaultSunoModels.V6Mini.Id, DefaultSunoModels.V6.Id, DefaultSunoModels.V6Wild.Id };
        var reordered = await ChangeAsync(client, HttpMethod.Put, Order, 2, JsonSerializer.Serialize(new { ids }));
        Assert.Equal(["v7|1", "v6-mini|2", "v6|3", "v6-wild|4"], Items(reordered).Select(static model => $"{model.GetProperty("name").GetString()}|{model.GetProperty("order").GetInt32()}"));

        // The pickers' list follows the order, without retired models.
        await ChangeAsync(client, HttpMethod.Patch, Model(DefaultSunoModels.V6Mini.Id), 3, """{"retired":true}""");
        Assert.Equal(["v7", "v6", "v6-wild"], await OfferedAsync(client));
        await ChangeAsync(client, HttpMethod.Patch, Model(v7), 4, """{"retired":true}""");
        var song = await SongApi.CreateAsync(client, "After the reorder");
        Assert.Equal("v6", (await InputsAsync(client, song)).GetProperty("model").GetString());

        // The same order again changes nothing.
        var same = await ChangeAsync(client, HttpMethod.Put, Order, 5, JsonSerializer.Serialize(new { ids }));
        Assert.Equal(5, same.GetProperty("revision").GetInt32());

        foreach (var wrong in new[]
        {
            new[] { v7, DefaultSunoModels.V6.Id },
            [v7, v7, DefaultSunoModels.V6.Id, DefaultSunoModels.V6Wild.Id],
            [v7, DefaultSunoModels.V6Mini.Id, DefaultSunoModels.V6.Id, Guid.CreateVersion7()],
        })
        {
            using var response = await SendAsync(client, HttpMethod.Put, Order, 5, JsonSerializer.Serialize(new { ids = wrong }));
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "order_mismatch");
            Assert.Equal(4, problem.GetProperty("current").GetProperty("items").GetArrayLength());
        }

        using var noIds = await SendAsync(client, HttpMethod.Put, Order, 5, "{}");
        await SetupApi.ProblemAsync(noIds, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
    }

    [Fact]
    public async Task AChangeNeedsTheCurrentListRevision()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ChangeAsync(client, HttpMethod.Post, Models, 1, """{"name":"v7"}""");

        using var stale = await SendAsync(client, HttpMethod.Patch, Model(DefaultSunoModels.V6.Id), 1, """{"name":"v6-old"}""");
        var conflict = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
        Assert.Equal(2, conflict.GetProperty("current").GetProperty("revision").GetInt32());

        using var missing = await SendAsync(client, HttpMethod.Post, Models, null, """{"name":"v8"}""");
        await SetupApi.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, Revisions.RequiredCode);

        Assert.Equal(["v6", "v6-wild", "v6-mini", "v7"], Items(await ListAsync(client)).Select(static model => model.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ATokenCannotManageTheListWhateverItsScopesButCanReadItWithCatalogRead()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        using var client = factory.CreateClient();
        var everything = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        foreach (var (method, uri) in new[]
        {
            (HttpMethod.Post, Models),
            (HttpMethod.Patch, Model(DefaultSunoModels.V6Mini.Id)),
            (HttpMethod.Put, Order),
            (HttpMethod.Delete, Model(DefaultSunoModels.V6Mini.Id)),
        })
        {
            using var response = await CredentialApi.SendAsync(client, method, uri, everything);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        var list = await ListAsync(setUp);
        Assert.Equal(1, list.GetProperty("revision").GetInt32());
        Assert.Equal(3, Items(list).Count);

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using var read = await CredentialApi.SendAsync(client, HttpMethod.Get, Models, reader);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // Complement: without catalog.read, reading is refused; signed out, so is everything.
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Models, writer);
        await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
        using var signedOut = await client.GetAsync(Models);
        await SetupApi.ProblemAsync(signedOut, HttpStatusCode.Unauthorized, "not_authenticated");
    }

    private static List<JsonElement> Items(JsonElement list) => [.. list.GetProperty("items").EnumerateArray()];

    private static string Describe(JsonElement model) =>
        $"{model.GetProperty("name").GetString()}|{model.GetProperty("order").GetInt32()}|{model.GetProperty("retired").GetBoolean()}|{model.GetProperty("discovered").GetBoolean()}|{model.GetProperty("versionCount").GetInt32()}";

    private static string Usage(JsonElement model) => $"{model.GetProperty("name").GetString()}|{model.GetProperty("versionCount").GetInt32()}";

    private static async Task<JsonElement> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Models);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>The models the create fields offer the pickers.</summary>
    private static async Task<List<string>> OfferedAsync(HttpClient client)
    {
        using var response = await client.GetAsync(CreateFields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("models").EnumerateArray().Select(static model => model.GetString()!)];
    }

    private static Uri CurrentVersion(JsonElement song) =>
        new($"/api/v1/versions/{song.GetProperty("currentVersion").GetProperty("id").GetGuid()}", UriKind.Relative);

    private static async Task<JsonElement> InputsAsync(HttpClient client, JsonElement song)
    {
        using var response = await client.GetAsync(CurrentVersion(song));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("inputs");
    }

    /// <summary>Edits the Song's current Version at its current revision; the caller reads the answer.</summary>
    private static async Task<HttpResponseMessage> SendVersionEditAsync(HttpClient client, JsonElement song, string json)
    {
        using var read = await client.GetAsync(CurrentVersion(song));
        var revision = (await SetupApi.JsonAsync(read)).GetProperty("revision").GetInt32();
        using var request = new HttpRequestMessage(HttpMethod.Patch, CurrentVersion(song))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> EditVersionAsync(HttpClient client, JsonElement song, string json)
    {
        using var response = await SendVersionEditAsync(client, song, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Sends a change with the anti-forgery header and, when given, the revision in <c>If-Match</c>; the caller reads the answer.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, int? revision, string? json)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (revision is { } value)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
        }

        return await client.SendAsync(request);
    }

    /// <summary>Sends a change and returns the list it answers, asserting success and that the ETag is the new revision.</summary>
    private static async Task<JsonElement> ChangeAsync(HttpClient client, HttpMethod method, Uri uri, int revision, string? json)
    {
        using var response = await SendAsync(client, method, uri, revision, json);
        Assert.True(response.IsSuccessStatusCode, $"{method} {uri}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var list = await SetupApi.JsonAsync(response);
        Assert.Equal(SongApi.Quoted(list.GetProperty("revision").GetInt32()), response.Headers.ETag?.Tag);
        return list;
    }
}
