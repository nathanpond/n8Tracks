using System.Text;
using System.Text.Json.Nodes;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The clip reader (#117) over the spikes' sanitized fixtures: a finished clip from <c>/api/feed/v3</c>
/// (TS-001), the clips <c>/api/generate/v2-web/</c> answers when Create is clicked, and the TS-003 clips
/// of each mode; and the refusals. Where a fixture and the design disagree, the fixture wins.
/// </summary>
public sealed class ClipReaderTests
{
    [Fact]
    public void AFinishedClipFromTheFeedIsReadFieldByField()
    {
        var fields = Read(Clips.FixtureClip("feed-v3.completed-clip.response.json"));

        Assert.Equal(
            new ClipFields(
                "00000000-0000-4000-8000-000000000003",
                "complete",
                "<redacted title>",
                143.52,
                "v6",
                "chirp-goose",
                "<redacted display_name>",
                "<redacted tags>",
                68.18,
                157.89,
                130.05,
                "C_major",
                new DateTimeOffset(2026, 10, 3, 14, 19, 53, 697, TimeSpan.Zero),

                // The only media_urls entry is m4a, so the audio address is audio_url, as returned.
                "https://studio-api.prod.suno.com/api/forbidden",
                "https://cdn2.suno.ai/image_00000000-0000-4000-8000-000000000003.jpeg",
                "00000000-0000-4000-8000-000000000004",
                1),
            fields);
        Assert.Equal("https://suno.com/song/00000000-0000-4000-8000-000000000003", fields.PageUrl);
    }

    [Fact]
    public void AJustSubmittedClipHasNoDurationAndItsBlankAudioAddressIsNone()
    {
        foreach (var index in new[] { 0, 1 })
        {
            var fields = Read(Clips.FixtureClip("generate-v2-web.response.json", index));

            Assert.Equal("submitted", fields.Status);
            Assert.Null(fields.DurationSeconds);
            Assert.Null(fields.MinimumBpm);
            Assert.Null(fields.Key);
            Assert.Null(fields.StyleTags);
            Assert.Null(fields.AudioUrl);
            Assert.Null(fields.ImageUrl);
            Assert.Null(fields.WorkspaceId);
            Assert.Null(fields.ModelLabel);

            // The reported model is major_model_version and model_name, never a form label.
            Assert.Equal("v6", fields.ModelVersion);
            Assert.Equal("chirp-goose", fields.ModelName);
            Assert.Equal(index, fields.BatchIndex);
            Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 19, 53, 697, TimeSpan.Zero), fields.SunoCreatedUtc);
        }

        // The two clips of one Create differ in their Suno ID.
        Assert.NotEqual(Read(Clips.FixtureClip("generate-v2-web.response.json", 0)).SunoId, Read(Clips.FixtureClip("generate-v2-web.response.json", 1)).SunoId);
    }

    [Fact]
    public void TheModelLabelComesFromTheSongRowBadgeWhenThereIsOne()
    {
        var advanced = Read(Clips.FixtureClip("feed-v3.songs-advanced.response.json"));
        Assert.Equal("V6-MINI", advanced.ModelLabel);
        Assert.Equal("v6", advanced.ModelVersion);
        Assert.Equal("chirp-goose", advanced.ModelName);
        Assert.Equal("D_major", advanced.Key);
        Assert.Equal(29.6, advanced.DurationSeconds);
        Assert.Equal("00000000-0000-4000-8000-000000000105", advanced.WorkspaceId);

        // A Sound reports an empty major_model_version and no badge: both are none.
        var sound = Read(Clips.FixtureClip("feed-v3.sounds.response.json"));
        Assert.Null(sound.ModelVersion);
        Assert.Equal("chirp-sfx", sound.ModelName);
        Assert.Null(sound.ModelLabel);
        Assert.Equal("Ab_minor", sound.Key);
    }

    [Theory]
    [InlineData("feed-v3.songs-simple.response.json")]
    [InlineData("feed-v3.songs-advanced.response.json")]
    [InlineData("feed-v3.speech-simple.response.json")]
    [InlineData("feed-v3.speech-advanced.response.json")]
    [InlineData("feed-v3.sounds.response.json")]
    [InlineData("feed-v3.library-page-1.response.json")]
    [InlineData("generate-v2-web.songs-simple.response.json")]
    [InlineData("generate-v2-web.songs-advanced.response.json")]
    [InlineData("generate-v2-web.speech-simple.response.json")]
    [InlineData("generate-v2-web.speech-advanced.response.json")]
    [InlineData("generate-v2-web.sounds.response.json")]
    public void EveryCapturedClipIsReadWithItsIdAndStatus(string fixture)
    {
        foreach (var clip in JsonNode.Parse(Clips.Fixture(fixture))!["clips"]!.AsArray())
        {
            var fields = Read(clip!.ToJsonString());
            Assert.Equal(clip["id"]!.GetValue<string>(), fields.SunoId);
            Assert.Equal(clip["status"]!.GetValue<string>(), fields.Status);
            Assert.Equal(clip["batch_index"]!.GetValue<int>(), fields.BatchIndex);
        }
    }

    [Fact]
    public void TheAudioAddressIsTheFirstMp3MediaEntryWhenThereIsOne()
    {
        var clip = JsonNode.Parse(Clips.FixtureClip("feed-v3.completed-clip.response.json"))!.AsObject();
        clip["media_urls"] = new JsonArray(
            new JsonObject { ["url"] = "https://d2lwuy8qc234o3.cloudfront.net/1/clip/x.m4a", ["content_type"] = "m4a-opus" },
            new JsonObject { ["url"] = "https://d2lwuy8qc234o3.cloudfront.net/1/clip/x.mp3", ["content_type"] = "audio/mpeg" },
            new JsonObject { ["url"] = "https://cdn1.suno.ai/second.mp3", ["content_type"] = "mp3" });

        Assert.Equal("https://d2lwuy8qc234o3.cloudfront.net/1/clip/x.mp3", Read(clip.ToJsonString()).AudioUrl);

        // An entry is an MP3 by its content type or its address.
        clip["media_urls"] = new JsonArray(new JsonObject { ["url"] = "https://cdn1.suno.ai/byname.mp3" });
        Assert.Equal("https://cdn1.suno.ai/byname.mp3", Read(clip.ToJsonString()).AudioUrl);
    }

    [Fact]
    public void MissingOddlyTypedAndUnknownFieldsAreLeftNoneWithoutRefusingTheClip()
    {
        var fields = Read("""
            {"id":"only-the-id","status":"queued_somewhere_new","title":42,"metadata":{"duration":"long","tags":null,"min_bpm":1e400,"key":["C"],"model_badges":{"songrow":"V9"}},
             "created_at":"not a time","batch_index":1.5,"project":"p","media_urls":"none","novel":{"deep":[1,2,3]}}
            """);

        Assert.Equal("only-the-id", fields.SunoId);

        // A status outside the four known values is kept verbatim.
        Assert.Equal("queued_somewhere_new", fields.Status);
        Assert.Equal(new ClipFields("only-the-id", "queued_somewhere_new", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null), fields);

        // A clip with no status has none.
        Assert.Null(Read("""{"id":"x"}""").Status);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("[{\"id\":\"x\"}]", "not a JSON object")]
    [InlineData("\"x\"", "not a JSON object")]
    [InlineData("{\"id\":\"x\"", "not valid JSON")]
    [InlineData("{}", "no Suno ID")]
    [InlineData("{\"id\":7}", "no Suno ID")]
    [InlineData("{\"id\":\"  \"}", "no Suno ID")]
    [InlineData("{\"id\":null}", "no Suno ID")]
    public void AValueThatIsNotAClipWithAStringIdIsRefused(string raw, string reason)
    {
        var invalid = Assert.IsType<ClipReading.Invalid>(ClipReader.Read(raw));
        Assert.Contains(reason, invalid.Reason, StringComparison.Ordinal);
        Assert.IsType<ClipReading.Invalid>(ClipReader.Read(null));
    }

    [Fact]
    public void TheRawTextIsLimitedTo2MbOfValidUtf8()
    {
        // Exactly 2 MB is kept; one byte more is refused.
        var prefix = "{\"id\":\"big\",\"pad\":\"";
        var suffix = "\"}";
        var fits = prefix + new string('x', ClipReader.MaximumBytes - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(ClipReader.MaximumBytes, Encoding.UTF8.GetByteCount(fits));
        Assert.IsType<ClipReading.Read>(ClipReader.Read(fits));
        var over = prefix + new string('x', ClipReader.MaximumBytes - prefix.Length - suffix.Length + 1) + suffix;
        Assert.Contains("larger than 2 MB", Assert.IsType<ClipReading.Invalid>(ClipReader.Read(over)).Reason, StringComparison.Ordinal);

        // A multi-byte character counts as its bytes, not as one.
        var multi = prefix + new string('é', (ClipReader.MaximumBytes - prefix.Length - suffix.Length) / 2 + 1) + suffix;
        Assert.IsType<ClipReading.Invalid>(ClipReader.Read(multi));

        // A lone surrogate cannot be stored as UTF-8.
        Assert.Contains("UTF-8", Assert.IsType<ClipReading.Invalid>(ClipReader.Read("{\"id\":\"x\",\"t\":\"\ud800\"}")).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRawTextIsKeptExactlyAsGiven()
    {
        var raw = Clips.Handwritten("handwritten", "secret");

        var read = Assert.IsType<ClipReading.Read>(ClipReader.Read(raw));

        Assert.Same(raw, read.Raw);
        Assert.Equal("streaming", read.Fields.Status);
        Assert.Equal(12.5, read.Fields.DurationSeconds);
        Assert.Equal("Café été \U0001F3B5", read.Fields.Title);
    }

    private static ClipFields Read(string raw) => Assert.IsType<ClipReading.Read>(ClipReader.Read(raw)).Fields;
}
