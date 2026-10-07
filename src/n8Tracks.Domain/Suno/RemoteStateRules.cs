using n8Tracks.Domain.Songs;

namespace n8Tracks.Domain.Suno;

/// <summary>Where a sync saw a linked clip (#142).</summary>
public enum RemoteSighting
{
    /// <summary>In the library (or a workspace or playlist read): Suno lists it.</summary>
    Listed,

    /// <summary>In Suno's Trash.</summary>
    Trashed,

    /// <summary>In neither, after a whole-library sync that read both the library and the Trash to the end.</summary>
    Unlisted,
}

/// <summary>What a sync found happened to a linked clip in Suno, as the review lists it (#142).</summary>
public enum RemoteStateChangeKind
{
    /// <summary>"In Suno Trash": the clip was moved to Suno's Trash.</summary>
    Trashed,

    /// <summary>"Restored in Suno": the clip is listed again after being trashed or missing.</summary>
    Restored,

    /// <summary>"Remote Missing": Suno lists the clip nowhere.</summary>
    Missing,
}

/// <summary>
/// What applying one remote-state row does to a Generation: its remote state from
/// <paramref name="From"/> to <paramref name="To"/>, and its state and archiver as they will be. Only the
/// three remote-state columns change; nothing is ever deleted (#142).
/// </summary>
public sealed record RemoteStateTransition(
    RemoteStateChangeKind Kind,
    GenerationRemoteState From,
    GenerationRemoteState To,
    GenerationState FromState,
    GenerationState State,
    GenerationArchiver? ArchivedBy)
{
    /// <summary>Whether the Generation is archived by it ("will be archived").</summary>
    public bool Archives => FromState == GenerationState.Active && State == GenerationState.Archived;

    /// <summary>Whether the Generation is reactivated by it ("will be reactivated").</summary>
    public bool Reactivates => FromState == GenerationState.Archived && State == GenerationState.Active;
}

/// <summary>
/// The rules of following Suno's Trash, restores, and missing clips (#142). A clip found in the Trash is
/// archived by sync; found listed again, it is reactivated only when sync archived it (a user's own archive
/// is never undone); found nowhere by a whole-library sync that read the library and the Trash to the end,
/// it is marked missing and nothing else changes. Transitions between the three remote states are direct.
/// Nothing here deletes anything, nor touches the Selected Generation or the retention process.
/// </summary>
public static class RemoteStateRules
{
    /// <summary>The scope of a whole-library sync, the only one that can find a clip missing.</summary>
    public const string LibraryScope = "library";

    /// <summary>
    /// Whether a sync can find a linked clip missing: only a whole-library sync that read both the library
    /// and the Trash to the end (the export's flags are trusted as sent). A scoped or incomplete one never can.
    /// </summary>
    public static bool ChecksMissing(SunoExportHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        return string.Equals(header.Scope, LibraryScope, StringComparison.Ordinal) && header.LibraryComplete && header.TrashedComplete;
    }

    /// <summary>
    /// What following <paramref name="seen"/> does to a Generation as it is now; null when its remote state
    /// already says so (no row). An archived Generation with no archiver counts as archived by the user.
    /// </summary>
    public static RemoteStateTransition? Transition(GenerationState state, GenerationArchiver? archivedBy, GenerationRemoteState remote, RemoteSighting seen)
    {
        var archiver = GenerationStates.ArchiverOf(state, archivedBy);
        switch (seen)
        {
            case RemoteSighting.Trashed when remote != GenerationRemoteState.Trashed:
                // An active Generation is archived by sync; one archived already keeps who archived it.
                return state == GenerationState.Active
                    ? new(RemoteStateChangeKind.Trashed, remote, GenerationRemoteState.Trashed, state, GenerationState.Archived, GenerationArchiver.Sync)
                    : new(RemoteStateChangeKind.Trashed, remote, GenerationRemoteState.Trashed, state, state, archiver);

            case RemoteSighting.Listed when remote != GenerationRemoteState.Present:
                // Only sync's own archive is undone.
                return archiver == GenerationArchiver.Sync
                    ? new(RemoteStateChangeKind.Restored, remote, GenerationRemoteState.Present, state, GenerationState.Active, null)
                    : new(RemoteStateChangeKind.Restored, remote, GenerationRemoteState.Present, state, state, archiver);

            case RemoteSighting.Unlisted when remote != GenerationRemoteState.Missing:
                return new(RemoteStateChangeKind.Missing, remote, GenerationRemoteState.Missing, state, state, archiver);

            default:
                return null;
        }
    }

    /// <summary>The API name of a change.</summary>
    public static string NameOf(RemoteStateChangeKind kind) => kind switch
    {
        RemoteStateChangeKind.Trashed => "trashed",
        RemoteStateChangeKind.Restored => "restored",
        RemoteStateChangeKind.Missing => "missing",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
