using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Catalog;
using SkiaSharp;
using static n8Tracks.Api.Tests.Assets.ArtworkApi;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// Relating Songs: the relationship types (<c>/api/v1/relationship-types</c>; the nine system types
/// cannot be renamed or deleted, the user's own are managed in a signed-in session) and a Song's
/// relationships (<c>POST/DELETE /api/v1/songs/{reference}/relationships</c>, <c>songs.write</c>),
/// embedded in the Song and read from each Song in its own direction. Relating and unrelating move
/// both Songs' last-updated times, never their revisions.
/// </summary>
public sealed class RelationshipEndpointTests
{
    private static readonly Uri Types = new("/api/v1/relationship-types", UriKind.Relative);

    [Fact]
    public async Task TheNineSystemTypesAreListedFirstThenTheUsersAlphabetically()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var list = await ListAsync(client);

        Assert.Equal(
            [
                "Cover/Covered by/cover", "Extend/Extended by/extend", "Reuse Prompt/Prompt reused by/reuse_prompt",
                "Mashup/Used in mashup/mashup", "Sample This Song/Sampled by/sample", "Use as Inspiration/Inspired/inspiration",
                "Voice/Voice used by/voice", "Remix/Remixed by/", "Derived From/Source of/",
            ],
            list.Select(static type => $"{type.GetProperty("name").GetString()}/{type.GetProperty("reverseName").GetString()}/{type.GetProperty("sunoAction").GetString()}"));
        Assert.All(list, static type =>
        {
            Assert.True(type.GetProperty("system").GetBoolean());
            Assert.False(type.GetProperty("symmetric").GetBoolean());
            Assert.Equal(0, type.GetProperty("relationshipCount").GetInt32());
            Assert.Equal(1, type.GetProperty("revision").GetInt32());
        });
        Assert.Equal(SystemRelationshipTypes.Cover.Id, list[0].GetProperty("id").GetGuid());

        await CreateTypeAsync(client, "sequel to", "Has sequel");
        await CreateTypeAsync(client, "Answer to", "Answered by");
        var after = await ListAsync(client);
        Assert.Equal(11, after.Count);
        Assert.Equal(["Answer to", "sequel to"], after.Skip(9).Select(static type => type.GetProperty("name").GetString()));
        Assert.All(after.Skip(9), static type => Assert.False(type.GetProperty("system").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, after[9].GetProperty("sunoAction").ValueKind);
    }

    [Fact]
    public async Task ATypeIsCreatedWithBothNamesAndASymmetricOneHasOneName()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var created = await SendAsync(client, HttpMethod.Post, Types, null, """{"name":"  Sequel   to ","reverseName":"Has sequel"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Contains("no-store", created.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            var type = await SetupApi.JsonAsync(created);
            Assert.Equal("Sequel to", type.GetProperty("name").GetString());
            Assert.Equal("Has sequel", type.GetProperty("reverseName").GetString());
            Assert.False(type.GetProperty("system").GetBoolean());
            Assert.False(type.GetProperty("symmetric").GetBoolean());
            Assert.Equal(1, type.GetProperty("revision").GetInt32());
            Assert.EndsWith($"/api/v1/relationship-types/{type.GetProperty("id").GetString()}", created.Headers.Location?.ToString(), StringComparison.Ordinal);
        }

        // A reverse name equal to the name, ignoring case, makes a symmetric type, stored with one name.
        var sibling = await CreateTypeAsync(client, "Sibling of", "SIBLING OF");
        Assert.True(sibling.GetProperty("symmetric").GetBoolean());
        Assert.Equal("Sibling of", sibling.GetProperty("reverseName").GetString());
        Assert.Equal(
            "Sibling of|SIBLING OF|Sibling of|SIBLING OF|0|",
            TestDatabase.Scalar(factory.DataPath, "SELECT name || '|' || name_key || '|' || reverse_name || '|' || reverse_name_key || '|' || is_system || '|' || ifnull(suno_action, '') FROM song_relationship_types WHERE name = 'Sibling of';"));
    }

    [Fact]
    public async Task ANameIsRefusedWhenWrongOrUsedByAnyTypeInEitherDirection()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await CreateTypeAsync(client, "Sequel to", "Has sequel");

        foreach (var (body, field) in new[]
        {
            ("{}", "name"),
            ("""{"name":"Prequel to"}""", "reverseName"),
            ("""{"name":"  ","reverseName":"Has prequel"}""", "name"),
            ("""{"name":42,"reverseName":"Has prequel"}""", "name"),
            ($$"""{"name":"Prequel to","reverseName":"{{new string('a', 51)}}"}""", "reverseName"),
            ("""{"name":"cover","reverseName":"Has cover"}""", "name"),
            ("""{"name":"Uses","reverseName":"covered BY"}""", "reverseName"),
            ("""{"name":"has SEQUEL","reverseName":"Is followed by"}""", "name"),
            ("""{"name":"Follows","reverseName":"sequel to"}""", "reverseName"),
            ("""{"name":"Source of","reverseName":"Source of"}""", "name"),
        })
        {
            using var wrong = await SendAsync(client, HttpMethod.Post, Types, null, body);
            var problem = await SetupApi.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        Assert.Equal("10", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_relationship_types;"));
    }

    [Fact]
    public async Task SystemTypesRefuseRenameAndDelete()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        foreach (var system in SystemRelationshipTypes.All)
        {
            using var renamed = await SendAsync(client, HttpMethod.Patch, TypeUri(system.Id.ToString()), 1, """{"name":"Mine now"}""");
            var problem = await SetupApi.ProblemAsync(renamed, HttpStatusCode.Conflict, "system_type");
            Assert.Equal(system.Name, problem.GetProperty("current").GetProperty("name").GetString());

            using var deleted = await SendAsync(client, HttpMethod.Delete, TypeUri(system.Id.ToString()), 1, null);
            await SetupApi.ProblemAsync(deleted, HttpStatusCode.Conflict, "system_type");

            using var removing = await SendAsync(client, HttpMethod.Delete, new Uri($"{TypeUri(system.Id.ToString())}?removeRelationships=true", UriKind.Relative), 1, null);
            await SetupApi.ProblemAsync(removing, HttpStatusCode.Conflict, "system_type");
        }

        Assert.Equal("9|9", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) || '|' || sum(revision) FROM song_relationship_types WHERE is_system = 1;"));
        Assert.Equal(["Cover", "Extend", "Reuse Prompt", "Mashup", "Sample This Song", "Use as Inspiration", "Voice", "Remix", "Derived From"], (await ListAsync(client)).Select(static type => type.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task RenamingAUserTypeChangesItEverywhereAndMovesNoSong()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = (await CreateTypeAsync(client, "Sequal to", "Has sequal")).GetProperty("id").GetString()!;
        var (a, b) = (await SongApi.CreateAsync(client, "Song A"), await SongApi.CreateAsync(client, "Song B"));
        var related = await RelateAsync(client, Id(a), type, "forward", Id(b));
        clock.Advance(TimeSpan.FromMinutes(5));

        using var renamed = await SendAsync(client, HttpMethod.Patch, TypeUri(type), 1, """{"name":"Sequel to","reverseName":"Has sequel"}""");

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("\"2\"", renamed.Headers.ETag?.ToString());
        var answer = await SetupApi.JsonAsync(renamed);
        Assert.Equal("Sequel to/Has sequel/1/2", $"{answer.GetProperty("name").GetString()}/{answer.GetProperty("reverseName").GetString()}/{answer.GetProperty("relationshipCount").GetInt32()}/{answer.GetProperty("revision").GetInt32()}");

        var songA = await SongAsync(client, Id(a));
        var songB = await SongAsync(client, Id(b));
        Assert.Equal("Sequel to: Song B", Single(songA));
        Assert.Equal("Has sequel: Song A", Single(songB));
        Assert.Equal(related.GetProperty("updatedAt").GetString(), songA.GetProperty("updatedAt").GetString());

        // Only the name sent changes; the same names again are no change and keep the revision.
        using (var reverseOnly = await SendAsync(client, HttpMethod.Patch, TypeUri(type), 2, """{"reverseName":"Followed by"}"""))
        {
            var body = await SetupApi.JsonAsync(reverseOnly);
            Assert.Equal("Sequel to/Followed by/3", $"{body.GetProperty("name").GetString()}/{body.GetProperty("reverseName").GetString()}/{body.GetProperty("revision").GetInt32()}");
        }

        using (var same = await SendAsync(client, HttpMethod.Patch, TypeUri(type), 3, """{"name":"Sequel to","reverseName":"Followed by"}"""))
        {
            Assert.Equal(3, (await SetupApi.JsonAsync(same)).GetProperty("revision").GetInt32());
        }

        // A symmetric type renamed without a reverse name stays symmetric.
        var sibling = (await CreateTypeAsync(client, "Sibling of", "Sibling of")).GetProperty("id").GetString()!;
        using (var symmetric = await SendAsync(client, HttpMethod.Patch, TypeUri(sibling), 1, """{"name":"Twin of"}"""))
        {
            var body = await SetupApi.JsonAsync(symmetric);
            Assert.Equal("Twin of/Twin of/True", $"{body.GetProperty("name").GetString()}/{body.GetProperty("reverseName").GetString()}/{body.GetProperty("symmetric").GetBoolean()}");
        }

        // Its own name is no clash; another type's name, either direction, is.
        using (var taken = await SendAsync(client, HttpMethod.Patch, TypeUri(sibling), 2, """{"name":"followed BY"}"""))
        {
            var problem = await SetupApi.ProblemAsync(taken, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));
        }

        using (var stale = await SendAsync(client, HttpMethod.Patch, TypeUri(type), 1, """{"name":"Late"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal(3, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var missing = await SendAsync(client, HttpMethod.Patch, TypeUri(Guid.CreateVersion7().ToString()), 1, """{"name":"Late"}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }

        using (var unrevisioned = await SendAsync(client, HttpMethod.Patch, TypeUri(type), null, """{"name":"Late"}"""))
        {
            await SetupApi.ProblemAsync(unrevisioned, (HttpStatusCode)428, "revision_required");
        }

        using (var empty = await SendAsync(client, HttpMethod.Patch, TypeUri(type), 3, "{}"))
        {
            await SetupApi.ProblemAsync(empty, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }
    }

    [Fact]
    public async Task ARelationshipShowsOnBothSongsEachInItsOwnDirection()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = (await CreateTypeAsync(client, "Sequel to", "Has sequel")).GetProperty("id").GetString()!;
        var (a, b) = (await SongApi.CreateAsync(client, "Song A"), await SongApi.CreateAsync(client, "Song B"));

        // A has a Generation and its own artwork; B has neither.
        await SongApi.AttachGenerationAsync(factory, a.GetProperty("currentVersion").GetProperty("shortcode").GetString()!);
        var asset = await StoreAsync(client, Assets.ArtworkImages.Halves(SKEncodedImageFormat.Png, 400, 200));
        a = await SongApi.EditAsync(client, Id(a), (await SongAsync(client, Id(a))).GetProperty("revision").GetInt32(), $$"""{"artworkAssetId":"{{IdOf(asset)}}"}""");
        var shared = (string[])["versions", "generations", "artwork_attachments", "assets"];
        var rowsBefore = shared.ToDictionary(static table => table, table => Dump(factory, table), StringComparer.Ordinal);
        clock.Advance(TimeSpan.FromMinutes(5));

        using var response = await SongApi.SendJsonAsync(
            client,
            HttpMethod.Post,
            Relationships(a.GetProperty("shortcode").GetString()!),
            $$"""{"typeId":"{{type}}","direction":"forward","otherSong":"{{b.GetProperty("shortcode").GetString()}}"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var songA = await SetupApi.JsonAsync(response);
        var relationship = Assert.Single(songA.GetProperty("relationships").EnumerateArray());
        Assert.Equal(type, relationship.GetProperty("typeId").GetString());
        Assert.Equal("Sequel to", relationship.GetProperty("name").GetString());
        Assert.Equal("forward", relationship.GetProperty("direction").GetString());
        Assert.Equal(Id(b), relationship.GetProperty("song").GetProperty("id").GetString());
        Assert.Equal(b.GetProperty("shortcode").GetString(), relationship.GetProperty("song").GetProperty("shortcode").GetString());
        Assert.Equal("Song B", relationship.GetProperty("song").GetProperty("title").GetString());
        Assert.EndsWith($"/relationships/{relationship.GetProperty("id").GetString()}", response.Headers.Location?.ToString(), StringComparison.Ordinal);

        var songB = await SongAsync(client, Id(b));
        var reverse = Assert.Single(songB.GetProperty("relationships").EnumerateArray());
        Assert.Equal(relationship.GetProperty("id").GetString(), reverse.GetProperty("id").GetString());
        Assert.Equal("Has sequel", reverse.GetProperty("name").GetString());
        Assert.Equal("reverse", reverse.GetProperty("direction").GetString());
        Assert.Equal(Id(a), reverse.GetProperty("song").GetProperty("id").GetString());

        // Both Songs' last-updated times moved; neither revision did.
        foreach (var (before, after) in new[] { (a, songA), (b, songB) })
        {
            Assert.Equal(before.GetProperty("revision").GetInt32(), after.GetProperty("revision").GetInt32());
            Assert.NotEqual(before.GetProperty("updatedAt").GetString(), after.GetProperty("updatedAt").GetString());
        }

        // Stored the way the forward name reads, and no Version, Generation, or Song row is shared.
        Assert.Equal($"{Id(a)}|{Id(b)}".ToUpperInvariant(), TestDatabase.Scalar(factory.DataPath, "SELECT upper(from_song_id) || '|' || upper(to_song_id) FROM song_relationships;"));
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(DISTINCT song_id) FROM versions;"));

        // Nor are Generations or artwork (#307): their rows are as they were, all of them still A's, and
        // B has no artwork and an unfrozen Version.
        foreach (var table in shared)
        {
            Assert.Equal(rowsBefore[table], Dump(factory, table));
        }

        Assert.Equal(
            Id(a).ToUpperInvariant(),
            TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(DISTINCT upper(v.song_id)) FROM generations g JOIN versions v ON v.id = g.version_id;"));
        Assert.Equal($"song|{Id(a)}".ToUpperInvariant(), TestDatabase.Scalar(factory.DataPath, "SELECT upper(group_concat(owner_type || '|' || owner_id)) FROM artwork_attachments;"));
        Assert.Equal(a.GetProperty("artwork").ToString(), songA.GetProperty("artwork").ToString());
        Assert.Equal(JsonValueKind.Null, songB.GetProperty("artwork").ValueKind);
        var versionsOfB = await SetupApi.JsonAsync(await client.GetAsync(new Uri($"/api/v1/songs/{Id(b)}/versions", UriKind.Relative)));
        Assert.False(Assert.Single(versionsOfB.GetProperty("items").EnumerateArray()).GetProperty("isFrozen").GetBoolean());

        // Related from C in the reverse direction (C "Has sequel" A), A reads it forward, after B by title.
        var c = await SongApi.CreateAsync(client, "Song C");
        var fromC = await RelateAsync(client, Id(c), type, "reverse", Id(a));
        Assert.Equal("Has sequel: Song A", Single(fromC));
        Assert.Equal(["Sequel to: Song B", "Sequel to: Song C"], Labels(await SongAsync(client, Id(a))));
    }

    [Fact]
    public async Task ASelfRelationshipADuplicateEitherWayOrAWrongFieldIsRefused()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = (await CreateTypeAsync(client, "Sequel to", "Has sequel")).GetProperty("id").GetString()!;
        var (a, b) = (await SongApi.CreateAsync(client, "Song A"), await SongApi.CreateAsync(client, "Song B"));
        await RelateAsync(client, Id(a), type, "forward", Id(b));

        // The pair is unordered: B "Sequel to" A, or A "Has sequel" B again, is the same pair under the same type.
        foreach (var (from, direction, other) in new[] { (Id(b), "forward", Id(a)), (Id(a), "forward", Id(b)), (Id(b), "reverse", Id(a)) })
        {
            using var duplicate = await SongApi.SendJsonAsync(client, HttpMethod.Post, Relationships(from), $$"""{"typeId":"{{type}}","direction":"{{direction}}","otherSong":"{{other}}"}""");
            var problem = await SetupApi.ProblemAsync(duplicate, HttpStatusCode.Conflict, "relationship_exists");
            Assert.Equal(from, problem.GetProperty("current").GetProperty("id").GetString());
        }

        // Under another type, the same pair may be related again.
        await RelateAsync(client, Id(b), SystemRelationshipTypes.Cover.Id.ToString(), "forward", Id(a));

        foreach (var (body, field) in new[]
        {
            ($$"""{"typeId":"{{type}}","direction":"forward","otherSong":"{{a.GetProperty("shortcode").GetString()}}"}""", "otherSong"),
            ($$"""{"typeId":"{{type}}","direction":"forward","otherSong":"{{Id(a)}}"}""", "otherSong"),
            ($$"""{"typeId":"{{type}}","direction":"forward","otherSong":"n8-99"}""", "otherSong"),
            ($$"""{"typeId":"{{type}}","direction":"forward","otherSong":"n8-2-v1"}""", "otherSong"),
            ($$"""{"typeId":"{{type}}","direction":"forward"}""", "otherSong"),
            ($$"""{"typeId":"{{Guid.CreateVersion7()}}","direction":"forward","otherSong":"n8-2"}""", "typeId"),
            ("""{"typeId":"cover","direction":"forward","otherSong":"n8-2"}""", "typeId"),
            ($$"""{"typeId":"{{type}}","direction":"sideways","otherSong":"n8-2"}""", "direction"),
            ($$"""{"typeId":"{{type}}","otherSong":"n8-2"}""", "direction"),
            ($$"""{"typeId":"{{type}}","direction":1,"otherSong":"n8-2"}""", "direction"),
        })
        {
            using var wrong = await SongApi.SendJsonAsync(client, HttpMethod.Post, Relationships(Id(a)), body);
            var problem = await SetupApi.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        using (var noSong = await SongApi.SendJsonAsync(client, HttpMethod.Post, Relationships("n8-99"), $$"""{"typeId":"{{type}}","direction":"forward","otherSong":"n8-1"}"""))
        {
            await SetupApi.ProblemAsync(noSong, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_relationships;"));
    }

    [Fact]
    public async Task TheDatabaseRefusesASelfRelationshipAndTheSamePairEitherWayRound()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var (a, b) = (await SongApi.CreateAsync(client, "Song A"), await SongApi.CreateAsync(client, "Song B"));
        await RelateAsync(client, Id(a), SystemRelationshipTypes.Cover.Id.ToString(), "forward", Id(b));
        var cover = SystemRelationshipTypes.Cover.Id.ToString().ToUpperInvariant();
        var (songA, songB) = (Id(a).ToUpperInvariant(), Id(b).ToUpperInvariant());

        var reversed = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO song_relationships (id, type_id, from_song_id, to_song_id, created_utc) VALUES ('{Guid.CreateVersion7().ToString().ToUpperInvariant()}', '{cover}', '{songB}', '{songA}', '2026-10-01T09:00:00.000Z');"));
        Assert.Contains("UNIQUE", reversed.Message, StringComparison.Ordinal);
        var self = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO song_relationships (id, type_id, from_song_id, to_song_id, created_utc) VALUES ('{Guid.CreateVersion7().ToString().ToUpperInvariant()}', '{cover}', '{songA}', '{songA}', '2026-10-01T09:00:00.000Z');"));
        Assert.Contains("CHECK", self.Message, StringComparison.Ordinal);

        // Complement: another type for the same pair is stored.
        TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO song_relationships (id, type_id, from_song_id, to_song_id, created_utc) VALUES ('{Guid.CreateVersion7().ToString().ToUpperInvariant()}', '{SystemRelationshipTypes.Remix.Id.ToString().ToUpperInvariant()}', '{songB}', '{songA}', '2026-10-01T09:00:00.000Z');");
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_relationships;"));
    }

    [Fact]
    public async Task ARelationshipIsRemovedFromEitherSongAndMovesBoth()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = (await CreateTypeAsync(client, "Sequel to", "Has sequel")).GetProperty("id").GetString()!;
        var (a, b, c) = (await SongApi.CreateAsync(client, "Song A"), await SongApi.CreateAsync(client, "Song B"), await SongApi.CreateAsync(client, "Song C"));
        var related = await RelateAsync(client, Id(a), type, "forward", Id(b));
        var relationship = related.GetProperty("relationships")[0].GetProperty("id").GetString()!;

        // A Song that is not one of its two has no such relationship; an unknown one is not found either.
        using (var otherSong = await SendAsync(client, HttpMethod.Delete, new Uri($"/api/v1/songs/{Id(c)}/relationships/{relationship}", UriKind.Relative), null, null))
        {
            await SetupApi.ProblemAsync(otherSong, HttpStatusCode.NotFound, "not_found");
        }

        using (var unknown = await SendAsync(client, HttpMethod.Delete, new Uri($"/api/v1/songs/{Id(b)}/relationships/{Guid.CreateVersion7()}", UriKind.Relative), null, null))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, "not_found");
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        using var removed = await SendAsync(client, HttpMethod.Delete, new Uri($"/api/v1/songs/{b.GetProperty("shortcode").GetString()}/relationships/{relationship}", UriKind.Relative), null, null);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var songB = await SetupApi.JsonAsync(removed);
        Assert.Empty(songB.GetProperty("relationships").EnumerateArray());
        var songA = await SongAsync(client, Id(a));
        Assert.Empty(songA.GetProperty("relationships").EnumerateArray());
        Assert.NotEqual(related.GetProperty("updatedAt").GetString(), songA.GetProperty("updatedAt").GetString());
        Assert.Equal(related.GetProperty("revision").GetInt32(), songA.GetProperty("revision").GetInt32());
        Assert.Equal(b.GetProperty("revision").GetInt32(), songB.GetProperty("revision").GetInt32());
        Assert.Equal(songA.GetProperty("updatedAt").GetString(), songB.GetProperty("updatedAt").GetString());
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM song_relationships;"));
    }

    [Fact]
    public async Task DeletingATypeInUseNeedsConfirmationAndRemovesExactlyItsRelationships()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = (await CreateTypeAsync(client, "Sequel to", "Has sequel")).GetProperty("id").GetString()!;
        var unused = (await CreateTypeAsync(client, "Answer to", "Answered by")).GetProperty("id").GetString()!;
        var (a, b, c) = (await SongApi.CreateAsync(client, "Song A"), await SongApi.CreateAsync(client, "Song B"), await SongApi.CreateAsync(client, "Song C"));
        await RelateAsync(client, Id(a), type, "forward", Id(b));
        await RelateAsync(client, Id(b), type, "forward", Id(c));
        var kept = await RelateAsync(client, Id(a), SystemRelationshipTypes.Cover.Id.ToString(), "forward", Id(c));

        using (var unconfirmed = await SendAsync(client, HttpMethod.Delete, TypeUri(type), 1, null))
        {
            var problem = await SetupApi.ProblemAsync(unconfirmed, HttpStatusCode.Conflict, "relationship_type_in_use");
            Assert.Equal(2, problem.GetProperty("relationshipCount").GetInt32());
        }

        Assert.Equal(2, (await ListAsync(client)).Single(item => item.GetProperty("id").GetString() == type).GetProperty("relationshipCount").GetInt32());

        foreach (var wrong in new[] { "yes", "TRUE", string.Empty })
        {
            using var response = await SendAsync(client, HttpMethod.Delete, new Uri($"{TypeUri(type)}?removeRelationships={wrong}", UriKind.Relative), 1, null);
            await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        }

        using (var stale = await SendAsync(client, HttpMethod.Delete, new Uri($"{TypeUri(type)}?removeRelationships=true", UriKind.Relative), 7, null))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        using (var confirmed = await SendAsync(client, HttpMethod.Delete, new Uri($"{TypeUri(type)}?removeRelationships=true", UriKind.Relative), 1, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        // Exactly its relationships went; the Cover stays; every Song that lost one moved, no revision did.
        Assert.Equal(["Cover: Song C"], Labels(await SongAsync(client, Id(a))));
        Assert.Empty((await SongAsync(client, Id(b))).GetProperty("relationships").EnumerateArray());
        Assert.Equal(["Covered by: Song A"], Labels(await SongAsync(client, Id(c))));
        foreach (var song in new[] { a, b, c })
        {
            var now = await SongAsync(client, Id(song));
            Assert.Equal(song.GetProperty("revision").GetInt32(), now.GetProperty("revision").GetInt32());
            Assert.NotEqual(kept.GetProperty("updatedAt").GetString(), now.GetProperty("updatedAt").GetString());
        }

        Assert.DoesNotContain(await ListAsync(client), item => item.GetProperty("id").GetString() == type);

        // A type no relationship uses is deleted directly.
        using (var direct = await SendAsync(client, HttpMethod.Delete, TypeUri(unused), 1, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, direct.StatusCode);
        }

        using (var gone = await SendAsync(client, HttpMethod.Delete, TypeUri(unused), 1, null))
        {
            await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal("9|1", TestDatabase.Scalar(factory.DataPath, "SELECT (SELECT count(*) FROM song_relationship_types) || '|' || (SELECT count(*) FROM song_relationships);"));
    }

    [Fact]
    public async Task RelatingNeedsSongsWriteReadingNeedsCatalogReadAndManagingTypesNeedsASession()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var type = (await CreateTypeAsync(client, "Sequel to", "Has sequel")).GetProperty("id").GetString()!;
        var (a, b) = (await SongApi.CreateAsync(client, "Song A"), await SongApi.CreateAsync(client, "Song B"));
        var relationship = (await RelateAsync(client, Id(a), type, "forward", Id(b))).GetProperty("relationships")[0].GetProperty("id").GetString()!;

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        var everything = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, Types, reader))
        {
            Assert.Equal(10, (await SetupApi.JsonAsync(read)).GetProperty("items").GetArrayLength());
        }

        using (var song = await CredentialApi.SendAsync(client, HttpMethod.Get, SongApi.Song(Id(b)), reader))
        {
            Assert.Equal("Has sequel", (await SetupApi.JsonAsync(song)).GetProperty("relationships")[0].GetProperty("name").GetString());
        }

        using (var refused = await BearerAsync(client, HttpMethod.Post, Relationships(Id(b)), reader, $$"""{"typeId":"{{SystemRelationshipTypes.Cover.Id}}","direction":"forward","otherSong":"{{Id(a)}}"}"""))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.SongsWrite, problem.GetProperty("requiredScope").GetString());
        }

        using (var refused = await BearerAsync(client, HttpMethod.Delete, new Uri($"/api/v1/songs/{Id(a)}/relationships/{relationship}", UriKind.Relative), reader, null))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
        }

        using (var related = await BearerAsync(client, HttpMethod.Post, Relationships(Id(b)), writer, $$"""{"typeId":"{{SystemRelationshipTypes.Cover.Id}}","direction":"forward","otherSong":"{{Id(a)}}"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, related.StatusCode);
        }

        using (var removed = await BearerAsync(client, HttpMethod.Delete, new Uri($"/api/v1/songs/{Id(a)}/relationships/{relationship}", UriKind.Relative), writer, null))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        }

        // A token holding every scope cannot manage types.
        foreach (var (method, uri, json) in new (HttpMethod, Uri, string?)[]
        {
            (HttpMethod.Post, Types, """{"name":"Prequel to","reverseName":"Has prequel"}"""),
            (HttpMethod.Patch, TypeUri(type), """{"name":"Renamed"}"""),
            (HttpMethod.Delete, TypeUri(type), null),
        })
        {
            using var response = await BearerAsync(client, method, uri, everything, json, revision: 1);
            await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, "session_required");
        }

        Assert.Equal("Sequel to|1|10", TestDatabase.Scalar(factory.DataPath, $"SELECT name || '|' || revision || '|' || (SELECT count(*) FROM song_relationship_types) FROM song_relationship_types WHERE upper(id) = '{type.ToUpperInvariant()}';"));
    }

    private static Uri TypeUri(string id) => new($"/api/v1/relationship-types/{id}", UriKind.Relative);

    private static Uri Relationships(string song) => new($"/api/v1/songs/{song}/relationships", UriKind.Relative);

    private static string Id(JsonElement song) => song.GetProperty("id").GetString()!;

    /// <summary>"Name: other Song's title" for the Song's only relationship.</summary>
    private static string Single(JsonElement song) => Assert.Single(Labels(song));

    /// <summary>"Name: other Song's title" for each of the Song's relationships, in order.</summary>
    private static List<string> Labels(JsonElement song) =>
        [.. song.GetProperty("relationships").EnumerateArray().Select(static item => $"{item.GetProperty("name").GetString()}: {item.GetProperty("song").GetProperty("title").GetString()}")];

    private static async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Types);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static async Task<JsonElement> SongAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(SongApi.Song(reference));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<JsonElement> CreateTypeAsync(HttpClient client, string name, string reverseName)
    {
        using var response = await SendAsync(client, HttpMethod.Post, Types, null, JsonSerializer.Serialize(new { name, reverseName }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Relates the Songs from <paramref name="song"/> and answers that Song.</summary>
    private static async Task<JsonElement> RelateAsync(HttpClient client, string song, string type, string direction, string other)
    {
        using var response = await SongApi.SendJsonAsync(client, HttpMethod.Post, Relationships(song), JsonSerializer.Serialize(new { typeId = type, direction, otherSong = other }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
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

    /// <summary>Sends a Bearer request (no session cookie is used for it) with an optional JSON body and revision.</summary>
    private static async Task<HttpResponseMessage> BearerAsync(HttpClient client, HttpMethod method, Uri uri, string token, string? json, int? revision = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (revision is { } value)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
        }

        return await client.SendAsync(request);
    }
}
