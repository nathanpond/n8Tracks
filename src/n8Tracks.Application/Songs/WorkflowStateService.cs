using n8Tracks.Application.Auth;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>
/// An edit of one workflow state: each field is left alone when null. <paramref name="Name"/> and
/// <paramref name="Colour"/> are unread text.
/// </summary>
public sealed record WorkflowStateEdit(string? Name, string? Colour, bool? Hidden);

/// <summary>How a change to the workflow states ended.</summary>
public abstract record WorkflowChangeOutcome
{
    private WorkflowChangeOutcome()
    {
    }

    /// <summary>The states as they are now: changed, at the next workflow revision, or unchanged when the change changed nothing.</summary>
    /// <param name="List">Every state, as it is now.</param>
    /// <param name="AffectedId">The state added, edited, or deleted.</param>
    /// <param name="MovedSongs">How many Songs a deletion moved to its replacement; 0 otherwise.</param>
    public sealed record Changed(WorkflowStateList List, Guid? AffectedId = null, int MovedSongs = 0) : WorkflowChangeOutcome;

    /// <summary>The workflow is at another revision than the change was based on. Nothing was changed.</summary>
    public sealed record Conflict(WorkflowStateList Current) : WorkflowChangeOutcome;

    /// <summary>A field is wrong. Nothing was changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : WorkflowChangeOutcome;

    /// <summary>There is no state with that ID.</summary>
    public sealed record NotFound : WorkflowChangeOutcome;

    /// <summary>The change would leave no visible state. Nothing was changed.</summary>
    public sealed record LastVisible : WorkflowChangeOutcome;

    /// <summary>The state has Songs and no replacement was given. Nothing was changed.</summary>
    public sealed record InUse(int SongCount) : WorkflowChangeOutcome;

    /// <summary>A new order does not hold exactly the current states' IDs. Nothing was changed.</summary>
    public sealed record OrderMismatch(WorkflowStateList Current) : WorkflowChangeOutcome;
}

/// <summary>
/// The Song workflow states. The instance ships with <see cref="DefaultWorkflowStates.All"/>; the
/// user adds, renames, recolours, reorders, hides, and deletes them by <see cref="WorkflowStateRules"/>.
/// Every change names the workflow revision it was based on and raises it by one, checked and
/// written in one transaction, so a change made against a stale list is never applied. A state
/// that has Songs is deleted only together with moving them all to a replacement.
/// </summary>
public sealed class WorkflowStateService(IWorkflowStateStore states, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string NameField = "name";
    public const string ColourField = "colour";
    public const string HiddenField = "hidden";
    public const string IdsField = "ids";
    public const string ReplacementField = "replacement";

    /// <summary>Every state, hidden ones included, in order.</summary>
    public Task<IReadOnlyList<WorkflowState>> ListAsync(CancellationToken cancellationToken) => states.ListAsync(cancellationToken);

    /// <summary>Every state in order with its Song count, and the workflow revision.</summary>
    public Task<WorkflowStateList> ListWithUsageAsync(CancellationToken cancellationToken) => states.ListWithUsageAsync(cancellationToken);

    /// <summary>
    /// Adds a state at the end of the order, visible, with <paramref name="colour"/> or, when none is
    /// given, the first palette colour no state has.
    /// </summary>
    public Task<WorkflowChangeOutcome> AddAsync(string? name, string? colour, int revision, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (WorkflowStateRules.NameErrors(name) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (colour is not null && !StateColours.IsKnown(colour))
        {
            errors[ColourField] = [ColourMessage];
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<WorkflowChangeOutcome>(new WorkflowChangeOutcome.Invalid(errors));
        }

        return ChangeAsync(
            revision,
            async (list, ct) =>
            {
                var all = list.States.Select(static usage => usage.State).ToList();
                if (IsTaken(all, name!, exceptId: null))
                {
                    return new WorkflowChangeOutcome.Invalid(NameTaken());
                }

                var now = time.GetUtcNow();
                var state = new WorkflowState(
                    Guid.CreateVersion7(now),
                    WorkflowStateRules.NormaliseName(name!),
                    colour ?? WorkflowStateRules.NextColour(all),
                    all.Count == 0 ? 1 : all.Max(static existing => existing.Order) + 1,
                    Hidden: false);
                await states.AddAsync(state, ct).ConfigureAwait(false);

                return new WorkflowChangeOutcome.Changed(list, state.Id);
            },
            cancellationToken);
    }

    /// <summary>
    /// Renames, recolours, hides, or shows a state; only the fields given change. The last visible
    /// state cannot be hidden. A change that changes nothing is not written.
    /// </summary>
    public Task<WorkflowChangeOutcome> UpdateAsync(Guid id, WorkflowStateEdit edit, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (edit.Name is not null && WorkflowStateRules.NameErrors(edit.Name) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (edit.Colour is not null && !StateColours.IsKnown(edit.Colour))
        {
            errors[ColourField] = [ColourMessage];
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<WorkflowChangeOutcome>(new WorkflowChangeOutcome.Invalid(errors));
        }

        return ChangeAsync(
            revision,
            async (list, ct) =>
            {
                var all = list.States.Select(static usage => usage.State).ToList();
                if (all.Find(state => state.Id == id) is not { } current)
                {
                    return new WorkflowChangeOutcome.NotFound();
                }

                if (edit.Name is not null && IsTaken(all, edit.Name, exceptId: id))
                {
                    return new WorkflowChangeOutcome.Invalid(NameTaken());
                }

                if (edit.Hidden == true && WorkflowStateRules.IsLastVisible(all, current))
                {
                    return new WorkflowChangeOutcome.LastVisible();
                }

                var changed = current with
                {
                    Name = edit.Name is null ? current.Name : WorkflowStateRules.NormaliseName(edit.Name),
                    Colour = edit.Colour ?? current.Colour,
                    Hidden = edit.Hidden ?? current.Hidden,
                };
                if (changed == current)
                {
                    return null;
                }

                await states.UpdateAsync(changed, ct).ConfigureAwait(false);
                return new WorkflowChangeOutcome.Changed(list, id);
            },
            cancellationToken);
    }

    /// <summary>
    /// Puts the states in the order of <paramref name="ids"/>, which must hold exactly the current
    /// states' IDs, each once.
    /// </summary>
    public Task<WorkflowChangeOutcome> ReorderAsync(IReadOnlyList<Guid>? ids, int revision, CancellationToken cancellationToken)
    {
        if (ids is null)
        {
            return Task.FromResult<WorkflowChangeOutcome>(new WorkflowChangeOutcome.Invalid(
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [IdsField] = ["Send every state's ID, in the new order."] }));
        }

        return ChangeAsync(
            revision,
            async (list, ct) =>
            {
                var current = list.States.Select(static usage => usage.State.Id).ToList();
                if (ids.Count != current.Count || ids.Distinct().Count() != ids.Count || !ids.All(current.Contains))
                {
                    return new WorkflowChangeOutcome.OrderMismatch(list);
                }

                if (ids.SequenceEqual(current))
                {
                    return null;
                }

                await states.SetOrderAsync(ids, ct).ConfigureAwait(false);
                return new WorkflowChangeOutcome.Changed(list);
            },
            cancellationToken);
    }

    /// <summary>
    /// Deletes a state. One with no Songs goes at once; one with Songs needs
    /// <paramref name="replacement"/>, the ID of another state (hidden or not), and its Songs are
    /// moved there in the same transaction. The last visible state cannot be deleted.
    /// </summary>
    public Task<WorkflowChangeOutcome> DeleteAsync(Guid id, string? replacement, int revision, CancellationToken cancellationToken)
    {
        Guid? replacementId = null;
        if (replacement is not null)
        {
            if (!Guid.TryParseExact(replacement, "D", out var parsed))
            {
                return Task.FromResult<WorkflowChangeOutcome>(new WorkflowChangeOutcome.Invalid(ReplacementInvalid()));
            }

            replacementId = parsed;
        }

        return ChangeAsync(
            revision,
            async (list, ct) =>
            {
                if (list.States.FirstOrDefault(usage => usage.State.Id == id) is not { } target)
                {
                    return new WorkflowChangeOutcome.NotFound();
                }

                if (replacementId is { } chosen && (chosen == id || list.States.All(usage => usage.State.Id != chosen)))
                {
                    return new WorkflowChangeOutcome.Invalid(ReplacementInvalid());
                }

                if (WorkflowStateRules.IsLastVisible(list.States.Select(static usage => usage.State), target.State))
                {
                    return new WorkflowChangeOutcome.LastVisible();
                }

                if (target.SongCount > 0 && replacementId is null)
                {
                    return new WorkflowChangeOutcome.InUse(target.SongCount);
                }

                var moved = await states.DeleteAsync(id, replacementId, time.GetUtcNow(), ct).ConfigureAwait(false);
                return new WorkflowChangeOutcome.Changed(list, id, moved);
            },
            cancellationToken);
    }

    private const string ColourMessage = "Choose one of the palette colours.";

    /// <summary>
    /// Runs <paramref name="change"/> in one transaction when the workflow is at
    /// <paramref name="revision"/>, then raises the revision when it changed anything and answers the
    /// list as it is now. <paramref name="change"/> answers null when there was nothing to change, a
    /// <see cref="WorkflowChangeOutcome.Changed"/> (its list is replaced) when it wrote, or any other
    /// outcome, which is answered as it is.
    /// </summary>
    private Task<WorkflowChangeOutcome> ChangeAsync(
        int revision,
        Func<WorkflowStateList, CancellationToken, Task<WorkflowChangeOutcome?>> change,
        CancellationToken cancellationToken) =>
        transaction.RunAsync<WorkflowChangeOutcome>(
            async ct =>
            {
                var list = await states.ListWithUsageAsync(ct).ConfigureAwait(false);
                if (list.Revision != revision)
                {
                    return new WorkflowChangeOutcome.Conflict(list);
                }

                switch (await change(list, ct).ConfigureAwait(false))
                {
                    case null:
                        return new WorkflowChangeOutcome.Changed(list);

                    case WorkflowChangeOutcome.Changed changed:
                        await states.BumpRevisionAsync(ct).ConfigureAwait(false);
                        return changed with { List = await states.ListWithUsageAsync(ct).ConfigureAwait(false) };

                    case var other:
                        return other;
                }
            },
            cancellationToken);

    private static bool IsTaken(IEnumerable<WorkflowState> all, string name, Guid? exceptId)
    {
        var key = WorkflowStateRules.NameKey(name);
        return all.Any(state => state.Id != exceptId && string.Equals(WorkflowStateRules.NameKey(state.Name), key, StringComparison.Ordinal));
    }

    private static Dictionary<string, string[]> NameTaken() =>
        new(StringComparer.Ordinal) { [NameField] = [WorkflowStateRules.NameTakenMessage] };

    private static Dictionary<string, string[]> ReplacementInvalid() =>
        new(StringComparer.Ordinal) { [ReplacementField] = ["Choose another existing state to move its Songs to."] };
}
