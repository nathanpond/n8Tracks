using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>The pure rules of remembering a declined change and a kept conflict (#141).</summary>
public sealed class RememberedChoiceRulesTests
{
    private static readonly ClipFields Fields = new(
        "clip-1",
        "complete",
        "A title",
        123.5,
        "v5",
        "chirp-v5",
        "v5",
        "dream pop",
        90,
        120,
        104.25,
        "C_major",
        DateTimeOffset.UnixEpoch,
        "https://cdn1.suno.ai/clip-1.mp3",
        "https://cdn2.suno.ai/image_clip-1.jpeg?sig=one",
        "studio",
        0);

    [Fact]
    public void TheDeclinedHashIsSha256HexAndStableForTheSameValues()
    {
        var hash = RememberedChoiceRules.DeclinedHash(Fields);

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, RememberedChoiceRules.DeclinedHash(Fields with { }));
    }

    [Fact]
    public void AnyComparedFieldChangesTheDeclinedHashButNothingElseDoes()
    {
        var hash = RememberedChoiceRules.DeclinedHash(Fields);

        Assert.All(
            new[]
            {
                Fields with { Title = "Another title" },
                Fields with { StyleTags = null },
                Fields with { DurationSeconds = 123.25 },
                Fields with { ModelVersion = "v6" },
                Fields with { ModelName = "chirp-v6" },
                Fields with { MinimumBpm = 91 },
                Fields with { MaximumBpm = 121 },
                Fields with { AverageBpm = 104 },
                Fields with { Key = "A_minor" },
                Fields with { ImageUrl = "https://cdn2.suno.ai/image_other.jpeg" },
            },
            changed => Assert.NotEqual(hash, RememberedChoiceRules.DeclinedHash(changed)));

        // Not compared: Suno's status, the audio address, the workspace, and an image signature.
        Assert.All(
            new[]
            {
                Fields with { Status = "error" },
                Fields with { AudioUrl = "https://cdn1.suno.ai/other.mp3" },
                Fields with { WorkspaceId = "elsewhere" },
                Fields with { ImageUrl = "https://cdn2.suno.ai/image_clip-1.jpeg?sig=two" },
            },
            same => Assert.Equal(hash, RememberedChoiceRules.DeclinedHash(same)));
    }

    [Fact]
    public void TheKeptHashCoversEveryComparedInputWhateverTheOrder()
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["lyrics"] = "\"Words\"", ["weirdness"] = "50" };
        var reordered = new Dictionary<string, string>(StringComparer.Ordinal) { ["weirdness"] = "50", ["lyrics"] = "\"Words\"" };
        var hash = RememberedChoiceRules.KeptInputsHash(inputs);

        Assert.Equal(hash, RememberedChoiceRules.KeptInputsHash(reordered));
        Assert.NotEqual(hash, RememberedChoiceRules.KeptInputsHash(new Dictionary<string, string>(inputs, StringComparer.Ordinal) { ["weirdness"] = "51" }));
        Assert.NotEqual(hash, RememberedChoiceRules.KeptInputsHash(new Dictionary<string, string>(inputs, StringComparer.Ordinal) { ["styles"] = "\"\"" }));
    }

    [Fact]
    public void OnlyAStoredEqualHashIsRemembered()
    {
        var hash = RememberedChoiceRules.DeclinedHash(Fields);

        Assert.True(RememberedChoiceRules.Remembers(hash, hash));
        Assert.False(RememberedChoiceRules.Remembers(null, hash));
        Assert.False(RememberedChoiceRules.Remembers(RememberedChoiceRules.DeclinedHash(Fields with { Title = "Other" }), hash));
    }
}
