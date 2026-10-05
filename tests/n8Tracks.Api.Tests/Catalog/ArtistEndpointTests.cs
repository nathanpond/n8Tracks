using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>
/// Artists: <c>GET /api/v1/artists</c> (a page by name, searched by name or alias) and
/// <c>GET /api/v1/artists/{id}</c> need <c>catalog.read</c>; <c>POST /api/v1/artists</c> and
/// <c>PATCH /api/v1/artists/{id}</c> (under the Artist's revision) need <c>collections.write</c>. A
/// name or alias another Artist has is 409 <c>duplicate_artist_name</c> unless confirmed.
/// </summary>
public sealed class ArtistEndpointTests
{
    private static readonly Uri Artists = new("/api/v1/artists", UriKind.Relative);

    [Fact]
    public async Task AnArtistIsCreatedWithItsAliasesNotesAndLinksAndReadBack()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var created = await SendAsync(client, HttpMethod.Post, Artists, revision: null, """
            {"name":"  n8  ","aliases":[" Nate  Pond ","N. P."],"notes":"Plays everything.\r\n","links":[{"label":" Site ","url":" https://example.com/n8 "},{"url":"http://example.org"}]}
            """);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.ToString());
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var artist = await SetupApi.JsonAsync(created);
        var id = artist.GetProperty("id").GetString()!;
        Assert.Equal($"/api/v1/artists/{id}", created.Headers.Location?.OriginalString);
        Assert.Equal("n8", artist.GetProperty("name").GetString());
        Assert.Equal(["Nate Pond", "N. P."], Strings(artist.GetProperty("aliases")));
        Assert.Equal("Plays everything.", artist.GetProperty("notes").GetString());
        Assert.Equal(["Site https://example.com/n8", "- http://example.org"], Links(artist));
        Assert.Equal(0, artist.GetProperty("songCount").GetInt32());
        Assert.Equal(0, artist.GetProperty("albumCount").GetInt32());
        Assert.Equal(1, artist.GetProperty("revision").GetInt32());
        Assert.Equal(artist.GetProperty("createdAt").GetString(), artist.GetProperty("updatedAt").GetString());

        using var read = await client.GetAsync(ArtistUri(id));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("\"1\"", read.Headers.ETag?.ToString());
        Assert.Equal(artist.GetRawText(), (await SetupApi.JsonAsync(read)).GetRawText());

        Assert.Equal("n8|N8|1", TestDatabase.Scalar(factory.DataPath, "SELECT name, name_key, revision FROM artists;"));
        Assert.Equal("0:Nate Pond:NATE POND,1:N. P.:N. P.", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(position || ':' || name || ':' || name_key) FROM (SELECT * FROM artist_aliases ORDER BY position);"));
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM artist_links;"));

        using var missing = await client.GetAsync(ArtistUri(Guid.CreateVersion7().ToString()));
        await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task AnEditChangesOnlyWhatIsSentUnderTheArtistsRevision()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, """{"name":"n8","aliases":["Nate"],"links":[{"url":"https://a.example"}]}""");
        clock.Advance(TimeSpan.FromMinutes(5));

        using (var edited = await SendAsync(client, HttpMethod.Patch, ArtistUri(id), 1, """{"aliases":["Nate","Pond"],"links":[{"label":"B","url":"https://b.example"},{"url":"https://a.example"}],"notes":"Hi"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            Assert.Equal("\"2\"", edited.Headers.ETag?.ToString());
            var body = await SetupApi.JsonAsync(edited);
            Assert.Equal("n8", body.GetProperty("name").GetString());
            Assert.Equal(["Nate", "Pond"], Strings(body.GetProperty("aliases")));
            Assert.Equal(["B https://b.example", "- https://a.example"], Links(body));
            Assert.Equal("Hi", body.GetProperty("notes").GetString());
            Assert.NotEqual(body.GetProperty("createdAt").GetString(), body.GetProperty("updatedAt").GetString());
        }

        // Notes only: the lists stay; null clears the notes; the same values again change nothing.
        using (var notes = await SendAsync(client, HttpMethod.Patch, ArtistUri(id), 2, """{"notes":null}"""))
        {
            var body = await SetupApi.JsonAsync(notes);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("notes").ValueKind);
            Assert.Equal(["Nate", "Pond"], Strings(body.GetProperty("aliases")));
            Assert.Equal(3, body.GetProperty("revision").GetInt32());
        }

        using (var same = await SendAsync(client, HttpMethod.Patch, ArtistUri(id), 3, """{"name":" n8 ","aliases":["Nate","Pond"],"notes":"  "}"""))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal(3, (await SetupApi.JsonAsync(same)).GetProperty("revision").GetInt32());
        }

        // Clearing the lists, and reordering is a change.
        using (var cleared = await SendAsync(client, HttpMethod.Patch, ArtistUri(id), 3, """{"aliases":["Pond","Nate"],"links":[]}"""))
        {
            var body = await SetupApi.JsonAsync(cleared);
            Assert.Equal(["Pond", "Nate"], Strings(body.GetProperty("aliases")));
            Assert.Empty(Links(body));
            Assert.Equal(4, body.GetProperty("revision").GetInt32());
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM artist_links;"));

        using (var stale = await SendAsync(client, HttpMethod.Patch, ArtistUri(id), 2, """{"name":"Other"}"""))
        {
            var problem = await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
            Assert.Equal("n8", problem.GetProperty("current").GetProperty("name").GetString());
            Assert.Equal(4, problem.GetProperty("current").GetProperty("revision").GetInt32());
        }

        using (var unrevisioned = await SendAsync(client, HttpMethod.Patch, ArtistUri(id), revision: null, """{"name":"Other"}"""))
        {
            await SetupApi.ProblemAsync(unrevisioned, (HttpStatusCode)428, "revision_required");
        }

        using (var missing = await SendAsync(client, HttpMethod.Patch, ArtistUri(Guid.CreateVersion7().ToString()), 1, """{"name":"Other"}"""))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal("n8|4", TestDatabase.Scalar(factory.DataPath, "SELECT name, revision FROM artists;"));
    }

    [Fact]
    public async Task WrongFieldsAreRefusedAndNothingIsStored()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, """{"name":"n8","aliases":["Nate"]}""");

        foreach (var (body, field) in new[]
        {
            ("{}", "name"),
            ("""{"name":"   "}""", "name"),
            ("""{"name":42}""", "name"),
            ($$"""{"name":"{{new string('a', 201)}}"}""", "name"),
            ("""{"name":"x","aliases":"Nate"}""", "aliases"),
            ("""{"name":"x","aliases":["a","A"]}""", "aliases"),
            ("""{"name":"x","aliases":["X"]}""", "aliases"),
            ("""{"name":"x","links":[{"url":"ftp://example.com"}]}""", "links"),
            ("""{"name":"x","links":[{"url":"javascript:alert(1)"}]}""", "links"),
            ("""{"name":"x","links":[{"url":"example.com"}]}""", "links"),
            ("""{"name":"x","links":[{"label":"no url"}]}""", "links"),
            ("""{"name":"x","links":["https://example.com"]}""", "links"),
            ($$"""{"name":"x","links":[{"label":"{{new string('l', 101)}}","url":"https://example.com"}]}""", "links"),
            ($$"""{"name":"x","notes":"{{new string('n', 10_001)}}"}""", "notes"),
            ("""{"name":"x","confirmDuplicate":"yes"}""", "confirmDuplicate"),
        })
        {
            using var wrong = await SendAsync(client, HttpMethod.Post, Artists, revision: null, body);
            var problem = await SetupApi.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        var tooMany = JsonSerializer.Serialize(new { aliases = Enumerable.Range(1, 21).Select(static i => $"alias {i}") });
        foreach (var (body, field) in new[]
        {
            ("""{"name":null}""", "name"),
            ("""{"name":""}""", "name"),
            ("""{"aliases":["n8"]}""", "aliases"),
            ("""{"name":"Nate"}""", "aliases"),
            (tooMany, "aliases"),
            ("""{"links":[{"url":"https://example.com","label":3}]}""", "links"),
            ("""{"links":[{"url":"mailto:n8@example.com"}]}""", "links"),
            ("""{"notes":5}""", "notes"),
        })
        {
            using var wrong = await SendAsync(client, HttpMethod.Patch, ArtistUri(id), 1, body);
            var problem = await SetupApi.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), body);
        }

        Assert.Equal("n8|1|Nate", TestDatabase.Scalar(factory.DataPath, "SELECT a.name, a.revision, group_concat(l.name) FROM artists a JOIN artist_aliases l ON l.artist_id = a.id;"));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM artist_links;"));
    }

    [Fact]
    public async Task ANameOrAliasAnotherArtistHasNeedsConfirmation()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var first = await CreateAsync(client, """{"name":"n8","aliases":["Nate"]}""");

        // The same name in another case: 409, listing the match, and nothing is stored.
        using (var duplicate = await SendAsync(client, HttpMethod.Post, Artists, revision: null, """{"name":"N8"}"""))
        {
            var problem = await SetupApi.ProblemAsync(duplicate, HttpStatusCode.Conflict, "duplicate_artist_name");
            var match = Assert.Single(problem.GetProperty("matches").EnumerateArray());
            Assert.Equal(first, match.GetProperty("id").GetString());
            Assert.Equal("n8", match.GetProperty("name").GetString());
            Assert.Equal("n8", match.GetProperty("matchedText").GetString());
            Assert.Equal("name", match.GetProperty("matchedOn").GetString());
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM artists;"));

        // Confirmed, it is created.
        var second = await CreateAsync(client, """{"name":"N8","confirmDuplicate":true}""");

        // An alias of the new Artist matching another's alias names that alias; a confirmed
        // duplicate with false is the same as unconfirmed.
        using (var byAlias = await SendAsync(client, HttpMethod.Post, Artists, revision: null, """{"name":"Someone","aliases":["NATE"],"confirmDuplicate":false}"""))
        {
            var problem = await SetupApi.ProblemAsync(byAlias, HttpStatusCode.Conflict, "duplicate_artist_name");
            var match = Assert.Single(problem.GetProperty("matches").EnumerateArray());
            Assert.Equal("Nate alias", $"{match.GetProperty("matchedText").GetString()} {match.GetProperty("matchedOn").GetString()}");
        }

        // A rename to another's alias asks too; an edit that changes neither name nor aliases never asks.
        var third = await CreateAsync(client, """{"name":"Third"}""");
        using (var rename = await SendAsync(client, HttpMethod.Patch, ArtistUri(third), 1, """{"name":"nate"}"""))
        {
            var problem = await SetupApi.ProblemAsync(rename, HttpStatusCode.Conflict, "duplicate_artist_name");
            Assert.Equal(first, Assert.Single(problem.GetProperty("matches").EnumerateArray()).GetProperty("id").GetString());
        }

        using (var notesOnly = await SendAsync(client, HttpMethod.Patch, ArtistUri(second), 1, """{"name":"N8","notes":"still a duplicate","links":[{"url":"https://n8.example"}]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, notesOnly.StatusCode);
            Assert.Equal(2, (await SetupApi.JsonAsync(notesOnly)).GetProperty("revision").GetInt32());
        }

        // Its own name in another case is no duplicate of itself, though another Artist has it: only new names are checked.
        using (var recased = await SendAsync(client, HttpMethod.Patch, ArtistUri(second), 2, """{"name":"n8"}"""))
        {
            Assert.Equal("n8", (await SetupApi.JsonAsync(recased)).GetProperty("name").GetString());
        }

        // A stale revision is reported before a duplicate.
        using (var stale = await SendAsync(client, HttpMethod.Patch, ArtistUri(third), 7, """{"name":"nate"}"""))
        {
            await SetupApi.ProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        }

        // Matches on both names of one Artist are each listed; confirming the rename stores it.
        using (var both = await SendAsync(client, HttpMethod.Patch, ArtistUri(third), 1, """{"name":"Nate","aliases":["N8"]}"""))
        {
            var problem = await SetupApi.ProblemAsync(both, HttpStatusCode.Conflict, "duplicate_artist_name");
            Assert.Equal(
                ["n8 n8 name", "n8 Nate alias", "n8 n8 name"],
                [.. problem.GetProperty("matches").EnumerateArray().Select(static m => $"{m.GetProperty("name").GetString()} {m.GetProperty("matchedText").GetString()} {m.GetProperty("matchedOn").GetString()}")]);
        }

        using (var confirmed = await SendAsync(client, HttpMethod.Patch, ArtistUri(third), 1, """{"name":"Nate","confirmDuplicate":true}"""))
        {
            var body = await SetupApi.JsonAsync(confirmed);
            Assert.Equal("Nate 2", $"{body.GetProperty("name").GetString()} {body.GetProperty("revision").GetInt32()}");
        }

        Assert.Equal("3", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM artists;"));
    }

    [Fact]
    public async Task TheListIsByNameIgnoringCasePagedAndSearchedByNameOrAlias()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        foreach (var body in new[]
        {
            """{"name":"beta"}""",
            """{"name":"Alpha","aliases":["The Gamma Ones"]}""",
            """{"name":"alpha","confirmDuplicate":true}""",
            """{"name":"Delta"}""",
            """{"name":"Épsilon"}""",
        })
        {
            await CreateAsync(client, body);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        async Task<List<string>> NamesAsync(string query)
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/artists{query}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray().Select(static a => a.GetProperty("name").GetString()!)];
        }

        // Ignoring case, the earlier created first on a tie.
        Assert.Equal(["Alpha", "alpha", "beta", "Delta", "Épsilon"], await NamesAsync(string.Empty));

        var page = await SetupApi.JsonAsync(await client.GetAsync(new Uri("/api/v1/artists?pageSize=2&page=2", UriKind.Relative)));
        Assert.Equal(["beta", "Delta"], [.. page.GetProperty("items").EnumerateArray().Select(static a => a.GetProperty("name").GetString()!)]);
        Assert.Equal("2 2 5", $"{page.GetProperty("page").GetInt32()} {page.GetProperty("pageSize").GetInt32()} {page.GetProperty("total").GetInt32()}");
        Assert.Empty(await NamesAsync("?page=9"));

        // A case-insensitive substring of a name or an alias.
        Assert.Equal(["Alpha", "alpha"], await NamesAsync("?search=ALP"));
        Assert.Equal(["Alpha"], await NamesAsync("?search=gamma%20%20ONES"));
        Assert.Equal(["Delta"], await NamesAsync("?search=ELT"));
        Assert.Equal(["Épsilon"], await NamesAsync("?search=%C3%A9ps"));
        Assert.Empty(await NamesAsync("?search=%25"));
        Assert.Equal(5, (await NamesAsync("?search=%20%20")).Count);

        foreach (var wrong in new[] { "page=0", "page=x", "pageSize=101", "pageSize=0", "search=a&search=b", "page=1&page=2" })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/artists?{wrong}", UriKind.Relative));
            await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }
    }

    [Fact]
    public async Task ReadingArtistsNeedsCatalogReadAndWritingThemNeedsCollectionsWrite()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CreateAsync(client, """{"name":"n8"}""");

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        foreach (var uri in new[] { Artists, ArtistUri(id) })
        {
            using var read = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, reader);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CollectionsWrite);
        using (var create = await SendAsTokenAsync(client, HttpMethod.Post, Artists, writer, """{"name":"By token"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        using (var edit = await SendAsTokenAsync(client, HttpMethod.Patch, ArtistUri(id), writer, """{"notes":"by token"}""", ifMatch: "\"1\""))
        {
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        }

        // Every other scope is refused, naming the one needed, and nothing is stored.
        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CollectionsWrite))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            using var create = await SendAsTokenAsync(client, HttpMethod.Post, Artists, token, """{"name":"Refused"}""");
            var problem = await SetupApi.ProblemAsync(create, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CollectionsWrite, problem.GetProperty("requiredScope").GetString());
            using var edit = await SendAsTokenAsync(client, HttpMethod.Patch, ArtistUri(id), token, """{"name":"Refused"}""", ifMatch: "\"2\"");
            problem = await SetupApi.ProblemAsync(edit, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CollectionsWrite, problem.GetProperty("requiredScope").GetString());
        }

        foreach (var scope in CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead))
        {
            var token = await CredentialApi.CreateTokenAsync(factory, scope);
            foreach (var uri in new[] { Artists, ArtistUri(id) })
            {
                using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, token);
                var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
                Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
            }
        }

        Assert.Equal("By token,n8", TestDatabase.Scalar(factory.DataPath, "SELECT group_concat(name) FROM (SELECT name FROM artists ORDER BY name);"));
        Assert.Equal("by token|2", TestDatabase.Scalar(factory.DataPath, "SELECT notes, revision FROM artists WHERE name = 'n8';"));

        // Signed out, nothing is served or stored.
        using var signedOut = factory.CreateClient();
        await SetupApi.ProblemAsync(await signedOut.GetAsync(Artists), HttpStatusCode.Unauthorized, "not_authenticated");
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, Artists) { Content = JsonContent.Create(new { name = "anonymous" }) };
        anonymous.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        await SetupApi.ProblemAsync(await signedOut.SendAsync(anonymous), HttpStatusCode.Unauthorized, "not_authenticated");
        Assert.Equal("2", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM artists;"));
    }

    private static Uri ArtistUri(string id) => new($"/api/v1/artists/{id}", UriKind.Relative);

    /// <summary>Creates an Artist from a JSON body and returns its ID.</summary>
    private static async Task<string> CreateAsync(HttpClient client, string json)
    {
        using var response = await SendAsync(client, HttpMethod.Post, Artists, revision: null, json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    /// <summary>Sends JSON as the signed-in browser does, with <c>If-Match</c> when a revision is given.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, int? revision, string json)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        if (revision is { } value)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsTokenAsync(HttpClient client, HttpMethod method, Uri uri, string token, string json, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", ifMatch));
        }

        return await client.SendAsync(request);
    }

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(static item => item.GetString()!)];

    /// <summary>Each link as "label url", with "-" for no label.</summary>
    private static List<string> Links(JsonElement artist) =>
        [.. artist.GetProperty("links").EnumerateArray().Select(static link => $"{(link.GetProperty("label").ValueKind == JsonValueKind.Null ? "-" : link.GetProperty("label").GetString())} {link.GetProperty("url").GetString()}")];
}
