namespace n8Tracks.Domain.Songs;

/// <summary>Which way a new Version's number branches from its source's.</summary>
public enum VersionNumberKind
{
    /// <summary>Beside the source: its last part raised (<c>2</c> from <c>1</c>).</summary>
    Sibling,

    /// <summary>Under the source: a new last part (<c>1.1</c> from <c>1</c>).</summary>
    Child,
}

/// <summary>A number a new Version may take, and whether it is the one proposed.</summary>
public sealed record VersionNumberOption(VersionNumber Number, VersionNumberKind Kind, bool Proposed);

/// <summary>
/// The tree-numbering rules for a new Version. A number is <em>used</em> when any Version of the
/// Song has it or ever had it, archived and deleted ones included; a used number is never offered
/// again, so no two Versions of a Song ever share one, and no Version is ever renumbered.
/// </summary>
public static class VersionNumbering
{
    /// <summary>
    /// The numbers a Version created from <paramref name="source"/> may take, the proposal first:
    /// <list type="bullet">
    /// <item>the next sibling, the source's number with its last part raised to the first unused
    /// value above it (<c>1</c> → <c>2</c>, or <c>3</c> when <c>2</c> is used);</item>
    /// <item>the child, the source's number with a new last part, the first unused value from 1
    /// (<c>1</c> → <c>1.1</c>).</item>
    /// </list>
    /// The sibling is proposed when the number straight after the source's is unused; otherwise the
    /// child is. An option longer than <see cref="VersionNumber.MaximumLength"/>, or a sibling whose
    /// last part would pass <see cref="int.MaxValue"/>, is left out, and the one left is then the
    /// proposal. Empty when both are left out.
    /// </summary>
    public static IReadOnlyList<VersionNumberOption> Options(VersionNumber source, IEnumerable<VersionNumber> used)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(used);

        var taken = used.ToHashSet();
        var sibling = NextSibling(source, taken);
        var child = FirstChild(source, taken);

        var siblingProposed = sibling is not null && (sibling.Last == source.Last + 1 || child is null);
        var options = new List<VersionNumberOption>(2);
        if (sibling is not null && siblingProposed)
        {
            options.Add(new VersionNumberOption(sibling, VersionNumberKind.Sibling, Proposed: true));
        }

        if (child is not null)
        {
            options.Add(new VersionNumberOption(child, VersionNumberKind.Child, Proposed: !siblingProposed));
        }

        if (sibling is not null && !siblingProposed)
        {
            options.Add(new VersionNumberOption(sibling, VersionNumberKind.Sibling, Proposed: false));
        }

        return options;
    }

    /// <summary>
    /// The number a Song's replacement Version takes when its last Version is deleted: the next
    /// top-level number above every one ever used (<c>6</c> after Versions up to <c>5.x</c>), never
    /// <c>1</c> again, because shortcodes embed the number. <c>1</c> when nothing was ever used; null
    /// when the top level is exhausted.
    /// </summary>
    public static VersionNumber? NextTopLevel(IEnumerable<VersionNumber> used)
    {
        ArgumentNullException.ThrowIfNull(used);

        var highest = used.Select(static number => number.Parts[0]).DefaultIfEmpty(0).Max();
        return highest == int.MaxValue ? null : VersionNumber.TopLevel(highest + 1);
    }

    private static VersionNumber? NextSibling(VersionNumber source, HashSet<VersionNumber> taken)
    {
        for (var last = (long)source.Last + 1; last <= int.MaxValue; last++)
        {
            if (source.WithLast((int)last) is not { } candidate)
            {
                // Longer than the limit: every higher value is at least as long.
                return null;
            }

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static VersionNumber? FirstChild(VersionNumber source, HashSet<VersionNumber> taken)
    {
        // At most taken.Count values are skipped, so this never runs past int.MaxValue in practice.
        for (var last = 1L; last <= int.MaxValue; last++)
        {
            if (source.Child((int)last) is not { } candidate)
            {
                return null;
            }

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
