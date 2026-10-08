using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Auth;

namespace n8Tracks.Application.Dashboard;

/// <summary>The dashboard's sections by key (#228, #229), as the API names them and the arrangement (#230) stores them.</summary>
public static class DashboardSectionKeys
{
    public const string RecentlyEdited = "recentlyEdited";
    public const string WorkflowStates = "workflowStates";
    public const string WithoutSelection = "withoutSelection";
    public const string UnmatchedFiles = "unmatchedFiles";
    public const string SunoReviews = "sunoReviews";
    public const string SunoProblems = "sunoProblems";

    /// <summary>
    /// Every section, in the default order: what the user changed last, then what needs attention, then
    /// where the Songs stand. A section a later version adds goes at the end of this list, and so
    /// appears at the end of an arrangement saved before it.
    /// </summary>
    public static IReadOnlyList<string> DefaultOrder { get; } =
        [RecentlyEdited, UnmatchedFiles, SunoReviews, SunoProblems, WorkflowStates, WithoutSelection];

    /// <summary>Whether <paramref name="key"/> names a section this version has.</summary>
    public static bool IsKnown(string key) => DefaultOrder.Contains(key, StringComparer.Ordinal);
}

/// <summary>Where one section is in the arrangement, and whether it is hidden.</summary>
/// <param name="Key">One of <see cref="DashboardSectionKeys"/>.</param>
/// <param name="Hidden">Hidden sections are not shown, but still counted by the badges (#233).</param>
public sealed record DashboardSectionPlacement(string Key, bool Hidden);

/// <summary>
/// The dashboard's arrangement (#230): every section this version has, each once, in the order the
/// user chose, each shown or hidden. Quick actions are not a section: they are always shown.
/// </summary>
public sealed record DashboardLayout
{
    public const string SectionsField = "sections";

    /// <summary>The most placements a client may send; far more than there are sections.</summary>
    public const int MaximumPlacements = 100;

    private DashboardLayout(IReadOnlyList<DashboardSectionPlacement> sections) => Sections = sections;

    /// <summary>Each section, once, in order.</summary>
    public IReadOnlyList<DashboardSectionPlacement> Sections { get; }

    /// <summary>Every section shown, in <see cref="DashboardSectionKeys.DefaultOrder"/>.</summary>
    public static DashboardLayout Default { get; } = Arrange([]);

    /// <summary>
    /// The arrangement <paramref name="placements"/> make: a key this version does not have is
    /// ignored, a key given again after its first placement is ignored, and each section not placed
    /// is appended, shown, in the default order. Never fails.
    /// </summary>
    public static DashboardLayout Arrange(IEnumerable<DashboardSectionPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);

        var placed = new List<DashboardSectionPlacement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in placements)
        {
            if (DashboardSectionKeys.IsKnown(placement.Key) && seen.Add(placement.Key))
            {
                placed.Add(placement);
            }
        }

        foreach (var key in DashboardSectionKeys.DefaultOrder)
        {
            if (seen.Add(key))
            {
                placed.Add(new DashboardSectionPlacement(key, Hidden: false));
            }
        }

        return new DashboardLayout(placed);
    }

    /// <summary>
    /// Reads an arrangement as a client sends it: <c>sections</c> is a list of
    /// <c>{ key, hidden }</c>, <c>key</c> text and <c>hidden</c> true or false, at most
    /// <see cref="MaximumPlacements"/> of them. Unknown and repeated keys are then ignored and missing
    /// ones appended, as <see cref="Arrange"/> does. The errors are keyed by field; empty when valid.
    /// </summary>
    public static (DashboardLayout? Layout, Dictionary<string, string[]> Errors) Parse(JsonElement sections)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (sections.ValueKind != JsonValueKind.Array || sections.GetArrayLength() > MaximumPlacements)
        {
            errors[SectionsField] = [string.Create(
                CultureInfo.InvariantCulture,
                $"Send the sections as a list of at most {MaximumPlacements} {{ key, hidden }}.")];
            return (null, errors);
        }

        var placements = new List<DashboardSectionPlacement>();
        var index = 0;
        foreach (var item in sections.EnumerateArray())
        {
            var field = string.Create(CultureInfo.InvariantCulture, $"{SectionsField}[{index}]");
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("key", out var key)
                || key.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("hidden", out var hidden)
                || hidden.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors[field] = ["Send each section as { key, hidden }: key is text, hidden is true or false."];
            }
            else
            {
                placements.Add(new DashboardSectionPlacement(key.GetString()!, hidden.GetBoolean()));
            }

            index++;
        }

        return errors.Count > 0 ? (null, errors) : (Arrange(placements), errors);
    }
}

/// <summary>The arrangement as it is stored.</summary>
/// <param name="Placements">What was saved, before <see cref="DashboardLayout.Arrange"/>; null when it was reset (the default applies).</param>
/// <param name="Revision">0 until it is first saved, then 1, 2, …; a reset counts as a save.</param>
public sealed record StoredDashboardLayout(IReadOnlyList<DashboardSectionPlacement>? Placements, int Revision)
{
    /// <summary>What an instance that never saved an arrangement has: the default, at revision 0.</summary>
    public static readonly StoredDashboardLayout Unstored = new(null, 0);
}

/// <summary>The arrangement as the API answers it.</summary>
/// <param name="Layout">The sections in order; the default when <paramref name="Customized"/> is false.</param>
/// <param name="Customized">Whether a saved arrangement applies, rather than the default.</param>
/// <param name="Revision">The stored revision.</param>
public sealed record DashboardLayoutView(DashboardLayout Layout, bool Customized, int Revision)
{
    public static DashboardLayoutView Of(StoredDashboardLayout stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return stored.Placements is { } placements
            ? new DashboardLayoutView(DashboardLayout.Arrange(placements), Customized: true, stored.Revision)
            : new DashboardLayoutView(DashboardLayout.Default, Customized: false, stored.Revision);
    }
}

/// <summary>Where the arrangement is kept.</summary>
public interface IDashboardLayoutStore
{
    /// <summary>The stored arrangement, or null when it was never saved.</summary>
    Task<StoredDashboardLayout?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the stored arrangement.</summary>
    Task WriteAsync(StoredDashboardLayout layout, CancellationToken cancellationToken);
}

/// <summary>How a change of the arrangement ended.</summary>
public abstract record DashboardLayoutUpdate
{
    private DashboardLayoutUpdate()
    {
    }

    /// <summary>Written; this is the arrangement now.</summary>
    public sealed record Updated(DashboardLayoutView Current) : DashboardLayoutUpdate;

    /// <summary>The revision sent is not the current one; nothing was written.</summary>
    public sealed record Stale(DashboardLayoutView Current) : DashboardLayoutUpdate;
}

/// <summary>
/// The dashboard's arrangement (#230): which sections show, and in what order. It is the instance's
/// one user's, so it is one <c>settings</c> row and applies on every browser. Saving and resetting
/// carry the revision read, as every setting does; a reset keeps the row with no arrangement, so the
/// revision keeps counting up and a stale save from before it is still refused.
/// </summary>
public sealed class DashboardLayoutService(IDashboardLayoutStore store, IExclusiveTransaction transaction)
{
    /// <summary>The arrangement: the saved one, or the default at revision 0 when none was ever saved.</summary>
    public async Task<DashboardLayoutView> GetAsync(CancellationToken cancellationToken) =>
        DashboardLayoutView.Of(await StoredAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Saves <paramref name="layout"/> when <paramref name="revision"/> is the current one (0 before the first save).</summary>
    public Task<DashboardLayoutUpdate> UpdateAsync(DashboardLayout layout, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return WriteAsync(layout.Sections, revision, cancellationToken);
    }

    /// <summary>
    /// Clears the saved arrangement when <paramref name="revision"/> is the current one, so the default
    /// order of whichever version runs applies.
    /// </summary>
    public Task<DashboardLayoutUpdate> ResetAsync(int revision, CancellationToken cancellationToken) =>
        WriteAsync(null, revision, cancellationToken);

    private Task<DashboardLayoutUpdate> WriteAsync(IReadOnlyList<DashboardSectionPlacement>? placements, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<DashboardLayoutUpdate>(
            async token =>
            {
                var current = await StoredAsync(token).ConfigureAwait(false);
                if (current.Revision != revision)
                {
                    return new DashboardLayoutUpdate.Stale(DashboardLayoutView.Of(current));
                }

                var updated = new StoredDashboardLayout(placements, current.Revision + 1);
                await store.WriteAsync(updated, token).ConfigureAwait(false);
                return new DashboardLayoutUpdate.Updated(DashboardLayoutView.Of(updated));
            },
            cancellationToken);

    private async Task<StoredDashboardLayout> StoredAsync(CancellationToken cancellationToken) =>
        await store.FindAsync(cancellationToken).ConfigureAwait(false) ?? StoredDashboardLayout.Unstored;
}
