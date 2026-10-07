using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>The pure rules of following Suno's Trash, restores, and missing clips (#142).</summary>
public sealed class RemoteStateRulesTests
{
    private const GenerationState Active = GenerationState.Active;
    private const GenerationState Archived = GenerationState.Archived;
    private const GenerationArchiver User = GenerationArchiver.User;
    private const GenerationArchiver Sync = GenerationArchiver.Sync;

    [Theory]
    [InlineData("library", true, true, true)]
    [InlineData("library", false, true, false)]
    [InlineData("library", true, false, false)]
    [InlineData("workspaces", true, true, false)]
    [InlineData("playlists", true, true, false)]
    [InlineData("clips", true, true, false)]
    public void OnlyAWholeLibrarySyncReadToTheEndChecksForMissingClips(string scope, bool libraryComplete, bool trashedComplete, bool checks)
    {
        var header = new SunoExportHeader(null, null, DateTimeOffset.UnixEpoch, scope, [], libraryComplete, trashedComplete, false, "[]", "[]");

        Assert.Equal(checks, RemoteStateRules.ChecksMissing(header));
    }

    [Fact]
    public void ATrashedClipArchivesAnActiveGenerationBySyncAndKeepsAnyArchiveAsItIs()
    {
        Assert.Equal(
            new RemoteStateTransition(RemoteStateChangeKind.Trashed, GenerationRemoteState.Present, GenerationRemoteState.Trashed, Active, Archived, Sync),
            RemoteStateRules.Transition(Active, null, GenerationRemoteState.Present, RemoteSighting.Trashed));
        Assert.Equal(
            new RemoteStateTransition(RemoteStateChangeKind.Trashed, GenerationRemoteState.Present, GenerationRemoteState.Trashed, Archived, Archived, User),
            RemoteStateRules.Transition(Archived, User, GenerationRemoteState.Present, RemoteSighting.Trashed));

        // An archive from before #142 has no archiver: it is the user's.
        Assert.Equal(User, RemoteStateRules.Transition(Archived, null, GenerationRemoteState.Missing, RemoteSighting.Trashed)!.ArchivedBy);
        Assert.Equal(Sync, RemoteStateRules.Transition(Archived, Sync, GenerationRemoteState.Missing, RemoteSighting.Trashed)!.ArchivedBy);
        Assert.Null(RemoteStateRules.Transition(Archived, Sync, GenerationRemoteState.Trashed, RemoteSighting.Trashed));
    }

    [Fact]
    public void ARestoreReactivatesOnlyWhatSyncArchived()
    {
        var restored = RemoteStateRules.Transition(Archived, Sync, GenerationRemoteState.Trashed, RemoteSighting.Listed)!;
        Assert.Equal((RemoteStateChangeKind.Restored, GenerationRemoteState.Present, Active, (GenerationArchiver?)null), (restored.Kind, restored.To, restored.State, restored.ArchivedBy));
        Assert.True(restored.Reactivates);

        var users = RemoteStateRules.Transition(Archived, User, GenerationRemoteState.Trashed, RemoteSighting.Listed)!;
        Assert.Equal((RemoteStateChangeKind.Restored, Archived, (GenerationArchiver?)User), (users.Kind, users.State, users.ArchivedBy));
        Assert.False(users.Reactivates);
        Assert.Equal(Archived, RemoteStateRules.Transition(Archived, null, GenerationRemoteState.Missing, RemoteSighting.Listed)!.State);

        Assert.Equal(Active, RemoteStateRules.Transition(Active, null, GenerationRemoteState.Missing, RemoteSighting.Listed)!.State);
        Assert.Null(RemoteStateRules.Transition(Active, null, GenerationRemoteState.Present, RemoteSighting.Listed));
    }

    [Fact]
    public void AMissingClipChangesOnlyTheRemoteState()
    {
        foreach (var (state, archiver) in new (GenerationState, GenerationArchiver?)[] { (Active, null), (Archived, User), (Archived, Sync) })
        {
            foreach (var from in new[] { GenerationRemoteState.Present, GenerationRemoteState.Trashed })
            {
                var missing = RemoteStateRules.Transition(state, archiver, from, RemoteSighting.Unlisted)!;
                Assert.Equal((RemoteStateChangeKind.Missing, from, GenerationRemoteState.Missing, state, state, archiver), (missing.Kind, missing.From, missing.To, missing.FromState, missing.State, missing.ArchivedBy));
                Assert.False(missing.Archives || missing.Reactivates);
            }
        }

        Assert.Null(RemoteStateRules.Transition(Active, null, GenerationRemoteState.Missing, RemoteSighting.Unlisted));
    }
}
