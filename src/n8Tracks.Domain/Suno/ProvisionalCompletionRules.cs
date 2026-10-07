namespace n8Tracks.Domain.Suno;

/// <summary>
/// When a Generation that an observed Create made (#149) may be filled in once Suno finishes its clip
/// (#154): the one change to a Generation's provider fields that no import review confirms (invariant 3).
/// Suno's statuses for a clip are <c>submitted</c>, <c>streaming</c>, <c>complete</c>, and <c>error</c>
/// (TS-001); the last two are final. n8Tracks writes a Generation's status only when it is attached and
/// by this completion, never by an import's refresh, so a Generation whose stored status is not final has
/// never been complete in n8Tracks, unless an import review has decided about its data since (its raw
/// clip replaced from an export, or a change declined or a conflict kept), which the store checks too.
/// </summary>
public static class ProvisionalCompletionRules
{
    /// <summary>Suno's status for a finished clip.</summary>
    public const string Complete = "complete";

    /// <summary>Suno's status for a clip that ended in error: final too, shown as Failed.</summary>
    public const string Error = "error";

    /// <summary>Whether <paramref name="status"/> is one a clip ends in: <see cref="Complete"/> or <see cref="Error"/>.</summary>
    public static bool IsFinal(string? status) =>
        string.Equals(status, Complete, StringComparison.Ordinal) || string.Equals(status, Error, StringComparison.Ordinal);

    /// <summary>
    /// Whether a Generation whose stored clip is <paramref name="stored"/> may take
    /// <paramref name="incoming"/> as its completion: the same clip, finished now, never finished before.
    /// </summary>
    public static bool MayComplete(ClipFields stored, ClipFields incoming)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(incoming);

        return string.Equals(stored.SunoId, incoming.SunoId, StringComparison.Ordinal)
            && !IsFinal(stored.Status)
            && IsFinal(incoming.Status);
    }
}
