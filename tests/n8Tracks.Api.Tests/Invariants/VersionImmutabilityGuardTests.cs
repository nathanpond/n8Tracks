using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Assets;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Inventory;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Suno;
using n8Tracks.Application.Artwork;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Application.Suno.Generate;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;
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
/// every shape upgrader. Generations (#117) are a catalog namespace of their own: attaching one from a
/// raw clip, the one way a Generation is made, is exercised here, and so are rating it, commenting
/// on it (#119), archiving it, choosing or clearing the Song's Selected Generation (#120), and
/// giving it an image and picking that image as the Song's artwork (#121, <c>Application.Artwork</c>),
/// and moving it into a new Song (#123), which copies its Version's inputs into the new Version 1 and
/// leaves the old Version's byte-identical. A Version's lineage (#122:
/// its sources, Inspiration, Voice, and file inputs) is a creation input like its options: the
/// entity's <see cref="SongVersion.Lineage"/>, the columns of each lineage table (each with its own
/// insert, update, and delete freeze triggers), every lineage key of <c>inputs</c> in every edit, and
/// the stored comparison, which reads each source's identity (its Suno ID when it has one, else its
/// Generation's or Song's ID), type, action, group, order, position, and secondary identifiers. The
/// rest of import (M4) and the MCP gateway (M7) are not covered yet: those stories extend this test.
/// </summary>
public sealed class VersionImmutabilityGuardTests
{
    /// <summary>
    /// Frozen once a Generation is attached: the lyrics, the styles, the Suno options (each key of
    /// <see cref="VersionInputs"/> in turn, below), and the lineage (#122: each of its parts in turn).
    /// </summary>
    private static readonly string[] CreationInputs = [nameof(SongVersion.Lyrics), nameof(SongVersion.Styles), nameof(SongVersion.Inputs), nameof(SongVersion.Lineage)];

    /// <summary>
    /// Each lineage table's columns (#122): the ones that tie a row to its Version, and the rest, every
    /// one a creation input. A new column must be classified here, and a new table that refers to
    /// <c>versions</c> must be a lineage table or say why it holds no input (<see cref="TablesReferringToVersionsWithNoInput"/>).
    /// </summary>
    private static readonly Dictionary<string, (string[] System, string[] Inputs)> LineageColumns = new(StringComparer.Ordinal)
    {
        ["version_sources"] = (
            ["id", "version_id"],
            ["source_group", "position", "type_id", "suno_action", "generation_id", "song_id", "external_reference_id", "continue_at_hundredths", "secondary_ids"]),
        ["version_inspiration_playlists"] = (["version_id"], ["suno_playlist_id", "name", "clip_ids"]),
        ["version_voices"] = (["version_id"], ["persona_id", "name"]),
        ["version_file_inputs"] = (["version_id"], ["kind", "description"]),
    };

    /// <summary>Tables with a foreign key to <c>versions</c> that hold no creation input, and why.</summary>
    private static readonly Dictionary<string, string> TablesReferringToVersionsWithNoInput = new(StringComparer.Ordinal)
    {
        ["songs"] = "its current Version: which Version the user works from, never an input",
        ["editor_revisions"] = "the editing history: snapshots of text, restored only through the Version's own write",
        ["generations"] = "what the Version produced, never what it was made from",
    };

    /// <summary>
    /// The columns of a source a rewrite may change on a frozen Version: a deleted Generation's ID
    /// becomes the external reference with the same Suno ID, which is the source's identity either way.
    /// </summary>
    private static readonly string[] RewrittenSourceColumns = ["generation_id", "external_reference_id"];

    /// <summary>The same, column by column: the options are stored as the kind, the model, and one JSON document.</summary>
    private static readonly string[] CreationInputColumns =
        [nameof(VersionRecord.Lyrics), nameof(VersionRecord.Styles), nameof(VersionRecord.Kind), nameof(VersionRecord.Model), nameof(VersionRecord.Inputs)];

    /// <summary>The text inputs, sent as top-level fields of an edit; the options go in its <c>inputs</c> object.</summary>
    private static readonly string[] TextInputs = [nameof(SongVersion.Lyrics), nameof(SongVersion.Styles)];

    /// <summary>Always editable, frozen or not.</summary>
    private static readonly string[] EditableMetadata = [nameof(SongVersion.Name), nameof(SongVersion.Notes), nameof(SongVersion.Visibility)];

    /// <summary>
    /// Identity, Song, number, revision, timestamps, the freeze itself, and what import recorded about
    /// the inputs (#135, set when import creates the Version): no edit sets them.
    /// </summary>
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
        nameof(SongVersion.Imported),
    ];

    /// <summary>
    /// The stored form of the same, column by column (<c>visibility</c> stores the archived flag; the
    /// sort key is the number's; <c>imported_inputs</c> stores the import marks).
    /// </summary>
    private static readonly string[] SystemColumns =
        [.. SystemFields.Where(static field => field != nameof(SongVersion.Imported)), nameof(VersionRecord.NumberSortKey), nameof(VersionRecord.ImportedInputs)];

    private const string Changed = "changed by the guard";

    /// <summary>
    /// The application namespaces whose services may take a Song, a Version, or a Generation, each
    /// named on its own (no prefix or wildcard), so a write path in a new namespace is enumerated here
    /// rather than escaping the guard: every public method of a class in one of them must be exercised
    /// or excused below, and a service anywhere else may take none of their types. A story that adds a
    /// catalog namespace adds it to this list.
    /// </summary>
    private static readonly string?[] CatalogServiceNamespaces =
    [
        typeof(VersionService).Namespace, // n8Tracks.Application.Songs
        typeof(CatalogReference).Namespace, // n8Tracks.Application.References
        typeof(RetentionService).Namespace, // n8Tracks.Application.Retention
        typeof(GenerationService).Namespace, // n8Tracks.Application.Generations (#117)
        typeof(GenerationArtworkService).Namespace, // n8Tracks.Application.Artwork (#121)
        typeof(ImportCommitService).Namespace, // n8Tracks.Application.Suno.Import (#140: the import commit attaches to Versions)
        typeof(ExternalReferenceResolver).Namespace, // n8Tracks.Application.Suno (#140: the resolver the commit calls takes the import's lineage)
        typeof(GenerationRequestService).Namespace, // n8Tracks.Application.Suno.Generate (#144: Generate on Suno reads a Version into a request)
    ];

    /// <summary>The same, with the domain namespace of the catalog entities: types no service elsewhere may take.</summary>
    private static readonly string?[] CatalogTypeNamespaces = [typeof(SongVersion).Namespace, .. CatalogServiceNamespaces];

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
    /// Every column of each lineage table (#122) is classified, every table that refers to
    /// <c>versions</c> is a lineage table or holds no input, and each lineage table has its three freeze
    /// triggers, the source table's update trigger comparing every input but the rewritten pointer.
    /// </summary>
    [Fact]
    public async Task EveryLineageTableIsClassifiedAndFrozenByItsOwnTriggers()
    {
        using var factory = new N8TracksApiFactory();
        using (var client = factory.CreateClient())
        using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        Assert.Equal(LineageColumns.Keys.Order(StringComparer.Ordinal), N8TracksDbContext.LineageTables.Order(StringComparer.Ordinal));
        var referring = TestDatabase.Rows(
            factory.DataPath,
            "SELECT DISTINCT m.name FROM sqlite_master AS m, pragma_foreign_key_list(m.name) AS f WHERE m.type = 'table' AND f.\"table\" = 'versions' ORDER BY m.name;");
        Assert.Equal(
            referring,
            [.. LineageColumns.Keys.Concat(TablesReferringToVersionsWithNoInput.Keys).Order(StringComparer.Ordinal)]);

        foreach (var (table, (system, inputs)) in LineageColumns)
        {
            var columns = TestDatabase.Rows(factory.DataPath, $"SELECT name FROM pragma_table_info('{table}') ORDER BY name;");
            Assert.Equal(system.Concat(inputs).Order(StringComparer.Ordinal), columns);
            Assert.Empty(system.Intersect(inputs, StringComparer.Ordinal));

            foreach (var name in N8TracksDbContext.LineageFrozenTriggers(table))
            {
                var sql = TestDatabase.Scalar(factory.DataPath, $"SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = '{name}' AND tbl_name = '{table}';");
                Assert.Contains("is_frozen = 1", sql, StringComparison.Ordinal);
                Assert.Contains("RAISE(ABORT", sql, StringComparison.Ordinal);
            }
        }

        var update = TestDatabase.Scalar(factory.DataPath, "SELECT sql FROM sqlite_master WHERE name = 'tr_version_sources_frozen_update';");
        foreach (var column in LineageColumns["version_sources"].Inputs.Concat(LineageColumns["version_sources"].System).Except(RewrittenSourceColumns, StringComparer.Ordinal))
        {
            Assert.Contains($"NEW.{column} IS OLD.{column}", update, StringComparison.Ordinal);
        }

        Assert.Contains("(SELECT suno_id FROM generations WHERE id = OLD.generation_id)", update, StringComparison.Ordinal);

        // And the reverse rewrite, an imported clip resolving a reference (#137), checks the Suno ID too.
        Assert.Contains("(SELECT suno_id FROM generations WHERE id = NEW.generation_id)", update, StringComparison.Ordinal);
        Assert.Contains("(SELECT suno_id FROM external_suno_references WHERE id = OLD.external_reference_id AND kind = 'clip')", update, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolving an external reference (#137) is the one write besides a deletion's that may touch a
    /// frozen Version's sources: it points the source at the Generation imported with the same Suno ID,
    /// and the Version's stored inputs, sources compared by Suno ID, are byte-identical afterwards. The
    /// database refuses the same rewrite to a Generation with another Suno ID.
    /// </summary>
    [Fact]
    public async Task ResolvingAnExternalReferenceLeavesAFrozenVersionsInputsByteIdentical()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var frozen = await TargetAsync(factory, client, "Resolve guard", frozen: true);
        var version = frozen.VersionId.ToString().ToUpperInvariant();
        var held = "held-Resolve-guard";
        Assert.Equal(held, TestDatabase.Scalar(factory.DataPath, $"SELECT e.suno_id FROM version_sources AS s JOIN external_suno_references AS e ON e.id = s.external_reference_id WHERE s.version_id = '{version}';"));

        // A Generation with another Suno ID is refused as the source's new target.
        var other = await SongApi.CreateAsync(client, "Resolve guard other");
        var wrong = await SongApi.AttachGenerationAsync(factory, other.GetProperty("currentVersion").GetProperty("id").GetString()!, Clips.Minimal("not-the-held-clip"));
        var before = Stored(factory, frozen.VersionId);
        var refused = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"UPDATE version_sources SET external_reference_id = NULL, generation_id = '{wrong.Generation.Id.ToString().ToUpperInvariant()}' WHERE version_id = '{version}';"));
        Assert.Contains("never change", refused.Message, StringComparison.Ordinal);
        Assert.True(before == Stored(factory, frozen.VersionId));

        // So is one to an ID no Generation has: a comparison the database cannot make is a change.
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"UPDATE version_sources SET external_reference_id = NULL, generation_id = '{Guid.NewGuid().ToString().ToUpperInvariant()}' WHERE version_id = '{version}';"));
        Assert.True(before == Stored(factory, frozen.VersionId));

        // The clip itself, imported into another Song, resolves the reference.
        var parent = await SongApi.CreateAsync(client, "Resolve guard parent");
        var imported = await SongApi.AttachGenerationAsync(factory, parent.GetProperty("currentVersion").GetProperty("id").GetString()!, Clips.Minimal(held));
        var resolution = await InScopeAsync<ExternalReferenceResolver, ReferenceResolution>(frozen, service => service.ResolveAsync(held, default));

        Assert.Equal(new ReferenceResolution(1, 1), resolution);
        Assert.True(before == Stored(factory, frozen.VersionId), "Resolving a reference changed a frozen Version's inputs.");
        Assert.Equal(
            imported.Generation.Id.ToString().ToUpperInvariant(),
            TestDatabase.Scalar(factory.DataPath, $"SELECT generation_id FROM version_sources WHERE version_id = '{version}' AND source_group = 'audio';"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM external_suno_references WHERE suno_id = '{held}';"));
    }

    /// <summary>
    /// The database's own layer (#122, D8): on a frozen Version, every insert, update, and delete of a
    /// lineage row is refused, whatever writes it; the complement passes on a mutable one.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRefusesEveryChangeToAFrozenVersionsLineage()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var frozen = await TargetAsync(factory, client, "Database lineage guard", frozen: true);
        var mutable = await TargetAsync(factory, client, "Database lineage mutable", frozen: false);
        var reference = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM external_suno_references LIMIT 1;");

        string[] Writes(Guid versionId)
        {
            var id = versionId.ToString().ToUpperInvariant();
            var type = SystemRelationshipTypes.SampleThisSong.Id.ToString().ToUpperInvariant();
            return
            [
                $"INSERT INTO version_sources (id, version_id, source_group, position, type_id, suno_action, external_reference_id) VALUES ('{Guid.NewGuid().ToString().ToUpperInvariant()}', '{id}', 'inspiration', 0, '{type}', 'sample', '{reference}');",
                $"UPDATE version_sources SET position = position + 10 WHERE version_id = '{id}';",
                $"DELETE FROM version_sources WHERE version_id = '{id}' AND source_group = 'audio';",
                $"UPDATE version_inspiration_playlists SET name = 'changed' WHERE version_id = '{id}';",
                $"DELETE FROM version_inspiration_playlists WHERE version_id = '{id}';",
                $"UPDATE version_voices SET persona_id = 'changed' WHERE version_id = '{id}';",
                $"DELETE FROM version_voices WHERE version_id = '{id}';",
                $"INSERT INTO version_voices (version_id, persona_id, name) VALUES ('{id}', 'another', '');",
                $"UPDATE version_file_inputs SET description = 'changed' WHERE version_id = '{id}';",
                $"DELETE FROM version_file_inputs WHERE version_id = '{id}' AND kind = 'video';",
                $"INSERT INTO version_file_inputs (version_id, kind, description) VALUES ('{id}', 'audio', 'added');",
                $"INSERT INTO version_inspiration_playlists (version_id, suno_playlist_id, name, clip_ids) VALUES ('{id}', 'another', '', '[]');",
            ];
        }

        foreach (var write in Writes(frozen.VersionId))
        {
            var before = Stored(factory, frozen.VersionId);
            var refused = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(factory.DataPath, write));
            Assert.Contains("never change", refused.Message, StringComparison.Ordinal);
            Assert.True(before == Stored(factory, frozen.VersionId), write);
        }

        // Complement: the same writes on a mutable Version go through (the insert of a second
        // playlist or Voice after the delete, as each holds one).
        foreach (var write in Writes(mutable.VersionId))
        {
            TestDatabase.Execute(factory.DataPath, write);
        }

        // An external reference's Suno ID is what a frozen source is compared by: it never changes.
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(factory.DataPath, $"UPDATE external_suno_references SET suno_id = 'changed' WHERE id = '{reference}';"));
    }

    /// <summary>
    /// #324: a frozen Version's source that points at a Generation is compared by that Generation's
    /// Suno ID, so the database refuses changing a Suno ID once set, whatever writes it, and the frozen
    /// Version's stored inputs stay byte-identical. Complement: a Generation attached without a Suno ID
    /// may be given one, which is then fixed too.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRefusesChangingTheSunoIdOfAGenerationAFrozenSourcePointsAt()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var frozen = await TargetAsync(factory, client, "Suno ID guard", frozen: true, generationSource: true);
        var version = frozen.VersionId.ToString().ToUpperInvariant();
        var source = TestDatabase.Scalar(factory.DataPath, $"SELECT COALESCE(generation_id, 'none') FROM version_sources WHERE version_id = '{version}' AND source_group = 'audio';");
        Assert.Equal("source-Suno-ID-guard", TestDatabase.Scalar(factory.DataPath, $"SELECT suno_id FROM generations WHERE id = '{source}';"));

        var before = Stored(factory, frozen.VersionId);
        Assert.Contains("source-Suno-ID-guard", System.Text.Encoding.UTF8.GetString(Convert.FromHexString(before.Split("|sources:")[1].Split('|')[0])), StringComparison.Ordinal);
        foreach (var write in new[] { "'renamed-clip'", "NULL" })
        {
            var refused = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(factory.DataPath, $"UPDATE generations SET suno_id = {write} WHERE id = '{source}';"));
            Assert.Contains("never changes", refused.Message, StringComparison.Ordinal);
            Assert.True(before == Stored(factory, frozen.VersionId), write);
        }

        // Complement: one attached with no Suno ID is given one, and from then on it is fixed.
        var bare = await SongApi.AttachGenerationAsync(factory, frozen.VersionId.ToString());
        var bareId = bare.Generation.Id.ToString().ToUpperInvariant();
        TestDatabase.Execute(factory.DataPath, $"UPDATE generations SET suno_id = 'given-later' WHERE id = '{bareId}';");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(factory.DataPath, $"UPDATE generations SET suno_id = 'given-again' WHERE id = '{bareId}';"));
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
        Assert.Equal(properties.Concat(VersionLineageInputs.Keys), inputs.EnumerateObject().Select(static option => option.Name));
    }

    [Fact]
    public void EveryEntityMethodThatChangesAnInputThrowsOnAFrozenVersion()
    {
        var mutable = new SongVersion(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "Name", "Notes", VersionVisibility.Active, "Lyrics", "Styles",
            InputValues.Defaults(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Revision: 1, LineageValues.Held);
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

        changesInputs[nameof(SongVersion.WithLineage)] = static version => version.WithLineage(VersionLineage.None);
        foreach (var (part, change) in LineageValues.EachPartChanged)
        {
            changesInputs[$"{nameof(SongVersion.WithLineage)} ({part})"] = version => version.WithLineage(change(version.Lineage));
        }

        var changesNoInput = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(SongVersion.EnsureMutable)] = "the check itself",
            [nameof(SongVersion.WithAnnotations)] = "copies the inputs as they are",
            [nameof(SongVersion.AttachGeneration)] = "copies the inputs as they are, and freezes",
            [nameof(SongVersion.ReceiveGeneration)] = "copies the inputs as they are, and freezes (a moved Generation, #123)",
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
            Assert.NotEqual((mutable.Lyrics, mutable.Styles, mutable.Inputs, mutable.Lineage), (changed.Lyrics, changed.Styles, changed.Inputs, changed.Lineage));
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

        var frozen = await TargetAsync(factory, client, "Frozen guard", frozen: true, generationSource: true);
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

        // Services elsewhere take nothing that names a Version, a Song, or a Generation.
        foreach (var type in ApplicationServices().Where(static type => !CatalogServiceNamespaces.Contains(type.Namespace)))
        {
            foreach (var method in PublicMethods(type))
            {
                Assert.All(method.GetParameters(), parameter => Assert.False(
                    CatalogTypeNamespaces.Contains(parameter.ParameterType.Namespace),
                    $"{Describe(method)} takes a catalog type: move it to the catalog services, where the guard sees it."));
            }
        }

        var frozen = await TargetAsync(factory, client, "Frozen service guard", frozen: true, generationSource: true);
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
            // A new Song's options may be sent; a lineage may not (a new Version 1 has none).
            using var response = await SongApi.SendJsonAsync(target.Client, HttpMethod.Post, SongApi.Songs, await target.InputsJsonAsync("""{"title":"Another Song",""", "}", withLineage: false));
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
                    .Concat(VersionLineageInputs.Keys.Select(key => (Func<Task<string>>)(async () =>
                        new JsonObject { ["inputs"] = LineageValues.Changed(key, (await target.ReadAsync()).GetProperty("inputs")) }.ToJsonString())))
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
        ["POST /api/v1/versions/{reference}/generation-requests"] = new(async target =>
        {
            // A request is a snapshot of the Version (#144): made from a frozen Version too, and the inputs sent alongside are not read.
            foreach (var reference in new[] { target.VersionId.ToString(), target.VersionShortcode })
            {
                using var response = await SongApi.SendJsonAsync(target.Client, HttpMethod.Post, new Uri($"/api/v1/versions/{reference}/generation-requests", UriKind.Relative), await target.InputsJsonAsync("{", "}"));
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            }
        }),
        ["POST /api/v1/suno/generation-requests/{id:guid}/observed-create"] = new(async target =>
        {
            // The user's Create (#149): clips with the requested Version's inputs are attached to it (a frozen
            // one stays as it is), and clips with other inputs go to a new child Version; the requested
            // Version's inputs never change. The inputs sent alongside are not read.
            var (id, token) = await ObservedCreateApi.WaitingRequestAsync(target.Factory, target.Client, target.VersionId);
            var stamp = Guid.NewGuid().ToString("N");
            var other = ObservedCreateApi.Response("songs-advanced", $"guard-{stamp}-1", [$"guard-{stamp}-a"]);
            var answered = await ObservedCreateApi.RecordAsync(target.Client, token, id, other, ObservedCreateApi.Request("songs-advanced"));
            Assert.Equal("branched", answered.GetProperty("observed")[0].GetProperty("outcome").GetString());
            var later = ObservedCreateApi.Response("songs-advanced", $"guard-{stamp}-2", [$"guard-{stamp}-b"], static clip => clip["metadata"]!["control_sliders"]!["weirdness_constraint"] = 0.1);
            Assert.Equal("branched", (await ObservedCreateApi.RecordAsync(target.Client, token, id, later, null)).GetProperty("observed")[1].GetProperty("outcome").GetString());
        }),
        ["POST /api/v1/suno/generation-requests/{id:guid}/clips"] = new(async target =>
        {
            // A finished clip of the user's Create (#154) fills in its Generation's clip columns only: the
            // Version that holds it (a new child here) and the requested Version keep their inputs.
            var (id, token) = await ObservedCreateApi.WaitingRequestAsync(target.Factory, target.Client, target.VersionId);
            var stamp = Guid.NewGuid().ToString("N");
            await ObservedCreateApi.RecordAsync(target.Client, token, id, ObservedCreateApi.Response("songs-advanced", $"clips-guard-{stamp}", [$"clips-guard-{stamp}-a"]), ObservedCreateApi.Request("songs-advanced"));
            var answered = await ProvisionalCompletionApi.CompleteAsync(target.Client, token, id, ProvisionalCompletionApi.Finished($"clips-guard-{stamp}-a"));
            Assert.Equal("completed", answered.GetProperty("outcome").GetString());
        }),
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
        ["DELETE /api/v1/versions/{reference}"] = new(static target => DeleteAndRestoreAsync(target, static async target =>
        {
            // Deleting retains the Version's row as stored (its inputs untouched); the inputs sent alongside are not read.
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Delete,
                new Uri($"/api/v1/versions/{target.VersionShortcode}", UriKind.Relative),
                await target.IfMatchAsync(),
                await target.InputsJsonAsync("{", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        })),
        ["DELETE /api/v1/songs/{reference}"] = new(static target => DeleteSongAndRestoreAsync(target, static async target =>
        {
            // Deleting a Song retains every Version's row as stored (inputs untouched); the inputs sent
            // alongside the typed title are not read.
            var (title, revision) = await target.SongAsync();
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Delete,
                new Uri($"/api/v1/songs/{target.SongShortcode}", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync($$"""{"confirmTitle":{{JsonSerializer.Serialize(title)}},""", "}"));
            Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        })),
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

        // Rating and comments (#119) are the user's judgement of a Generation, never a creation input:
        // the inputs sent alongside are not read.
        ["PATCH /api/v1/generations/{reference}"] = new(async target =>
        {
            var (shortcode, revision, rating) = await target.GenerationAsync();
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Patch,
                new Uri($"/api/v1/generations/{shortcode}", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync($$"""{"rating":{{(rating == 4 ? 5 : 4)}},""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["POST /api/v1/generations/{reference}/comments"] = new(static async target => await target.CommentAsync()),

        // The Suno import commit (#140): a choice attaching a clip whose inputs are not the Version's
        // (as if the Version changed after the choice was made) fails its target; the Version is untouched.
        ["POST /api/v1/suno/exports/{id:guid}/commit"] = new(static target => CommitDifferingClipAsync(target, async exportId =>
        {
            using var response = await ImportCommitApi.SendAsync(target.Client, exportId, "\"1\"");
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
            return (await SetupApi.JsonAsync(response)).GetProperty("jobId").GetGuid();
        })),

        // Creating a new Song from a Generation (#123): a fresh Generation of the frozen Version moves
        // into a new Song whose Version 1 is a copy; the Version it leaves keeps its inputs. The inputs
        // sent alongside are not read.
        ["POST /api/v1/generations/{reference}/move-to-new-song"] = new(async target =>
        {
            var leaving = await SongApi.AttachGenerationAsync(target.Factory, target.VersionId.ToString());
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Post,
                new Uri($"/api/v1/generations/{leaving.Shortcode}/move-to-new-song", UriKind.Relative),
                SongApi.Quoted(leaving.Generation.Revision),
                await target.InputsJsonAsync("""{"title":"Moved by the guard",""", "}"));
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            var moved = (await SetupApi.JsonAsync(response)).GetProperty("version").GetProperty("id").GetGuid();
            Assert.Equal(Stored(target.Factory, target.VersionId), Stored(target.Factory, moved));
        }),

        // Deleting a Generation (#124): a fresh Generation of the frozen Version goes into retention and
        // comes back; the Version stays frozen with its inputs. The inputs sent alongside are not read.
        ["DELETE /api/v1/generations/{reference}"] = new(async target =>
        {
            var leaving = await SongApi.AttachGenerationAsync(target.Factory, target.VersionId.ToString());
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Delete,
                new Uri($"/api/v1/generations/{leaving.Shortcode}", UriKind.Relative),
                SongApi.Quoted(leaving.Generation.Revision),
                await target.InputsJsonAsync("{", "}"));
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Assert.IsType<DeletedItemRestoreOutcome.Restored>(await InScopeAsync<DeletedItemsService, DeletedItemRestoreOutcome>(target, service => service.RestoreAsync(leaving.Shortcode, default)));
        }),

        // The Song's Selected Generation (#120): the inputs sent alongside are not read.
        ["PUT /api/v1/songs/{reference}/selected-generation"] = new(async target =>
        {
            var (_, revision) = await target.SongAsync();
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Put,
                new Uri($"/api/v1/songs/{target.SongShortcode}/selected-generation", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync($$"""{"generation":"{{target.VersionShortcode}}-g1",""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["DELETE /api/v1/songs/{reference}/selected-generation"] = new(async target =>
        {
            var (_, revision) = await target.SongAsync();
            using var response = await SendAsync(target.Client, HttpMethod.Delete, new Uri($"/api/v1/songs/{target.SongShortcode}/selected-generation", UriKind.Relative), SongApi.Quoted(revision), json: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["PATCH /api/v1/generations/{reference}/comments/{commentId:guid}"] = new(async target =>
        {
            var (shortcode, id) = await target.CommentAsync();
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Patch,
                new Uri($"/api/v1/generations/{shortcode}/comments/{id}", UriKind.Relative),
                SongApi.Quoted(1),
                await target.InputsJsonAsync("""{"text":"Edited by the guard",""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }),
        ["DELETE /api/v1/generations/{reference}/comments/{commentId:guid}"] = new(async target =>
        {
            var (shortcode, id) = await target.CommentAsync();
            using var response = await SendAsync(target.Client, HttpMethod.Delete, new Uri($"/api/v1/generations/{shortcode}/comments/{id}", UriKind.Relative), SongApi.Quoted(1), json: null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }),

        // A Generation's image and picking it as the Song's artwork (#121): an asset and the Song's
        // artwork attachment, never a creation input; the inputs sent alongside the pick are not read.
        ["PUT /api/v1/generations/{reference}/artwork"] = new(static async target =>
        {
            foreach (var colour in new[] { ArtworkImages.Red, ArtworkImages.Blue })
            {
                await target.GenerationImageAsync(colour);
            }
        }),
        // The Suno workspace a Song lives in (#129) is the Song's own, never a Version's: a bulk move
        // changes only the Songs' workspace and revision. The inputs sent alongside are not read.
        ["POST /api/v1/suno/workspaces/{id}/move-songs"] = new(async target =>
        {
            var (from, to) = await InWorkspaceAsync(target);
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Post,
                new Uri($"/api/v1/suno/workspaces/{from}/move-songs", UriKind.Relative),
                SongApi.Quoted(1),
                await target.InputsJsonAsync($$"""{"songIds":["{{target.SongShortcode}}"],"targetWorkspaceId":"{{to}}",""", "}"));
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }),
        ["POST /api/v1/songs/{reference}/artwork/from-generation"] = new(async target =>
        {
            await target.GenerationImageAsync(ArtworkImages.Red);
            var (_, revision) = await target.SongAsync();
            using var response = await SendAsync(
                target.Client,
                HttpMethod.Post,
                new Uri($"/api/v1/songs/{target.SongShortcode}/artwork/from-generation", UriKind.Relative),
                SongApi.Quoted(revision),
                await target.InputsJsonAsync($$"""{"generation":"{{target.VersionShortcode}}-g1",""", "}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
        ["DELETE /api/v1/artists/{id:guid}"] = "retains an Artist with its aliases, links, and artwork; its Song credits (song_artist_credits) and Album Artists are reassigned or removed, raising those Songs' and Albums' revisions, never a Version",
        ["POST /api/v1/albums"] = "adds an Album record (albums) from a title; no Song or Version is touched",
        ["PATCH /api/v1/albums/{id:guid}"] = "edits an Album record's title, Album Artist, release details, and links; no Song or Version is touched",
        ["POST /api/v1/relationship-types"] = "adds a relationship type (song_relationship_types); no Song or Version is touched",
        ["PATCH /api/v1/relationship-types/{id:guid}"] = "renames a user-defined relationship type or sets its Suno action; no relationship, Song, or Version changes: a source keeps the action it was written with, and a different action is refused while any Version source is of the type (#126, RelationshipSunoActionEndpointTests)",
        ["DELETE /api/v1/relationship-types/{id:guid}"] = "removes a type and its relationships (song_relationships and the Songs' last-updated times), never a Version: a type any Version source is of, deleted Versions included, is refused (#126)",
        ["POST /api/v1/playlists"] = "adds an empty Playlist record (playlists) from a title; no Song or Version is touched",
        ["PATCH /api/v1/playlists/{id:guid}"] = "edits a Playlist record's title and description; no Song or Version is touched",
        ["DELETE /api/v1/albums/{id:guid}"] = "retains an Album with its links, tracks (album_songs), and artwork; its Songs' last-updated times move, never a Song's field or a Version",
        ["DELETE /api/v1/playlists/{id:guid}"] = "retains a Playlist with its entries (playlist_songs) and artwork; its Songs' last-updated times move, never a Song's field or a Version",
        ["PUT /api/v1/suno/models/order"] = "reorders the model list; a Version's model is not touched",
        ["DELETE /api/v1/workflow-states/{id:guid}"] = "workflow states; moves Songs to another state, never a Version",
        ["POST /api/v1/backups"] = "queues a backup: reads the database, writes only an archive file",
        ["POST /api/v1/media/scans"] = "queues a media scan (#203): it writes only audio_files and the settings row media.lastScan, never a Version",
        ["DELETE /api/v1/backups/{location}/{name}"] = "deletes an archive file, never a database row",
        ["PUT /api/v1/settings/backup-schedule"] = "the backup schedule: one settings row",
        ["PUT /api/v1/settings/version-defaults"] = "the defaults for new Versions: one settings row, applied only when a Song is created",
        ["PUT /api/v1/settings/catalog"] = "the default Artist: one settings row, applied only as a new Song's credit",
        ["PUT /api/v1/settings/media-scan"] = "the media scan schedule (#204): one settings row; the scans it leads to write only audio_files and media.lastScan",
        ["POST /api/v1/restores/validate"] = "reads a backup archive into a temporary folder; changes no row",
        ["POST /api/v1/restores/uploads"] = "writes an uploaded archive to a temporary file and reads it; changes no row",
        ["POST /api/v1/artwork"] = "stores an uploaded image as an asset (assets and its files); attaching it is the owner's own edit, and no Version is touched",
        ["POST /api/v1/restores"] = "starts maintenance and a safety backup; it replaces the instance as a whole (#74), never edits a Version",
        ["PUT /api/v1/suno/workspaces/discovered"] = "records Suno workspaces as the extension reports them (suno_workspaces, #129); no Song or Version is touched, even when one becomes unavailable",
        ["POST /api/v1/suno/exports"] = "stages a Suno export's header (suno_exports, #131); the catalog is not touched (invariant 3, SunoExportStagingGuardTests)",
        ["POST /api/v1/suno/exports/{id:guid}/parts"] = "stages a part of a Suno export (suno_export_parts, #131); the catalog is not touched",
        ["POST /api/v1/suno/exports/{id:guid}/complete"] = "classifies a staged export (suno_export_records, #131) and applies a complete workspace list (provider state); no Song, Version, or Generation is touched",
        ["POST /api/v1/suno/exports/{id:guid}/discard"] = "removes a staged export's rows (#131); the catalog is not touched",
        ["PATCH /api/v1/suno/exports/{id:guid}/records"] = "changes the choices of a staged export's records (suno_export_records, #138); a choice only names a Version, and the catalog is not touched until the commit (#140), which the guard extends to",
        ["PATCH /api/v1/suno/exports/{id:guid}/remote-states"] = "sets Suno state changes (#142) to apply or Skip, on the staged export (suno_exports); the catalog is not touched until the commit, which changes only a Generation's remote state, state, and archiver",
        ["PUT /api/v1/suno/exports/{id:guid}/artwork/{sunoId}"] = "stores an image as an asset held by a staged record (#131); no Generation or Version is touched until the commit",
        ["POST /api/v1/suno/ignored"] = "adds a Not imported source's Suno ID to the ignore list (suno_ignored_items, #153); the source and its external reference are left as they are",
        ["POST /api/v1/suno/ignored/remove"] = "removes Suno IDs from the ignore list (suno_ignored_items, #143) and reclassifies a ready export; nothing is imported and no Version is touched",
        ["POST /api/v1/suno/generation-requests/{id:guid}/claim"] = "binds a generation request (suno_generation_requests, #144) to the extension's credential; the Version is only read",
        ["PATCH /api/v1/suno/generation-requests/{id:guid}"] = "records the extension's progress on a generation request (suno_generation_requests, #144); the Version is only read",
        ["POST /api/v1/suno/generation-requests/{id:guid}/cancel"] = "cancels a generation request (suno_generation_requests, #144); the Version is only read",
    };

    /// <summary>How each public method of a catalog service is called, each creation input touched in turn.</summary>
    private static Dictionary<string, Exerciser> ServiceExercisers() => new(StringComparer.Ordinal)
    {
        ["VersionService.NextNumbersAsync(Guid, CancellationToken)"] = Service<VersionService>(static (service, target) => service.NextNumbersAsync(target.VersionId, default)),
        ["VersionService.ListAsync(String, CancellationToken)"] = Service<VersionService>(static (service, target) => service.ListAsync(target.SongShortcode, default)),
        ["VersionService.FindAsync(Guid, CancellationToken)"] = Service<VersionService>(static (service, target) => service.FindAsync(target.VersionId, default)),
        ["GenerationRequestService.CreateAsync(Guid, CancellationToken)"] = Service<GenerationRequestService>(static (service, target) => service.CreateAsync(target.VersionId, default)),
        ["GenerationRequestService.CurrentForVersionAsync(Guid, CancellationToken)"] = Service<GenerationRequestService>(static (service, target) => service.CurrentForVersionAsync(target.VersionId, default)),
        ["GenerationRequestService.FindAsync(Guid, CancellationToken)"] = Service<GenerationRequestService>(static async (service, target) =>
            Assert.NotNull(await service.FindAsync(await GenerationRequestOfAsync(service, target), default))),
        ["GenerationRequestService.ClaimAsync(Guid, Guid, CancellationToken)"] = Service<GenerationRequestService>(static async (service, target) =>
            await service.ClaimAsync(await GenerationRequestOfAsync(service, target), GuardCredential, default)),
        ["GenerationRequestService.ReportAsync(Guid, Nullable`1, GenerationProgress, CancellationToken)"] = Service<GenerationRequestService>(static async (service, target) =>
        {
            var id = await GenerationRequestOfAsync(service, target);
            await service.ClaimAsync(id, GuardCredential, default);
            Assert.IsType<GenerationRequestChangeOutcome.Changed>(await service.ReportAsync(id, GuardCredential, new GenerationProgress(GenerationRequestState.Waiting, "fill", null), default));
        }),
        ["ObservedCreateService.RecordAsync(Guid, Nullable`1, ObservedCreate, CancellationToken)"] = new(async target =>
        {
            // The service behind the endpoint above: a Create with other inputs branches; the requested Version stays as it is.
            var (id, _) = await ObservedCreateApi.WaitingRequestAsync(target.Factory, target.Client, target.VersionId);
            var credential = await InScopeAsync<GenerationRequestService, Guid?>(target, async service => (await service.FindAsync(id, default))!.CredentialId);
            var stamp = Guid.NewGuid().ToString("N");
            using var response = JsonDocument.Parse(ObservedCreateApi.Response("songs-advanced", $"service-guard-{stamp}", [$"service-guard-{stamp}-a"]).ToJsonString());
            using var request = JsonDocument.Parse(ObservedCreateApi.Request("songs-advanced").ToJsonString());
            var outcome = await InScopeAsync<ObservedCreateService, GenerationRequestChangeOutcome>(target, service => service.RecordAsync(id, credential, new ObservedCreate(response.RootElement, request.RootElement), default));
            Assert.IsType<GenerationRequestChangeOutcome.Changed>(outcome);
        }),
        ["ProvisionalCompletionService.CompleteAsync(Guid, Nullable`1, String, CancellationToken)"] = new(async target =>
        {
            // The service behind the endpoint above: the finished clip fills in its Generation; no Version changes.
            var (id, token) = await ObservedCreateApi.WaitingRequestAsync(target.Factory, target.Client, target.VersionId);
            var credential = await InScopeAsync<GenerationRequestService, Guid?>(target, async service => (await service.FindAsync(id, default))!.CredentialId);
            var stamp = Guid.NewGuid().ToString("N");
            await ObservedCreateApi.RecordAsync(target.Client, token, id, ObservedCreateApi.Response("songs-advanced", $"service-clips-{stamp}", [$"service-clips-{stamp}-a"]), ObservedCreateApi.Request("songs-advanced"));
            var finished = ProvisionalCompletionApi.Finished($"service-clips-{stamp}-a").ToJsonString();
            var outcome = await InScopeAsync<ProvisionalCompletionService, ProvisionalCompletionOutcome>(target, service => service.CompleteAsync(id, credential, finished, default));
            Assert.IsType<ProvisionalCompletionOutcome.Completed>(outcome);
        }),
        ["GenerationRequestService.CancelAsync(Guid, CancellationToken)"] = Service<GenerationRequestService>(static async (service, target) =>
            await service.CancelAsync(await GenerationRequestOfAsync(service, target), default)),
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
        ["VersionDeletionService.ImpactAsync(Guid, CancellationToken)"] = Service<VersionDeletionService>(static (service, target) => service.ImpactAsync(target.VersionId, default)),
        ["VersionDeletionService.PlaceholdersAsync(Guid, CancellationToken)"] = Service<VersionDeletionService>(static (service, target) => service.PlaceholdersAsync(target.SongId, default)),
        ["VersionDeletionService.FindDeletedAsync(CatalogReference, CancellationToken)"] = Service<VersionDeletionService>(static (service, target) =>
            service.FindDeletedAsync(CatalogReference.Parse(target.VersionShortcode), default)),
        ["VersionDeletionService.DeleteAsync(Guid, Int32, CancellationToken)"] = new(static target => DeleteAndRestoreAsync(target, ServiceDeleteAsync)),
        ["SongDeletionService.ImpactAsync(Guid, CancellationToken)"] = Service<SongDeletionService>(static (service, target) => service.ImpactAsync(target.SongId, default)),
        ["SongDeletionService.FindDeletedAsync(CatalogReference, CancellationToken)"] = Service<SongDeletionService>(static (service, target) =>
            service.FindDeletedAsync(CatalogReference.Parse(target.SongShortcode), default)),
        ["SongDeletionService.DeleteAsync(Guid, Int32, String, CancellationToken)"] = new(static target => DeleteSongAndRestoreAsync(target, static async target =>
        {
            var (title, revision) = await target.SongAsync();
            Assert.IsType<SongDeleteOutcome.Deleted>(
                await InScopeAsync<SongDeletionService, SongDeleteOutcome>(target, service => service.DeleteAsync(target.SongId, revision, title, default)));
        })),
        // The Suno import commit (#140), through the service: the same differing clip fails its target.
        ["ImportCommitService.CommitAsync(Guid, Int32, CancellationToken)"] = new(static target => CommitDifferingClipAsync(target, async exportId =>
            Assert.IsType<ImportCommitOutcome.Started>(await InScopeAsync<ImportCommitService, ImportCommitOutcome>(target, service => service.CommitAsync(exportId, 1, default)))
                .View.Export.JobId!.Value)),
        ["ImportCommitService.RecoverInterruptedAsync(CancellationToken)"] = Service<ImportCommitService>(static (service, _) => service.RecoverInterruptedAsync(default)),

        // What the commit calls in Application.Suno (#140): resolving a Suno ID rewrites only pointers of
        // sources that name it, never a frozen source's identity; renaming or deleting the model a Version
        // names is refused, as the model's API is.
        ["ExternalReferenceResolver.ResolveAsync(String, CancellationToken)"] = Service<ExternalReferenceResolver>(static async (service, target) =>
            await service.ResolveAsync((await SongApi.AttachGenerationAsync(target.Factory, target.VersionId.ToString(), Clips.Minimal("guard-resolve-" + Guid.NewGuid().ToString("N")))).Generation.SunoId!, default)),
        ["ModelCatalogService.UpdateAsync(Guid, SunoModelEdit, Int32, CancellationToken)"] = new(static async target =>
        {
            var (id, revision) = await target.ModelAsync();
            Assert.IsType<ModelChangeOutcome.InUse>(await InScopeAsync<ModelCatalogService, ModelChangeOutcome>(target, service =>
                service.UpdateAsync(id, new SunoModelEdit("renamed by the guard", null, null), revision, default)));
        }),
        ["ModelCatalogService.DeleteAsync(Guid, Int32, CancellationToken)"] = new(static async target =>
        {
            var (id, revision) = await target.ModelAsync();
            Assert.IsType<ModelChangeOutcome.InUse>(await InScopeAsync<ModelCatalogService, ModelChangeOutcome>(target, service => service.DeleteAsync(id, revision, default)));
        }),

        // Generations (#117): attaching one, with Suno data and without, is the one way one is made;
        // it freezes the Version and never writes its inputs.
        ["GenerationService.AttachAsync(String, String, GenerationAttachOptions, CancellationToken)"] = new(static async target =>
        {
            Assert.IsType<GenerationAttachOutcome.Attached>(await InScopeAsync<GenerationService, GenerationAttachOutcome>(target, service =>
                service.AttachAsync(target.VersionShortcode, Clips.Minimal(Guid.NewGuid().ToString()), null, default)));
            Assert.IsType<GenerationAttachOutcome.Attached>(await InScopeAsync<GenerationService, GenerationAttachOutcome>(target, service =>
                service.AttachAsync(target.VersionId.ToString(), null, new GenerationAttachOptions(), default)));
        }),
        ["GenerationService.FindAsync(CatalogReference, CancellationToken)"] = Service<GenerationService>(static (service, target) =>
            service.FindAsync(CatalogReference.Parse(target.VersionShortcode + "-g1"), default)),
        ["GenerationService.ListForVersionAsync(CatalogReference, CancellationToken)"] = Service<GenerationService>(static (service, target) =>
            service.ListForVersionAsync(CatalogReference.Parse(target.VersionShortcode), default)),
        ["GenerationService.ListForSongAsync(CatalogReference, CancellationToken)"] = Service<GenerationService>(static (service, target) =>
            service.ListForSongAsync(CatalogReference.Parse(target.SongShortcode), default)),
        ["GenerationService.ProviderRecordAsync(CatalogReference, CancellationToken)"] = Service<GenerationService>(static (service, target) =>
            service.ProviderRecordAsync(CatalogReference.Parse(target.VersionShortcode + "-g1"), default)),
        ["GenerationService.RecordEventAsync(GenerationEventRequest, CancellationToken)"] = new(static async target =>
        {
            var attached = Assert.IsType<GenerationAttachOutcome.Attached>(await InScopeAsync<GenerationService, GenerationAttachOutcome>(target, service =>
                service.AttachAsync(target.VersionShortcode, null, null, default)));
            Assert.IsType<GenerationEventOutcome.Recorded>(await InScopeAsync<GenerationService, GenerationEventOutcome>(target, service => service.RecordEventAsync(
                new GenerationEventRequest(null, GenerationEventSource.User, GenerationEventConfidence.High, 1, DateTimeOffset.UnixEpoch, [attached.Generation.Generation.Id]),
                default)));
        }),
        // The user's judgement of a Generation (#119): rating and comments, never a creation input.
        ["GenerationEvaluationService.UpdateAsync(CatalogReference, GenerationEdit, Int32, CancellationToken)"] = new(static async target =>
        {
            var (shortcode, revision, rating) = await target.GenerationAsync();
            Assert.IsType<GenerationUpdateOutcome.Updated>(await InScopeAsync<GenerationEvaluationService, GenerationUpdateOutcome>(target, service =>
                service.UpdateAsync(CatalogReference.Parse(shortcode), new GenerationEdit(GenerationRatingEdit.Of(rating == 2 ? 3 : 2), GenerationState.Archived), revision, default)));
        }),

        // The Song's Selected Generation (#120) is the Song's own field, never a creation input.
        ["GenerationSelectionService.SelectAsync(CatalogReference, String, Int32, CancellationToken)"] = new(static async target =>
        {
            var (_, revision) = await target.SongAsync();
            Assert.IsType<GenerationSelectionOutcome.Selected>(await InScopeAsync<GenerationSelectionService, GenerationSelectionOutcome>(target, service =>
                service.SelectAsync(CatalogReference.Parse(target.SongShortcode), target.VersionShortcode + "-g1", revision, default)));
        }),
        ["GenerationSelectionService.ClearAsync(CatalogReference, Int32, CancellationToken)"] = new(static async target =>
        {
            var (_, revision) = await target.SongAsync();
            Assert.IsType<GenerationSelectionOutcome.Selected>(await InScopeAsync<GenerationSelectionService, GenerationSelectionOutcome>(target, service =>
                service.ClearAsync(CatalogReference.Parse(target.SongShortcode), revision, default)));
        }),
        // Moving a Generation into a new Song (#123): the Version it leaves keeps its inputs, and the
        // new Version 1 holds the same ones.
        ["GenerationMoveService.MoveToNewSongAsync(CatalogReference, GenerationMoveRequest, Int32, CancellationToken)"] = new(static async target =>
        {
            var leaving = await SongApi.AttachGenerationAsync(target.Factory, target.VersionShortcode);
            var outcome = Assert.IsType<GenerationMoveOutcome.Moved>(await InScopeAsync<GenerationMoveService, GenerationMoveOutcome>(target, service =>
                service.MoveToNewSongAsync(CatalogReference.Parse(leaving.Shortcode), new GenerationMoveRequest("Moved by the guard", SelectionChoice.None), leaving.Generation.Revision, default)));
            Assert.Equal(Stored(target.Factory, target.VersionId), Stored(target.Factory, outcome.Version.Id));
        }),

        // Deleting a Generation (#124): the Version it leaves keeps its inputs, frozen, before and after a restore.
        ["GenerationDeletionService.ImpactAsync(CatalogReference, CancellationToken)"] = Service<GenerationDeletionService>(static (service, target) =>
            service.ImpactAsync(CatalogReference.Parse(target.VersionShortcode + "-g1"), default)),
        ["GenerationDeletionService.DeleteAsync(CatalogReference, SelectionChoice, Int32, CancellationToken)"] = new(static async target =>
        {
            var leaving = await SongApi.AttachGenerationAsync(target.Factory, target.VersionShortcode);
            Assert.IsType<GenerationDeleteOutcome.Deleted>(await InScopeAsync<GenerationDeletionService, GenerationDeleteOutcome>(target, service =>
                service.DeleteAsync(CatalogReference.Parse(leaving.Shortcode), SelectionChoice.None, leaving.Generation.Revision, default)));
            Assert.IsType<DeletedItemRestoreOutcome.Restored>(await InScopeAsync<DeletedItemsService, DeletedItemRestoreOutcome>(target, service => service.RestoreAsync(leaving.Shortcode, default)));
        }),

        // A Generation's image and its use as the Song's artwork (#121): never a creation input.
        ["GenerationArtworkService.UploadAsync(CatalogReference, ReadOnlyMemory`1, Boolean, CancellationToken)"] = new(static async target =>
        {
            foreach (var colour in new[] { ArtworkImages.Blue, ArtworkImages.Red })
            {
                var image = ArtworkImages.Solid(SkiaSharp.SKEncodedImageFormat.Png, 64, 64, colour);
                Assert.IsType<GenerationArtworkUploadOutcome.Stored>(await InScopeAsync<GenerationArtworkService, GenerationArtworkUploadOutcome>(target, service =>
                    service.UploadAsync(CatalogReference.Parse(target.VersionShortcode + "-g1"), image, mayReplace: true, default)));
            }
        }),
        ["GenerationArtworkService.CopyToSongAsync(CatalogReference, String, Int32, CancellationToken)"] = new(static async target =>
        {
            await target.GenerationImageAsync(ArtworkImages.Blue);
            var (_, revision) = await target.SongAsync();
            Assert.IsType<GenerationArtworkCopyOutcome.Copied>(await InScopeAsync<GenerationArtworkService, GenerationArtworkCopyOutcome>(target, service =>
                service.CopyToSongAsync(CatalogReference.Parse(target.SongShortcode), target.VersionShortcode + "-g1", revision, default)));
        }),
        ["GenerationEvaluationService.AddCommentAsync(CatalogReference, String, CancellationToken)"] = Service<GenerationEvaluationService>(static async (service, target) =>
            Assert.IsType<GenerationCommentOutcome.Saved>(await service.AddCommentAsync(CatalogReference.Parse(target.VersionShortcode + "-g1"), "Added by the guard", default))),
        ["GenerationEvaluationService.EditCommentAsync(CatalogReference, Guid, String, Int32, CancellationToken)"] = new(static async target =>
        {
            var (shortcode, id) = await target.CommentAsync();
            Assert.IsType<GenerationCommentOutcome.Saved>(await InScopeAsync<GenerationEvaluationService, GenerationCommentOutcome>(target, service =>
                service.EditCommentAsync(CatalogReference.Parse(shortcode), id, "Edited by the guard", 1, default)));
        }),
        ["GenerationEvaluationService.DeleteCommentAsync(CatalogReference, Guid, Int32, CancellationToken)"] = new(static async target =>
        {
            var (shortcode, id) = await target.CommentAsync();
            Assert.IsType<GenerationCommentOutcome.Deleted>(await InScopeAsync<GenerationEvaluationService, GenerationCommentOutcome>(target, service =>
                service.DeleteCommentAsync(CatalogReference.Parse(shortcode), id, 1, default)));
        }),
        // A bulk move between Suno workspaces (#129): only the Songs' workspace and revision change.
        ["SongWorkspaceService.MoveSongsAsync(String, SongWorkspaceMove, CancellationToken)"] = new(static async target =>
        {
            var (from, to) = await InWorkspaceAsync(target);
            Assert.IsType<SongWorkspaceMoveOutcome.Moved>(await InScopeAsync<SongWorkspaceService, SongWorkspaceMoveOutcome>(target, service =>
                service.MoveSongsAsync(from, new SongWorkspaceMove(null, All: true, to, ExpectedCount: 1), default)));
        }),
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
        ["RetentionService.FindByRecordAsync(String, Guid, CancellationToken)"] = Service<RetentionService>(static (service, target) =>
            service.FindByRecordAsync(RetainedRecordTypes.Version, target.VersionId, default)),
        ["RetentionService.ListAsync(CancellationToken)"] = Service<RetentionService>(static (service, _) => service.ListAsync(default)),

        // Recovery (#105): the Version deleted, then restored by its shortcode, as restore-deleted does it.
        ["DeletedItemsService.RestoreAsync(String, CancellationToken)"] = new(static async target =>
        {
            await ServiceDeleteAsync(target);
            Assert.IsType<DeletedItemRestoreOutcome.Restored>(
                await InScopeAsync<DeletedItemsService, DeletedItemRestoreOutcome>(target, service => service.RestoreAsync(target.VersionShortcode, default)));
        }),
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
        ["DeletedItemsService.ListAsync(Boolean, CancellationToken)"] = "reads only: the recovery listing",
        ["RetentionPruneTask.TickAsync(CancellationToken)"] = "queues the prune job; the prune itself is RetentionService.PruneAsync, exercised above",

        // The Suno import (#131-#139): everything before the commit stages, reads, or maps, and changes no
        // catalog row (invariant 3: SunoExportStagingGuardTests); the commit itself is exercised above.
        ["ExportStagingService.CreateAsync(SunoExportHeader, Nullable`1, CancellationToken)"] = "writes only the staging tables (suno_exports and their records, #131) and Suno's own workspace records; no catalog row, so no Version (SunoExportStagingGuardTests)",
        ["ExportStagingService.ReceivePartAsync(Guid, Nullable`1, Read, String, CancellationToken)"] = "writes only the staging tables (suno_exports and their records, #131) and Suno's own workspace records; no catalog row, so no Version (SunoExportStagingGuardTests)",
        ["ExportStagingService.CompleteAsync(Guid, Nullable`1, CancellationToken)"] = "writes only the staging tables (suno_exports and their records, #131) and Suno's own workspace records; no catalog row, so no Version (SunoExportStagingGuardTests)",
        ["ExportStagingService.DiscardAsync(Guid, Nullable`1, CancellationToken)"] = "writes only the staging tables (suno_exports and their records, #131) and Suno's own workspace records; no catalog row, so no Version (SunoExportStagingGuardTests)",
        ["ExportStagingService.ExpireAsync(CancellationToken)"] = "writes only the staging tables (suno_exports and their records, #131) and Suno's own workspace records; no catalog row, so no Version (SunoExportStagingGuardTests)",
        ["ExportStagingService.StageArtworkAsync(Guid, Nullable`1, String, ReadOnlyMemory`1, CancellationToken)"] = "stores an image as an asset held by a staged record; no Generation or Version is touched until the commit",
        ["ExportStagingService.FindAsync(Guid, Nullable`1, CancellationToken)"] = "reads only",
        ["ExportStagingService.RecordsAsync(Guid, StagedRecordQuery, CancellationToken)"] = "reads only",
        ["ProposalService.ChangeChoicesAsync(Guid, Int32, IReadOnlyList`1, ImportChoiceRequest, CancellationToken)"] = "a choice only names a Version, stored on the staged record; the commit, exercised above, is what attaches",
        ["ProposalService.ChangeChoicesAsync(Guid, Int32, ChoiceFilter, ImportChoiceRequest, CancellationToken)"] = "a choice only names a Version, stored on the staged record; the commit, exercised above, is what attaches",
        ["ProposalService.ValidateAsync(Guid, CancellationToken)"] = "reads only: the stored choices checked against the catalog",
        ["ProposalService.TargetsAsync(Guid, String, String, String, CancellationToken)"] = "reads only: the Versions a record may go to",
        ["ImportReviewService.CurrentAsync(CancellationToken)"] = "reads only",
        ["ChangeResolutionService.DiffAsync(Guid, String, CancellationToken)"] = "reads only: a Changed or Conflict record's diff (#141); the commit, exercised above, is what moves a Conflict's Generation",
        ["ImportReviewService.RecordsAsync(Guid, StagedRecordQuery, CancellationToken)"] = "reads only",
        ["ImportReviewService.SummaryAsync(Guid, CancellationToken)"] = "reads only",
        ["RemoteStateService.ListAsync(Guid, String, Int32, Int32, CancellationToken)"] = "reads only: the Suno state changes of an export (#142)",
        ["RemoteStateService.SetAppliedAsync(Guid, Int32, IReadOnlyCollection`1, Boolean, CancellationToken)"] = "writes only which Suno state changes are skipped, on the staged export (suno_exports, #142); the commit, exercised above, applies them, and changes only a Generation's remote state, state, and archiver, never a Version",
        ["RecordClassifier.ClassifyAsync(IReadOnlyList`1, CancellationToken)"] = "reads only: classes records by Suno ID",
        ["ImportFieldMap.Capture(String, String)"] = "pure: the import field map",
        ["ImportFieldMap.Find(String)"] = "pure: the import field map",
        ["ImportFieldMap.Parse(String)"] = "pure: the import field map",
        ["ImportFieldMap.Read(JsonElement, String)"] = "pure: the import field map",
        ["ExternalReferenceResolver.LinkAsync(ImportedLineage, CancellationToken)"] = "reads only: a clip's lineage pointed at the Generations already imported, for a new Version",
        ["ModelCatalogService.AddAsync(String, String, Int32, CancellationToken)"] = "adds a model to the list; a Version's model is not touched",
        ["ModelCatalogService.EnsureReportedAsync(String, CancellationToken)"] = "adds a model the list lacks, inside the commit, before a new Version names it; an existing Version's model is not touched",
        ["ModelCatalogService.ReorderAsync(IReadOnlyList`1, Int32, CancellationToken)"] = "reorders the model list; a Version's model is not touched",
        ["ModelCatalogService.ListAsync(CancellationToken)"] = "reads only",
        ["ModelCatalogService.ListWithUsageAsync(CancellationToken)"] = "reads only",
        ["ModelCatalogService.OfferedAsync(CancellationToken)"] = "reads only",
        ["CreateFieldInventory.Find(String)"] = "pure: the Create field inventory",
        ["CreateFieldInventory.Get(String)"] = "pure: the Create field inventory",
        ["CreateFieldInventory.Parse(String)"] = "pure: the Create field inventory",
        ["SunoLibraryService.PersonasAsync(CancellationToken)"] = "reads only",
        ["SunoLibraryService.PlaylistsAsync(CancellationToken)"] = "reads only",
        ["SunoWorkspaceService.ListAsync(CancellationToken)"] = "reads only",
        ["SunoWorkspaceService.ReadReport(JsonElement)"] = "pure: reads a workspace report",
        ["SunoWorkspaceService.ReportAsync(IReadOnlyList`1, Boolean, CancellationToken)"] = "writes Suno's own workspace records (provider state); no Song or Version",
        ["TombstoneService.FindAsync(String, CancellationToken)"] = "reads only",
        ["IgnoreListService.IgnoreReferenceAsync(String, CancellationToken)"] = "adds a Not imported source's Suno ID to the ignore list (#153); it reads the external reference and writes no Version, source, or reference",
        ["IgnoreListService.ListAsync(IgnoredItemQuery, CancellationToken)"] = "reads only: the ignore list (#143)",
        ["IgnoreListService.RemoveAsync(IReadOnlyCollection`1, CancellationToken)"] = "removes Suno IDs from the ignore list (#143) and reclassifies a ready export's records; nothing is imported and no Version is touched",
        ["TombstoneService.TombstonedAsync(IReadOnlyCollection`1, CancellationToken)"] = "reads only",
        ["VersionDefaultsService.GetAsync(CancellationToken)"] = "reads only: the user's defaults",
        ["VersionDefaultsService.NewVersionInputsAsync(String, CancellationToken)"] = "reads only: the inputs a new, mutable Version starts with",
        ["VersionDefaultsService.UpdateAsync(IReadOnlyDictionary`2, Int32, CancellationToken)"] = "the user's defaults for new Versions; an existing Version is not touched",
    };

    /// <summary>
    /// Retained types over the <c>versions</c> table, each with how it retains and restores a frozen
    /// Version (its shape upgraders run on fixtures of every earlier shape: shape 1 has none yet).
    /// </summary>
    private static Dictionary<string, Exerciser> RetainedVersionTypes() => new(StringComparer.Ordinal)
    {
        // Version deletion (#101): the Version, its Generations, and its history in one group, then
        // the group restored; the blank Version the deletion created (it was the last) goes again.
        [RetainedRecordTypes.Version] = new(static target => DeleteAndRestoreAsync(target, ServiceDeleteAsync)),
    };

    /// <summary>Deletes the target's Version through <see cref="VersionDeletionService.DeleteAsync"/>, at its current revision.</summary>
    private static async Task ServiceDeleteAsync(Target target)
    {
        var revision = (await target.ReadAsync()).GetProperty("revision").GetInt32();
        Assert.IsType<VersionDeleteOutcome.Deleted>(
            await InScopeAsync<VersionDeletionService, VersionDeleteOutcome>(target, service => service.DeleteAsync(target.VersionId, revision, default)));
    }

    /// <summary>
    /// Deletes the target's Version with <paramref name="delete"/> and restores the group it went
    /// into with <see cref="RetentionService.RestoreAsync"/>, asserting both went through: the
    /// Version is back as it was, its revision incremented.
    /// </summary>
    private static async Task DeleteAndRestoreAsync(Target target, Func<Target, Task> delete)
    {
        await delete(target);
        var group = await InScopeAsync<RetentionService, RetentionGroup?>(target, service => service.FindByShortcodeAsync(target.VersionShortcode, default));
        Assert.NotNull(group);
        Assert.IsType<RetentionRestoreOutcome.Restored>(await InScopeAsync<RetentionService, RetentionRestoreOutcome>(target, service => service.RestoreAsync(group.Id, default)));
    }

    /// <summary>
    /// Deletes the target's Song with <paramref name="delete"/> (every Version goes with it) and
    /// restores the group it went into, asserting both went through: the Song and its Versions are
    /// back as they were, their revisions incremented.
    /// </summary>
    private static async Task DeleteSongAndRestoreAsync(Target target, Func<Target, Task> delete)
    {
        await delete(target);
        var group = await InScopeAsync<RetentionService, RetentionGroup?>(target, service => service.FindByShortcodeAsync(target.SongShortcode, default));
        Assert.NotNull(group);
        Assert.Equal(RetainedRecordTypes.Song, group.Kind);
        Assert.IsType<RetentionRestoreOutcome.Restored>(await InScopeAsync<RetentionService, RetentionRestoreOutcome>(target, service => service.RestoreAsync(group.Id, default)));
    }

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
        edits.AddRange(VersionLineageInputs.Keys.Select(key => (Func<JsonElement, VersionEdit>)(inputs =>
            new VersionEdit(SongEditField.Unsent, SongEditField.Unsent, null, SongEditField.Unsent, SongEditField.Unsent, Options(LineageValues.Changed(key, inputs))))));
        edits.Add(static inputs => new VersionEdit(
            SongEditField.Of("Renamed"),
            SongEditField.Of("Noted"),
            true,
            SongEditField.Of(Changed + Guid.NewGuid()),
            SongEditField.Of(Changed + Guid.NewGuid()),
            Options(EveryInputChanged(inputs))));
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

    /// <summary>The credential the guard claims generation requests with (#144); no such row is needed.</summary>
    private static readonly Guid GuardCredential = Guid.CreateVersion7();

    /// <summary>A new generation request made from the target Version (#144).</summary>
    private static async Task<Guid> GenerationRequestOfAsync(GenerationRequestService service, Target target) =>
        Assert.IsType<GenerationRequestCreateOutcome.Created>(await service.CreateAsync(target.VersionId, default)).Request.Id;

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
    /// <summary>
    /// A Song titled <paramref name="title"/> whose Version holds lyrics, styles, a full lineage, and
    /// three history entries, frozen by a Generation when <paramref name="frozen"/>. With
    /// <paramref name="generationSource"/>, its audio source is another Song's Generation with a Suno ID
    /// (#324), the target a source has once #137 resolves it, rather than an external reference.
    /// </summary>
    private static async Task<Target> TargetAsync(N8TracksApiFactory factory, HttpClient client, string title, bool frozen, bool generationSource = false)
    {
        var song = await SongApi.CreateAsync(client, title);
        var version = song.GetProperty("currentVersion");
        var id = version.GetProperty("id").GetGuid();
        using (var written = await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative), "\"1\"", """{"lyrics":"[Verse]\nKept","styles":"kept"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        }

        // Every part of a lineage (#122), so the frozen Version has rows in each lineage table.
        var tag = title.Replace(' ', '-');
        var lineage = JsonNode.Parse(LineageValues.InitialInputsJson(tag))!.AsObject();
        if (generationSource)
        {
            var source = await SongApi.CreateAsync(client, "Source of " + title);
            var sourceGeneration = await SongApi.AttachGenerationAsync(factory, source.GetProperty("currentVersion").GetProperty("id").GetString()!, Clips.Minimal("source-" + tag));
            lineage[VersionLineageInputs.SourcesKey] = new JsonArray(new JsonObject { ["typeId"] = SystemRelationshipTypes.SampleThisSong.Id.ToString(), ["generation"] = sourceGeneration.Shortcode });
        }

        using (var written = await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative), "\"2\"", $$"""{"inputs":{{lineage.ToJsonString()}}}"""))
        {
            Assert.True(written.StatusCode == HttpStatusCode.OK, await written.Content.ReadAsStringAsync());
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

    /// <summary>Every public method of a class in the catalog namespaces (<see cref="CatalogServiceNamespaces"/>), described by its signature.</summary>
    private static List<string> CatalogServiceMethods() =>
        [.. ApplicationServices()
            .Where(static type => CatalogServiceNamespaces.Contains(type.Namespace))
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
    private static string InputsJson(string prefix, string suffix, JsonElement inputs, bool withLineage) =>
        prefix + string.Join(',', TextInputs.Select(input => $$"""
            "{{Camel(input)}}":"{{Changed}} {{input}} {{Guid.NewGuid()}}"
            """)) + ",\"inputs\":" + (withLineage ? EveryInputChanged(inputs) : InputValues.EveryOptionChanged(inputs)).ToJsonString() + suffix;

    /// <summary>Every option changed, and every lineage key changed to suit the options as changed (#122).</summary>
    private static JsonObject EveryInputChanged(JsonElement inputs)
    {
        var options = InputValues.EveryOptionChanged(inputs);
        foreach (var (key, value) in LineageValues.EveryPartChanged(options).ToList())
        {
            options[key] = value?.DeepClone();
        }

        return options;
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>
    /// The Version's creation inputs exactly as stored, each column hex-encoded, so the comparison is
    /// byte for byte, followed by its lineage (#122): each source in its group and order, by its
    /// identity (its Suno ID when it has one, through its Generation or its external reference; else
    /// its Generation's or Song's ID), type, action, position, and secondary identifiers; then the
    /// playlist, the Voice, and the file inputs, every column.
    /// </summary>
    internal static string Stored(N8TracksApiFactory factory, Guid id)
    {
        var version = id.ToString().ToUpperInvariant();
        return TestDatabase.Scalar(
            factory.DataPath,
            $"""
            SELECT {string.Join(" || '|' || ", CreationInputColumns.Select(static input => $"hex({Snake(input)})"))}
                || '|sources:' || COALESCE((SELECT group_concat(line, ';') FROM (
                    SELECT hex(s.source_group || ',' || s.position || ',' || s.type_id || ',' || COALESCE(s.suno_action, '-') || ',' ||
                        CASE
                            WHEN s.generation_id IS NOT NULL THEN COALESCE((SELECT g.suno_id FROM generations AS g WHERE g.id = s.generation_id), s.generation_id)
                            WHEN s.external_reference_id IS NOT NULL THEN (SELECT e.suno_id FROM external_suno_references AS e WHERE e.id = s.external_reference_id)
                            ELSE s.song_id
                        END || ',' || COALESCE(s.continue_at_hundredths, '-') || ',' || COALESCE(s.secondary_ids, '-')) AS line
                    FROM version_sources AS s WHERE s.version_id = '{version}' ORDER BY s.source_group, s.position)), '')
                || '|playlist:' || COALESCE((SELECT hex(suno_playlist_id || ',' || name || ',' || clip_ids) FROM version_inspiration_playlists WHERE version_id = '{version}'), '')
                || '|voice:' || COALESCE((SELECT hex(persona_id || ',' || name) FROM version_voices WHERE version_id = '{version}'), '')
                || '|files:' || COALESCE((SELECT group_concat(line, ';') FROM (
                    SELECT hex(kind || ',' || description) AS line FROM version_file_inputs WHERE version_id = '{version}' ORDER BY kind)), '')
            FROM versions WHERE id = '{version}';
            """);
    }

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

    /// <summary>
    /// A Suno export of one clip whose choice attaches it to the target's Version although its inputs are
    /// not the Version's (written on the staged record, as if the Version had changed after the choice
    /// was made), committed by <paramref name="commit"/> (which answers the job): the target fails with
    /// <c>inputs_differ</c> and no Generation is attached.
    /// </summary>
    private static async Task CommitDifferingClipAsync(Target target, Func<Guid, Task<Guid>> commit)
    {
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(target.Factory);
        var sunoId = "guard-" + Guid.NewGuid().ToString("N");
        var (exportId, _) = await ProposalApi.ExportAsync(target.Client, token, JsonNode.Parse(Clips.Minimal(sunoId))!);
        TestDatabase.Execute(
            target.Factory.DataPath,
            $$$"""UPDATE suno_export_records SET choice_json = '{"action":"import","target":{"kind":"version","version":"{{{target.VersionId}}}"}}' WHERE suno_id = '{{{sunoId}}}';""");

        var job = await TestJobs.WaitForStatusAsync(target.Client, await commit(exportId), "succeeded");
        var record = ImportCommitApi.Records(job.GetProperty("result"))[sunoId];
        Assert.Equal(("failed", "inputs_differ"), (ImportCommitApi.Outcome(record), ImportCommitApi.Reason(record)));
        Assert.Equal(0, ImportCommitApi.GenerationCount(target.Factory, sunoId));

        await MoveConflictOffAsync(target, token, commit);
    }

    /// <summary>
    /// #141: a Generation of the target's Version whose clip Suno now reports with other lyrics (a
    /// Conflict), resolved by moving it to a new Version: committed by <paramref name="commit"/>, the
    /// Generation moves to a new child Version holding the clip's inputs, and the Version it leaves keeps
    /// its own (the guard compares them after).
    /// </summary>
    private static async Task MoveConflictOffAsync(Target target, string token, Func<Guid, Task<Guid>> commit)
    {
        var sunoId = "guard-" + Guid.NewGuid().ToString("N");
        var clip = ProposalApi.Clip(sunoId, null, ProposalApi.At, 0, "Guard conflict words");
        await SongApi.AttachGenerationAsync(target.Factory, target.VersionId.ToString(), clip.ToJsonString());
        var (exportId, records) = await ProposalApi.ExportAsync(target.Client, token, clip);
        Assert.Equal("conflict", records[sunoId].GetProperty("class").GetString());
        TestDatabase.Execute(target.Factory.DataPath, $$"""UPDATE suno_export_records SET choice_json = '{"action":"moveToNewVersion"}' WHERE suno_id = '{{sunoId}}';""");

        var job = await TestJobs.WaitForStatusAsync(target.Client, await commit(exportId), "succeeded");
        var record = ImportCommitApi.Records(job.GetProperty("result"))[sunoId];
        Assert.Equal("moved", ImportCommitApi.Outcome(record));
        var generationId = record.GetProperty("generation").GetProperty("id").GetGuid().ToString().ToUpperInvariant();
        var moved = Guid.Parse(TestDatabase.Scalar(target.Factory.DataPath, $"SELECT version_id FROM generations WHERE id = '{generationId}';"));
        Assert.NotEqual(target.VersionId, moved);
        Assert.StartsWith(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("Guard conflict words")) + "|", Stored(target.Factory, moved), StringComparison.Ordinal);
    }

    /// <summary>
    /// Two available Suno workspaces (#129), reported as the extension would, with the target's Song
    /// put in the first through the Song's own edit: the Suno IDs to move from and to.
    /// </summary>
    private static async Task<(string From, string To)> InWorkspaceAsync(Target target)
    {
        var from = "guard-" + Guid.NewGuid().ToString("N");
        var to = "guard-" + Guid.NewGuid().ToString("N");
        await InScopeAsync<SunoWorkspaceService, SunoWorkspaceReport>(target, service => service.ReportAsync(
            [new SunoWorkspaceSighting(from, "Guard from", null, false, "{}"), new SunoWorkspaceSighting(to, "Guard to", null, false, "{}")],
            complete: false,
            default));
        var (_, revision) = await target.SongAsync();
        Assert.IsType<SongUpdateOutcome.Updated>(await InScopeAsync<SongService, SongUpdateOutcome>(target, service =>
            service.UpdateAsync(target.SongId, new SongEdit(SongEditField.Unsent, SongEditField.Unsent, SongEditField.Unsent, SunoWorkspaceId: SongEditField.Of(from)), revision, default)));
        return (from, to);
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

        /// <summary>The Song's title and revision as they are now (other exercisers rename it).</summary>
        public async Task<(string Title, int Revision)> SongAsync()
        {
            using var response = await Client.GetAsync(new Uri($"/api/v1/songs/{SongId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var song = await SetupApi.JsonAsync(response);
            return (song.GetProperty("title").GetString()!, song.GetProperty("revision").GetInt32());
        }

        /// <summary>The Version's current revision, so the freeze, not a stale revision, is what refuses.</summary>
        /// <summary>The Version's first Generation (attached when the target was frozen): its shortcode, revision, and rating.</summary>
        public async Task<(string Shortcode, int Revision, int? Rating)> GenerationAsync()
        {
            using var response = await Client.GetAsync(new Uri($"/api/v1/generations/{VersionShortcode}-g1", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var generation = await SetupApi.JsonAsync(response);
            var rating = generation.GetProperty("rating");
            return (
                generation.GetProperty("shortcode").GetString()!,
                generation.GetProperty("revision").GetInt32(),
                rating.ValueKind == JsonValueKind.Number ? rating.GetInt32() : null);
        }

        /// <summary>A new comment on the Version's first Generation, its inputs sent alongside (not read): the Generation's shortcode and the comment's ID.</summary>
        public async Task<(string Shortcode, Guid Id)> CommentAsync()
        {
            var shortcode = VersionShortcode + "-g1";
            using var response = await SongApi.SendJsonAsync(
                Client,
                HttpMethod.Post,
                new Uri($"/api/v1/generations/{shortcode}/comments", UriKind.Relative),
                await InputsJsonAsync("""{"text":"Written by the guard",""", "}"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (shortcode, (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid());
        }

        /// <summary>Gives the Version's first Generation a 64-pixel image of <paramref name="colour"/>, as the browser uploads one.</summary>
        public async Task GenerationImageAsync(SkiaSharp.SKColor colour)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(ArtworkImages.Solid(SkiaSharp.SKEncodedImageFormat.Png, 64, 64, colour)), "file", "cover.png");
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/generations/{VersionShortcode}-g1/artwork", UriKind.Relative)) { Content = form };
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            using var response = await Client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        public async Task<string> IfMatchAsync() => SongApi.Quoted((await ReadAsync()).GetProperty("revision").GetInt32());

        /// <summary>Body fields changing every creation input of the Version as it is now (<see cref="InputsJson"/>), its lineage included unless told not to.</summary>
        public async Task<string> InputsJsonAsync(string prefix, string suffix, bool withLineage = true) => InputsJson(prefix, suffix, (await ReadAsync()).GetProperty("inputs"), withLineage);

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
