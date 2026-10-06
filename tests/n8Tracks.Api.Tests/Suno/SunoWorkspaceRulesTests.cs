using System.Text.Json.Nodes;
using n8Tracks.Domain.Suno;
using n8Tracks.Infrastructure.Retention;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>The pure rules of keeping workspace records (#129).</summary>
public sealed class SunoWorkspaceRulesTests
{
    private static readonly DateTimeOffset Then = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Then.AddDays(1);

    private static readonly SunoWorkspace Known = new("w-1", "Known", "About it", SunoWorkspaceState.Available, Then, Then);

    [Theory]
    [InlineData(false, SunoWorkspaceState.Available)]
    [InlineData(true, SunoWorkspaceState.Unavailable)]
    public void AWorkspaceFirstSeenIsAvailableUnlessTrashed(bool trashed, SunoWorkspaceState expected)
    {
        foreach (var complete in new[] { true, false })
        {
            var seen = SunoWorkspaceRules.Seen(null, new SunoWorkspaceSighting("w-2", " Named ", null, trashed, "{}"), complete, Now);

            Assert.Equal(new SunoWorkspace("w-2", "Named", string.Empty, expected, Now, Now), seen);
        }
    }

    [Fact]
    public void ARenameChangesTheNameAndLastSeenOnly()
    {
        var seen = SunoWorkspaceRules.Seen(Known, new SunoWorkspaceSighting("w-1", "Renamed", null, false, "{}"), complete: true, Now);

        Assert.Equal(Known with { Name = "Renamed", LastSeenUtc = Now }, seen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameNeverOverwritesAKnownOne(string? name)
    {
        var seen = SunoWorkspaceRules.Seen(Known, new SunoWorkspaceSighting("w-1", name, "New description", false, "{}"), complete: false, Now);

        Assert.Equal("Known", seen.Name);
        Assert.Equal("New description", seen.Description);
    }

    [Fact]
    public void OnlyACompleteListChangesAvailability()
    {
        var trashed = new SunoWorkspaceSighting("w-1", "Known", null, true, "{}");

        Assert.Equal(SunoWorkspaceState.Available, SunoWorkspaceRules.Seen(Known, trashed, complete: false, Now).State);
        Assert.Equal(SunoWorkspaceState.Unavailable, SunoWorkspaceRules.Seen(Known, trashed, complete: true, Now).State);

        var gone = Known with { State = SunoWorkspaceState.Unavailable };
        var listed = new SunoWorkspaceSighting("w-1", "Known", null, false, "{}");
        Assert.Equal(SunoWorkspaceState.Unavailable, SunoWorkspaceRules.Seen(gone, listed, complete: false, Now).State);
        Assert.Equal(SunoWorkspaceState.Available, SunoWorkspaceRules.Seen(gone, listed, complete: true, Now).State);
    }

    [Fact]
    public void AWorkspaceLeftOutIsUnavailableWithEverythingElseKept()
    {
        Assert.Equal(Known with { State = SunoWorkspaceState.Unavailable }, SunoWorkspaceRules.Unlisted(Known));
    }

    [Fact]
    public void ABlankNameIsShownAsUnnamedAndStatesHaveTheirApiNames()
    {
        Assert.Equal("(unnamed)", SunoWorkspaceRules.DisplayName(Known with { Name = " " }));
        Assert.Equal("Known", SunoWorkspaceRules.DisplayName(Known));
        foreach (var state in Enum.GetValues<SunoWorkspaceState>())
        {
            Assert.Equal(state, SunoWorkspaceRules.StateOf(SunoWorkspaceRules.NameOf(state)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(static () => SunoWorkspaceRules.StateOf("Available"));
    }

    [Fact]
    public void ASongRetainedBeforeWorkspacesRestoresInNone()
    {
        var shape2 = new JsonObject { ["id"] = "A", ["selected_generation_id"] = null };

        var shape3 = RetainedTypes.SongShape2To3(shape2);

        Assert.True(shape3.ContainsKey("suno_workspace_id"));
        Assert.Null(shape3["suno_workspace_id"]);
        Assert.Equal(3, RetainedTypes.Song.ShapeVersion);
        Assert.True(RetainedTypes.Song.Upgraders.ContainsKey(2));
    }
}
