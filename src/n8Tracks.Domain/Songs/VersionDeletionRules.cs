namespace n8Tracks.Domain.Songs;

/// <summary>A live Version as the deletion rules read it: its number and whether it is archived.</summary>
/// <param name="Number">Its hierarchical number.</param>
/// <param name="Archived">Whether it is archived.</param>
public sealed record LiveVersionNumber(VersionNumber Number, bool Archived);

/// <summary>
/// The rules of deleting one Version (#101): its descendants stay where they are and keep their
/// numbers, a number that has no live Version but has live descendants is drawn as a "Deleted
/// Version" placeholder, and a Song never ends up without a Version.
/// </summary>
public static class VersionDeletionRules
{
    /// <summary>Whether <paramref name="descendant"/> is below <paramref name="ancestor"/> in the tree (<c>1.1.1</c> below <c>1</c>).</summary>
    public static bool IsDescendant(VersionNumber descendant, VersionNumber ancestor)
    {
        ArgumentNullException.ThrowIfNull(descendant);
        ArgumentNullException.ThrowIfNull(ancestor);

        return descendant.Depth > ancestor.Depth && descendant.Parts.Take(ancestor.Depth).SequenceEqual(ancestor.Parts);
    }

    /// <summary>How many of <paramref name="live"/> are descendants of <paramref name="deleted"/>: the Versions that remain under its placeholder.</summary>
    public static int RemainingDescendants(VersionNumber deleted, IEnumerable<VersionNumber> live)
    {
        ArgumentNullException.ThrowIfNull(live);

        return live.Count(number => IsDescendant(number, deleted));
    }

    /// <summary>
    /// The Version that becomes current when the current one, <paramref name="deleted"/>, is deleted
    /// and <paramref name="remaining"/> are left: its nearest remaining ancestor that is not archived;
    /// else the lowest-numbered remaining Version that is not archived; else the lowest-numbered
    /// remaining Version. Null when none remains (the last Version: a blank one is created).
    /// </summary>
    public static VersionNumber? NewCurrent(VersionNumber deleted, IReadOnlyCollection<LiveVersionNumber> remaining)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        ArgumentNullException.ThrowIfNull(remaining);

        var left = remaining.Where(version => version.Number != deleted).ToList();
        if (left.Count == 0)
        {
            return null;
        }

        for (var ancestor = deleted.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            var candidate = ancestor;
            if (left.Any(version => version.Number == candidate && !version.Archived))
            {
                return candidate;
            }
        }

        return left.Where(static version => !version.Archived).Select(static version => version.Number).Min()
            ?? left.Select(static version => version.Number).Min();
    }

    /// <summary>
    /// The numbers drawn as "Deleted Version" placeholders, in tree order: each number of
    /// <paramref name="used"/> that no live Version has and that has at least one live descendant
    /// (archived ones included).
    /// </summary>
    public static IReadOnlyList<VersionNumber> Placeholders(IEnumerable<VersionNumber> used, IReadOnlyCollection<VersionNumber> live)
    {
        ArgumentNullException.ThrowIfNull(used);
        ArgumentNullException.ThrowIfNull(live);

        var liveSet = live.ToHashSet();
        return [.. used
            .Distinct()
            .Where(number => !liveSet.Contains(number) && live.Any(other => IsDescendant(other, number)))
            .Order()];
    }

    /// <summary>
    /// The blank, mutable Version created when a Song's last Version is deleted: numbered
    /// <paramref name="number"/> (the next never-used top-level number), with no name, notes, lyrics,
    /// or styles, active, at revision 1, holding <paramref name="inputs"/> (the options a new Song's
    /// Version 1 would start with).
    /// </summary>
    public static SongVersion Blank(Guid id, Guid songId, VersionNumber number, VersionInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(inputs);

        return new SongVersion(
            id,
            songId,
            number.ToString(),
            Name: null,
            Notes: null,
            VersionVisibility.Active,
            Lyrics: string.Empty,
            Styles: string.Empty,
            inputs,
            now,
            now,
            Revision: 1,
            VersionLineage.None);
    }
}
