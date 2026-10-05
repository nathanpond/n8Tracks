using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Invariants;

/// <summary>
/// The guard of invariant 1 (CLAUDE.md), Version immutability: once a Generation is attached, a
/// Version's creation inputs cannot change through any path. It fails when
/// <list type="bullet">
/// <item>a property of the Version entity, or a column of <c>versions</c>, is in none of the three
/// lists below (a creation input added later cannot escape the rule unnoticed);</item>
/// <item>an entity method that changes an input does not throw on a frozen Version;</item>
/// <item>an unsafe <c>/api/v1</c> endpoint, or a public method of an application service, is one it
/// has not been told how to exercise (or why it touches no Version); or</item>
/// <item>any of them, called on a frozen Version with a payload touching each creation input, leaves
/// the stored inputs other than byte-identical. The complement (the same calls on a mutable Version
/// do change them) keeps it from passing because nothing works.</item>
/// </list>
/// It covers the web UI and the REST API (which the extension uses). Import (M4) and the MCP gateway
/// (M7) are not covered yet: those milestones extend this test.
/// </summary>
public sealed class VersionImmutabilityGuardTests
{
    /// <summary>Frozen once a Generation is attached. Lineage sources and the Suno settings join this list when they are added.</summary>
    private static readonly string[] CreationInputs = [nameof(SongVersion.Lyrics), nameof(SongVersion.Styles)];

    /// <summary>Always editable, frozen or not.</summary>
    private static readonly string[] EditableMetadata = [nameof(SongVersion.Name), nameof(SongVersion.Notes), nameof(SongVersion.Visibility)];

    /// <summary>Identity, Song, number, revision, timestamps, and the freeze itself: no edit sets them.</summary>
    private static readonly string[] SystemFields =
    [
        nameof(SongVersion.Id),
        nameof(SongVersion.SongId),
        nameof(SongVersion.Number),
        nameof(SongVersion.CreatedUtc),
        nameof(SongVersion.UpdatedUtc),
        nameof(SongVersion.Revision),
        nameof(SongVersion.IsFrozen),
        nameof(SongVersion.LastGenerationOrdinal),
    ];

    /// <summary>The stored form of the same, column by column (<c>visibility</c> stores the archived flag; the sort key is the number's).</summary>
    private static readonly string[] SystemColumns = [.. SystemFields, nameof(VersionRecord.NumberSortKey)];

    private const string Changed = "changed by the guard";

    [Fact]
    public void EveryPropertyOfTheVersionEntityIsClassified()
    {
        var properties = typeof(SongVersion).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(static property => property.Name).ToList();

        AssertClassified(properties, SystemFields);

        // No property can be set from outside, so `with` cannot change an input.
        Assert.All(
            typeof(SongVersion).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => Assert.True(property.SetMethod is null, $"{property.Name} has a setter."));
    }

    [Fact]
    public async Task EveryColumnOfTheVersionsTableIsClassifiedAndEachInputIsInTheDatabaseFreeze()
    {
        using var factory = new N8TracksApiFactory();
        using (var client = factory.CreateClient())
        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<N8TracksDbContext>().Model.FindEntityType(typeof(VersionRecord))!;
        var columns = model.GetProperties().Select(static property => property.Name).ToList();

        AssertClassified(columns, SystemColumns);

        // Every creation input is compared by the trigger that refuses changing a frozen Version.
        var trigger = TestDatabase.Scalar(factory.DataPath, $"SELECT sql FROM sqlite_master WHERE name = '{N8TracksDbContext.VersionFrozenTrigger}';");
        foreach (var input in CreationInputs)
        {
            var column = model.FindProperty(input)!.GetColumnName();
            Assert.Contains($"NEW.{column} IS NOT OLD.{column}", trigger, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryEntityMethodThatChangesAnInputThrowsOnAFrozenVersion()
    {
        var mutable = new SongVersion(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "Name", "Notes", VersionVisibility.Active, "Lyrics", "Styles",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Revision: 1);
        var frozen = mutable.AttachGeneration(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch).Version;

        // Each public method of the entity: how it would change an input, or why it cannot.
        var changesInputs = new Dictionary<string, Func<SongVersion, SongVersion>>(StringComparer.Ordinal)
        {
            [nameof(SongVersion.WithInputs)] = static version => version.WithInputs(Changed, version.Styles),
            [nameof(SongVersion.WithInputs) + " (styles)"] = static version => version.WithInputs(version.Lyrics, Changed),
        };
        var changesNoInput = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(SongVersion.EnsureMutable)] = "the check itself",
            [nameof(SongVersion.WithAnnotations)] = "copies the inputs as they are",
            [nameof(SongVersion.AttachGeneration)] = "copies the inputs as they are, and freezes",
            [nameof(SongVersion.Deconstruct)] = "reads only",
            [nameof(SongVersion.Equals)] = "reads only",
            [nameof(SongVersion.GetHashCode)] = "reads only",
            [nameof(SongVersion.ToString)] = "reads only",
        };
        var methods = typeof(SongVersion).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName && !method.Name.StartsWith('<'))
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal);
        Assert.All(methods, method => Assert.True(
            changesInputs.ContainsKey(method) || changesNoInput.ContainsKey(method),
            $"SongVersion.{method} is not classified: add it to the inputs it changes (and make it call EnsureMutable) or say why it changes none."));

        foreach (var change in changesInputs.Values)
        {
            Assert.Throws<VersionFrozenException>(() => change(frozen));
            var changed = change(mutable);
            Assert.NotEqual((mutable.Lyrics, mutable.Styles), (changed.Lyrics, changed.Styles));
        }
    }

    [Fact]
    public async Task NoApiEndpointCanChangeACreationInputOfAFrozenVersion()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var endpoints = UnsafeApiEndpoints(factory);
        var exercisers = ApiExercisers();
        var exempt = ApiEndpointsTouchingNoVersion();

        Assert.All(endpoints, endpoint => Assert.True(
            exercisers.ContainsKey(endpoint) || exempt.ContainsKey(endpoint),
            $"{endpoint} is not known to the Version immutability guard: add how to exercise it on a frozen Version, or why it touches no Version."));
        Assert.Equal(endpoints.Order(StringComparer.Ordinal), exercisers.Keys.Concat(exempt.Keys).Order(StringComparer.Ordinal));

        var frozen = await TargetAsync(factory, client, "Frozen guard", frozen: true);
        foreach (var (endpoint, exercise) in exercisers)
        {
            var before = Stored(factory, frozen.VersionId);
            await exercise.Run(frozen);
            Assert.True(before == Stored(factory, frozen.VersionId), $"{endpoint} changed a frozen Version's inputs.");
            Assert.True((await frozen.ReadAsync()).GetProperty("isFrozen").GetBoolean(), $"{endpoint} unfroze the Version.");
        }

        // Complement: on a mutable Version the endpoints that write inputs do change them.
        var mutable = await TargetAsync(factory, client, "Mutable guard", frozen: false);
        var changing = exercisers.Where(static pair => pair.Value.ChangesInputs).ToList();
        Assert.NotEmpty(changing);
        foreach (var (endpoint, exercise) in changing)
        {
            var before = Stored(factory, mutable.VersionId);
            await exercise.Run(mutable);
            Assert.True(before != Stored(factory, mutable.VersionId), $"{endpoint} did not change a mutable Version's inputs: the guard would pass because nothing works.");
        }
    }

    [Fact]
    public async Task NoApplicationServiceMethodCanChangeACreationInputOfAFrozenVersion()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var methods = CatalogServiceMethods();
        var exercisers = ServiceExercisers();
        var exempt = ServiceMethodsTakingNoVersion();

        Assert.All(methods, method => Assert.True(
            exercisers.ContainsKey(method) || exempt.ContainsKey(method),
            $"{method} is not known to the Version immutability guard: add how to exercise it on a frozen Version, or why it takes no Version."));
        Assert.Equal(methods.Order(StringComparer.Ordinal), exercisers.Keys.Concat(exempt.Keys).Order(StringComparer.Ordinal));

        // Services elsewhere take nothing that names a Version or a Song.
        var catalogNamespaces = new[] { typeof(SongVersion).Namespace, typeof(VersionService).Namespace, typeof(CatalogReference).Namespace };
        foreach (var type in ApplicationServices().Where(type => !catalogNamespaces.Contains(type.Namespace)))
        {
            foreach (var method in PublicMethods(type))
            {
                Assert.All(method.GetParameters(), parameter => Assert.False(
                    catalogNamespaces.Contains(parameter.ParameterType.Namespace),
                    $"{Describe(method)} takes a catalog type: move it to the catalog services, where the guard sees it."));
            }
        }

        var frozen = await TargetAsync(factory, client, "Frozen service guard", frozen: true);
        foreach (var (method, exercise) in exercisers)
        {
            var before = Stored(factory, frozen.VersionId);
            await exercise.Run(frozen);
            Assert.True(before == Stored(factory, frozen.VersionId), $"{method} changed a frozen Version's inputs.");
        }

        var mutable = await TargetAsync(factory, client, "Mutable service guard", frozen: false);
        var changing = exercisers.Where(static pair => pair.Value.ChangesInputs).ToList();
        Assert.NotEmpty(changing);
        foreach (var (method, exercise) in changing)
        {
            var before = Stored(factory, mutable.VersionId);
            await exercise.Run(mutable);
            Assert.True(before != Stored(factory, mutable.VersionId), $"{method} did not change a mutable Version's inputs: the guard would pass because nothing works.");
        }
    }

    /// <summary>How each unsafe endpoint that takes a Song or a Version is called, each creation input touched in turn.</summary>
    private static Dictionary<string, Exerciser> ApiExercisers() => new(StringComparer.Ordinal)
    {
        ["POST /api/v1/songs"] = new(async target =>
        {
            using var response = await SongApi.SendJsonAsync(target.Client, HttpMethod.Post, SongApi.Songs, InputsJson("""{"title":"Another Song",""", "}"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }),
        ["PATCH /api/v1/songs/{reference}"] = new(async target =>
        {
            foreach (var reference in new[] { target.SongId.ToString(), target.SongShortcode })
            {
                var song = await SetupApi.JsonAsync(await target.Client.GetAsync(SongApi.Song(reference)));
                using var response = await SongApi.PatchAsync(target.Client, reference, SongApi.Quoted(song.GetProperty("revision").GetInt32()), InputsJson("""{"title":"Renamed",""", "}"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }),
        ["POST /api/v1/songs/{reference}/versions"] = new(async target =>
        {
            var options = await SetupApi.JsonAsync(await target.Client.GetAsync(new Uri($"/api/v1/versions/{target.VersionId}/next-numbers", UriKind.Relative)));
            var number = options.GetProperty("options")[0].GetProperty("number").GetString();
            using var response = await SongApi.SendJsonAsync(
                target.Client,
                HttpMethod.Post,
                new Uri($"/api/v1/songs/{target.SongShortcode}/versions", UriKind.Relative),
                InputsJson($$"""{"sourceVersionId":"{{target.VersionShortcode}}","number":"{{number}}",""", "}"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }),
        ["PUT /api/v1/songs/{reference}/current-version"] = new(async target =>
        {
            using var response = await SongApi.SendJsonAsync(
                target.Client,
                HttpMethod.Put,
                new Uri($"/api/v1/songs/{target.SongShortcode}/current-version", UriKind.Relative),
                InputsJson($$"""{"versionId":"{{target.VersionShortcode}}",""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["PATCH /api/v1/versions/{reference}"] = new(
            async target =>
            {
                // Each input alone, then both; by ID and by shortcode; with metadata and without.
                var bodies = CreationInputs.Select(input => $$"""{"{{Camel(input)}}":"{{Changed}} {{input}}"}""")
                    .Append(InputsJson("{", "}"))
                    .Append(InputsJson("""{"name":"Renamed","notes":"Noted",""", "}"));
                foreach (var (body, index) in bodies.Select(static (body, index) => (body, index)))
                {
                    var reference = index % 2 == 0 ? target.VersionId.ToString() : target.VersionShortcode;
                    using var response = await SendAsync(target.Client, HttpMethod.Patch, new Uri($"/api/v1/versions/{reference}", UriKind.Relative), await target.IfMatchAsync(), body);
                    await target.ExpectInputsWriteAsync(response);
                }
            },
            ChangesInputs: true),
        ["POST /api/v1/versions/{reference}/snapshots"] = new(async target =>
        {
            using var response = await SongApi.SendJsonAsync(target.Client, HttpMethod.Post, new Uri($"/api/v1/versions/{target.VersionShortcode}/snapshots", UriKind.Relative), InputsJson("{", "}"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }),
        ["POST /api/v1/versions/{reference}/snapshots/{snapshotId:guid}/restore"] = new(
            async target =>
            {
                foreach (var snapshot in target.Snapshots)
                {
                    using var response = await SendAsync(
                        target.Client,
                        HttpMethod.Post,
                        new Uri($"/api/v1/versions/{target.VersionShortcode}/snapshots/{snapshot}/restore", UriKind.Relative),
                        await target.IfMatchAsync(),
                        json: null);
                    await target.ExpectInputsWriteAsync(response);
                }
            },
            ChangesInputs: true),
    };

    /// <summary>Unsafe endpoints that take neither a Song nor a Version, and why they cannot change one's inputs.</summary>
    private static Dictionary<string, string> ApiEndpointsTouchingNoVersion() => new(StringComparer.Ordinal)
    {
        ["POST /api/v1/setup"] = "first-run setup: the administrator only",
        ["POST /api/v1/session"] = "sign-in",
        ["DELETE /api/v1/session"] = "sign-out",
        ["DELETE /api/v1/sessions"] = "ends sessions",
        ["POST /api/v1/account/password"] = "the administrator's password",
        ["POST /api/v1/credentials"] = "credentials",
        ["PATCH /api/v1/credentials/{id:guid}"] = "credentials",
        ["POST /api/v1/credentials/{id:guid}/revoke"] = "credentials",
        ["POST /api/v1/workflow-states"] = "workflow states",
        ["PATCH /api/v1/workflow-states/{id:guid}"] = "workflow states",
        ["PUT /api/v1/workflow-states/order"] = "workflow states",
        ["DELETE /api/v1/workflow-states/{id:guid}"] = "workflow states; moves Songs to another state, never a Version",
        ["POST /api/v1/backups"] = "queues a backup: reads the database, writes only an archive file",
        ["DELETE /api/v1/backups/{location}/{name}"] = "deletes an archive file, never a database row",
        ["PUT /api/v1/settings/backup-schedule"] = "the backup schedule: one settings row",
    };

    /// <summary>How each public method of a catalog service is called, each creation input touched in turn.</summary>
    private static Dictionary<string, Exerciser> ServiceExercisers() => new(StringComparer.Ordinal)
    {
        ["VersionService.NextNumbersAsync(Guid, CancellationToken)"] = Service<VersionService>(static (service, target) => service.NextNumbersAsync(target.VersionId, default)),
        ["VersionService.ListAsync(String, CancellationToken)"] = Service<VersionService>(static (service, target) => service.ListAsync(target.SongShortcode, default)),
        ["VersionService.FindAsync(Guid, CancellationToken)"] = Service<VersionService>(static (service, target) => service.FindAsync(target.VersionId, default)),
        ["VersionService.SetCurrentAsync(String, String, CancellationToken)"] = Service<VersionService>(static (service, target) => service.SetCurrentAsync(target.SongShortcode, target.VersionShortcode, default)),
        ["VersionService.CreateFromAsync(Guid, VersionCreateRequest, CancellationToken)"] = Service<VersionService>(static async (service, target) =>
        {
            var options = Assert.IsType<NextNumbersOutcome.Found>(await service.NextNumbersAsync(target.VersionId, default));
            return Assert.IsType<VersionCreateOutcome.Created>(
                await service.CreateFromAsync(target.SongId, new VersionCreateRequest(target.VersionShortcode, options.Options[0].Number.ToString(), "Branch"), default));
        }),
        ["VersionService.UpdateAsync(Guid, VersionEdit, Int32, CancellationToken)"] = new(
            target => EachInputEditAsync(target, static (service, id, edit, revision) => service.UpdateAsync(id, edit, revision, default)),
            ChangesInputs: true),
        ["VersionService.UpdateAsync(Guid, VersionEdit, Int32, VersionEditSource, CancellationToken)"] = new(
            async target =>
            {
                foreach (var source in Enum.GetValues<VersionEditSource>())
                {
                    await EachInputEditAsync(target, (service, id, edit, revision) => service.UpdateAsync(id, edit, revision, source, default));
                }
            },
            ChangesInputs: true),
        ["EditorRevisionService.SnapshotAsync(Guid, EditorRevisionRequest, CancellationToken)"] = Service<EditorRevisionService>(static (service, target) =>
            service.SnapshotAsync(target.VersionId, new EditorRevisionRequest(Changed, Changed, null), default)),
        ["EditorRevisionService.ListAsync(Guid, CancellationToken)"] = Service<EditorRevisionService>(static (service, target) => service.ListAsync(target.VersionId, default)),
        ["EditorRevisionService.FindAsync(Guid, Guid, CancellationToken)"] = Service<EditorRevisionService>(static (service, target) => service.FindAsync(target.VersionId, target.Snapshots[0], default)),
        ["EditorRevisionService.RestoreAsync(Guid, Guid, Int32, CancellationToken)"] = new(
            async target =>
            {
                foreach (var snapshot in target.Snapshots)
                {
                    var revision = (await target.ReadAsync()).GetProperty("revision").GetInt32();
                    var outcome = await InScopeAsync<EditorRevisionService, RestoreOutcome>(target, service => service.RestoreAsync(target.VersionId, snapshot, revision, default));
                    Assert.IsType(target.Frozen ? typeof(RestoreOutcome.Frozen) : typeof(RestoreOutcome.Restored), outcome);
                }
            },
            ChangesInputs: true),
        ["GenerationService.AttachAsync(String, CancellationToken)"] = Service<GenerationService>(static (service, target) => service.AttachAsync(target.VersionShortcode, default)),
        ["SongService.FindAsync(String, CancellationToken)"] = Service<SongService>(static (service, target) => service.FindAsync(target.SongShortcode, default)),
        ["SongService.UpdateAsync(Guid, SongEdit, Int32, CancellationToken)"] = Service<SongService>(static async (service, target) =>
        {
            var song = await service.FindAsync(target.SongShortcode, default);
            return await service.UpdateAsync(target.SongId, new SongEdit(SongEditField.Of("Renamed"), SongEditField.Of(Changed), SongEditField.Unsent), song!.Revision, default);
        }),
        ["ReferenceResolver.ResolveAsync(CatalogReference, CancellationToken)"] = Service<ReferenceResolver>(static (service, target) => service.ResolveAsync(CatalogReference.Parse(target.VersionShortcode), default)),
        ["ReferenceResolver.SongIdAsync(CatalogReference, CancellationToken)"] = Service<ReferenceResolver>(static (service, target) => service.SongIdAsync(CatalogReference.Parse(target.SongShortcode), default)),
        ["ReferenceResolver.VersionIdAsync(CatalogReference, CancellationToken)"] = Service<ReferenceResolver>(static (service, target) => service.VersionIdAsync(CatalogReference.Parse(target.VersionShortcode), default)),
    };

    /// <summary>Public catalog-service methods that take neither a Song nor a Version, and why.</summary>
    private static Dictionary<string, string> ServiceMethodsTakingNoVersion() => new(StringComparer.Ordinal)
    {
        ["SongService.CreateAsync(SongRequest, CancellationToken)"] = "creates a new Song with an empty, mutable Version 1",
        ["SongService.ListAsync(SongListRequest, CancellationToken)"] = "reads only",
        ["SongService.Validate(SongRequest)"] = "pure validation",
        ["WorkflowStateService.ListAsync(CancellationToken)"] = "reads only",
        ["WorkflowStateService.ListWithUsageAsync(CancellationToken)"] = "reads only",
        ["WorkflowStateService.AddAsync(String, String, Int32, CancellationToken)"] = "workflow states",
        ["WorkflowStateService.UpdateAsync(Guid, WorkflowStateEdit, Int32, CancellationToken)"] = "workflow states",
        ["WorkflowStateService.ReorderAsync(IReadOnlyList`1, Int32, CancellationToken)"] = "workflow states",
        ["WorkflowStateService.DeleteAsync(Guid, String, Int32, CancellationToken)"] = "workflow states; moves Songs to another state, never a Version",
    };

    /// <summary>The Version's edit through the service: each input alone, both, and both with metadata.</summary>
    private static async Task EachInputEditAsync(Target target, Func<VersionService, Guid, VersionEdit, int, Task<VersionUpdateOutcome>> update)
    {
        var edits = new[]
        {
            new VersionEdit(SongEditField.Unsent, SongEditField.Unsent, null, SongEditField.Of(Changed + " lyrics " + Guid.NewGuid()), SongEditField.Unsent),
            new VersionEdit(SongEditField.Unsent, SongEditField.Unsent, null, SongEditField.Unsent, SongEditField.Of(Changed + " styles " + Guid.NewGuid())),
            new VersionEdit(SongEditField.Of("Renamed"), SongEditField.Of("Noted"), true, SongEditField.Of(Changed + Guid.NewGuid()), SongEditField.Of(Changed + Guid.NewGuid())),
        };
        foreach (var edit in edits)
        {
            var revision = (await target.ReadAsync()).GetProperty("revision").GetInt32();
            var outcome = await InScopeAsync<VersionService, VersionUpdateOutcome>(target, service => update(service, target.VersionId, edit, revision));
            Assert.IsType(target.Frozen ? typeof(VersionUpdateOutcome.Frozen) : typeof(VersionUpdateOutcome.Updated), outcome);
        }
    }

    private static Exerciser Service<TService>(Func<TService, Target, Task> call)
        where TService : notnull =>
        new(target => InScopeAsync<TService, bool>(target, async service =>
        {
            await call(service, target);
            return true;
        }));

    private static Exerciser Service<TService>(Func<TService, Target, Task<object>> call)
        where TService : notnull =>
        Service<TService>((service, target) => (Task)call(service, target));

    private static async Task<TResult> InScopeAsync<TService, TResult>(Target target, Func<TService, Task<TResult>> call)
        where TService : notnull
    {
        var scope = target.Factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await call(scope.ServiceProvider.GetRequiredService<TService>());
        }
    }

    /// <summary>
    /// A new Song whose Version 1 holds lyrics and styles and three snapshots (lyrics changed, styles
    /// changed, both changed), frozen by a Generation when <paramref name="frozen"/>.
    /// </summary>
    private static async Task<Target> TargetAsync(N8TracksApiFactory factory, HttpClient client, string title, bool frozen)
    {
        var song = await SongApi.CreateAsync(client, title);
        var version = song.GetProperty("currentVersion");
        var id = version.GetProperty("id").GetGuid();
        using (var written = await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative), "\"1\"", """{"lyrics":"[Verse]\nKept","styles":"kept"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        }

        var snapshots = new List<Guid>();
        foreach (var body in new[] { """{"lyrics":"Older lyrics","styles":"kept"}""", """{"lyrics":"[Verse]\nKept","styles":"older styles"}""", """{"lyrics":"Older both","styles":"older both"}""" })
        {
            using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/versions/{id}/snapshots", UriKind.Relative), body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            snapshots.Add((await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid());
        }

        if (frozen)
        {
            await SongApi.AttachGenerationAsync(factory, id.ToString());
        }

        return new Target(
            factory,
            client,
            song.GetProperty("id").GetGuid(),
            song.GetProperty("shortcode").GetString()!,
            id,
            version.GetProperty("shortcode").GetString()!,
            snapshots,
            frozen);
    }

    /// <summary>Every unsafe <c>/api/v1</c> endpoint, as <c>METHOD pattern</c>.</summary>
    private static List<string> UnsafeApiEndpoints(N8TracksApiFactory factory) =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            .SelectMany(static endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Where(static endpoint => !endpoint.StartsWith("GET ", StringComparison.Ordinal) && !endpoint.StartsWith("HEAD ", StringComparison.Ordinal))
            .Where(static endpoint => endpoint != "* /api/v1/{**path}")
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Public classes of the application layer that are services: not records, not static.</summary>
    private static IEnumerable<Type> ApplicationServices() =>
        typeof(VersionService).Assembly.GetExportedTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false } && type.GetMethod("<Clone>$") is null && !type.IsSubclassOf(typeof(Exception)))
            .Where(static type => PublicMethods(type).Any());

    /// <summary>Every public method of a class in the catalog namespaces (Songs, References), described by its signature.</summary>
    private static List<string> CatalogServiceMethods() =>
        [.. ApplicationServices()
            .Where(static type => type.Namespace == typeof(VersionService).Namespace || type.Namespace == typeof(CatalogReference).Namespace)
            .SelectMany(PublicMethods)
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)];

    private static IEnumerable<MethodInfo> PublicMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName);

    private static string Describe(MethodInfo method) =>
        $"{method.DeclaringType!.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(static parameter => parameter.ParameterType.Name))})";

    /// <summary>Each name is in exactly one of the three lists.</summary>
    private static void AssertClassified(List<string> names, string[] systemFields)
    {
        var lists = new[] { CreationInputs, EditableMetadata, systemFields };
        Assert.All(names, name => Assert.True(
            lists.Count(list => list.Contains(name, StringComparer.Ordinal)) == 1,
            $"{name} is not classified: a creation input (frozen once a Generation is attached), editable metadata, or a system field."));
        Assert.Equal(lists.SelectMany(static list => list).Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
    }

    /// <summary>The body fields for every creation input, with changed values, between <paramref name="prefix"/> and <paramref name="suffix"/>.</summary>
    private static string InputsJson(string prefix, string suffix) =>
        prefix + string.Join(',', CreationInputs.Select(input => $$"""
            "{{Camel(input)}}":"{{Changed}} {{input}} {{Guid.NewGuid()}}"
            """)) + suffix;

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>The Version's creation inputs exactly as stored, each hex-encoded, so the comparison is byte for byte.</summary>
    private static string Stored(N8TracksApiFactory factory, Guid id) =>
        TestDatabase.Scalar(
            factory.DataPath,
            $"SELECT {string.Join(" || '|' || ", CreationInputs.Select(static input => $"hex({Snake(input)})"))} FROM versions WHERE id = '{id.ToString().ToUpperInvariant()}';");

    private static string Snake(string name) =>
        string.Concat(name.Select(static (letter, index) => char.IsUpper(letter) ? (index > 0 ? "_" : string.Empty) + char.ToLowerInvariant(letter) : letter.ToString()));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string ifMatch, string? json)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        return await client.SendAsync(request);
    }

    /// <summary>How a guard exercises one endpoint or method; <paramref name="ChangesInputs"/> when it does change a mutable Version's inputs.</summary>
    private sealed record Exerciser(Func<Target, Task> Run, bool ChangesInputs = false);

    /// <summary>The Version a guard runs against, and what its exercisers need.</summary>
    private sealed record Target(
        N8TracksApiFactory Factory,
        HttpClient Client,
        Guid SongId,
        string SongShortcode,
        Guid VersionId,
        string VersionShortcode,
        IReadOnlyList<Guid> Snapshots,
        bool Frozen)
    {
        public async Task<JsonElement> ReadAsync()
        {
            using var response = await Client.GetAsync(new Uri($"/api/v1/versions/{VersionId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await SetupApi.JsonAsync(response);
        }

        /// <summary>The Version's current revision, so the freeze, not a stale revision, is what refuses.</summary>
        public async Task<string> IfMatchAsync() => SongApi.Quoted((await ReadAsync()).GetProperty("revision").GetInt32());

        /// <summary>A write of inputs: 409 <c>version_frozen</c> on a frozen Version, 200 on a mutable one.</summary>
        public async Task ExpectInputsWriteAsync(HttpResponseMessage response)
        {
            if (Frozen)
            {
                await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "version_frozen");
            }
            else
            {
                Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            }
        }
    }
}
