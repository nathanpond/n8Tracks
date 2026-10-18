using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// Audio Influence (#436), a Version input that applies only to an Advanced Song built on another
/// song's audio (a Cover, Mashup, Sample This Song, or Inspiration): it is held and edited like Style
/// Influence, sent in <c>effectiveInputs</c> only where Suno shows it, and kept otherwise. A Version
/// stored before it existed reads it at the inventory's default without its row being rewritten, so a
/// frozen Version's inputs stay byte for byte as they were (invariant 1).
/// </summary>
public sealed class AudioInfluenceTests
{
    private static readonly string Cover = SystemRelationshipTypes.Cover.Id.ToString();
    private static readonly string Extend = SystemRelationshipTypes.Extend.Id.ToString();

    [Fact]
    public async Task AudioInfluenceIsEditedLikeStyleInfluenceAndSentOnlyWithACoverMashupSampleOrInspiration()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Audio Influence");
        var id = song.GetProperty("currentVersion").GetProperty("id").GetGuid().ToString();

        // A new Version holds it at Suno's default; with no source it is not sent.
        var created = await GetAsync(client, id);
        Assert.Equal(25, created.GetProperty("inputs").GetProperty("audioInfluence").GetInt32());
        Assert.False(created.GetProperty("effectiveInputs").TryGetProperty("audioInfluence", out _));

        // Edited and refused as a percentage, like Style Influence; kept, and still not sent, without a source.
        var edited = await EditAsync(client, id, 1, """{"inputs":{"audioInfluence":85}}""");
        Assert.Equal(85, edited.GetProperty("inputs").GetProperty("audioInfluence").GetInt32());
        Assert.False(edited.GetProperty("effectiveInputs").TryGetProperty("audioInfluence", out _));
        using (var refused = await PatchAsync(client, id, 2, """{"inputs":{"audioInfluence":101}}"""))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("inputs.audioInfluence", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A Cover sends it, beside Style Influence; an Extend keeps it but does not.
        var covered = await EditAsync(client, id, 2, $$$"""{"inputs":{"sources":[{"typeId":"{{{Cover}}}","external":{"sunoId":"cover-source"}}]}}""");
        var effective = covered.GetProperty("effectiveInputs").EnumerateObject().Select(static option => option.Name).ToList();
        Assert.Equal(85, covered.GetProperty("effectiveInputs").GetProperty("audioInfluence").GetInt32());
        Assert.Equal(effective.IndexOf("styleInfluence") + 1, effective.IndexOf("audioInfluence"));

        var extended = await EditAsync(client, id, 3, $$$"""{"inputs":{"sources":[{"typeId":"{{{Extend}}}","external":{"sunoId":"cover-source"}}]}}""");
        Assert.False(extended.GetProperty("effectiveInputs").TryGetProperty("audioInfluence", out _));
        Assert.Equal(85, extended.GetProperty("inputs").GetProperty("audioInfluence").GetInt32());
    }

    [Fact]
    public async Task AVersionStoredBeforeAudioInfluenceReadsTheDefaultAndItsFrozenRowIsNeverRewritten()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Stored before");
        var id = song.GetProperty("currentVersion").GetProperty("id").GetGuid();
        var upper = id.ToString().ToUpperInvariant();

        // As a row written before #436: its inputs have no audioInfluence. Then a Generation freezes it.
        TestDatabase.Execute(factory.DataPath, $"UPDATE versions SET inputs = json_remove(inputs, '$.audioInfluence') WHERE id = '{upper}';");
        var stored = TestDatabase.Scalar(factory.DataPath, $"SELECT inputs FROM versions WHERE id = '{upper}';");
        Assert.DoesNotContain("audioInfluence", stored, StringComparison.Ordinal);
        await SongApi.AttachGenerationAsync(factory, id.ToString());

        var read = await GetAsync(client, id.ToString());
        Assert.True(read.GetProperty("isFrozen").GetBoolean());
        Assert.Equal(25, read.GetProperty("inputs").GetProperty("audioInfluence").GetInt32());
        Assert.Equal(stored, TestDatabase.Scalar(factory.DataPath, $"SELECT inputs FROM versions WHERE id = '{upper}';"));
    }

    [Fact]
    public void AnImportedVersionStoredBeforeAudioInfluenceMarksItNotReturned()
    {
        var (_, _, inputs) = VersionInputsColumns.From(VersionInputRules.Defaults(CreateFieldInventory.Embedded, "Imported") with { AudioInfluence = 60 });
        var before = JsonNode.Parse(inputs)!.AsObject();
        before.Remove("audioInfluence");
        var marks = VersionInputsColumns.ImportedJson(new ImportedInputMarks(["personalize"], [], new Dictionary<string, string>(StringComparer.Ordinal)));

        Assert.Equal(25, VersionInputsColumns.Read("song", null, before.ToJsonString()).AudioInfluence);
        Assert.Equal(["personalize", "audioInfluence"], VersionInputsColumns.ReadImported(marks, before.ToJsonString())!.NotReturned);

        // Stored with it, it is read as stored and marked as the import marked it.
        Assert.Equal(60, VersionInputsColumns.Read("song", null, inputs).AudioInfluence);
        Assert.Equal(["personalize"], VersionInputsColumns.ReadImported(marks, inputs)!.NotReturned);
        Assert.Null(VersionInputsColumns.ReadImported(null, before.ToJsonString()));
    }

    private static async Task<JsonElement> EditAsync(HttpClient client, string id, int revision, string json)
    {
        using var response = await PatchAsync(client, id, revision, json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string id, int revision, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/versions/{id}", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }
}
