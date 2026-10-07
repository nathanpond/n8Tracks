using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Assets;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Backups;
using n8Tracks.Api.Tests.Inventory;
using n8Tracks.Api.Tests.Invariants;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Generations;

/// <summary>
/// Creating a new Song from a Generation (#123): <c>POST /api/v1/generations/{reference}/move-to-new-song</c>,
/// session-only, under the Generation's revision. The Generation moves (never a copy) into the new
/// Song's Version 1, which holds a copy of its Version's creation inputs and sources, frozen; a
/// Derived From relationship is recorded; the old shortcode becomes a permanent alias that resolves as
/// <c>moved</c> and is never given to another Generation; all in one transaction.
/// </summary>
public sealed class GenerationMoveEndpointTests
{
    private const string Origin = "n8-1";

    [Fact]
    public async Task TheGenerationMovesWithEverythingAttachedIntoANewSongWhoseVersionOneCopiesItsInputs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var source = await OriginAsync(factory, client);
        var sourceInputs = VersionImmutabilityGuardTests.Stored(factory, source);
        var before = await GenerationAsync(client, "n8-1-v1-g2");

        // Everything the user and Suno attached to it.
        var revision = before.GetProperty("revision").GetInt32();
        using (var rated = await SendAsync(client, HttpMethod.Patch, "generations/n8-1-v1-g2", revision, """{"rating":4,"state":"archived"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, rated.StatusCode);
        }

        using (var commented = await SongApi.SendJsonAsync(client, HttpMethod.Post, Uri("generations/n8-1-v1-g2/comments"), """{"text":"Keeper"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, commented.StatusCode);
        }

        await ImageAsync(client, "n8-1-v1-g2");
        var attached = await GenerationAsync(client, "n8-1-v1-g2");

        var moved = await MoveAsync(client, "n8-1-v1-g2", attached.GetProperty("revision").GetInt32(), """{"title":"  Split out  "}""", HttpStatusCode.Created);

        // The new Song, its Version 1 frozen, and the Generation as its first.
        var song = moved.GetProperty("song");
        Assert.Equal("n8-2", song.GetProperty("shortcode").GetString());
        Assert.Equal("Split out", song.GetProperty("title").GetString());
        Assert.Equal("n8-2-v1", moved.GetProperty("version").GetProperty("shortcode").GetString());
        Assert.True(moved.GetProperty("version").GetProperty("isFrozen").GetBoolean());
        Assert.Equal("n8-1-v1-g2", moved.GetProperty("alias").GetString());
        var generation = moved.GetProperty("generation");
        Assert.Equal("n8-2-v1-g1", generation.GetProperty("shortcode").GetString());
        Assert.Equal(1, generation.GetProperty("ordinal").GetInt32());
        Assert.Equal(attached.GetProperty("id").GetString(), generation.GetProperty("id").GetString());

        // What moved with it, unchanged: rating, comments, image, states, Suno data; its revision is one up.
        foreach (var field in new[] { "rating", "comments", "artwork", "state", "remoteState", "sunoId", "title", "durationSeconds", "createdAt" })
        {
            Assert.Equal(attached.GetProperty(field).GetRawText(), generation.GetProperty(field).GetRawText());
        }

        Assert.Equal(attached.GetProperty("revision").GetInt32() + 1, generation.GetProperty("revision").GetInt32());
        Assert.Equal(SongApi.Quoted(generation.GetProperty("revision").GetInt32()), await MovedETagAsync(client, generation));

        // Version 1 is a copy of the source Version's creation inputs and sources, byte for byte; the
        // source Version's own are byte-identical to before.
        var copy = moved.GetProperty("version").GetProperty("id").GetGuid();
        Assert.Equal(sourceInputs, VersionImmutabilityGuardTests.Stored(factory, copy));
        Assert.Equal(sourceInputs, VersionImmutabilityGuardTests.Stored(factory, source));
        var copied = await SetupApi.JsonAsync(await client.GetAsync(Uri($"versions/{copy}")));
        Assert.Equal("Named source", copied.GetProperty("name").GetString());
        Assert.Contains("n8-1-v1", copied.GetProperty("notes").GetString(), StringComparison.Ordinal);
        Assert.Contains("n8-1-v1-g2", copied.GetProperty("notes").GetString(), StringComparison.Ordinal);

        // The original Version stays frozen and keeps its other Generation.
        var original = await SetupApi.JsonAsync(await client.GetAsync(Uri("versions/n8-1-v1")));
        Assert.True(original.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(["n8-1-v1-g1"], await ShortcodesAsync(client, "songs/n8-1/generations"));
        Assert.Equal(["n8-2-v1-g1"], await ShortcodesAsync(client, "songs/n8-2/generations"));
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT count(*) FROM generations WHERE id = '{Upper(generation.GetProperty("id").GetGuid())}';"));

        // The new Song copies nothing else, starts in the first state, and selects the moved Generation.
        var newSong = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-2")));
        Assert.Equal(0, newSong.GetProperty("genres").GetArrayLength());
        Assert.Equal(0, newSong.GetProperty("tags").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, newSong.GetProperty("credits").GetProperty("primary").ValueKind);
        Assert.Equal(0, newSong.GetProperty("playlists").GetArrayLength());
        Assert.Equal(DefaultWorkflowStates.All[0].Id, newSong.GetProperty("state").GetProperty("id").GetGuid());
        Assert.Equal("n8-2-v1-g1", newSong.GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
        Assert.Equal("selectedGeneration", newSong.GetProperty("artwork").GetProperty("source").GetString());

        // Derived From, from the new Song to the original, which reads it as Source of.
        var relation = Assert.Single(newSong.GetProperty("relationships").EnumerateArray());
        Assert.Equal(SystemRelationshipTypes.DerivedFrom.Id, relation.GetProperty("typeId").GetGuid());
        Assert.Equal("Derived From", relation.GetProperty("name").GetString());
        Assert.Equal(Origin, relation.GetProperty("song").GetProperty("shortcode").GetString());
        var originSong = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(Origin)));
        Assert.Equal("Source of", Assert.Single(originSong.GetProperty("relationships").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheOldShortcodeResolvesForGoodAsMovedAndIsNeverGivenToAnotherGeneration()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await MoveAsync(client, "n8-1-v1-g2", 1, """{"title":"Split out"}""", HttpStatusCode.Created);

        // The resolver, in any letter case, and every Generation endpoint, by the old shortcode.
        foreach (var reference in new[] { "n8-1-v1-g2", "N8-1-V1-G2" })
        {
            var resolved = await SetupApi.JsonAsync(await client.GetAsync(Uri($"resolve/{reference}")));
            Assert.Equal("generation", resolved.GetProperty("entityType").GetString());
            Assert.Equal("moved", resolved.GetProperty("status").GetString());
            Assert.Equal("n8-2-v1-g1", resolved.GetProperty("shortcode").GetString());
            Assert.Equal("n8-2-v1-g1", resolved.GetProperty("canonicalShortcode").GetString());
            Assert.Equal("n8-2", resolved.GetProperty("song").GetProperty("shortcode").GetString());
            Assert.Equal("n8-2-v1", resolved.GetProperty("version").GetProperty("shortcode").GetString());
        }

        Assert.Equal("n8-2-v1-g1", (await GenerationAsync(client, "n8-1-v1-g2")).GetProperty("shortcode").GetString());

        // The live shortcode resolves as itself, without canonicalShortcode.
        var live = await SetupApi.JsonAsync(await client.GetAsync(Uri("resolve/n8-2-v1-g1")));
        Assert.Equal("active", live.GetProperty("status").GetString());
        Assert.False(live.TryGetProperty("canonicalShortcode", out _));

        // Complement: the old Version's next Generation takes the next ordinal, not the moved one's.
        var next = await SongApi.AttachGenerationAsync(factory, "n8-1-v1");
        Assert.Equal("n8-1-v1-g3", next.Shortcode);
        Assert.Equal("moved", (await SetupApi.JsonAsync(await client.GetAsync(Uri("resolve/n8-1-v1-g2")))).GetProperty("status").GetString());

        // The database never gives the alias to another Generation, however it is written.
        var version = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM versions WHERE number = '1' AND song_id = (SELECT id FROM songs WHERE shortcode_number = 1);");
        var song = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM songs WHERE shortcode_number = 1;");
        var refused = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO generations (id, version_id, song_id, ordinal, created_utc) VALUES ('{Upper(Guid.CreateVersion7())}', '{version}', '{song}', 2, '2026-10-06T00:00:00.000Z');"));
        Assert.Contains("alias", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGenerationMovedAgainLeavesAnotherAliasAndEveryAliasLeadsToWhereItIsNow()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        var first = await MoveAsync(client, "n8-1-v1-g2", 1, """{"title":"First move"}""", HttpStatusCode.Created);

        // The moved Generation is its new Song's Selected Generation, so moving it again needs a choice;
        // the only other choice there is a state.
        var state = DefaultWorkflowStates.All[1].Id;
        var second = await MoveAsync(
            client,
            "n8-1-v1-g2",
            first.GetProperty("generation").GetProperty("revision").GetInt32(),
            $$"""{"title":"Second move","workflowState":"{{state}}"}""",
            HttpStatusCode.Created);
        Assert.Equal("n8-3-v1-g1", second.GetProperty("generation").GetProperty("shortcode").GetString());
        Assert.Equal("n8-2-v1-g1", second.GetProperty("alias").GetString());

        foreach (var alias in new[] { "n8-1-v1-g2", "n8-2-v1-g1" })
        {
            var resolved = await SetupApi.JsonAsync(await client.GetAsync(Uri($"resolve/{alias}")));
            Assert.Equal("moved", resolved.GetProperty("status").GetString());
            Assert.Equal("n8-3-v1-g1", resolved.GetProperty("canonicalShortcode").GetString());
        }

        Assert.Equal(["n8-1-v1-g2", "n8-2-v1-g1"], TestDatabase.Rows(factory.DataPath, "SELECT alias FROM shortcode_aliases ORDER BY alias;"));
        var left = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song("n8-2")));
        Assert.Equal(JsonValueKind.Null, left.GetProperty("selectedGeneration").ValueKind);
        Assert.Equal(state, left.GetProperty("state").GetProperty("id").GetGuid());
        Assert.Empty(await ShortcodesAsync(client, "songs/n8-2/generations"));
    }

    [Fact]
    public async Task TheSelectedGenerationMovesOnlyOnceTheUserSaysWhatItsSongSelectsInstead()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        var originRevision = (await SelectAsync(client, "n8-1-v1-g2")).GetProperty("revision").GetInt32();
        var originState = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(Origin)))).GetProperty("state").GetProperty("id").GetGuid();
        var before = RestoreApi.Fingerprint(factory.DataPath);

        // Without a choice: 422 selection_choice_required, and nothing changes.
        using (var refused = await SendAsync(client, HttpMethod.Post, "generations/n8-1-v1-g2/move-to-new-song", 1, """{"title":"Split out"}"""))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "selection_choice_required");
            Assert.Equal("n8-1-v1-g2", problem.GetProperty("shortcode").GetString());
        }

        // Both, the leaving Generation itself, another Song's, and an unknown state: 422, nothing changes.
        await SongApi.CreateAsync(client, "Elsewhere");
        var elsewhere = await SongApi.AttachGenerationAsync(factory, "n8-2-v1");
        before = RestoreApi.Fingerprint(factory.DataPath);
        foreach (var (body, field) in new[]
        {
            ($$"""{"title":"X","replacementGeneration":"n8-1-v1-g1","workflowState":"{{originState}}"}""", "replacementGeneration"),
            ("""{"title":"X","replacementGeneration":"n8-1-v1-g2"}""", "replacementGeneration"),
            ($$"""{"title":"X","replacementGeneration":"{{elsewhere.Shortcode}}"}""", "replacementGeneration"),
            ($$"""{"title":"X","workflowState":"{{Guid.CreateVersion7()}}"}""", "workflowState"),
            ("""{"title":"X","workflowState":"none"}""", "workflowState"),
        })
        {
            using var invalid = await SendAsync(client, HttpMethod.Post, "generations/n8-1-v1-g2/move-to-new-song", 1, body);
            var problem = await SetupApi.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));

        // With a replacement: the original Song selects it, its workflow state is unchanged, and the
        // moved Generation is the new Song's Selected Generation.
        var moved = await MoveAsync(client, "n8-1-v1-g2", 1, """{"title":"Split out","replacementGeneration":"n8-1-v1-g1"}""", HttpStatusCode.Created);
        var origin = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(Origin)));
        Assert.Equal("n8-1-v1-g1", origin.GetProperty("selectedGeneration").GetProperty("shortcode").GetString());
        Assert.Equal(originState, origin.GetProperty("state").GetProperty("id").GetGuid());
        Assert.True(origin.GetProperty("revision").GetInt32() > originRevision);
        Assert.Equal(moved.GetProperty("generation").GetProperty("id").GetString(), moved.GetProperty("song").GetProperty("selectedGeneration").GetProperty("id").GetString());
        Assert.True(moved.GetProperty("generation").GetProperty("isSelected").GetBoolean());

        // A choice for a Generation that is not selected is refused: nothing to replace.
        using var unwanted = await SendAsync(client, HttpMethod.Post, $"generations/{elsewhere.Shortcode}/move-to-new-song", 1, """{"title":"X","workflowState":"x"}""");
        var unwantedProblem = await SetupApi.ProblemAsync(unwanted, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.True(unwantedProblem.GetProperty("errors").TryGetProperty("workflowState", out _));
    }

    [Fact]
    public async Task WithAWorkflowStateTheOriginalSongHasNoSelectedGenerationAndMovesToThatState()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await SelectAsync(client, "n8-1-v1-g2");
        var state = DefaultWorkflowStates.All[3].Id;

        await MoveAsync(client, "n8-1-v1-g2", 1, $$"""{"title":"Split out","workflowState":"{{state}}"}""", HttpStatusCode.Created);

        var origin = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(Origin)));
        Assert.Equal(JsonValueKind.Null, origin.GetProperty("selectedGeneration").ValueKind);
        Assert.Equal(state, origin.GetProperty("state").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AnUnknownGenerationAStaleOrMissingRevisionABadTitleAndABearerTokenAreRefusedAndNothingChanges()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        var before = RestoreApi.Fingerprint(factory.DataPath, "credentials");

        using (var unknown = await SendAsync(client, HttpMethod.Post, "generations/n8-1-v1-g9/move-to-new-song", 1, """{"title":"X"}"""))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, "not_found");
        }

        using (var stale = await SendAsync(client, HttpMethod.Post, "generations/n8-1-v1-g2/move-to-new-song", 7, """{"title":"X"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal("n8-1-v1-g2", problem.GetProperty("current").GetProperty("shortcode").GetString());
        }

        using (var missing = await SendAsync(client, HttpMethod.Post, "generations/n8-1-v1-g2/move-to-new-song", null, """{"title":"X"}"""))
        {
            await SetupApi.ProblemAsync(missing, (HttpStatusCode)428, "revision_required");
        }

        foreach (var body in new[] { """{"title":"   "}""", "{}", """{"title":42}""", """{"title":"X","replacementGeneration":7}""" })
        {
            using var invalid = await SendAsync(client, HttpMethod.Post, "generations/n8-1-v1-g2/move-to-new-song", 1, body);
            await SetupApi.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        // Session-only: a token with every scope is refused.
        var token = await CredentialApi.CreateTokenAsync(factory, [.. n8Tracks.Application.Credentials.CredentialScopes.All]);
        using (var bearer = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Post, new Uri("/api/v1/generations/n8-1-v1-g2/move-to-new-song", UriKind.Relative), token))
        {
            await SetupApi.ProblemAsync(bearer, HttpStatusCode.Forbidden, "session_required");
        }

        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath, "credentials"));
    }

    /// <summary>
    /// Atomicity: a failure after the new Song, its Version, and the relationship are written, at the
    /// move itself, leaves every table as it was. The Generation is never in two Songs or in none.
    /// </summary>
    [Fact]
    public async Task AFailureAfterTheSongIsCreatedChangesNothing()
    {
        using var factory = new N8TracksApiFactory { TestServices = FailingMoves.Install };
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await SelectAsync(client, "n8-1-v1-g2");
        var before = RestoreApi.Fingerprint(factory.DataPath);

        using (var failed = await SendAsync(client, HttpMethod.Post, "generations/n8-1-v1-g2/move-to-new-song", 1, """{"title":"Split out","replacementGeneration":"n8-1-v1-g1"}"""))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }

        Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));
        Assert.Equal(["n8-1-v1-g1", "n8-1-v1-g2"], await ShortcodesAsync(client, "songs/n8-1/generations"));

        // The next Song still takes the next number: the failed one's was given back.
        Assert.Equal("n8-2", (await SongApi.CreateAsync(client, "Next")).GetProperty("shortcode").GetString());
    }

    /// <summary>
    /// The database's own layer (D2): a Generation's Version, Song, or ordinal changes only by a move
    /// that leaves an alias, to the newest ordinal of a frozen Version of the Song it names.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRefusesAnyOtherChangeOfAGenerationsPlace()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await SongApi.CreateAsync(client, "Target");
        await SongApi.AttachGenerationAsync(factory, "n8-2-v1");
        var g2 = Upper((await GenerationAsync(client, "n8-1-v1-g2")).GetProperty("id").GetGuid());
        var target = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM versions WHERE song_id = (SELECT id FROM songs WHERE shortcode_number = 2);");
        var targetSong = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM songs WHERE shortcode_number = 2;");
        var originSong = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM songs WHERE shortcode_number = 1;");
        string Move(string song, int ordinal) => $"UPDATE generations SET version_id = '{target}', song_id = '{song}', ordinal = {ordinal} WHERE id = '{g2}';";
        void Refused(string sql)
        {
            var refused = Assert.Throws<SqliteException>(() => TestDatabase.Execute(factory.DataPath, sql));
            Assert.Equal(19, refused.SqliteErrorCode);
        }

        // Within its Version, its ordinal never changes; nor its Song alone; nor a move to an ordinal the target has not given.
        Refused("UPDATE generations SET ordinal = ordinal + 10;");
        Refused($"UPDATE generations SET song_id = '{targetSong}' WHERE id = '{g2}';");
        Refused(Move(targetSong, 2));

        // The target gives its next ordinal: still refused while the old shortcode is not its alias.
        TestDatabase.Execute(factory.DataPath, $"UPDATE versions SET last_generation_ordinal = 2 WHERE id = '{target}';");
        Refused(Move(targetSong, 2));

        // With the alias: refused into a Song that is not the Version's, or at an ordinal not the newest.
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO shortcode_aliases (alias, generation_id, created_utc) VALUES ('n8-1-v1-g2', '{g2}', '2026-10-06T00:00:00.000Z');");
        Refused(Move(originSong, 2));
        Refused(Move(targetSong, 3));

        // Never into a Version that is not frozen (invariant 1): its inputs could still change under
        // the Generation. A mutable Version of a third Song, giving its next ordinal, is still refused.
        await SongApi.CreateAsync(client, "Mutable");
        var mutable = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM versions WHERE song_id = (SELECT id FROM songs WHERE shortcode_number = 3);");
        var mutableSong = TestDatabase.Scalar(factory.DataPath, "SELECT id FROM songs WHERE shortcode_number = 3;");
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, $"SELECT is_frozen FROM versions WHERE id = '{mutable}';"));
        TestDatabase.Execute(factory.DataPath, $"UPDATE versions SET last_generation_ordinal = 1 WHERE id = '{mutable}';");
        Refused($"UPDATE generations SET version_id = '{mutable}', song_id = '{mutableSong}', ordinal = 1 WHERE id = '{g2}';");

        // Never onto a shortcode that is another Generation's alias (here one whose Generation is
        // gone): while the target's next shortcode is reserved, the move is refused.
        TestDatabase.Execute(factory.DataPath, $"INSERT INTO shortcode_aliases (alias, generation_id, created_utc) VALUES ('n8-2-v1-g2', '{Upper(Guid.CreateVersion7())}', '2026-10-06T00:00:00.000Z');");
        Refused(Move(targetSong, 2));
        TestDatabase.Execute(factory.DataPath, "DELETE FROM shortcode_aliases WHERE alias = 'n8-2-v1-g2';");

        // Complement: with the alias recorded and the target's next ordinal given, the move goes through.
        TestDatabase.Execute(factory.DataPath, Move(targetSong, 2));
        Assert.Equal("n8-2-v1-g2", (await GenerationAsync(client, "n8-1-v1-g2")).GetProperty("shortcode").GetString());
    }

    [Fact]
    public void ReceivingAGenerationGivesItTheNextOrdinalAndFreezesTheVersionWithoutTouchingAnInput()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var target = new SongVersion(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1.1", "Name", "Notes", VersionVisibility.Active, "Lyrics", "Styles",
            InputValues.Defaults(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Revision: 3, LineageValues.Held, LastGenerationOrdinal: 4);
        var generation = new Generation(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 2, DateTimeOffset.UnixEpoch)
        {
            Rating = 5,
            State = GenerationState.Archived,
            Revision = 6,
        };

        var (frozen, moved) = target.ReceiveGeneration(generation, now);

        Assert.True(frozen.IsFrozen);
        Assert.Equal(5, frozen.LastGenerationOrdinal);
        Assert.Equal(4, frozen.Revision);
        Assert.Equal(now, frozen.UpdatedUtc);
        Assert.Equal((target.Lyrics, target.Styles, target.Inputs, target.Lineage), (frozen.Lyrics, frozen.Styles, frozen.Inputs, frozen.Lineage));
        Assert.Equal((target.Id, target.SongId, 5, 7), (moved.VersionId, moved.SongId, moved.Ordinal, moved.Revision));
        Assert.Equal((generation.Id, generation.Rating, generation.State, generation.CreatedUtc), (moved.Id, moved.Rating, moved.State, moved.CreatedUtc));
        Assert.Throws<ArgumentException>(() => frozen.ReceiveGeneration(moved, now));

        Assert.Equal("n8-1-v1-g2", ShortcodeAlias.Leaving("N8-1-V1-G2", generation.Id, now).Alias);
    }

    /// <summary>
    /// Song <c>n8-1</c>: Version 1 named "Named source", with lyrics, styles, and a full lineage, and
    /// two Generations with Suno data (<c>-g1</c>, <c>-g2</c>); the Version's ID.
    /// </summary>
    private static async Task<Guid> OriginAsync(N8TracksApiFactory factory, HttpClient client)
    {
        var song = await SongApi.CreateAsync(client, "Origin");
        var id = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        using (var written = await SendAsync(client, HttpMethod.Patch, $"versions/{id}", 1, """{"name":"Named source","lyrics":"[Verse]\nMoved","styles":"kept"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        }

        using (var lineage = await SendAsync(client, HttpMethod.Patch, $"versions/{id}", 2, $$"""{"inputs":{{LineageValues.InitialInputsJson("move")}}}"""))
        {
            Assert.True(lineage.StatusCode == HttpStatusCode.OK, await lineage.Content.ReadAsStringAsync());
        }

        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("clip-one"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("clip-two"));
        return id;
    }

    private static async Task<JsonElement> MoveAsync(HttpClient client, string reference, int revision, string body, HttpStatusCode expected)
    {
        using var response = await SendAsync(client, HttpMethod.Post, $"generations/{reference}/move-to-new-song", revision, body);
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>The ETag a read of the moved Generation answers.</summary>
    private static async Task<string?> MovedETagAsync(HttpClient client, JsonElement generation)
    {
        using var response = await client.GetAsync(Uri($"generations/{generation.GetProperty("id").GetString()}"));
        return response.Headers.ETag?.Tag;
    }

    private static async Task<JsonElement> SelectAsync(HttpClient client, string generation)
    {
        var revision = (await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(Origin)))).GetProperty("revision").GetInt32();
        using var response = await SendAsync(client, HttpMethod.Put, $"songs/{Origin}/selected-generation", revision, $$"""{"generation":"{{generation}}"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task ImageAsync(HttpClient client, string generation)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(ArtworkImages.Solid(SkiaSharp.SKEncodedImageFormat.Png, 64, 64, ArtworkImages.Red)), "file", "cover.png");
        using var request = new HttpRequestMessage(HttpMethod.Put, Uri($"generations/{generation}/artwork")) { Content = form };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> GenerationAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(Uri($"generations/{reference}"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<List<string>> ShortcodesAsync(HttpClient client, string path) =>
        [.. (await SetupApi.JsonAsync(await client.GetAsync(Uri(path)))).GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("shortcode").GetString()!)];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int? revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, Uri(path));
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

    private static Uri Uri(string path) => new($"/api/v1/{path}", UriKind.Relative);

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    /// <summary>A Version store whose move always fails, after everything before it was written.</summary>
    public class FailingMoves : DispatchProxy
    {
        private object? inner;

        /// <summary>Replaces the registered Version store with one that fails at the move.</summary>
        public static void Install(IServiceCollection services)
        {
            var registered = services.Single(static descriptor => descriptor.ServiceType == typeof(IVersionStore));
            var implementation = registered.ImplementationType!;
            services.RemoveAll<IVersionStore>();
            services.AddScoped(provider =>
            {
                var proxy = Create<IVersionStore, FailingMoves>();
                ((FailingMoves)(object)proxy).inner = ActivatorUtilities.CreateInstance(provider, implementation);
                return proxy;
            });
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IVersionStore.TryMoveGenerationAsync))
            {
                throw new InvalidOperationException("The move fails, as the test asks.");
            }

            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(invocation.InnerException).Throw();
                throw;
            }
        }
    }
}
