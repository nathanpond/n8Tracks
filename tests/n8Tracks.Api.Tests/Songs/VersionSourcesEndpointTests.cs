using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Generations;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// A Version's sources and file inputs through the API (#122): written whole under the lineage keys
/// of <c>inputs</c>, read back there, the applicable ones in <c>effectiveInputs</c>, refused with a
/// named rule, frozen with the Version, copied by Create New Version From, completed before a
/// Generation is attached, and kept by Suno ID when a source's Generation is deleted.
/// </summary>
public sealed class VersionSourcesEndpointTests
{
    private static readonly string Cover = SystemRelationshipTypes.Cover.Id.ToString();
    private static readonly string Mashup = SystemRelationshipTypes.Mashup.Id.ToString();
    private static readonly string Extend = SystemRelationshipTypes.Extend.Id.ToString();
    private static readonly string Sample = SystemRelationshipTypes.SampleThisSong.Id.ToString();

    [Fact]
    public async Task SourcesRoundTripInInputsAndAReadSentBackIsNoChange()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var other = await SongApi.CreateAsync(client, "The Original");
        var otherVersion = VersionId(other);
        var generation = await SongApi.AttachGenerationAsync(factory, otherVersion.ToString(), Clips.Minimal("original-clip"));
        var song = await SongApi.CreateAsync(client, "The Cover");
        var id = VersionId(song);

        var written = await EditAsync(client, id, $$$"""
            {"inputs":{
              "sources":[{"typeId":"{{{Cover}}}","generation":"{{{generation.Shortcode}}}","secondaryIds":{"edited_clip_id":"edited-1","cover_clip_id":"original-clip"}}],
              "inspiration":null,
              "voice":{"personaId":"persona-1","name":"  Smoky  "},
              "fileInputs":[{"kind":"audio","description":"Demo\r\nhum"}]
            }}
            """, expectFailure: true);
        Assert.Equal(VersionLineageRules.AudioSlotTaken, Assert.Single(Rules(written)).Value[0]);

        var edited = await EditAsync(client, id, $$$"""
            {"inputs":{
              "sources":[{"typeId":"{{{Cover}}}","generation":"{{{generation.Shortcode}}}","secondaryIds":{"edited_clip_id":"edited-1","cover_clip_id":"original-clip"}}],
              "voice":{"personaId":"persona-1","name":"  Smoky  "}
            }}
            """);
        var inputs = edited.GetProperty("inputs");
        var source = Assert.Single(inputs.GetProperty("sources").EnumerateArray());
        Assert.Equal(Cover, source.GetProperty("typeId").GetString());
        Assert.Equal("cover", source.GetProperty("sunoAction").GetString());
        Assert.Equal(generation.Generation.Id, source.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal(generation.Shortcode, source.GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal("The Original", source.GetProperty("generation").GetProperty("songTitle").GetString());
        Assert.False(source.GetProperty("generation").GetProperty("missing").GetBoolean());
        Assert.Equal("""{"cover_clip_id":"original-clip","edited_clip_id":"edited-1"}""", source.GetProperty("secondaryIds").GetRawText());
        Assert.Equal("""{"personaId":"persona-1","name":"Smoky"}""", inputs.GetProperty("voice").GetRawText());
        Assert.Equal(JsonValueKind.Null, inputs.GetProperty("inspiration").ValueKind);

        // Sent back as read, nothing changes: not even the revision.
        var revision = edited.GetProperty("revision").GetInt32();
        var again = await EditAsync(client, id, new JsonObject { ["inputs"] = Lineage(inputs) }.ToJsonString());
        Assert.Equal(revision, again.GetProperty("revision").GetInt32());

        // Another form of each target, and the other parts, in one edit; omitted keys stay as they are.
        var edit = await EditAsync(client, id, $$$"""
            {"inputs":{
              "sources":[{"typeId":"{{{Mashup}}}","song":"{{{other.GetProperty("shortcode").GetString()}}}"},{"typeId":"{{{Mashup}}}","external":{"sunoId":"outside-clip","title":"Outside","address":"https://suno.com/song/outside-clip"}}],
              "fileInputs":[{"kind":"video","description":"b"},{"kind":"image","description":"a"}],
              "songMode":"simple"
            }}
            """);
        var sources = edit.GetProperty("inputs").GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(2, sources.Count);
        Assert.Equal(other.GetProperty("id").GetGuid(), sources[0].GetProperty("song").GetProperty("id").GetGuid());
        Assert.Equal("The Original", sources[0].GetProperty("song").GetProperty("title").GetString());
        Assert.Equal("""{"sunoId":"outside-clip","title":"Outside","address":"https://suno.com/song/outside-clip","label":null}""", sources[1].GetProperty("external").GetRawText());
        Assert.Equal("persona-1", edit.GetProperty("inputs").GetProperty("voice").GetProperty("personaId").GetString());
        Assert.Equal(["image", "video"], edit.GetProperty("inputs").GetProperty("fileInputs").EnumerateArray().Select(static file => file.GetProperty("kind").GetString()));

        // null and [] clear.
        var cleared = await EditAsync(client, id, """{"inputs":{"sources":[],"voice":null,"fileInputs":null}}""");
        Assert.Equal("""{"sources":[],"inspiration":null,"voice":null,"fileInputs":[]}""", Lineage(cleared.GetProperty("inputs")).ToJsonString());
    }

    /// <summary>
    /// #320: an image note kept, and hidden, from Simple mode never refuses an Advanced-mode change of
    /// the audio note beside it. The editor sends the whole list, the image included, exactly as held;
    /// only a note sent new or changed is checked against the mode.
    /// </summary>
    [Fact]
    public async Task AnImageNoteKeptFromSimpleModeLetsTheAudioNoteChangeInAdvancedMode()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Kept image"));

        await EditAsync(client, id, """{"inputs":{"songMode":"simple","fileInputs":[{"kind":"image","description":"art"}]}}""");
        await EditAsync(client, id, """{"inputs":{"songMode":"advanced"}}""");

        // Add an audio note, sending the kept image back as the editor does.
        var added = await EditAsync(client, id, """{"inputs":{"fileInputs":[{"kind":"image","description":"art"},{"kind":"audio","description":"hum"}]}}""");
        Assert.Equal("""[{"kind":"audio","description":"hum"},{"kind":"image","description":"art"}]""", Sorted(added));
        Assert.Equal("audio", Assert.Single(added.GetProperty("effectiveInputs").GetProperty("fileInputs").EnumerateArray()).GetProperty("kind").GetString());

        // Edit it, then remove it: the image stays stored and hidden.
        var edited = await EditAsync(client, id, """{"inputs":{"fileInputs":[{"kind":"image","description":"art"},{"kind":"audio","description":"hummed tune"}]}}""");
        Assert.Equal("""[{"kind":"audio","description":"hummed tune"},{"kind":"image","description":"art"}]""", Sorted(edited));
        var removed = await EditAsync(client, id, """{"inputs":{"fileInputs":[{"kind":"image","description":"art"}]}}""");
        Assert.Equal("""[{"kind":"image","description":"art"}]""", Sorted(removed));
        Assert.False(removed.GetProperty("effectiveInputs").TryGetProperty("fileInputs", out _));

        // Complement: in Advanced mode the image itself cannot be changed, nor a video added.
        foreach (var body in new[]
        {
            """{"inputs":{"fileInputs":[{"kind":"image","description":"new art"}]}}""",
            """{"inputs":{"fileInputs":[{"kind":"image","description":"art"},{"kind":"video","description":"clip"}]}}""",
        })
        {
            var refused = await EditAsync(client, id, body, expectFailure: true);
            Assert.Contains(VersionLineageRules.FileInputSimpleOnly, Rules(refused).Values.SelectMany(static rules => rules));
        }

        Assert.Equal("""[{"kind":"image","description":"art"}]""", Sorted(await GetAsync(client, id)));

        static string Sorted(JsonElement version) =>
            new JsonArray([.. version.GetProperty("inputs").GetProperty("fileInputs").EnumerateArray()
                .OrderBy(static file => file.GetProperty("kind").GetString(), StringComparer.Ordinal)
                .Select(static file => JsonNode.Parse(file.GetRawText()))]).ToJsonString();
    }

    [Fact]
    public async Task EffectiveInputsHoldOnlyWhatAppliesToTheKindAndMode()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Effective"));

        // Advanced: individual Inspiration sources apply.
        var advanced = await EditAsync(client, id, """{"inputs":{"inspiration":{"sources":[{"external":{"sunoId":"inspo-1"}},{"external":{"sunoId":"inspo-2"}}]},"voice":{"personaId":"p","name":""}}}""");
        Assert.Equal(2, advanced.GetProperty("effectiveInputs").GetProperty("inspiration").GetProperty("sources").GetArrayLength());
        Assert.True(advanced.GetProperty("effectiveInputs").TryGetProperty("voice", out _));
        Assert.False(advanced.GetProperty("effectiveInputs").TryGetProperty("fileInputs", out _));

        // Simple: they are kept, but no longer sent; a Simple-only image is.
        var simple = await EditAsync(client, id, """{"inputs":{"songMode":"simple","fileInputs":[{"kind":"image","description":"Album art"}]}}""");
        Assert.Equal(2, simple.GetProperty("inputs").GetProperty("inspiration").GetProperty("sources").GetArrayLength());
        Assert.False(simple.GetProperty("effectiveInputs").TryGetProperty("inspiration", out _));
        Assert.Equal("image", simple.GetProperty("effectiveInputs").GetProperty("fileInputs")[0].GetProperty("kind").GetString());

        // Back to Advanced: the image is kept (changing the mode never refuses) but not sent.
        var back = await EditAsync(client, id, """{"inputs":{"songMode":"advanced"}}""");
        Assert.Equal("image", back.GetProperty("inputs").GetProperty("fileInputs")[0].GetProperty("kind").GetString());
        Assert.False(back.GetProperty("effectiveInputs").TryGetProperty("fileInputs", out _));
        Assert.True(back.GetProperty("effectiveInputs").TryGetProperty("inspiration", out _));

        // A Speech sends no lineage at all.
        var speech = await EditAsync(client, id, """{"inputs":{"kind":"speech"}}""");
        Assert.All(new[] { "sources", "inspiration", "voice", "fileInputs" }, key => Assert.False(speech.GetProperty("effectiveInputs").TryGetProperty(key, out _)));

        // And an image cannot be added while the Song is in Advanced mode.
        await EditAsync(client, id, """{"inputs":{"kind":"song"}}""");
        var refused = await EditAsync(client, id, """{"inputs":{"fileInputs":[{"kind":"video","description":"Clip"}]}}""", expectFailure: true);
        Assert.Contains(VersionLineageRules.FileInputSimpleOnly, Rules(refused).Values.SelectMany(static rules => rules));
    }

    [Fact]
    public async Task ASourceMayNotBeAGenerationOfItsOwnVersionButMayBeOneOfItsSong()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Own");
        var first = VersionId(song);
        var generation = await SongApi.AttachGenerationAsync(factory, first.ToString());

        // Refused as a field error naming the rule, before the freeze is even considered.
        var own = await EditAsync(client, first, $$$"""{"inputs":{"sources":[{"typeId":"{{{Extend}}}","generation":"{{{generation.Generation.Id}}}"}]}}""", expectFailure: true);
        Assert.Equal([VersionLineageRules.SourceIsOwnGeneration], Rules(own)["inputs.sources[0].generation"]);

        // Complement: a new Version of the same Song may be made from it, and no relationship is made.
        var second = await CreateFromAsync(client, song.GetProperty("shortcode").GetString()!, first);
        var edited = await EditAsync(client, second, $$$"""{"inputs":{"sources":[{"typeId":"{{{Extend}}}","generation":"{{{generation.Shortcode}}}","continueAtSeconds":12.25}]}}""");
        Assert.Equal(12.25m, edited.GetProperty("inputs").GetProperty("sources")[0].GetProperty("continueAtSeconds").GetDecimal());
        Assert.Empty((await SongAsync(client, song.GetProperty("id").GetString()!)).GetProperty("relationships").EnumerateArray());
    }

    [Fact]
    public async Task ASourceInAnotherSongRelatesTheSongsOnceAndRemovingItKeepsTheRelationship()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var original = await SongApi.CreateAsync(client, "Original");
        var generation = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString());
        var inspiration = await SongApi.CreateAsync(client, "Inspiration");
        var cover = await SongApi.CreateAsync(client, "Cover");
        var id = VersionId(cover);

        await EditAsync(client, id, $$$$"""{"inputs":{"sources":[{"typeId":"{{{{Sample}}}}","generation":"{{{{generation.Generation.Id}}}}"}],"inspiration":{"sources":[{"song":{"id":"{{{{inspiration.GetProperty("id").GetString()}}}}"}}]}}}""");
        await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Sample}}}","generation":"{{{generation.Shortcode}}}","continueAtSeconds":null}]}}""");

        var relationships = (await SongAsync(client, cover.GetProperty("id").GetString()!)).GetProperty("relationships").EnumerateArray().ToList();
        Assert.Equal(2, relationships.Count);
        Assert.Contains(relationships, relation =>
            relation.GetProperty("typeId").GetGuid() == SystemRelationshipTypes.SampleThisSong.Id
            && relation.GetProperty("direction").GetString() == "forward"
            && relation.GetProperty("song").GetProperty("id").GetString() == original.GetProperty("id").GetString());
        Assert.Contains(relationships, relation =>
            relation.GetProperty("typeId").GetGuid() == SystemRelationshipTypes.UseAsInspiration.Id
            && relation.GetProperty("song").GetProperty("id").GetString() == inspiration.GetProperty("id").GetString());

        await EditAsync(client, id, """{"inputs":{"sources":[],"inspiration":null}}""");
        Assert.Equal(2, (await SongAsync(client, cover.GetProperty("id").GetString()!)).GetProperty("relationships").GetArrayLength());
    }

    [Fact]
    public async Task ASourcesTypeMustExistStandForAnActionAndNotBeTheGeneralRemix()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Types"));
        using var created = await SongApi.SendJsonAsync(client, HttpMethod.Post, new Uri("/api/v1/relationship-types", UriKind.Relative), """{"name":"Answer to","reverseName":"Answered by"}""");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var userType = (await SetupApi.JsonAsync(created)).GetProperty("id").GetString();

        Assert.Equal([VersionLineageRules.SourceTypeNotMapped], Rules(await EditAsync(client, id, Sources(userType!), expectFailure: true))["inputs.sources[0].typeId"]);
        Assert.Equal([VersionLineageRules.SourceTypeUnknown], Rules(await EditAsync(client, id, Sources(Guid.NewGuid().ToString()), expectFailure: true))["inputs.sources[0].typeId"]);
        Assert.Equal([VersionLineageRules.SourceTypeImportOnly], Rules(await EditAsync(client, id, Sources(SystemRelationshipTypes.Remix.Id.ToString()), expectFailure: true))["inputs.sources[0]"]);
        Assert.Equal([VersionLineageRules.SourceTypeNotAudioAction], Rules(await EditAsync(client, id, Sources(SystemRelationshipTypes.Voice.Id.ToString()), expectFailure: true))["inputs.sources[0]"]);
        Assert.Equal([VersionLineageRules.SourceNotFound], Rules(await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Cover}}}","generation":"n8-999-v1-g1"}]}}""", expectFailure: true))["inputs.sources[0].generation"]);
        Assert.Equal([VersionLineageRules.OneTarget], Rules(await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Cover}}}","song":"n8-1","external":{"sunoId":"x"}}]}}""", expectFailure: true))["inputs.sources[0]"]);
        Assert.Equal([VersionLineageRules.OneAudioAction], Rules(await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Cover}}}","external":{"sunoId":"a"}},{"typeId":"{{{Sample}}}","external":{"sunoId":"b"}}]}}""", expectFailure: true))["inputs.sources"]);
        Assert.Equal([VersionLineageRules.OneVoice], Rules(await EditAsync(client, id, """{"inputs":{"voice":[{"personaId":"a"},{"personaId":"b"}]}}""", expectFailure: true))["inputs.voice"]);
        Assert.Equal(
            [VersionLineageRules.InspirationWithCover],
            Rules(await EditAsync(client, id, "{\"inputs\":{\"sources\":[{\"typeId\":\"" + Cover + "\",\"external\":{\"sunoId\":\"a\"}}],\"inspiration\":{\"playlist\":{\"sunoPlaylistId\":\"p\"}}}}", expectFailure: true))["inputs.inspiration"]);

        // Nothing was stored by any of them.
        Assert.Equal(0, (await GetAsync(client, id)).GetProperty("inputs").GetProperty("sources").GetArrayLength());
    }

    [Fact]
    public async Task EverySourceAndFileInputWriteIsRefusedOnAFrozenVersion()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Frozen sources"));
        await EditAsync(client, id, $$$"""{"inputs":{{{Inventory.LineageValues.InitialInputsJson("frozen")}}}}""");
        await SongApi.AttachGenerationAsync(factory, id.ToString());
        var before = Invariants.VersionImmutabilityGuardTests.Stored(factory, id);
        var read = (await GetAsync(client, id)).GetProperty("inputs");

        foreach (var body in new[]
        {
            $$$"""{"sources":[{"typeId":"{{{Sample}}}","external":{"sunoId":"another"}}]}""",
            """{"sources":[]}""",
            """{"inspiration":{"playlist":{"sunoPlaylistId":"another"}}}""",
            """{"inspiration":null}""",
            """{"voice":{"personaId":"another","name":""}}""",
            """{"voice":null}""",
            """{"fileInputs":[{"kind":"image","description":"another"}]}""",
            """{"fileInputs":[]}""",
        })
        {
            using var refused = await PatchAsync(client, id, $$$"""{"name":"Renamed","inputs":{{{body}}}}""");
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Conflict, "version_frozen");
            Assert.Equal(before, Invariants.VersionImmutabilityGuardTests.Stored(factory, id));
        }

        // The lineage sent back as it is read is no change, so the name may change with it.
        var renamed = await EditAsync(client, id, new JsonObject { ["name"] = "Renamed", ["inputs"] = Lineage(read) }.ToJsonString());
        Assert.Equal("Renamed", renamed.GetProperty("name").GetString());
        Assert.Equal(before, Invariants.VersionImmutabilityGuardTests.Stored(factory, id));
    }

    [Fact]
    public async Task CreateNewVersionFromCopiesTheLineageWithTheSameTargets()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Copied sources");
        var id = VersionId(song);
        await EditAsync(client, id, $$$"""{"inputs":{{{Inventory.LineageValues.InitialInputsJson("copied")}}}}""");
        await SongApi.AttachGenerationAsync(factory, id.ToString());

        var copy = await CreateFromAsync(client, song.GetProperty("shortcode").GetString()!, id);

        var source = await GetAsync(client, id);
        var copied = await GetAsync(client, copy);
        Assert.Equal(Lineage(source.GetProperty("inputs")).ToJsonString(), Lineage(copied.GetProperty("inputs")).ToJsonString());
        Assert.False(copied.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(
            TestDatabase.Scalar(factory.DataPath, $"SELECT external_reference_id FROM version_sources WHERE version_id = '{id.ToString().ToUpperInvariant()}';"),
            TestDatabase.Scalar(factory.DataPath, $"SELECT external_reference_id FROM version_sources WHERE version_id = '{copy.ToString().ToUpperInvariant()}';"));

        // The copy is mutable: its sources change, the source's do not.
        await EditAsync(client, copy, """{"inputs":{"sources":[]}}""");
        Assert.Equal(1, (await GetAsync(client, id)).GetProperty("inputs").GetProperty("sources").GetArrayLength());
    }

    [Fact]
    public async Task AGenerationIsAttachedOnlyOnceTheSourcesAreComplete()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = VersionId(await SongApi.CreateAsync(client, "Complete sources"));

        // The editor may hold partial work: a Mashup's first source, an Extend without its position.
        await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Mashup}}}","external":{"sunoId":"one"}}]}}""");
        var mashup = Assert.IsType<GenerationAttachOutcome.IncompleteSources>(await SongApi.AttachAsync(factory, id.ToString(), null));
        Assert.Equal(VersionLineageRules.AudioSourceCount, Assert.Single(mashup.Errors).Rule);

        await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Extend}}}","external":{"sunoId":"one"}}]}}""");
        var extend = Assert.IsType<GenerationAttachOutcome.IncompleteSources>(await SongApi.AttachAsync(factory, id.ToString(), null));
        Assert.Equal(VersionLineageRules.ContinueAtRequired, Assert.Single(extend.Errors).Rule);
        Assert.False((await GetAsync(client, id)).GetProperty("isFrozen").GetBoolean());

        // Extend's position must be within its source, when that is known.
        var longSource = await SongApi.CreateAsync(client, "Long source");
        var clip = JsonNode.Parse(Clips.Minimal("source-30s"))!;
        clip["metadata"] = new JsonObject { ["duration"] = 30.0 };
        var generation = await SongApi.AttachGenerationAsync(factory, VersionId(longSource).ToString(), clip.ToJsonString());
        var beyond = await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Extend}}}","generation":"{{{generation.Shortcode}}}","continueAtSeconds":30.01}]}}""", expectFailure: true);
        Assert.Equal([VersionLineageRules.ContinueAtBeyondSource], Rules(beyond)["inputs.sources[0].continueAtSeconds"]);

        await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Extend}}}","generation":"{{{generation.Shortcode}}}","continueAtSeconds":30}]}}""");
        Assert.IsType<GenerationAttachOutcome.Attached>(await SongApi.AttachAsync(factory, id.ToString(), null));
    }

    [Fact]
    public async Task ASourceWhoseGenerationIsDeletedKeepsItsSunoIdAndAFrozenVersionIsUnchanged()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var original = await SongApi.CreateAsync(client, "Deleted original");
        var withSunoId = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("kept-suno-id"));
        var withoutSunoId = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString());
        var derived = await SongApi.CreateAsync(client, "Derived");
        var id = VersionId(derived);
        await EditAsync(client, id, $$$"""
            {"inputs":{"sources":[{"typeId":"{{{Mashup}}}","generation":"{{{withSunoId.Shortcode}}}"},{"typeId":"{{{Mashup}}}","generation":"{{{withoutSunoId.Shortcode}}}"}]}}
            """);
        await SongApi.AttachGenerationAsync(factory, id.ToString());
        var before = Invariants.VersionImmutabilityGuardTests.Stored(factory, id);

        // Deleting the Song deletes both Generations.
        var (title, revision) = (original.GetProperty("title").GetString()!, (await SongAsync(client, original.GetProperty("id").GetString()!)).GetProperty("revision").GetInt32());
        using (var deleted = await SendAsync(client, HttpMethod.Delete, SongApi.Song(original.GetProperty("shortcode").GetString()!), SongApi.Quoted(revision), $$$"""{"confirmTitle":{{{JsonSerializer.Serialize(title)}}}}"""))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        var sources = (await GetAsync(client, id)).GetProperty("inputs").GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal("kept-suno-id", sources[0].GetProperty("external").GetProperty("sunoId").GetString());
        Assert.Equal("Deleted", sources[0].GetProperty("external").GetProperty("label").GetString());
        Assert.Equal("Minimal clip", sources[0].GetProperty("external").GetProperty("title").GetString());
        Assert.Equal(withoutSunoId.Generation.Id, sources[1].GetProperty("generation").GetProperty("id").GetGuid());
        Assert.True(sources[1].GetProperty("generation").GetProperty("missing").GetBoolean());

        // The source's identity is its Suno ID either way: the frozen Version is unchanged.
        Assert.Equal(before, Invariants.VersionImmutabilityGuardTests.Stored(factory, id));

        // Read and sent back, the missing Generation is kept as it is: no change, so no refusal.
        var read = await GetAsync(client, id);
        var resent = await EditAsync(client, id, new JsonObject { ["inputs"] = Lineage(read.GetProperty("inputs")) }.ToJsonString());
        Assert.Equal(read.GetProperty("revision").GetInt32(), resent.GetProperty("revision").GetInt32());
        Assert.Equal(before, Invariants.VersionImmutabilityGuardTests.Stored(factory, id));

        // Only that rewrite gets past the database: pointing the source anywhere else is refused.
        var version = id.ToString().ToUpperInvariant();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"UPDATE version_sources SET external_reference_id = NULL, generation_id = '{withSunoId.Generation.Id.ToString().ToUpperInvariant()}' WHERE version_id = '{version}' AND position = 0;"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"UPDATE version_sources SET generation_id = NULL, song_id = '{original.GetProperty("id").GetString()!.ToUpperInvariant()}' WHERE version_id = '{version}' AND position = 1;"));
    }

    [Fact]
    public async Task EachSourceSaysWhetherItIsStillAvailable()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var original = await SongApi.CreateAsync(client, "Available original");
        var present = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("present-clip"));
        var trashed = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("trashed-clip"));
        var gone = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("gone-clip"));
        TestDatabase.Execute(factory.DataPath, $"""
            UPDATE generations SET remote_state = 'trashed' WHERE id = '{trashed.Generation.Id.ToString().ToUpperInvariant()}';
            UPDATE generations SET remote_state = 'missing' WHERE id = '{gone.Generation.Id.ToString().ToUpperInvariant()}';
            UPDATE generations SET duration_seconds = 187.5 WHERE id = '{present.Generation.Id.ToString().ToUpperInvariant()}';
            """);
        var id = VersionId(await SongApi.CreateAsync(client, "Available derived"));

        var edited = await EditAsync(client, id, $$$"""
            {"inputs":{
              "sources":[{"typeId":"{{{Mashup}}}","generation":"{{{present.Shortcode}}}"},{"typeId":"{{{Mashup}}}","external":{"sunoId":"never-imported"}}],
              "inspiration":{"sources":[{"generation":"{{{trashed.Shortcode}}}"},{"generation":"{{{gone.Shortcode}}}"},{"song":"{{{original.GetProperty("shortcode").GetString()}}}"}]}
            }}
            """);
        var sources = edited.GetProperty("inputs").GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(["ok", "not_imported"], sources.Select(static source => source.GetProperty("availability").GetString()));
        Assert.Equal("Minimal clip", sources[0].GetProperty("generation").GetProperty("title").GetString());
        Assert.Equal(187.5, sources[0].GetProperty("generation").GetProperty("durationSeconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, sources[1].GetProperty("external").GetProperty("label").ValueKind);
        var inspiration = edited.GetProperty("inputs").GetProperty("inspiration").GetProperty("sources").EnumerateArray();
        Assert.Equal(["trashed", "missing", "ok"], inspiration.Select(static source => source.GetProperty("availability").GetString()));

        // Deleting the Song makes each of them Deleted: a Generation with a Suno ID becomes a Suno clip
        // labelled so, and the Song itself is missing.
        var (title, revision) = (original.GetProperty("title").GetString()!, (await SongAsync(client, original.GetProperty("id").GetString()!)).GetProperty("revision").GetInt32());
        using (var deleted = await SendAsync(client, HttpMethod.Delete, SongApi.Song(original.GetProperty("shortcode").GetString()!), SongApi.Quoted(revision), $$$"""{"confirmTitle":{{{JsonSerializer.Serialize(title)}}}}"""))
        {
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        var read = (await GetAsync(client, id)).GetProperty("inputs");
        Assert.Equal(["deleted", "not_imported"], read.GetProperty("sources").EnumerateArray().Select(static source => source.GetProperty("availability").GetString()));
        Assert.All(read.GetProperty("inspiration").GetProperty("sources").EnumerateArray(), static source => Assert.Equal("deleted", source.GetProperty("availability").GetString()));
    }

    [Fact]
    public async Task APastedSunoIdThatIsImportedIsThatGenerationAndAnyOtherIsNotImported()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var original = await SongApi.CreateAsync(client, "Pasted original");
        var generation = await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("known-clip"));
        var song = await SongApi.CreateAsync(client, "Pasted cover");
        var id = VersionId(song);

        var edited = await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Cover}}}","external":{"sunoId":"known-clip"}}]}}""");
        var source = Assert.Single(edited.GetProperty("inputs").GetProperty("sources").EnumerateArray());
        Assert.False(source.TryGetProperty("external", out _));
        Assert.Equal(generation.Generation.Id, source.GetProperty("generation").GetProperty("id").GetGuid());
        Assert.Equal("ok", source.GetProperty("availability").GetString());

        // Complement: an ID n8Tracks does not have stays a Suno clip, Not imported.
        var unknown = await EditAsync(client, id, $$$"""{"inputs":{"sources":[{"typeId":"{{{Cover}}}","external":{"sunoId":"unknown-clip"}}]}}""");
        source = Assert.Single(unknown.GetProperty("inputs").GetProperty("sources").EnumerateArray());
        Assert.Equal("unknown-clip", source.GetProperty("external").GetProperty("sunoId").GetString());
        Assert.Equal("not_imported", source.GetProperty("availability").GetString());

        // Complement: a Suno clip the Version already names stays one when sent back, even once
        // n8Tracks has a Generation with that ID, so a read sent back is no change.
        await SongApi.AttachGenerationAsync(factory, VersionId(original).ToString(), Clips.Minimal("unknown-clip"));
        var read = await GetAsync(client, id);
        var resent = await EditAsync(client, id, new JsonObject { ["inputs"] = Lineage(read.GetProperty("inputs")) }.ToJsonString());
        Assert.Equal(read.GetProperty("revision").GetInt32(), resent.GetProperty("revision").GetInt32());
        Assert.Equal("unknown-clip", Assert.Single(resent.GetProperty("inputs").GetProperty("sources").EnumerateArray()).GetProperty("external").GetProperty("sunoId").GetString());
    }

    private static string Sources(string typeId) => $$$"""{"inputs":{"sources":[{"typeId":"{{{typeId}}}","external":{"sunoId":"typed"}}]}}""";

    /// <summary>The lineage keys of a read's <c>inputs</c>, as an edit sends them.</summary>
    private static JsonObject Lineage(JsonElement inputs)
    {
        var lineage = new JsonObject();
        foreach (var key in new[] { "sources", "inspiration", "voice", "fileInputs" })
        {
            lineage[key] = JsonNode.Parse(inputs.GetProperty(key).GetRawText());
        }

        return lineage;
    }

    /// <summary>The rule codes of a 422, by field.</summary>
    private static Dictionary<string, string[]> Rules(JsonElement problem)
    {
        Assert.Equal(ApiProblem.ValidationFailedCode, problem.GetProperty("code").GetString());
        return problem.GetProperty("rules").EnumerateObject()
            .ToDictionary(static field => field.Name, static field => field.Value.EnumerateArray().Select(static rule => rule.GetString()!).ToArray(), StringComparer.Ordinal);
    }

    private static Guid VersionId(JsonElement song) => song.GetProperty("currentVersion").GetProperty("id").GetGuid();

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> SongAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(SongApi.Song(reference));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>PATCHes the Version at its current revision.</summary>
    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, string json)
    {
        var revision = (await GetAsync(client, id)).GetProperty("revision").GetInt32();
        return await SendAsync(client, HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative), SongApi.Quoted(revision), json);
    }

    /// <summary>Edits the Version and returns the answer: the Version (200), or with <paramref name="expectFailure"/> the problem (422).</summary>
    private static async Task<JsonElement> EditAsync(HttpClient client, Guid id, string json, bool expectFailure = false)
    {
        using var response = await PatchAsync(client, id, json);
        var expected = expectFailure ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK;
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Creates a Version of the Song from <paramref name="source"/>, with the proposed number; its ID.</summary>
    private static async Task<Guid> CreateFromAsync(HttpClient client, string songShortcode, Guid source)
    {
        var options = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/versions/{source}/next-numbers", UriKind.Relative)));
        var number = options.GetProperty("options")[0].GetProperty("number").GetString();
        using var response = await SongApi.SendJsonAsync(
            client,
            HttpMethod.Post,
            new Uri($"/api/v1/songs/{songShortcode}/versions", UriKind.Relative),
            $$$"""{"sourceVersionId":"{{{source}}}","number":"{{{number}}}"}""");
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string ifMatch, string json)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        return await client.SendAsync(request);
    }
}
