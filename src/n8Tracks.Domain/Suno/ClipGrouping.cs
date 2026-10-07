namespace n8Tracks.Domain.Suno;

/// <summary>What grouping reads of a clip: its Suno ID, workspace, when Suno created it, and its <c>batch_index</c>.</summary>
public sealed record GroupableClip(string SunoId, string? WorkspaceId, DateTimeOffset? CreatedUtc, int? BatchIndex);

/// <summary>
/// Which clips one Suno Create request is thought to have made, by spike TS-001's rule (inferred tier):
/// clips in the same workspace whose <c>created_at</c> is within one second of each other and whose
/// <c>batch_index</c> values are distinct, starting at 0. Suno sends no request ID with a clip, so this is
/// only ever a suggestion (#138), and timestamp proximity alone never groups.
/// <para>
/// A group is anchored on its earliest clip: the others are within one second after it, taken in time
/// order, and a clip whose <c>batch_index</c> is already in the group is left for a later group. A set
/// without index 0, or with one clip, is not a group: its anchor stands alone and the rest are tried
/// again. A clip with no workspace, no creation time, or no <c>batch_index</c> stands alone.
/// </para>
/// </summary>
public static class ClipGrouping
{
    /// <summary>How far after the anchor a clip of its group may be created.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Every clip, in exactly one group: the groups in order of their first clip (by creation time, then
    /// Suno ID, with clips that cannot be grouped after the rest), each group's clips by
    /// <c>batch_index</c>.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<GroupableClip>> Group(IEnumerable<GroupableClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var groups = new List<IReadOnlyList<GroupableClip>>();
        var all = clips.ToList();
        var groupable = all
            .Where(static clip => clip.WorkspaceId is not null && clip.CreatedUtc is not null && clip.BatchIndex is not null)
            .ToList();
        foreach (var workspace in groupable.GroupBy(static clip => clip.WorkspaceId!, StringComparer.Ordinal))
        {
            var waiting = workspace
                .OrderBy(static clip => clip.CreatedUtc)
                .ThenBy(static clip => clip.BatchIndex)
                .ThenBy(static clip => clip.SunoId, StringComparer.Ordinal)
                .ToList();
            while (waiting.Count > 0)
            {
                var anchor = waiting[0];
                var members = new List<GroupableClip> { anchor };
                var indexes = new HashSet<int> { anchor.BatchIndex!.Value };
                foreach (var clip in waiting.Skip(1).TakeWhile(clip => clip.CreatedUtc - anchor.CreatedUtc <= Window))
                {
                    if (indexes.Add(clip.BatchIndex!.Value))
                    {
                        members.Add(clip);
                    }
                }

                if (members.Count < 2 || !indexes.Contains(0))
                {
                    members = [anchor];
                }

                waiting.RemoveAll(members.Contains);
                groups.Add([.. members.OrderBy(static clip => clip.BatchIndex).ThenBy(static clip => clip.SunoId, StringComparer.Ordinal)]);
            }
        }

        var ordered = groups
            .OrderBy(static group => group.Min(static clip => clip.CreatedUtc))
            .ThenBy(static group => group.Min(static clip => clip.SunoId), StringComparer.Ordinal)
            .ToList();
        ordered.AddRange(all
            .Except(groupable)
            .OrderBy(static clip => clip.CreatedUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(static clip => clip.SunoId, StringComparer.Ordinal)
            .Select(static clip => (IReadOnlyList<GroupableClip>)[clip]));
        return ordered;
    }
}
