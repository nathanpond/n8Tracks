using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Invariants;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Retention;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// A user-defined relationship type mapped to a Suno action (#126): set, changed, and cleared on the
/// type's PATCH under its revision; a mapped type is usable as a Version source's type and follows the
/// action's rules, an unmapped one is not; and the mapping, like the type itself, cannot change while
/// a Version's source is of it, so a frozen Version's lineage never changes under it.
/// </summary>
public sealed class RelationshipSunoActionEndpointTests
{
    private static readonly Uri Types = new("/api/v1/relationship-types", UriKind.Relative);

    [Fact]
    public async Task AUserTypeIsMappedChangedAndClearedAndSavingTheSameValueIsNoChange()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = Id(await CreateTypeAsync(client, "Reimagining of", "Reimagined as"));
        Assert.Equal(JsonValueKind.Null, (await FindAsync(client, type)).GetProperty("sunoAction").ValueKind);

        var mapped = await PatchTypeAsync(client, type, 1, """{"sunoAction":"cover"}""");
        Assert.Equal("cover/2/Reimagining of", Describe(mapped));
        Assert.Equal("cover", (await FindAsync(client, type)).GetProperty("sunoAction").GetString());

        // The same value again is no change: the revision stays.
        Assert.Equal("cover/2/Reimagining of", Describe(await PatchTypeAsync(client, type, 2, """{"sunoAction":"cover"}""")));

        // Omitted, the action stays as it is while the names change.
        Assert.Equal("cover/3/Reimagined from", Describe(await PatchTypeAsync(client, type, 2, """{"name":"Reimagined from"}""")));

        foreach (var (action, revision) in new[] { ("extend", 3), ("mashup", 4), ("sample", 5), ("reuse_prompt", 6) })
        {
            Assert.Equal($"{action}/{revision + 1}/Reimagined from", Describe(await PatchTypeAsync(client, type, revision, $$"""{"sunoAction":"{{action}}"}""")));
        }

        var cleared = await PatchTypeAsync(client, type, 7, """{"sunoAction":null}""");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("sunoAction").ValueKind);
        Assert.Equal(8, cleared.GetProperty("revision").GetInt32());
        Assert.Equal("-", TestDatabase.Scalar(factory.DataPath, $"SELECT ifnull(suno_action, '-') FROM song_relationship_types WHERE id = '{Upper(Guid.Parse(type))}';"));

        // Several user types may stand for the same action as a system type.
        var second = Id(await CreateTypeAsync(client, "Retelling of", "Retold as"));
        await PatchTypeAsync(client, type, 8, """{"sunoAction":"cover"}""");
        await PatchTypeAsync(client, second, 1, """{"sunoAction":"cover"}""");
        Assert.Equal(3, (await ListAsync(client)).Count(static item => item.GetProperty("sunoAction").GetString() == "cover"));
    }

    [Fact]
    public async Task ASystemTypeAnActionOutsideTheFiveAndAWrongValueAreRefusedAndChangeNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = Id(await CreateTypeAsync(client, "Answer to", "Answered by"));

        foreach (var system in SystemRelationshipTypes.All)
        {
            using var remapped = await SendAsync(client, HttpMethod.Patch, TypeUri(system.Id.ToString()), 1, """{"sunoAction":"extend"}""");
            var problem = await SetupApi.ProblemAsync(remapped, HttpStatusCode.Conflict, "system_type");
            Assert.Equal(system.SunoAction, problem.GetProperty("current").GetProperty("sunoAction").GetString());

            using var cleared = await SendAsync(client, HttpMethod.Patch, TypeUri(system.Id.ToString()), 1, """{"sunoAction":null}""");
            await SetupApi.ProblemAsync(cleared, HttpStatusCode.Conflict, "system_type");
        }

        foreach (var body in new[] { """{"sunoAction":"inspiration"}""", """{"sunoAction":"voice"}""", """{"sunoAction":"remix"}""", """{"sunoAction":"Cover"}""", """{"sunoAction":""}""", """{"sunoAction":1}""", """{"sunoAction":["cover"]}""" })
        {
            using var refused = await SendAsync(client, HttpMethod.Patch, TypeUri(type), 1, body);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("sunoAction", out _), body);
        }

        // The database holds the same line: a user type stands for an audio action or for none. The
        // migration widened the CHECK by editing the stored definition, which leaves the file sound.
        Assert.Equal("ok", TestDatabase.Scalar(factory.DataPath, "PRAGMA integrity_check;"));
        Assert.Empty(TestDatabase.Rows(factory.DataPath, "PRAGMA foreign_key_check;"));
        Assert.Contains("suno_action IN ('cover', 'extend', 'mashup', 'sample', 'reuse_prompt')", TestDatabase.Scalar(factory.DataPath, "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'song_relationship_types';"), StringComparison.Ordinal);
        foreach (var action in new[] { "inspiration", "voice", "remix" })
        {
            Assert.Throws<SqliteException>(() => TestDatabase.Execute(factory.DataPath, $"UPDATE song_relationship_types SET suno_action = '{action}' WHERE id = '{Upper(Guid.Parse(type))}';"));
        }

        var stored = TestDatabase.Rows(factory.DataPath, "SELECT id || '|' || ifnull(suno_action, '-') || '|' || revision FROM song_relationship_types ORDER BY id;");
        Assert.Equal(10, stored.Count);
        Assert.Contains($"{Upper(Guid.Parse(type))}|-|1", stored);
        Assert.Equal(
            SystemRelationshipTypes.All.Select(static system => $"{Upper(system.Id)}|{system.SunoAction ?? "-"}|1").Order(StringComparer.Ordinal),
            stored.Where(row => !row.StartsWith(Upper(Guid.Parse(type)), StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AMappedTypeIsASourcesTypeAndFollowsItsActionWhileAnUnmappedOneIsRefusedButStillRelatesSongs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = Id(await CreateTypeAsync(client, "Reimagining of", "Reimagined as"));
        var song = await SongApi.CreateAsync(client, "Reimagined");
        var version = VersionId(song);

        // Unmapped: refused as a source's type, nothing stored; usable for relating Songs.
        var refused = await EditVersionAsync(client, version, Sources(type, "unmapped"), HttpStatusCode.UnprocessableEntity);
        Assert.Equal([VersionLineageRules.SourceTypeNotMapped], Rules(refused)["inputs.sources[0].typeId"]);
        Assert.Equal(0, (await VersionAsync(client, version)).GetProperty("inputs").GetProperty("sources").GetArrayLength());
        var other = await SongApi.CreateAsync(client, "Original");
        using (var related = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri($"/api/v1/songs/{Id(song)}/relationships", UriKind.Relative), JsonSerializer.Serialize(new { typeId = type, direction = "forward", otherSong = Id(other) })))
        {
            Assert.Equal(HttpStatusCode.Created, related.StatusCode);
        }

        // Mapped to Cover: accepted, and the source stands for Cover.
        await PatchTypeAsync(client, type, 1, """{"sunoAction":"cover"}""");
        var covered = await EditVersionAsync(client, version, Sources(type, "covered"), HttpStatusCode.OK);
        var source = Assert.Single(covered.GetProperty("inputs").GetProperty("sources").EnumerateArray());
        Assert.Equal(type, source.GetProperty("typeId").GetString());
        Assert.Equal("cover", source.GetProperty("sunoAction").GetString());

        // Cover's rules: one source only, and no Inspiration with it.
        var two = await EditVersionAsync(client, version, Sources(type, "a", "b"), HttpStatusCode.UnprocessableEntity);
        Assert.Equal([VersionLineageRules.AudioSourceCount], Rules(two)["inputs.sources"]);
        var withInspiration = await EditVersionAsync(client, version, """{"inputs":{"inspiration":{"playlist":{"sunoPlaylistId":"p"}}}}""", HttpStatusCode.UnprocessableEntity);
        Assert.Equal([VersionLineageRules.InspirationWithCover], Rules(withInspiration)["inputs.inspiration"]);

        // A type mapped to Mashup takes two sources, and the pair is complete for a Generation.
        var mashup = Id(await CreateTypeAsync(client, "Blend of", "Blended into"));
        await PatchTypeAsync(client, mashup, 1, """{"sunoAction":"mashup"}""");
        var blend = VersionId(await SongApi.CreateAsync(client, "Blend"));
        var blended = await EditVersionAsync(client, blend, Sources(mashup, "first", "second"), HttpStatusCode.OK);
        Assert.All(blended.GetProperty("inputs").GetProperty("sources").EnumerateArray(), static item => Assert.Equal("mashup", item.GetProperty("sunoAction").GetString()));
        var three = await EditVersionAsync(client, blend, Sources(mashup, "a", "b", "c"), HttpStatusCode.UnprocessableEntity);
        Assert.Equal([VersionLineageRules.AudioSourceCount], Rules(three)["inputs.sources"]);
        await SongApi.AttachGenerationAsync(factory, blend.ToString());
        Assert.True((await VersionAsync(client, blend)).GetProperty("isFrozen").GetBoolean());

        // A type mapped to Extend needs the position it continues from, and only it takes one.
        var extension = Id(await CreateTypeAsync(client, "Continuation of", "Continued by"));
        await PatchTypeAsync(client, extension, 1, """{"sunoAction":"extend"}""");
        var longer = VersionId(await SongApi.CreateAsync(client, "Longer"));
        await EditVersionAsync(client, longer, $$$"""{"inputs":{"sources":[{"typeId":"{{{extension}}}","external":{"sunoId":"long"},"continueAtSeconds":12.5}]}}""", HttpStatusCode.OK);
        var notExtend = await EditVersionAsync(client, version, $$$"""{"inputs":{"sources":[{"typeId":"{{{type}}}","external":{"sunoId":"c"},"continueAtSeconds":3}]}}""", HttpStatusCode.UnprocessableEntity);
        Assert.Equal([VersionLineageRules.ContinueAtOnlyExtend], Rules(notExtend)["inputs.sources[0].continueAtSeconds"]);
    }

    [Fact]
    public async Task AMappingInUseCannotChangeOrClearAndTheTypeCannotBeDeletedSoAFrozenVersionKeepsItsLineage()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = Id(await CreateTypeAsync(client, "Blend of", "Blended into"));
        await PatchTypeAsync(client, type, 1, """{"sunoAction":"mashup"}""");

        // Two sources on one frozen Version count once; a second, mutable Version counts too.
        var frozen = VersionId(await SongApi.CreateAsync(client, "Frozen blend"));
        await EditVersionAsync(client, frozen, Sources(type, "first", "second"), HttpStatusCode.OK);
        await SongApi.AttachGenerationAsync(factory, frozen.ToString());
        var stored = VersionImmutabilityGuardTests.Stored(factory, frozen);
        var lineage = Lineage(factory, frozen);

        await AssertMappingInUseAsync(client, factory, type, 2, 1);

        var mutable = VersionId(await SongApi.CreateAsync(client, "Mutable blend"));
        await EditVersionAsync(client, mutable, Sources(type, "third", "fourth"), HttpStatusCode.OK);
        await AssertMappingInUseAsync(client, factory, type, 2, 2);

        // A stale revision is reported first.
        using (var stale = await SendAsync(client, HttpMethod.Patch, TypeUri(type), 1, """{"sunoAction":"cover"}"""))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        // Saving the same value is no change, and a rename in use is allowed: sources name the type by ID.
        Assert.Equal("mashup/2/Blend of", Describe(await PatchTypeAsync(client, type, 2, """{"sunoAction":"mashup"}""")));
        Assert.Equal("mashup/3/Mix of", Describe(await PatchTypeAsync(client, type, 2, """{"name":"Mix of","sunoAction":"mashup"}""")));

        // Deleting it is refused too, even when its relationships may go with it.
        foreach (var path in new[] { TypeUri(type), new Uri($"{TypeUri(type)}?removeRelationships=true", UriKind.Relative) })
        {
            using var deleted = await SendAsync(client, HttpMethod.Delete, path, 3, null);
            var problem = await SetupApi.ProblemAsync(deleted, HttpStatusCode.Conflict, "type_in_use");
            Assert.Equal(2, problem.GetProperty("versionCount").GetInt32());
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM song_relationship_types WHERE id = '{Upper(Guid.Parse(type))}';"));
        Assert.True(stored == VersionImmutabilityGuardTests.Stored(factory, frozen), "A refused mapping change changed a frozen Version.");
        Assert.Equal(lineage, Lineage(factory, frozen));

        // With the mutable Version's sources gone, the frozen one still holds it; it alone counts.
        await EditVersionAsync(client, mutable, """{"inputs":{"sources":[]}}""", HttpStatusCode.OK);
        await AssertMappingInUseAsync(client, factory, type, 3, 1);
        Assert.True(stored == VersionImmutabilityGuardTests.Stored(factory, frozen), "A refused mapping change changed a frozen Version.");

        // A type no source is of changes freely.
        var unused = Id(await CreateTypeAsync(client, "Unused", "Unused by"));
        await PatchTypeAsync(client, unused, 1, """{"sunoAction":"mashup"}""");
        Assert.Equal("cover/3/Unused", Describe(await PatchTypeAsync(client, unused, 2, """{"sunoAction":"cover"}""")));
    }

    [Fact]
    public async Task ADeletedVersionsSourcesDoNotHoldTheMappingButKeepTheTypeSoTheVersionStillRestores()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = Id(await CreateTypeAsync(client, "Reimagining of", "Reimagined as"));
        await PatchTypeAsync(client, type, 1, """{"sunoAction":"cover"}""");
        var version = VersionId(await SongApi.CreateAsync(client, "Gone"));
        var revision = (await EditVersionAsync(client, version, Sources(type, "kept"), HttpStatusCode.OK)).GetProperty("revision").GetInt32();

        using (var deleted = await SendAsync(client, HttpMethod.Delete, new Uri($"/api/v1/versions/{version}", UriKind.Relative), revision, null))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM version_sources;"));

        // The retained source kept its own action, so the mapping may change.
        Assert.Equal("extend/3/Reimagining of", Describe(await PatchTypeAsync(client, type, 2, """{"sunoAction":"extend"}""")));

        // But the type stays while the deleted Version can be restored.
        using (var refused = await SendAsync(client, HttpMethod.Delete, TypeUri(type), 3, null))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "type_in_use");
            Assert.Equal(1, problem.GetProperty("versionCount").GetInt32());
        }

        var group = Assert.Single(await WithServiceAsync(factory, static service => service.ListAsync(CancellationToken.None)));
        Assert.Contains(group.Records, static record => record.RecordType == RetainedRecordTypes.VersionSource);
        Assert.IsType<RetentionRestoreOutcome.Restored>(await RestoreAsync(factory, group.Id));
        var restored = Assert.Single((await VersionAsync(client, version)).GetProperty("inputs").GetProperty("sources").EnumerateArray());
        Assert.Equal("cover", restored.GetProperty("sunoAction").GetString());

        // A type no source is of is deleted as before.
        var unused = Id(await CreateTypeAsync(client, "Unused", "Unused by"));
        await PatchTypeAsync(client, unused, 1, """{"sunoAction":"sample"}""");
        using var gone = await SendAsync(client, HttpMethod.Delete, TypeUri(unused), 2, null);
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
    }

    /// <summary>Changing and clearing the mapping are both refused with the Version count, and the type is as it was.</summary>
    private static async Task AssertMappingInUseAsync(HttpClient client, N8TracksApiFactory factory, string type, int revision, int versionCount)
    {
        var before = TestDatabase.Scalar(factory.DataPath, $"SELECT name || '|' || ifnull(suno_action, '-') || '|' || revision FROM song_relationship_types WHERE id = '{Upper(Guid.Parse(type))}';");
        foreach (var body in new[] { """{"sunoAction":"cover"}""", """{"sunoAction":null}""", """{"name":"Renamed too","sunoAction":"sample"}""" })
        {
            using var refused = await SendAsync(client, HttpMethod.Patch, TypeUri(type), revision, body);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "mapping_in_use");
            Assert.Equal(versionCount, problem.GetProperty("versionCount").GetInt32());
            Assert.Contains(versionCount == 1 ? "1 Version has" : $"{versionCount} Versions have", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }

        Assert.Equal(before, TestDatabase.Scalar(factory.DataPath, $"SELECT name || '|' || ifnull(suno_action, '-') || '|' || revision FROM song_relationship_types WHERE id = '{Upper(Guid.Parse(type))}';"));
    }

    private static string Lineage(N8TracksApiFactory factory, Guid version) =>
        string.Join(';', TestDatabase.Rows(factory.DataPath, $"SELECT position || '|' || type_id || '|' || ifnull(suno_action, '-') FROM version_sources WHERE version_id = '{Upper(version)}' ORDER BY source_group, position;"));

    private static string Sources(string typeId, params string[] sunoIds) =>
        JsonSerializer.Serialize(new { inputs = new { sources = sunoIds.Select(id => new { typeId, external = new { sunoId = id } }) } });

    private static string Describe(JsonElement type) =>
        $"{(type.GetProperty("sunoAction").ValueKind == JsonValueKind.Null ? "-" : type.GetProperty("sunoAction").GetString())}/{type.GetProperty("revision").GetInt32()}/{type.GetProperty("name").GetString()}";

    private static Dictionary<string, string[]> Rules(JsonElement problem) =>
        problem.GetProperty("rules").EnumerateObject()
            .ToDictionary(static field => field.Name, static field => field.Value.EnumerateArray().Select(static rule => rule.GetString()!).ToArray(), StringComparer.Ordinal);

    private static Uri TypeUri(string id) => new($"/api/v1/relationship-types/{id}", UriKind.Relative);

    private static string Id(JsonElement item) => item.GetProperty("id").GetString()!;

    private static Guid VersionId(JsonElement song) => song.GetProperty("currentVersion").GetProperty("id").GetGuid();

    private static async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Types);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static async Task<JsonElement> FindAsync(HttpClient client, string id) =>
        (await ListAsync(client)).Single(item => Id(item) == id);

    private static async Task<JsonElement> CreateTypeAsync(HttpClient client, string name, string reverseName)
    {
        using var response = await SendAsync(client, HttpMethod.Post, Types, null, JsonSerializer.Serialize(new { name, reverseName }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> PatchTypeAsync(HttpClient client, string id, int revision, string json)
    {
        using var response = await SendAsync(client, HttpMethod.Patch, TypeUri(id), revision, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var answer = await SetupApi.JsonAsync(response);
        Assert.Equal(SongApi.Quoted(answer.GetProperty("revision").GetInt32()), response.Headers.ETag?.ToString());
        return answer;
    }

    private static async Task<JsonElement> VersionAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Edits the Version at its current revision and answers the body, asserting <paramref name="expected"/>.</summary>
    private static async Task<JsonElement> EditVersionAsync(HttpClient client, Guid id, string json, HttpStatusCode expected)
    {
        var revision = (await VersionAsync(client, id)).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative), revision, json);
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Sends a session request with the anti-forgery header and, when given, the revision in If-Match.</summary>
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
}
