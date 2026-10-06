using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Application.Retention;
using n8Tracks.Infrastructure.Retention;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Retention;

/// <summary>
/// The retained-shape guard (#95). A record retained today must still restore after any later
/// migration, so each retained type's table shape (column names, types, and nullability) is hashed
/// and compared with the checked-in baseline, <c>Retention/retained-shapes.json</c>. A migration that
/// changes a retained table fails this test until the type's shape version is bumped with an upgrader
/// from the old shape (without one, the registry refuses to start) and the baseline is updated.
/// </summary>
public sealed class RetainedShapeGuardTests
{
    private static readonly string BaselinePath = Path.Combine(AppContext.BaseDirectory, "Retention", "retained-shapes.json");

    [Fact]
    public async Task EveryRetainedTypeHasTheShapeItsBaselineRecords()
    {
        using var factory = Host(TimeProvider.System);
        using var client = await SessionApi.SignedInClientAsync(factory);

        var current = await ShapesAsync(factory, RetainedTypes.BuiltIn);

        var problems = Problems(current, Baseline());
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task AMigrationThatChangesARetainedTableWithoutANewShapeFailsTheGuard()
    {
        using var factory = Host(TimeProvider.System);
        using var client = await SessionApi.SignedInClientAsync(factory);
        CreateTestTables(factory);
        var before = await ShapesAsync(factory, [TestArtwork]);
        var baseline = new Dictionary<string, (int ShapeVersion, string Hash)>(StringComparer.Ordinal) { [TestArtwork.RecordType] = (2, before[0].Hash) };
        Assert.Empty(Problems(before, baseline));

        // A later migration adds a column (or renames, retypes, or relaxes one).
        TestDatabase.Execute(factory.DataPath, "ALTER TABLE test_artwork ADD COLUMN width INTEGER NULL;");
        var after = await ShapesAsync(factory, [TestArtwork]);

        Assert.NotEqual(before[0].Hash, after[0].Hash);
        var problem = Assert.Single(Problems(after, baseline));
        Assert.StartsWith("The shape of test_artwork changed but 'test-artwork' is still at shape 2", problem, StringComparison.Ordinal);

        // Bumped with its upgrader, the guard asks only for the new baseline.
        var bumped = TestArtwork with
        {
            ShapeVersion = 3,
            Upgraders = new Dictionary<int, Func<JsonObject, JsonObject>>(TestArtwork.Upgraders) { [2] = static document => document },
        };
        Assert.StartsWith("Record shape 3", Assert.Single(Problems([(bumped, after[0].Hash)], baseline)), StringComparison.Ordinal);
        Assert.Empty(Problems([(bumped, after[0].Hash)], new Dictionary<string, (int, string)>(StringComparer.Ordinal) { [bumped.RecordType] = (3, after[0].Hash) }));

        // A type missing from the baseline, or a baseline entry with no type, fails too.
        Assert.Single(Problems(after, new Dictionary<string, (int, string)>(StringComparer.Ordinal)));
        Assert.Single(Problems([], baseline));
    }

    [Fact]
    public void ATypeAtANewShapeWithoutAnUpgraderFromEveryEarlierShapeIsRefused()
    {
        var withoutUpgrader = TestArtwork with { ShapeVersion = 3 };

        var refused = Assert.Throws<InvalidOperationException>(() => new RetainedTypeRegistry([withoutUpgrader]));
        Assert.Contains("has no upgrader from shape 2", refused.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new RetainedTypeRegistry([TestArtwork with { ShapeVersion = 0 }]));
        Assert.Single(new RetainedTypeRegistry([TestArtwork]).All);
    }

    [Fact]
    public async Task ARecordRetainedUnderAnOlderShapeRestoresUnderTheCurrentOne()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        CreateTestTables(factory);
        var (version, _, _) = await VersionWithSnapshotsAsync(client, "Old shape");

        // A fixture: test artwork retained when its table had a "path" column and no caption (shape 1).
        var group = Guid.CreateVersion7();
        var artwork = Guid.CreateVersion7();
        var document = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = Upper(artwork),
            ["version_id"] = Upper(version),
            ["path"] = "art/old.png",
            ["revision"] = 3,
        });
        TestDatabase.Execute(
            factory.DataPath,
            $"""
            INSERT INTO retention_groups (id, kind, label, shortcode, deleted_utc, prune_after_utc, files) VALUES ('{Upper(group)}', 'test-artwork', 'Old', NULL, '2026-09-01T00:00:00.000Z', '2026-10-01T00:00:00.000Z', '["art/old.png"]');
            INSERT INTO retention_records (group_id, position, record_type, original_id, shape_version, document) VALUES ('{Upper(group)}', 0, 'test-artwork', '{Upper(artwork)}', 1, '{document}');
            """);

        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group));

        Assert.Equal($"'{Upper(artwork)}'|'{Upper(version)}'|'art/old.png'|NULL|4", Assert.Single(Dump(factory, "test_artwork")));
    }

    [Fact]
    public async Task ARecordWhoseShapeDoesNotMatchAfterUpgradingIsNeverRestored()
    {
        using var factory = Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        CreateTestTables(factory);
        var group = Guid.CreateVersion7();
        TestDatabase.Execute(
            factory.DataPath,
            $$"""
            INSERT INTO retention_groups (id, kind, label, shortcode, deleted_utc, prune_after_utc, files) VALUES ('{{Upper(group)}}', 'test-artwork', 'Odd', NULL, '2026-09-01T00:00:00.000Z', '2026-10-01T00:00:00.000Z', '[]');
            INSERT INTO retention_records (group_id, position, record_type, original_id, shape_version, document) VALUES ('{{Upper(group)}}', 0, 'test-artwork', 'x', 2, '{"id":"x","unexpected":1}');
            """);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RestoreAsync(factory, group));
        Assert.Contains("the shape changed without an upgrader", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Dump(factory, "test_artwork"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
    }

    /// <summary>What the guard reports: one line per type whose shape and baseline disagree.</summary>
    internal static List<string> Problems(IReadOnlyList<(RetainedType Type, string Hash)> current, IReadOnlyDictionary<string, (int ShapeVersion, string Hash)> baseline)
    {
        var problems = new List<string>();
        foreach (var (type, hash) in current)
        {
            if (!baseline.TryGetValue(type.RecordType, out var recorded))
            {
                problems.Add($"'{type.RecordType}' is not in retained-shapes.json: record shape {type.ShapeVersion} as {hash}.");
            }
            else if (type.ShapeVersion == recorded.ShapeVersion && hash != recorded.Hash)
            {
                problems.Add(
                    $"The shape of {type.Table} changed but '{type.RecordType}' is still at shape {type.ShapeVersion}: records retained before the migration "
                    + $"could not be restored. Bump its ShapeVersion to {type.ShapeVersion + 1}, register an upgrader from {type.ShapeVersion}, and record shape "
                    + $"{type.ShapeVersion + 1} as {hash} in retained-shapes.json.");
            }
            else if (type.ShapeVersion < recorded.ShapeVersion)
            {
                problems.Add($"'{type.RecordType}' went back from shape {recorded.ShapeVersion} to {type.ShapeVersion}: records retained under the later shape could not be restored.");
            }
            else if (type.ShapeVersion > recorded.ShapeVersion)
            {
                problems.Add($"Record shape {type.ShapeVersion} of '{type.RecordType}' as {hash} in retained-shapes.json (its upgrader from {recorded.ShapeVersion} is registered).");
            }
        }

        foreach (var orphan in baseline.Keys.Except(current.Select(static pair => pair.Type.RecordType), StringComparer.Ordinal))
        {
            problems.Add($"'{orphan}' is in retained-shapes.json but no type is registered under it: records retained under it could not be restored.");
        }

        return problems;
    }

    private static async Task<List<(RetainedType Type, string Hash)>> ShapesAsync(N8TracksApiFactory factory, IEnumerable<RetainedType> types)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope)
        {
            var store = (RetentionStore)scope.ServiceProvider.GetRequiredService<IRetentionStore>();
            var shapes = new List<(RetainedType, string)>();
            foreach (var type in types)
            {
                shapes.Add((type, RetainedShapes.Hash(await store.ShapeColumnsAsync(type, CancellationToken.None))));
            }

            return shapes;
        }
    }

    private static Dictionary<string, (int ShapeVersion, string Hash)> Baseline()
    {
        var document = JsonNode.Parse(File.ReadAllText(BaselinePath))!.AsObject();
        return document
            .Where(static entry => entry.Key != "$comment")
            .ToDictionary(
                static entry => entry.Key,
                static entry => (entry.Value!["shapeVersion"]!.GetValue<int>(), entry.Value!["hash"]!.GetValue<string>()),
                StringComparer.Ordinal);
    }
}
