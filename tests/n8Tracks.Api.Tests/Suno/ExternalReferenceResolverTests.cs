using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Invariants;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Imported lineage connected by Suno ID (#137): a source already imported links to its Generation; one
/// that is not is a "Not imported" external reference, which the resolver turns into a link once the
/// clip is imported, in either order, without a second Generation or reference, and with the frozen
/// Version's stored sources byte-identical. Resolution is called directly, against Generations
/// attached through the application service, as the commit story (#140) will call it.
/// </summary>
public sealed class ExternalReferenceResolverTests
{
    private const string ParentId = "00000000-0000-4000-8000-0000000000b1";
    private const string OtherParentId = "00000000-0000-4000-8000-0000000000b2";
    private const string ChildId = "00000000-0000-4000-8000-0000000000c1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AParentAndAChildConnectWhicheverIsImportedFirst(bool parentFirst)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var parentSong = await SongApi.CreateAsync(client, "Lineage parent");
        var childSong = await SongApi.CreateAsync(client, "Lineage child");

        ImportedClip parent;
        ImportedClip child;
        if (parentFirst)
        {
            parent = await ImportedVersions.ImportAsync(factory, parentSong, "2", Clip(ParentId));
            child = await ImportedVersions.ImportAsync(factory, childSong, "2", Cover(ChildId, ParentId));

            // The source is already a Generation: linked at once, with no reference stored for it.
            Assert.Empty(child.Linked.References);
            Assert.Equal(parent.Generation.Generation.Id, Assert.Single(child.Linked.Generations).Id);
            Assert.Equal(0, ReferenceCount(factory, ParentId));
        }
        else
        {
            child = await ImportedVersions.ImportAsync(factory, childSong, "2", Cover(ChildId, ParentId));

            // Not imported: the cover shows what it was made from, and importing it imported nothing else.
            var reference = Assert.Single(child.Linked.References);
            Assert.Equal(ExternalSunoReferenceRules.NotImportedLabel, reference.Label);
            Assert.Equal("Suno clip 00000000", reference.Title);
            Assert.Equal("0", Generations(factory, ParentId));
            var source = Assert.Single((await GetAsync(client, child.VersionId)).GetProperty("inputs").GetProperty("sources").EnumerateArray());
            Assert.Equal("not_imported", source.GetProperty("availability").GetString());
            Assert.Equal(ParentId, source.GetProperty("external").GetProperty("sunoId").GetString());

            var before = VersionImmutabilityGuardTests.Stored(factory, child.VersionId);
            parent = await ImportedVersions.ImportAsync(factory, parentSong, "2", Clip(ParentId));

            // The parent's import resolves the reference: the source is now its Generation, the
            // stored sources (compared by Suno ID) are byte-identical, and the Songs are related.
            Assert.Equal(new ReferenceResolution(1, 1), parent.Resolution);
            Assert.Equal(before, VersionImmutabilityGuardTests.Stored(factory, child.VersionId));
            Assert.Equal(1, ReferenceCount(factory, ParentId));
            source = Assert.Single((await GetAsync(client, child.VersionId)).GetProperty("inputs").GetProperty("sources").EnumerateArray());
            Assert.Equal("ok", source.GetProperty("availability").GetString());
            Assert.Equal(parent.Generation.Generation.Id, source.GetProperty("generation").GetProperty("id").GetGuid());
            Assert.Equal("1", Relationships(factory, childSong, parentSong, SystemRelationshipTypes.Cover.Id));
        }

        // Either way: the child's source is the parent's Generation, and there is one Generation for the Suno ID.
        Assert.Equal(
            parent.Generation.Generation.Id.ToString().ToUpperInvariant(),
            TestDatabase.Scalar(factory.DataPath, $"SELECT generation_id FROM version_sources WHERE version_id = '{Upper(child.VersionId)}';"));
        Assert.Equal("1", Generations(factory, ParentId));
        Assert.Equal("1", Generations(factory, ChildId));

        // Resolving again changes nothing: no second relationship, no second reference.
        var stored = VersionImmutabilityGuardTests.Stored(factory, child.VersionId);
        Assert.Equal(new ReferenceResolution(0, 0), await ImportedVersions.ResolveAsync(factory, ParentId));
        Assert.Equal(stored, VersionImmutabilityGuardTests.Stored(factory, child.VersionId));
        Assert.Equal(parentFirst ? 0 : 1, ReferenceCount(factory, ParentId));
        if (!parentFirst)
        {
            Assert.Equal("1", Relationships(factory, childSong, parentSong, SystemRelationshipTypes.Cover.Id));
        }
    }

    [Fact]
    public async Task OnlyTheSourceWhoseClipIsImportedResolvesAndTheOrderStays()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var childSong = await SongApi.CreateAsync(client, "Mashup child");
        var parentSong = await SongApi.CreateAsync(client, "Mashup parent");
        var mashup = Clip(ChildId);
        mashup["metadata"] = new JsonObject { ["task"] = "mashup_condition", ["mashup_clip_ids"] = new JsonArray(OtherParentId, ParentId) };

        var child = await ImportedVersions.ImportAsync(factory, childSong, "2", mashup);
        Assert.Equal([OtherParentId, ParentId], child.Linked.References.Select(static reference => reference.SunoId));
        var before = VersionImmutabilityGuardTests.Stored(factory, child.VersionId);

        var parent = await ImportedVersions.ImportAsync(factory, parentSong, "2", Clip(ParentId));

        Assert.Equal(new ReferenceResolution(1, 1), parent.Resolution);
        Assert.Equal(before, VersionImmutabilityGuardTests.Stored(factory, child.VersionId));
        var sources = (await GetAsync(client, child.VersionId)).GetProperty("inputs").GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(["not_imported", "ok"], sources.Select(static source => source.GetProperty("availability").GetString()));
        Assert.Equal(OtherParentId, sources[0].GetProperty("external").GetProperty("sunoId").GetString());
        Assert.Equal(parent.Generation.Generation.Id, sources[1].GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal("1", Relationships(factory, childSong, parentSong, SystemRelationshipTypes.Mashup.Id));
    }

    [Fact]
    public async Task ASunoIdNoGenerationHasResolvesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var child = await ImportedVersions.ImportAsync(factory, await SongApi.CreateAsync(client, "Unresolved child"), "2", Cover(ChildId, ParentId));
        var before = VersionImmutabilityGuardTests.Stored(factory, child.VersionId);

        Assert.Equal(new ReferenceResolution(0, 0), await ImportedVersions.ResolveAsync(factory, ParentId));
        Assert.Equal(new ReferenceResolution(0, 0), await ImportedVersions.ResolveAsync(factory, "never-imported"));

        Assert.Equal(before, VersionImmutabilityGuardTests.Stored(factory, child.VersionId));
        Assert.Equal(1, ReferenceCount(factory, ParentId));
    }

    private static JsonObject Clip(string sunoId) => new() { ["id"] = sunoId, ["status"] = "complete", ["title"] = "Clip " + sunoId[^2..] };

    private static JsonObject Cover(string sunoId, string sourceId)
    {
        var clip = Clip(sunoId);
        clip["metadata"] = new JsonObject { ["task"] = "cover", ["cover_clip_id"] = sourceId, ["edited_clip_id"] = sourceId };
        return clip;
    }

    private static string Generations(N8TracksApiFactory factory, string sunoId) =>
        TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM generations WHERE suno_id = '{sunoId}';");

    private static int ReferenceCount(N8TracksApiFactory factory, string sunoId) =>
        int.Parse(TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM external_suno_references WHERE suno_id = '{sunoId}';"), System.Globalization.CultureInfo.InvariantCulture);

    private static string Relationships(N8TracksApiFactory factory, JsonElement from, JsonElement to, Guid typeId) =>
        TestDatabase.Scalar(
            factory.DataPath,
            $"""
            SELECT count(*) FROM song_relationships
            WHERE upper(from_song_id) = '{Upper(from.GetProperty("id").GetGuid())}' AND upper(to_song_id) = '{Upper(to.GetProperty("id").GetGuid())}' AND upper(type_id) = '{Upper(typeId)}';
            """);

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }
}
