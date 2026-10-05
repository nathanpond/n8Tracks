using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// Managing the workflow states through <c>/api/v1/workflow-states</c>: add, rename, recolour,
/// reorder, hide, show, and delete (moving a state's Songs to a replacement), each against the one
/// workflow revision, and never leaving the workflow without a visible state.
/// </summary>
public sealed class WorkflowStateEndpointTests
{
    private static readonly Uri Order = new("/api/v1/workflow-states/order", UriKind.Relative);

    private static Uri State(Guid id, string? replacement = null) =>
        new($"/api/v1/workflow-states/{id}{(replacement is null ? string.Empty : "?replacement=" + replacement)}", UriKind.Relative);

    [Fact]
    public async Task TheListCarriesTheWorkflowRevisionAndEachStatesSongCount()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "One");
        await SongApi.CreateAsync(client, "Two");

        using var response = await client.GetAsync(SongApi.WorkflowStates);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        var list = await SetupApi.JsonAsync(response);
        Assert.Equal(1, list.GetProperty("revision").GetInt32());
        Assert.Equal(
            ["Idea:2", "Writing:0", "Generating:0", "Refining:0", "Final:0", "Released:0", "Archived:0"],
            Items(list).Select(static state => $"{state.GetProperty("name").GetString()}:{state.GetProperty("songCount").GetInt32()}"));
    }

    [Fact]
    public async Task AStateIsAddedAtTheEndVisibleWithTheFirstUnusedColour()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await SendAsync(client, HttpMethod.Post, SongApi.WorkflowStates, 1, """{"name":"  Mixing "}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var list = await SetupApi.JsonAsync(response);
        Assert.Equal(2, list.GetProperty("revision").GetInt32());
        var added = Items(list)[^1];
        Assert.Equal("Mixing", added.GetProperty("name").GetString());
        Assert.Equal(StateColours.Red, added.GetProperty("colour").GetString());
        Assert.Equal(8, added.GetProperty("order").GetInt32());
        Assert.False(added.GetProperty("hidden").GetBoolean());
        Assert.Equal(0, added.GetProperty("songCount").GetInt32());

        // The next one takes the next free colour, and a colour can be chosen.
        var second = Items(await ChangeAsync(client, HttpMethod.Post, SongApi.WorkflowStates, 2, """{"name":"Mastering"}"""))[^1];
        Assert.Equal(StateColours.Pink, second.GetProperty("colour").GetString());
        var third = Items(await ChangeAsync(client, HttpMethod.Post, SongApi.WorkflowStates, 3, """{"name":"Demo","colour":"teal"}"""))[^1];
        Assert.Equal(StateColours.Teal, third.GetProperty("colour").GetString());
        Assert.Equal(10, third.GetProperty("order").GetInt32());
    }

    [Fact]
    public async Task ThereIsNoLimitOnStatesAndColoursAreReusedOnceAllTwelveAreTaken()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        // The seven defaults leave five colours free; the next five states take them in palette order.
        var revision = 1;
        string[] free = [StateColours.Red, StateColours.Pink, StateColours.Grape, StateColours.Indigo, StateColours.Cyan];
        for (var number = 8; number <= 15; number++)
        {
            using var response = await SendAsync(client, HttpMethod.Post, SongApi.WorkflowStates, revision, $$"""{"name":"Step {{number}}"}""");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var added = Items(await SetupApi.JsonAsync(response))[^1];
            revision++;
            Assert.Equal($"Step {number}", added.GetProperty("name").GetString());
            Assert.Equal(number, added.GetProperty("order").GetInt32());

            // From the 13th state on, every colour is in use, so each new state takes the first again.
            var expected = number <= 12 ? free[number - 8] : StateColours.All[0].Name;
            Assert.Equal(expected, added.GetProperty("colour").GetString());
        }

        var list = await ListAsync(client);
        Assert.Equal(15, Items(list).Count);
        Assert.Equal(StateColours.Gray, Items(list)[12].GetProperty("colour").GetString());
        Assert.Equal(StateColours.Gray, Items(list)[14].GetProperty("colour").GetString());
    }

    [Fact]
    public async Task ABadOrTakenNameOrAnUnknownColourIsRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ChangeAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Archived.Id), 1, """{"hidden":true}""");

        foreach (var (body, field) in new[]
        {
            ("""{"name":"  iDEA  "}""", "name"),
            ("""{"name":"archived"}""", "name"),
            ("""{"name":"   "}""", "name"),
            ("""{}""", "name"),
            ($$"""{"name":"{{new string('a', 51)}}"}""", "name"),
            ("""{"name":"Mixing","colour":"Red"}""", "colour"),
        })
        {
            using var response = await SendAsync(client, HttpMethod.Post, SongApi.WorkflowStates, 2, body);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        // A rename is checked the same way, against every other state, and not against itself.
        using var taken = await SendAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Writing.Id), 2, """{"name":"FINAL"}""");
        await SetupApi.ProblemAsync(taken, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        using var wrongType = await SendAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Writing.Id), 2, """{"name":null,"hidden":"yes"}""");
        var typeProblem = await SetupApi.ProblemAsync(wrongType, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        Assert.True(typeProblem.GetProperty("errors").TryGetProperty("name", out _));
        Assert.True(typeProblem.GetProperty("errors").TryGetProperty("hidden", out _));

        var list = await ListAsync(client);
        Assert.Equal(2, list.GetProperty("revision").GetInt32());
        Assert.Equal(7, Items(list).Count);

        // Complement: the same name in another case is a rename of itself.
        var renamed = await ChangeAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Writing.Id), 2, """{"name":"WRITING"}""");
        Assert.Equal("WRITING", Items(renamed)[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AStateIsRenamedRecolouredHiddenAndShown()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var idea = State(DefaultWorkflowStates.Idea.Id);

        var renamed = await ChangeAsync(client, HttpMethod.Patch, idea, 1, """{"name":" Spark ","colour":"cyan"}""");
        Assert.Equal(2, renamed.GetProperty("revision").GetInt32());
        Assert.Equal("Spark|cyan|1|False", Describe(Items(renamed)[0]));

        var hidden = await ChangeAsync(client, HttpMethod.Patch, idea, 2, """{"hidden":true}""");
        Assert.Equal("Spark|cyan|1|True", Describe(Items(hidden)[0]));

        var shown = await ChangeAsync(client, HttpMethod.Patch, idea, 3, """{"hidden":false}""");
        Assert.Equal("Spark|cyan|1|False", Describe(Items(shown)[0]));
        Assert.Equal(4, shown.GetProperty("revision").GetInt32());

        // An edit that changes nothing is not written: the revision stays.
        var same = await ChangeAsync(client, HttpMethod.Patch, idea, 4, """{"name":"Spark"}""");
        Assert.Equal(4, same.GetProperty("revision").GetInt32());

        using var missing = await SendAsync(client, HttpMethod.Patch, State(Guid.CreateVersion7()), 4, """{"name":"Nowhere"}""");
        await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task AHiddenStateKeepsItsSongsButANewSongStartsInTheFirstVisibleState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Stays in Idea");

        await ChangeAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Idea.Id), 1, """{"hidden":true}""");

        var after = await SongApi.ListAsync(client);
        Assert.Equal("Idea", after.GetProperty("items")[0].GetProperty("state").GetProperty("name").GetString());
        Assert.Equal(song.GetProperty("revision").GetInt32(), after.GetProperty("items")[0].GetProperty("revision").GetInt32());
        Assert.Equal("Writing", (await SongApi.CreateAsync(client, "Starts later")).GetProperty("state").GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheStatesAreReorderedByTheFullListOfIds()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var ids = DefaultWorkflowStates.All.Select(static state => state.Id).ToList();
        List<Guid> reordered = [ids[4], .. ids.Take(4), .. ids.Skip(5)];

        var list = await ChangeAsync(client, HttpMethod.Put, Order, 1, JsonSerializer.Serialize(new { ids = reordered }));

        Assert.Equal(2, list.GetProperty("revision").GetInt32());
        Assert.Equal(reordered, Items(list).Select(static state => state.GetProperty("id").GetGuid()));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], Items(list).Select(static state => state.GetProperty("order").GetInt32()));
        Assert.Equal("Final", (await SongApi.CreateAsync(client, "Starts in Final")).GetProperty("state").GetProperty("name").GetString());

        // Complement: a list missing a state, repeating one, or naming an unknown one is refused with the list as it is.
        foreach (var wrong in new List<Guid>[] { [.. reordered.Skip(1)], [reordered[0], .. reordered.Skip(1).Take(5), reordered[0]], [.. reordered.Skip(1), Guid.CreateVersion7()] })
        {
            using var response = await SendAsync(client, HttpMethod.Put, Order, 2, JsonSerializer.Serialize(new { ids = wrong }));
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, WorkflowStatesEndpoints.OrderMismatchCode);
            Assert.Equal(2, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using var noIds = await SendAsync(client, HttpMethod.Put, Order, 2, "{}");
        await SetupApi.ProblemAsync(noIds, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
        Assert.Equal(2, (await ListAsync(client)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task EveryChangeNeedsTheCurrentWorkflowRevision()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await ChangeAsync(client, HttpMethod.Post, SongApi.WorkflowStates, 1, """{"name":"Mixing"}""");

        using var stale = await SendAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Idea.Id), 1, """{"name":"Spark"}""");
        var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
        Assert.Equal(2, problem.GetProperty("current").GetProperty("revision").GetInt32());
        Assert.Equal("Mixing", Items(problem.GetProperty("current"))[^1].GetProperty("name").GetString());

        using var ahead = await SendAsync(client, HttpMethod.Delete, State(DefaultWorkflowStates.Released.Id), 3, null);
        await SetupApi.ProblemAsync(ahead, HttpStatusCode.Conflict, Revisions.ConflictCode);

        using var none = await SendAsync(client, HttpMethod.Post, SongApi.WorkflowStates, null, """{"name":"Mastering"}""");
        await SetupApi.ProblemAsync(none, HttpStatusCode.PreconditionRequired, Revisions.RequiredCode);

        var list = await ListAsync(client);
        Assert.Equal(2, list.GetProperty("revision").GetInt32());
        Assert.Equal("Idea", Items(list)[0].GetProperty("name").GetString());
        Assert.Equal(8, Items(list).Count);
    }

    [Fact]
    public async Task TheLastVisibleStateCanBeNeitherHiddenNorDeleted()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var revision = 1;
        foreach (var state in DefaultWorkflowStates.All.Skip(1))
        {
            await ChangeAsync(client, HttpMethod.Patch, State(state.Id), revision++, """{"hidden":true}""");
        }

        using var hide = await SendAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Idea.Id), revision, """{"hidden":true}""");
        await SetupApi.ProblemAsync(hide, HttpStatusCode.Conflict, WorkflowStatesEndpoints.LastVisibleCode);
        using var delete = await SendAsync(client, HttpMethod.Delete, State(DefaultWorkflowStates.Idea.Id, DefaultWorkflowStates.Final.Id.ToString()), revision, null);
        await SetupApi.ProblemAsync(delete, HttpStatusCode.Conflict, WorkflowStatesEndpoints.LastVisibleCode);
        Assert.Equal(revision, (await ListAsync(client)).GetProperty("revision").GetInt32());

        // Complement: a hidden state can still be deleted, and once another is shown, Idea can be hidden.
        await ChangeAsync(client, HttpMethod.Delete, State(DefaultWorkflowStates.Released.Id), revision++, null);
        await ChangeAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Final.Id), revision++, """{"hidden":false}""");
        var list = await ChangeAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Idea.Id), revision, """{"hidden":true}""");
        Assert.Equal(["Final"], Items(list).Where(static state => !state.GetProperty("hidden").GetBoolean()).Select(static state => state.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task AStateWithoutSongsIsDeletedAndTheRestCloseUp()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var list = await ChangeAsync(client, HttpMethod.Delete, State(DefaultWorkflowStates.Generating.Id), 1, null);

        Assert.Equal(2, list.GetProperty("revision").GetInt32());
        Assert.Equal(
            ["Idea|yellow|1|False", "Writing|blue|2|False", "Refining|orange|3|False", "Final|green|4|False", "Released|teal|5|False", "Archived|gray|6|False"],
            Items(list).Select(Describe));

        using var again = await SendAsync(client, HttpMethod.Delete, State(DefaultWorkflowStates.Generating.Id), 2, null);
        await SetupApi.ProblemAsync(again, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task AStateWithSongsIsDeletedOnlyWithAReplacementWhichGetsEveryOneOfThemAndNothingElse()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var mixing = Items(await ChangeAsync(client, HttpMethod.Post, SongApi.WorkflowStates, 1, """{"name":"Mixing"}"""))[^1].GetProperty("id").GetGuid();
        await ChangeAsync(client, HttpMethod.Patch, State(DefaultWorkflowStates.Archived.Id), 2, """{"hidden":true}""");
        var moving = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var song = await SongApi.CreateAsync(client, $"Moving {index}");
            moving.Add(song.GetProperty("id").GetString()!);
            await SongApi.EditAsync(client, moving[^1], 1, $$"""{"stateId":"{{mixing}}"}""");
        }

        var staying = await SongApi.CreateAsync(client, "Staying");
        clock.Advance(TimeSpan.FromMinutes(10));

        // Without a replacement: refused with how many Songs it has.
        using var bare = await SendAsync(client, HttpMethod.Delete, State(mixing), 3, null);
        var inUse = await SetupApi.ProblemAsync(bare, HttpStatusCode.Conflict, WorkflowStatesEndpoints.InUseCode);
        Assert.Equal(3, inUse.GetProperty("songCount").GetInt32());

        // A replacement that is the state itself, no state, or not an ID is refused.
        foreach (var replacement in new[] { mixing.ToString(), Guid.CreateVersion7().ToString(), "final" })
        {
            using var wrong = await SendAsync(client, HttpMethod.Delete, State(mixing, replacement), 3, null);
            var problem = await SetupApi.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("replacement", out _), replacement);
        }

        Assert.Equal(3, Items(await ListAsync(client))[^1].GetProperty("songCount").GetInt32());

        // A hidden replacement is allowed; every Song moves, its revision goes up, and its time moves.
        var list = await ChangeAsync(client, HttpMethod.Delete, State(mixing, DefaultWorkflowStates.Archived.Id.ToString()), 3, null);

        Assert.DoesNotContain(Items(list), state => state.GetProperty("id").GetGuid() == mixing);
        Assert.Equal(3, Items(list).Single(static state => state.GetProperty("name").GetString() == "Archived").GetProperty("songCount").GetInt32());
        foreach (var id in moving)
        {
            using var response = await client.GetAsync(SongApi.Song(id));
            var song = await SetupApi.JsonAsync(response);
            Assert.Equal("Archived", song.GetProperty("state").GetProperty("name").GetString());
            Assert.Equal(3, song.GetProperty("revision").GetInt32());
            Assert.Equal(clock.GetUtcNow().UtcDateTime, song.GetProperty("updatedAt").GetDateTime());
        }

        using var stayingNow = await client.GetAsync(SongApi.Song(staying.GetProperty("id").GetString()!));
        var unchanged = await SetupApi.JsonAsync(stayingNow);
        Assert.Equal("Idea", unchanged.GetProperty("state").GetProperty("name").GetString());
        Assert.Equal(1, unchanged.GetProperty("revision").GetInt32());
        Assert.Equal(staying.GetProperty("updatedAt").GetDateTime(), unchanged.GetProperty("updatedAt").GetDateTime());

        // An edit of a moved Song made against its old revision gets the ordinary conflict.
        using var stale = await SongApi.PatchAsync(client, moving[0], "\"2\"", """{"title":"Late"}""");
        await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, Revisions.ConflictCode);
    }

    [Fact]
    public async Task ATokenCannotManageStatesWhateverItsScopesButCanReadThemWithCatalogRead()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        using var client = factory.CreateClient();
        var everything = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        foreach (var (method, uri) in new[]
        {
            (HttpMethod.Post, SongApi.WorkflowStates),
            (HttpMethod.Patch, State(DefaultWorkflowStates.Idea.Id)),
            (HttpMethod.Put, Order),
            (HttpMethod.Delete, State(DefaultWorkflowStates.Released.Id)),
        })
        {
            using var response = await CredentialApi.SendAsync(client, method, uri, everything);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
        }

        Assert.Equal(1, (await ListAsync(setUp)).GetProperty("revision").GetInt32());

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using var read = await CredentialApi.SendAsync(client, HttpMethod.Get, SongApi.WorkflowStates, reader);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // Complement: without catalog.read, reading is refused.
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, SongApi.WorkflowStates, writer);
        await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
    }

    private static List<JsonElement> Items(JsonElement list) => [.. list.GetProperty("items").EnumerateArray()];

    private static string Describe(JsonElement state) =>
        $"{state.GetProperty("name").GetString()}|{state.GetProperty("colour").GetString()}|{state.GetProperty("order").GetInt32()}|{state.GetProperty("hidden").GetBoolean()}";

    private static async Task<JsonElement> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(SongApi.WorkflowStates);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
