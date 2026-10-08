using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Api.Tests.Suno;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Dashboard;

/// <summary>
/// #228: <c>GET /api/v1/dashboard</c>, the home page's catalog sections. Each count is checked
/// against the total the Songs list answers for the filters the section links to, archived Songs are
/// left out but for their own state's count, and a section that fails leaves the others.
/// </summary>
public sealed class DashboardTests
{
    private static readonly Uri Dashboard = new("/api/v1/dashboard", UriKind.Relative);

    /// <summary>The address the Without a Selected Generation section links to (the web page's too).</summary>
    private const string WithoutSelectionQuery = "archived=active&selected=no&generations=some";

    [Fact]
    public async Task RecentlyEditedListsTheTenNonArchivedSongsChangedLastNewestFirst()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        for (var number = 1; number <= 13; number++)
        {
            await SongApi.CreateAsync(client, $"Song {number}");
        }

        // Updated in an order unlike creation: n8-<k> was changed on day 20 - k, so n8-1 is the newest.
        // n8-1 is archived, so it is left out; n8-13 is the oldest and falls off the ten.
        for (var number = 1; number <= 13; number++)
        {
            SetUpdated(factory, number, Day(20 - number));
        }

        SetState(factory, 1, DefaultWorkflowStates.Archived);
        SetState(factory, 2, DefaultWorkflowStates.Refining);

        var recent = Data(await GetAsync(client), "recentlyEdited");
        Assert.Equal(Enumerable.Range(2, 10).Select(static number => $"n8-{number}"), Shortcodes(recent.GetProperty("songs")));
        Assert.Equal(12, recent.GetProperty("total").GetInt32());

        var first = recent.GetProperty("songs")[0];
        Assert.Equal("Song 2", first.GetProperty("title").GetString());
        Assert.Equal(DefaultWorkflowStates.Refining.Id, first.GetProperty("state").GetProperty("id").GetGuid());
        Assert.Equal("Refining", first.GetProperty("state").GetProperty("name").GetString());
        Assert.Equal(DefaultWorkflowStates.Refining.Colour, first.GetProperty("state").GetProperty("colour").GetString());
        Assert.Equal(At(Day(18)), first.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.True(first.TryGetProperty("id", out _));

        // Complement: the list it stands for, the Songs table's active Songs by last update, agrees.
        var list = await SongApi.ListAsync(client, "archived=active");
        Assert.Equal(12, list.GetProperty("total").GetInt32());
        Assert.Equal(Shortcodes(recent.GetProperty("songs")), SongApi.Shortcodes(list).Take(10));
    }

    [Fact]
    public async Task ByWorkflowStateCountsEveryStateInOrderAndEachCountIsTheListsTotalForThatState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        for (var number = 1; number <= 6; number++)
        {
            await SongApi.CreateAsync(client, $"Song {number}");
        }

        SetState(factory, 2, DefaultWorkflowStates.Writing);
        SetState(factory, 3, DefaultWorkflowStates.Writing);
        SetState(factory, 4, DefaultWorkflowStates.Archived);
        SetState(factory, 5, DefaultWorkflowStates.Final);

        // Two hidden states: Final has a Song, so it is shown; Released has none, so it is not (#67).
        TestDatabase.Execute(factory.DataPath, $"UPDATE workflow_states SET hidden = 1 WHERE id IN ('{Upper(DefaultWorkflowStates.Final.Id)}', '{Upper(DefaultWorkflowStates.Released.Id)}');");

        // The user's order: Writing first.
        await ReorderAsync(client, [DefaultWorkflowStates.Writing, DefaultWorkflowStates.Idea, DefaultWorkflowStates.Generating, DefaultWorkflowStates.Refining, DefaultWorkflowStates.Final, DefaultWorkflowStates.Released, DefaultWorkflowStates.Archived]);

        // A deleted Song is in the retention store: it is in no count.
        await DeleteSongAsync(client, "n8-6");

        var states = Data(await GetAsync(client), "workflowStates").GetProperty("states").EnumerateArray().ToList();
        Assert.Equal(
            [("Writing", 2), ("Idea", 1), ("Generating", 0), ("Refining", 0), ("Final", 1), ("Archived", 1)],
            states.Select(static state => (state.GetProperty("name").GetString()!, state.GetProperty("songCount").GetInt32())));
        Assert.Equal([false, false, false, false, true, false], states.Select(static state => state.GetProperty("hidden").GetBoolean()));

        // Complement: every count is what the Songs list totals for that state, zeros included.
        foreach (var state in states)
        {
            var list = await SongApi.ListAsync(client, $"state={state.GetProperty("id").GetString()}");
            Assert.Equal(state.GetProperty("songCount").GetInt32(), list.GetProperty("total").GetInt32());
        }
    }

    [Fact]
    public async Task WithoutASelectionCountsOnlyNonArchivedSongsWithAGenerationAndNoneSelected()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "No Generations");
        await SongApi.CreateAsync(client, "Unselected");
        await SongApi.CreateAsync(client, "Selected");
        await SongApi.CreateAsync(client, "Archived unselected");
        await SongApi.CreateAsync(client, "Archived Generation only");

        await SongApi.AttachGenerationAsync(factory, "n8-2-v1");
        var selected = (await SongApi.AttachGenerationAsync(factory, "n8-3-v1")).Generation.Id;
        await SongApi.AttachGenerationAsync(factory, "n8-4-v1");
        var archivedGeneration = (await SongApi.AttachGenerationAsync(factory, "n8-5-v1")).Generation.Id;
        TestDatabase.Execute(
            factory.DataPath,
            $"""
            UPDATE songs SET selected_generation_id = '{Upper(selected)}' WHERE shortcode_number = 3;
            UPDATE generations SET state = 'archived', archived_by = 'user' WHERE id = '{Upper(archivedGeneration)}';
            """);
        SetState(factory, 4, DefaultWorkflowStates.Archived);
        SetUpdated(factory, 2, Day(1));
        SetUpdated(factory, 5, Day(2));

        // A Generation in any state counts as having one; no Generations, a selection, or Archived does not.
        var section = Data(await GetAsync(client), "withoutSelection");
        Assert.Equal(2, section.GetProperty("count").GetInt32());
        Assert.Equal(["n8-5", "n8-2"], Shortcodes(section.GetProperty("songs")));
        Assert.Equal("Archived Generation only", section.GetProperty("songs")[0].GetProperty("title").GetString());

        // Complement: the address the section links to lists the same Songs, in the same order.
        var list = await SongApi.ListAsync(client, WithoutSelectionQuery);
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        Assert.Equal(["n8-5", "n8-2"], SongApi.Shortcodes(list));

        // The generations filter on its own: none keeps only the Song with no Generation at all.
        Assert.Equal(["n8-1"], SongApi.Shortcodes(await SongApi.ListAsync(client, "generations=none")));
        Assert.Equal(4, (await SongApi.ListAsync(client, "generations=some")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task TheGenerationsFilterRefusesAnUnknownOrRepeatedValue()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var query in new[] { "generations=all", "generations=", "generations=some&generations=none" })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/songs?{query}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await SetupApi.JsonAsync(response);
            Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task AChangeImportedFromSunoDoesNotMoveASongUpButARatingDoes()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        await SongApi.CreateAsync(client, "Followed");
        var generation = await SongApi.AttachGenerationAsync(factory, "n8-1-v1", RemoteStateApi.Clip("clip-a", "Alpha").ToJsonString());
        await SongApi.CreateAsync(client, "Later");
        SetUpdated(factory, 1, Day(1));
        SetUpdated(factory, 2, Day(2));

        // A sync finds the clip in Suno's Trash, and the commit archives the Generation (by sync).
        var export = await RemoteStateApi.ExportAsync(client, token, [], [RemoteStateApi.Clip("clip-a", "Alpha")]);
        await ImportCommitApi.CommitAsync(client, export);
        Assert.Equal("trashed", (await RemoteStateApi.StatesAsync(client, generation.Shortcode)).RemoteState);

        var recent = Data(await GetAsync(client), "recentlyEdited").GetProperty("songs");
        Assert.Equal(["n8-2", "n8-1"], Shortcodes(recent));
        Assert.Equal(At(Day(1)), recent[1].GetProperty("updatedAt").GetDateTimeOffset());

        // Complement: the user rating the Generation is a change of theirs, and moves the Song up.
        await RateAsync(client, generation.Shortcode, 4);
        Assert.Equal(["n8-1", "n8-2"], Shortcodes(Data(await GetAsync(client), "recentlyEdited").GetProperty("songs")));
    }

    [Fact]
    public async Task AnEmptyCatalogAnswersEverySectionWithNothingInIt()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var dashboard = await GetAsync(client);
        Assert.Equal(0, Data(dashboard, "recentlyEdited").GetProperty("total").GetInt32());
        Assert.Empty(Data(dashboard, "recentlyEdited").GetProperty("songs").EnumerateArray());
        Assert.Equal(0, Data(dashboard, "withoutSelection").GetProperty("count").GetInt32());
        var states = Data(dashboard, "workflowStates").GetProperty("states").EnumerateArray().ToList();
        Assert.Equal(DefaultWorkflowStates.All.Select(static state => state.Name), states.Select(static state => state.GetProperty("name").GetString()!));
        Assert.All(states, static state => Assert.Equal(0, state.GetProperty("songCount").GetInt32()));
        Assert.All(new[] { "recentlyEdited", "workflowStates", "withoutSelection" }, name => Assert.False(dashboard.GetProperty(name).TryGetProperty("error", out _)));
    }

    [Fact]
    public async Task ASectionWhoseReadFailsIsAnsweredAsFailedAndTheOthersAreIntact()
    {
        // The workflow states cannot be counted, and the Without a Selected Generation list cannot be read.
        using var factory = FailingHost(
            failStates: true,
            failSongs: static query => query.HasSelectedGeneration == false);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Still listed");

        using var response = await client.GetAsync(Dashboard);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dashboard = await SetupApi.JsonAsync(response);
        Assert.Equal(["n8-1"], Shortcodes(Data(dashboard, "recentlyEdited").GetProperty("songs")));
        foreach (var name in new[] { "workflowStates", "withoutSelection" })
        {
            var section = dashboard.GetProperty(name);
            Assert.Equal("section_failed", section.GetProperty("error").GetProperty("code").GetString());
            Assert.False(section.TryGetProperty("data", out _));
        }

        // The failure is the store's, not the request's: the same reads work elsewhere.
        Assert.Equal(1, (await SongApi.ListAsync(client)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task EverySectionFailingIsA500()
    {
        // #229: what needs attention fails too; with it read, the answer would be 200 (AttentionTests).
        using var factory = FailingHost(failStates: true, failSongs: static _ => true, failAttention: true);
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(Dashboard);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await SetupApi.JsonAsync(response);
        Assert.Equal("section_failed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ATokenNeedsCatalogReadAndNoAnswerIsCached()
    {
        using var factory = SongApi.Host();
        using (var session = await SessionApi.SignedInClientAsync(factory))
        {
            await SongApi.CreateAsync(session, "Visible to a reader");
        }

        using var client = factory.CreateClient();
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var response = await CredentialApi.SendAsync(client, HttpMethod.Get, Dashboard, reader))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(["n8-1"], Shortcodes(Data(await SetupApi.JsonAsync(response), "recentlyEdited").GetProperty("songs")));
        }

        var syncOnly = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SunoSync);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Dashboard, syncOnly))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using var anonymous = await client.GetAsync(Dashboard);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Dashboard);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>A section's data, asserting it has no error.</summary>
    private static JsonElement Data(JsonElement dashboard, string section)
    {
        var answer = dashboard.GetProperty(section);
        Assert.False(answer.TryGetProperty("error", out var error), $"{section} failed: {error}");
        return answer.GetProperty("data");
    }

    private static List<string> Shortcodes(JsonElement songs) =>
        [.. songs.EnumerateArray().Select(static song => song.GetProperty("shortcode").GetString()!)];

    /// <summary>An instant on a day of January 2026, as the API writes times.</summary>
    private static string Day(int day) => string.Create(CultureInfo.InvariantCulture, $"2026-01-{day:00}T12:00:00.000Z");

    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);

    private static void SetUpdated(N8TracksApiFactory factory, int number, string updated) =>
        TestDatabase.Execute(factory.DataPath, string.Create(CultureInfo.InvariantCulture, $"UPDATE songs SET updated_utc = '{updated}' WHERE shortcode_number = {number};"));

    private static void SetState(N8TracksApiFactory factory, int number, WorkflowState state) =>
        TestDatabase.Execute(factory.DataPath, string.Create(CultureInfo.InvariantCulture, $"UPDATE songs SET workflow_state_id = '{Upper(state.Id)}' WHERE shortcode_number = {number};"));

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    private static async Task ReorderAsync(HttpClient client, WorkflowState[] order)
    {
        using var current = await client.GetAsync(SongApi.WorkflowStates);
        var revision = (await SetupApi.JsonAsync(current)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Put, "/api/v1/workflow-states/order", revision, JsonSerializer.Serialize(new { ids = order.Select(static state => state.Id) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task DeleteSongAsync(HttpClient client, string reference)
    {
        using var read = await client.GetAsync(SongApi.Song(reference));
        var song = await SetupApi.JsonAsync(read);
        using var response = await SendAsync(
            client,
            HttpMethod.Delete,
            $"/api/v1/songs/{reference}",
            song.GetProperty("revision").GetInt32(),
            JsonSerializer.Serialize(new { confirmTitle = song.GetProperty("title").GetString() }));
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static async Task RateAsync(HttpClient client, string generation, int rating)
    {
        var revision = (await RemoteStateApi.GenerationAsync(client, generation)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/generations/{generation}", revision, new JsonObject { ["rating"] = rating }.ToJsonString());
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int revision, string json)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }

    /// <summary>
    /// A host whose workflow-state store throws when counting (when <paramref name="failStates"/>), and
    /// whose Song store throws when listing a query <paramref name="failSongs"/> picks; everything else
    /// is the real store.
    /// </summary>
    private static N8TracksApiFactory FailingHost(bool failStates, Func<SongListQuery, bool> failSongs, bool failAttention = false) =>
        new()
        {
            TestServices = services =>
            {
                Wrap<ISongStore>(services, (method, arguments) =>
                    method.Name == nameof(ISongStore.ListAsync) && arguments?[0] is SongListQuery query && failSongs(query));
                Wrap<IWorkflowStateStore>(services, (method, _) =>
                    failStates && method.Name == nameof(IWorkflowStateStore.ListWithUsageAsync));
                if (failAttention)
                {
                    Wrap<ISunoWorkspaceStore>(services, static (method, _) => method.Name == nameof(ISunoWorkspaceStore.ListAsync));
                    Wrap<IAudioFileStore>(services, static (method, _) => method.Name == nameof(IAudioFileStore.ListAsync));
                    Wrap<ISunoExportStore>(services, static (method, _) => method.Name == nameof(ISunoExportStore.InStatesAsync));
                }
            },
        };

    /// <summary>
    /// Replaces the registration of <typeparamref name="T"/> with the real one wrapped by
    /// <see cref="FailingStore{T}"/>: a registration by type or by factory (#229).
    /// </summary>
    internal static void Wrap<T>(IServiceCollection services, Func<MethodInfo, object?[]?, bool> fails)
        where T : class
    {
        var registered = services.Single(static descriptor => descriptor.ServiceType == typeof(T));
        services.Remove(registered);
        services.Add(new ServiceDescriptor(
            typeof(T),
            provider => FailingStore<T>.Wrap(
                registered.ImplementationType is { } type
                    ? (T)ActivatorUtilities.CreateInstance(provider, type)
                    : (T)registered.ImplementationFactory!(provider),
                fails),
            registered.Lifetime));
    }
}

/// <summary>A store that throws on the calls a test picks and passes every other call to the real one.</summary>
public class FailingStore<T> : DispatchProxy
    where T : class
{
    private T? inner;
    private Func<MethodInfo, object?[]?, bool>? fails;

    public static T Wrap(T inner, Func<MethodInfo, object?[]?, bool> fails)
    {
        var proxy = Create<T, FailingStore<T>>();
        var store = (FailingStore<T>)(object)proxy;
        store.inner = inner;
        store.fails = fails;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (fails!(targetMethod, args))
        {
            throw new InvalidOperationException($"{typeof(T).Name}.{targetMethod.Name} failed for the test.");
        }

        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
