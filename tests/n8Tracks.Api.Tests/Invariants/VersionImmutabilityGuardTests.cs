using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Inventory;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;
using n8Tracks.Infrastructure.Retention;

namespace n8Tracks.Api.Tests.Invariants;

/// <summary>
/// The guard of invariant 1 (CLAUDE.md), Version immutability: once a Generation is attached, a
/// Version's creation inputs cannot change through any path. It fails when
/// <list type="bullet">
/// <item>a property of the Version entity, or a column of <c>versions</c>, is in none of the three
/// lists below (a creation input added later cannot escape the rule unnoticed), or a key of the
/// options document (<see cref="VersionInputs"/>) is not one every write below changes in turn;</item>
/// <item>an entity method that changes an input does not throw on a frozen Version;</item>
/// <item>an unsafe <c>/api/v1</c> endpoint, or a public method of an application service, is one it
/// has not been told how to exercise (or why it touches no Version); or</item>
/// <item>any of them, called on a frozen Version with a payload touching each creation input, leaves
/// the stored inputs other than byte-identical. The complement (the same calls on a mutable Version
/// do change them) keeps it from passing because nothing works.</item>
/// </list>
/// It covers the web UI and the REST API (which the extension uses), and retention (#95): the retention
/// service's methods are exercised like any catalog service's, and a retained type over the
/// <c>versions</c> table must come with an exerciser that retains and restores a frozen Version through
/// every shape upgrader. Import (M4) and the MCP gateway (M7) are not covered yet: those milestones
/// extend this test.
/// </summary>
public sealed class VersionImmutabilityGuardTests
{
    /// <summary>
    /// Frozen once a Generation is attached: the lyrics, the styles, and the Suno options (each key of
    /// <see cref="VersionInputs"/> in turn, below). Lineage sources join this list when they are added.
    /// </summary>
    private static readonly string[] CreationInputs = [nameof(SongVersion.Lyrics), nameof(SongVersion.Styles), nameof(SongVersion.Inputs)];

    /// <summary>The same, column by column: the options are stored as the kind, the model, and one JSON document.</summary>
    private static readonly string[] CreationInputColumns =
        [nameof(VersionRecord.Lyrics), nameof(VersionRecord.Styles), nameof(VersionRecord.Kind), nameof(VersionRecord.Model), nameof(VersionRecord.Inputs)];

    /// <summary>The text inputs, sent as top-level fields of an edit; the options go in its <c>inputs</c> object.</summary>
    private static readonly string[] TextInputs = [nameof(SongVersion.Lyrics), nameof(SongVersion.Styles)];

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

        AssertClassified(properties, CreationInputs, SystemFields);

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

        AssertClassified(columns, CreationInputColumns, SystemColumns);

        // Every creation input is compared by the trigger that refuses changing a frozen Version.
        var trigger = TestDatabase.Scalar(factory.DataPath, $"SELECT sql FROM sqlite_master WHERE name = '{N8TracksDbContext.VersionFrozenTrigger}';");
        foreach (var input in CreationInputColumns)
        {
            var column = model.FindProperty(input)!.GetColumnName();
            Assert.Contains($"NEW.{column} IS NOT OLD.{column}", trigger, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every key of the options document is one the writes below change in turn: the entity's
    /// properties, the rules' keys, and the API's <c>inputs</c> agree, so a new option cannot be left out.
    /// </summary>
    [Fact]
    public async Task EveryKeyOfTheOptionsDocumentIsACreationInputTheGuardChanges()
    {
        var properties = typeof(VersionInputs).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(static property => Camel(property.Name)).ToList();

        Assert.Equal(properties, VersionInputRules.Keys);

        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var target = await TargetAsync(factory, client, "Options guard", frozen: false);
        var inputs = (await target.ReadAsync()).GetProperty("inputs");
        Assert.Equal(properties, inputs.EnumerateObject().Select(static option => option.Name));
    }

    [Fact]
    public void EveryEntityMethodThatChangesAnInputThrowsOnAFrozenVersion()
    {
        var mutable = new SongVersion(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "Name", "Notes", VersionVisibility.Active, "Lyrics", "Styles",
            InputValues.Defaults(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Revision: 1);
        var frozen = mutable.AttachGeneration(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch).Version;

        // Each public method of the entity: how it would change an input, or why it cannot. Every
        // option is changed alone.
        var changesInputs = new Dictionary<string, Func<SongVersion, SongVersion>>(StringComparer.Ordinal)
        {
            [nameof(SongVersion.WithInputs)] = static version => version.WithInputs(Changed, version.Styles, version.Inputs),
            [nameof(SongVersion.WithInputs) + " (styles)"] = static version => version.WithInputs(version.Lyrics, Changed, version.Inputs),
        };
        foreach (var key in VersionInputRules.Keys)
        {
            changesInputs[$"{nameof(SongVersion.WithInputs)} (inputs.{key})"] = version => version.WithInputs(version.Lyrics, version.Styles, InputValues.WithChanged(version.Inputs, key));
        }

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
            Assert.NotEqual((mutable.Lyrics, mutable.Styles, mutable.Inputs), (changed.Lyrics, changed.Styles, changed.Inputs));
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
        var catalogNamespaces = new[] { typeof(SongVersion).Namespace, typeof(VersionService).Namespace, typeof(CatalogReference).Namespace, typeof(RetentionService).Namespace };
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
            using var response = await SongApi.SendJsonAsync(target.Client, HttpMethod.Post, SongApi.Songs, await target.InputsJsonAsync("""{"title":"Another Song",""", "}"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }),
        ["PATCH /api/v1/songs/{reference}"] = new(async target =>
        {
            foreach (var reference in new[] { target.SongId.ToString(), target.SongShortcode })
            {
                var song = await SetupApi.JsonAsync(await target.Client.GetAsync(SongApi.Song(reference)));
                using var response = await SongApi.PatchAsync(target.Client, reference, SongApi.Quoted(song.GetProperty("revision").GetInt32()), await target.InputsJsonAsync("""{"title":"Renamed",""", "}"));
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
                await target.InputsJsonAsync($$"""{"sourceVersionId":"{{target.VersionShortcode}}","number":"{{number}}",""", "}"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }),
        ["PUT /api/v1/songs/{reference}/credits"] = new(async target =>
        {
            // Credits are the Song's own, never a Version's: the inputs sent alongside are not read.
            using var created = await SongApi.SendJsonAsync(target.Client, HttpMethod.Post, new Uri("/api/v1/artists", UriKind.Relative), """{"name":"Guard Artist","confirmDuplicate":true}""");
            var artist = await SetupApi.JsonAsync(created);
            var song = await SetupApi.JsonAsync(await target.Client.GetAsync(SongApi.Song(target.SongShortcode)));
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Put,
                new Uri($"/api/v1/songs/{target.SongShortcode}/credits", UriKind.Relative),
                SongApi.Quoted(song.GetProperty("revision").GetInt32()),
                await target.InputsJsonAsync($$"""{"primaryArtistId":"{{artist.GetProperty("id").GetString()}}","featuredArtistIds":[],""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["POST /api/v1/songs/{reference}/relationships"] = new(async target =>
        {
            // A relationship joins two Songs, never their Versions: the inputs sent alongside are not read.
            var other = await SongApi.CreateAsync(target.Client, "Guard Related");
            using var response = await SongApi.SendJsonAsync(
                target.Client,
                HttpMethod.Post,
                new Uri($"/api/v1/songs/{target.SongShortcode}/relationships", UriKind.Relative),
                await target.InputsJsonAsync($$"""{"typeId":"{{SystemRelationshipTypes.Cover.Id}}","direction":"forward","otherSong":"{{other.GetProperty("shortcode").GetString()}}",""", "}"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }),
        ["DELETE /api/v1/songs/{reference}/relationships/{id:guid}"] = new(async target =>
        {
            var other = await SongApi.CreateAsync(target.Client, "Guard Unrelated");
            using var related = await SongApi.SendJsonAsync(
                target.Client,
                HttpMethod.Post,
                new Uri($"/api/v1/songs/{target.SongId}/relationships", UriKind.Relative),
                $$"""{"typeId":"{{SystemRelationshipTypes.Remix.Id}}","direction":"reverse","otherSong":"{{other.GetProperty("id").GetString()}}"}""");
            Assert.Equal(HttpStatusCode.Created, related.StatusCode);
            var relationship = (await SetupApi.JsonAsync(related)).GetProperty("relationships").EnumerateArray()
                .Single(item => item.GetProperty("song").GetProperty("id").GetString() == other.GetProperty("id").GetString())
                .GetProperty("id").GetString();
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Delete,
                new Uri($"/api/v1/songs/{target.SongShortcode}/relationships/{relationship}", UriKind.Relative),
                SongApi.Quoted(1),
                await target.InputsJsonAsync("{", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["PUT /api/v1/songs/{reference}/current-version"] = new(async target =>
        {
            using var response = await SongApi.SendJsonAsync(
                target.Client,
                HttpMethod.Put,
                new Uri($"/api/v1/songs/{target.SongShortcode}/current-version", UriKind.Relative),
                await target.InputsJsonAsync($$"""{"versionId":"{{target.VersionShortcode}}",""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["PATCH /api/v1/versions/{reference}"] = new(
            async target =>
            {
                // Each text input alone, each option alone, then all of them; by ID and by shortcode;
                // with metadata and without. Each body is worked out from the Version as it is now.
                var bodies = TextInputs.Select(input => (Func<Task<string>>)(() => Task.FromResult($$"""{"{{Camel(input)}}":"{{Changed}} {{input}} {{Guid.NewGuid()}}"}""")))
                    .Concat(VersionInputRules.Keys.Select(key => (Func<Task<string>>)(async () =>
                        $$$"""{"inputs":{"{{{key}}}":{{{InputValues.ChangedJson(key, (await target.ReadAsync()).GetProperty("inputs").GetProperty(key))}}}}}""")))
                    .Append(() => target.InputsJsonAsync("{", "}"))
                    .Append(() => target.InputsJsonAsync("""{"name":"Renamed","notes":"Noted",""", "}"));
                foreach (var (body, index) in bodies.Select(static (body, index) => (body, index)))
                {
                    var reference = index % 2 == 0 ? target.VersionId.ToString() : target.VersionShortcode;
                    var json = await body();
                    using var response = await SendAsync(target.Client, HttpMethod.Patch, new Uri($"/api/v1/versions/{reference}", UriKind.Relative), await target.IfMatchAsync(), json);
                    await target.ExpectInputsWriteAsync(response, json);
                }
            },
            ChangesInputs: true),
        ["POST /api/v1/versions/{reference}/snapshots"] = new(async target =>
        {
            using var response = await SongApi.SendJsonAsync(target.Client, HttpMethod.Post, new Uri($"/api/v1/versions/{target.VersionShortcode}/snapshots", UriKind.Relative), await target.InputsJsonAsync("{", "}"));
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
                    await target.ExpectInputsWriteAsync(response, "restore");
                }
            },
            ChangesInputs: true),
        ["DELETE /api/v1/versions/{reference}/snapshots/{snapshotId:guid}"] = new(async target =>
        {
            // Deleting a history entry retains the snapshot row only; the inputs sent alongside are not read.
            var entry = await NewHistoryEntryAsync(target);
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Delete,
                new Uri($"/api/v1/versions/{target.VersionShortcode}/snapshots/{entry}", UriKind.Relative),
                SongApi.Quoted(1),
                await target.InputsJsonAsync("{", "}"));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }),
        ["POST /api/v1/playlists/{id:guid}/songs"] = new(async target =>
        {
            // A Playlist holds the Song, never a Version: the inputs sent alongside are not read.
            var (id, revision) = await PlaylistAsync(target, withSong: false);
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Post,
                new Uri($"/api/v1/playlists/{id}/songs", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync($$"""{"songId":"{{target.SongShortcode}}",""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["PUT /api/v1/playlists/{id:guid}/songs"] = new(async target =>
        {
            var (id, revision) = await PlaylistAsync(target, withSong: true);
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Put,
                new Uri($"/api/v1/playlists/{id}/songs", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync($$"""{"songIds":["{{target.SongId}}"],""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["DELETE /api/v1/playlists/{id:guid}/songs/{reference}"] = new(async target =>
        {
            var (id, revision) = await PlaylistAsync(target, withSong: true);
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Delete,
                new Uri($"/api/v1/playlists/{id}/songs/{target.SongShortcode}", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync("{", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["POST /api/v1/albums/{id:guid}/tracks"] = new(async target =>
        {
            // An Album holds the Song as a track, never a Version: the inputs sent alongside are not read.
            var (id, revision) = await AlbumAsync(target, withSong: false);
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Post,
                new Uri($"/api/v1/albums/{id}/tracks", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync($$"""{"songId":"{{target.SongShortcode}}",""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["PUT /api/v1/albums/{id:guid}/tracks"] = new(async target =>
        {
            var (id, revision) = await AlbumAsync(target, withSong: true);
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Put,
                new Uri($"/api/v1/albums/{id}/tracks", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync($$"""{"tracks":[{"songId":"{{target.SongId}}","disc":1,"track":7}],""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["DELETE /api/v1/albums/{id:guid}/tracks/{reference}"] = new(async target =>
        {
            var (id, revision) = await AlbumAsync(target, withSong: true);
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Delete,
                new Uri($"/api/v1/albums/{id}/tracks/{target.SongShortcode}", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync("{", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["PATCH /api/v1/suno/models/{id:guid}"] = new(async target =>
        {
            // Renaming the model a Version names would change that Version's model, so it is refused.
            var (id, revision) = await target.ModelAsync();
            using var response = await SendAsync(target.Client, HttpMethod.Patch, new Uri($"/api/v1/suno/models/{id}", UriKind.Relative), SongApi.Quoted(revision), """{"name":"renamed by the guard"}""");
            await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "model_in_use");
        }),
        ["DELETE /api/v1/suno/models/{id:guid}"] = new(async target =>
        {
            var (id, revision) = await target.ModelAsync();
            using var response = await SendAsync(target.Client, HttpMethod.Delete, new Uri($"/api/v1/suno/models/{id}", UriKind.Relative), SongApi.Quoted(revision), json: null);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "model_in_use");
        }),
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
        ["POST /api/v1/suno/models"] = "adds a model to the list; a Version's model is not touched",
        ["POST /api/v1/genres"] = "adds a Genre to the list; assigning it to a Song is the Song's PATCH, which touches no Version",
        ["PATCH /api/v1/genres/{id:guid}"] = "renames a Genre; no assignment and no Version changes",
        ["POST /api/v1/genres/{id:guid}/merge"] = "moves Songs' Genre assignments (song_genres and the Songs' revisions), never a Version",
        ["DELETE /api/v1/genres/{id:guid}"] = "removes or reassigns Songs' Genre assignments (song_genres and the Songs' revisions), never a Version",
        ["POST /api/v1/tags"] = "adds a Tag to the list; assigning it to a Song is the Song's PATCH, which touches no Version",
        ["PATCH /api/v1/tags/{id:guid}"] = "renames or recolours a Tag; no assignment and no Version changes",
        ["POST /api/v1/tags/{id:guid}/merge"] = "moves Songs' Tag assignments (song_tags and the Songs' revisions), never a Version",
        ["DELETE /api/v1/tags/{id:guid}"] = "removes Songs' Tag assignments (song_tags and the Songs' revisions), never a Version",
        ["POST /api/v1/artists"] = "adds an Artist record (artists, artist_aliases, artist_links); no Song or Version is touched",
        ["PATCH /api/v1/artists/{id:guid}"] = "edits an Artist record's name, aliases, notes, and links; no Song or Version is touched",
        ["POST /api/v1/albums"] = "adds an Album record (albums) from a title; no Song or Version is touched",
        ["PATCH /api/v1/albums/{id:guid}"] = "edits an Album record's title, Album Artist, release details, and links; no Song or Version is touched",
        ["POST /api/v1/relationship-types"] = "adds a relationship type (song_relationship_types); no Song or Version is touched",
        ["PATCH /api/v1/relationship-types/{id:guid}"] = "renames a user-defined relationship type; no relationship, Song, or Version changes",
        ["DELETE /api/v1/relationship-types/{id:guid}"] = "removes a type and its relationships (song_relationships and the Songs' last-updated times), never a Version",
        ["POST /api/v1/playlists"] = "adds an empty Playlist record (playlists) from a title; no Song or Version is touched",
        ["PATCH /api/v1/playlists/{id:guid}"] = "edits a Playlist record's title and description; no Song or Version is touched",
        ["PUT /api/v1/suno/models/order"] = "reorders the model list; a Version's model is not touched",
        ["DELETE /api/v1/workflow-states/{id:guid}"] = "workflow states; moves Songs to another state, never a Version",
        ["POST /api/v1/backups"] = "queues a backup: reads the database, writes only an archive file",
        ["DELETE /api/v1/backups/{location}/{name}"] = "deletes an archive file, never a database row",
        ["PUT /api/v1/settings/backup-schedule"] = "the backup schedule: one settings row",
        ["PUT /api/v1/settings/version-defaults"] = "the defaults for new Versions: one settings row, applied only when a Song is created",
        ["PUT /api/v1/settings/catalog"] = "the default Artist: one settings row, applied only as a new Song's credit",
        ["POST /api/v1/restores/validate"] = "reads a backup archive into a temporary folder; changes no row",
        ["POST /api/v1/restores/uploads"] = "writes an uploaded archive to a temporary file and reads it; changes no row",
        ["POST /api/v1/artwork"] = "stores an uploaded image as an asset (assets and its files); attaching it is the owner's own edit, and no Version is touched",
        ["POST /api/v1/restores"] = "starts maintenance and a safety backup; it replaces the instance as a whole (#74), never edits a Version",
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
        ["EditorRevisionService.DeleteAsync(Guid, Guid, CancellationToken)"] = new(static async target =>
        {
            var entry = await NewHistoryEntryAsync(target);
            Assert.IsType<SnapshotDeleteOutcome.Deleted>(
                await InScopeAsync<EditorRevisionService, SnapshotDeleteOutcome>(target, service => service.DeleteAsync(target.VersionId, entry, default)));
        }),
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

        // Retention: each call retains a new history entry of the Version and restores it, so the
        // Version's own row is what a later story's retained type would put back.
        ["RetentionService.RetainAsync(RetentionRequest, CancellationToken)"] = new(static target => RetainAndRestoreAsync(target, static (service, request, _) => service.RetainAsync(request, default))),
        ["RetentionService.RetainWithinAsync(RetentionRequest, CancellationToken)"] = new(static target => RetainAndRestoreAsync(target, static (service, request, transaction) =>
            transaction.RunAsync(token => service.RetainWithinAsync(request, token), default))),
        ["RetentionService.RestoreAsync(Guid, CancellationToken)"] = new(static target => RetainAndRestoreAsync(target, static (service, request, _) => service.RetainAsync(request, default))),
        ["RetentionService.RestoreWithinAsync(Guid, CancellationToken)"] = new(static target => RetainAndRestoreAsync(
            target,
            static (service, request, _) => service.RetainAsync(request, default),
            static (service, group, transaction) => transaction.RunAsync(token => service.RestoreWithinAsync(group, token), default))),
        ["RetentionService.FindAsync(Guid, CancellationToken)"] = Service<RetentionService>(static (service, target) => service.FindAsync(target.VersionId, default)),
        ["RetentionService.FindByShortcodeAsync(String, CancellationToken)"] = Service<RetentionService>(static (service, target) => service.FindByShortcodeAsync(target.VersionShortcode, default)),
        ["RetentionService.ListAsync(CancellationToken)"] = Service<RetentionService>(static (service, _) => service.ListAsync(default)),
        ["RetentionService.PruneAsync(CancellationToken)"] = new(static async target =>
        {
            await RetainAndRestoreAsync(target, static (service, request, _) => service.RetainAsync(request, default), restore: null);
            await InScopeAsync<RetentionService, RetentionPruneSummary>(target, static service => service.PruneAsync(default));
        }),
    };

    /// <summary>Public catalog-service methods that take neither a Song nor a Version, and why.</summary>
    private static Dictionary<string, string> ServiceMethodsTakingNoVersion() => new(StringComparer.Ordinal)
    {
        ["SongService.CreateAsync(SongRequest, CancellationToken)"] = "creates a new Song with a new, mutable Version 1 (its options from the defaults and the request)",
        ["SongService.ListAsync(SongListRequest, CancellationToken)"] = "reads only",
        ["SongService.Validate(SongRequest)"] = "pure validation",
        ["WorkflowStateService.ListAsync(CancellationToken)"] = "reads only",
        ["WorkflowStateService.ListWithUsageAsync(CancellationToken)"] = "reads only",
        ["WorkflowStateService.AddAsync(String, String, Int32, CancellationToken)"] = "workflow states",
        ["WorkflowStateService.UpdateAsync(Guid, WorkflowStateEdit, Int32, CancellationToken)"] = "workflow states",
        ["WorkflowStateService.ReorderAsync(IReadOnlyList`1, Int32, CancellationToken)"] = "workflow states",
        ["WorkflowStateService.DeleteAsync(Guid, String, Int32, CancellationToken)"] = "workflow states; moves Songs to another state, never a Version",
        ["RetentionService.IsManagedFilePath(String)"] = "pure path check",
        ["RetentionPruneTask.TickAsync(CancellationToken)"] = "queues the prune job; the prune itself is RetentionService.PruneAsync, exercised above",
    };

    /// <summary>
    /// Retained types over the <c>versions</c> table, each with how it retains and restores a frozen
    /// Version (its shape upgraders run on fixtures of every earlier shape). None is registered until
    /// Version deletion (#101), which adds its type here; the test below fails until it does.
    /// </summary>
    private static Dictionary<string, Exerciser> RetainedVersionTypes() => new(StringComparer.Ordinal);

    [Fact]
    public async Task EveryRetainedTypeOverTheVersionsTableRestoresAFrozenVersionWithItsInputsUnchanged()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var overVersions = factory.Services.GetRequiredService<RetainedTypeRegistry>().All
            .Where(static type => type.Table == "versions")
            .Select(static type => type.RecordType)
            .Order(StringComparer.Ordinal)
            .ToList();
        var exercisers = RetainedVersionTypes();

        Assert.True(
            overVersions.SequenceEqual(exercisers.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            $"Retained types over versions ({string.Join(", ", overVersions)}) must each have an exerciser here: retain and restore a frozen Version, through every shape upgrader.");

        var frozen = await TargetAsync(factory, client, "Retained frozen guard", frozen: true);
        foreach (var (type, exercise) in exercisers)
        {
            var before = Stored(factory, frozen.VersionId);
            await exercise.Run(frozen);
            Assert.True(before == Stored(factory, frozen.VersionId), $"Restoring {type} changed a frozen Version's inputs.");
            Assert.True((await frozen.ReadAsync()).GetProperty("isFrozen").GetBoolean(), $"Restoring {type} unfroze the Version.");
        }
    }

    /// <summary>
    /// Takes a new history entry of the target, retains it with <paramref name="retain"/>, and restores
    /// it with <see cref="RetentionService.RestoreAsync"/>, asserting the restore succeeded.
    /// </summary>
    private static Task RetainAndRestoreAsync(
        Target target,
        Func<RetentionService, RetentionRequest, IExclusiveTransaction, Task<RetentionGroup>> retain) =>
        RetainAndRestoreAsync(target, retain, static (service, group, _) => service.RestoreAsync(group, default));

    /// <summary>As above, restoring with <paramref name="restore"/>; with null, the entry stays retained.</summary>
    private static async Task RetainAndRestoreAsync(
        Target target,
        Func<RetentionService, RetentionRequest, IExclusiveTransaction, Task<RetentionGroup>> retain,
        Func<RetentionService, Guid, IExclusiveTransaction, Task<RetentionRestoreOutcome>>? restore)
    {
        var id = await NewHistoryEntryAsync(target);
        var request = new RetentionRequest(
            RetainedRecordTypes.EditorSnapshot,
            $"History entry of {target.VersionShortcode}",
            null,
            [new RetainedRoot(RetainedRecordTypes.EditorSnapshot, id)],
            []);

        var scope = target.Factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var service = scope.ServiceProvider.GetRequiredService<RetentionService>();
            var transaction = scope.ServiceProvider.GetRequiredService<IExclusiveTransaction>();
            var group = await retain(service, request, transaction);
            if (restore is not null)
            {
                Assert.IsType<RetentionRestoreOutcome.Restored>(await restore(service, group.Id, transaction));
            }
        }
    }

    /// <summary>Takes a new history entry of the target (text no other entry holds); its ID.</summary>
    private static async Task<Guid> NewHistoryEntryAsync(Target target)
    {
        var snapshot = await InScopeAsync<EditorRevisionService, SnapshotOutcome>(target, service =>
            service.SnapshotAsync(target.VersionId, new EditorRevisionRequest(Changed + " " + Guid.NewGuid(), Changed, null), default));
        return Assert.IsType<SnapshotOutcome.Created>(snapshot).Revision.Id;
    }

    /// <summary>The Version's edit through the service: each input alone (every option in turn), all of them, and all with metadata.</summary>
    private static async Task EachInputEditAsync(Target target, Func<VersionService, Guid, VersionEdit, int, Task<VersionUpdateOutcome>> update)
    {
        var edits = new List<Func<JsonElement, VersionEdit>>
        {
            static _ => new VersionEdit(SongEditField.Unsent, SongEditField.Unsent, null, SongEditField.Of(Changed + " lyrics " + Guid.NewGuid()), SongEditField.Unsent),
            static _ => new VersionEdit(SongEditField.Unsent, SongEditField.Unsent, null, SongEditField.Unsent, SongEditField.Of(Changed + " styles " + Guid.NewGuid())),
        };
        edits.AddRange(VersionInputRules.Keys.Select(key => (Func<JsonElement, VersionEdit>)(inputs =>
            new VersionEdit(SongEditField.Unsent, SongEditField.Unsent, null, SongEditField.Unsent, SongEditField.Unsent, Options(new JsonObject
            {
                [key] = JsonNode.Parse(InputValues.ChangedJson(key, inputs.GetProperty(key))),
            })))));
        edits.Add(static inputs => new VersionEdit(
            SongEditField.Of("Renamed"),
            SongEditField.Of("Noted"),
            true,
            SongEditField.Of(Changed + Guid.NewGuid()),
            SongEditField.Of(Changed + Guid.NewGuid()),
            Options(InputValues.EveryOptionChanged(inputs))));
        foreach (var editFor in edits)
        {
            var read = await target.ReadAsync();
            var revision = read.GetProperty("revision").GetInt32();
            var edit = editFor(read.GetProperty("inputs"));
            var outcome = await InScopeAsync<VersionService, VersionUpdateOutcome>(target, service => update(service, target.VersionId, edit, revision));
            Assert.IsType(target.Frozen ? typeof(VersionUpdateOutcome.Frozen) : typeof(VersionUpdateOutcome.Updated), outcome);
        }
    }

    /// <summary>Options as an edit carries them, from a JSON object.</summary>
    private static Dictionary<string, JsonElement> Options(JsonObject options)
    {
        using var document = JsonDocument.Parse(options.ToJsonString());
        return document.RootElement.EnumerateObject().ToDictionary(static option => option.Name, static option => option.Value.Clone(), StringComparer.Ordinal);
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

    /// <summary>Every public method of a class in the catalog namespaces (Songs, References, Retention), described by its signature.</summary>
    private static List<string> CatalogServiceMethods() =>
        [.. ApplicationServices()
            .Where(static type => type.Namespace == typeof(VersionService).Namespace
                || type.Namespace == typeof(CatalogReference).Namespace
                || type.Namespace == typeof(RetentionService).Namespace)
            .SelectMany(PublicMethods)
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)];

    private static IEnumerable<MethodInfo> PublicMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName);

    private static string Describe(MethodInfo method) =>
        $"{method.DeclaringType!.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(static parameter => parameter.ParameterType.Name))})";

    /// <summary>Each name is in exactly one of the three lists.</summary>
    private static void AssertClassified(List<string> names, string[] creationInputs, string[] systemFields)
    {
        var lists = new[] { creationInputs, EditableMetadata, systemFields };
        Assert.All(names, name => Assert.True(
            lists.Count(list => list.Contains(name, StringComparer.Ordinal)) == 1,
            $"{name} is not classified: a creation input (frozen once a Generation is attached), editable metadata, or a system field."));
        Assert.Equal(lists.SelectMany(static list => list).Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The body fields for every creation input, with changed values (every option in <c>inputs</c>,
    /// changed from <paramref name="inputs"/>), between <paramref name="prefix"/> and <paramref name="suffix"/>.
    /// </summary>
    private static string InputsJson(string prefix, string suffix, JsonElement inputs) =>
        prefix + string.Join(',', TextInputs.Select(input => $$"""
            "{{Camel(input)}}":"{{Changed}} {{input}} {{Guid.NewGuid()}}"
            """)) + ",\"inputs\":" + InputValues.EveryOptionChanged(inputs).ToJsonString() + suffix;

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>The Version's creation inputs exactly as stored, each column hex-encoded, so the comparison is byte for byte.</summary>
    private static string Stored(N8TracksApiFactory factory, Guid id) =>
        TestDatabase.Scalar(
            factory.DataPath,
            $"SELECT {string.Join(" || '|' || ", CreationInputColumns.Select(static input => $"hex({Snake(input)})"))} FROM versions WHERE id = '{id.ToString().ToUpperInvariant()}';");

    private static string Snake(string name) =>
        string.Concat(name.Select(static (letter, index) => char.IsUpper(letter) ? (index > 0 ? "_" : string.Empty) + char.ToLowerInvariant(letter) : letter.ToString()));

    /// <summary>A new Album, holding the target's Song as a track when <paramref name="withSong"/>; its ID and revision.</summary>
    private static async Task<(string Id, int Revision)> AlbumAsync(Target target, bool withSong)
    {
        using var created = await SongApi.SendJsonAsync(target.Client, HttpMethod.Post, new Uri("/api/v1/albums", UriKind.Relative), """{"title":"Guard Album"}""");
        var album = await SetupApi.JsonAsync(created);
        var id = album.GetProperty("id").GetString()!;
        if (!withSong)
        {
            return (id, 1);
        }

        using var added = await SendAsync(target.Client, HttpMethod.Post, new Uri($"/api/v1/albums/{id}/tracks", UriKind.Relative), SongApi.Quoted(1), $$"""{"songId":"{{target.SongId}}"}""");
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        return (id, 2);
    }

    /// <summary>A new Playlist, holding the target's Song when <paramref name="withSong"/>; its ID and revision.</summary>
    private static async Task<(string Id, int Revision)> PlaylistAsync(Target target, bool withSong)
    {
        using var created = await SongApi.SendJsonAsync(target.Client, HttpMethod.Post, new Uri("/api/v1/playlists", UriKind.Relative), """{"title":"Guard Playlist"}""");
        var playlist = await SetupApi.JsonAsync(created);
        var id = playlist.GetProperty("id").GetString()!;
        if (!withSong)
        {
            return (id, 1);
        }

        using var added = await SendAsync(target.Client, HttpMethod.Post, new Uri($"/api/v1/playlists/{id}/songs", UriKind.Relative), SongApi.Quoted(1), $$"""{"songId":"{{target.SongId}}"}""");
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        return (id, 2);
    }

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

        /// <summary>The ID of the model the Version names, and the model list's revision.</summary>
        public async Task<(Guid Id, int Revision)> ModelAsync()
        {
            var name = (await ReadAsync()).GetProperty("inputs").GetProperty("model").GetString();
            var list = await SetupApi.JsonAsync(await Client.GetAsync(new Uri("/api/v1/suno/models", UriKind.Relative)));
            var model = list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
            return (model.GetProperty("id").GetGuid(), list.GetProperty("revision").GetInt32());
        }

        /// <summary>The Version's current revision, so the freeze, not a stale revision, is what refuses.</summary>
        public async Task<string> IfMatchAsync() => SongApi.Quoted((await ReadAsync()).GetProperty("revision").GetInt32());

        /// <summary>Body fields changing every creation input of the Version as it is now (<see cref="InputsJson"/>).</summary>
        public async Task<string> InputsJsonAsync(string prefix, string suffix) => InputsJson(prefix, suffix, (await ReadAsync()).GetProperty("inputs"));

        /// <summary>A write of inputs (<paramref name="what"/>): 409 <c>version_frozen</c> on a frozen Version, 200 on a mutable one.</summary>
        public async Task ExpectInputsWriteAsync(HttpResponseMessage response, string what)
        {
            if (Frozen)
            {
                Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{what}: {await response.Content.ReadAsStringAsync()}");
                await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, "version_frozen");
            }
            else
            {
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{what}: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }
}
