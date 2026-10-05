using n8Tracks.Application.Auth;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>
/// An edit of one model: each field is left alone when null. <paramref name="Name"/> is unread text;
/// <paramref name="Note"/> is unread text, empty to remove the note.
/// </summary>
public sealed record SunoModelEdit(string? Name, string? Note, bool? Retired);

/// <summary>How a change to the model list ended.</summary>
public abstract record ModelChangeOutcome
{
    private ModelChangeOutcome()
    {
    }

    /// <summary>The list as it is now: changed, at the next revision, or unchanged when the change changed nothing.</summary>
    /// <param name="Catalog">Every model, as it is now.</param>
    /// <param name="AffectedId">The model added, edited, or deleted.</param>
    public sealed record Changed(SunoModelCatalog Catalog, Guid? AffectedId = null) : ModelChangeOutcome;

    /// <summary>The list is at another revision than the change was based on. Nothing was changed.</summary>
    public sealed record Conflict(SunoModelCatalog Current) : ModelChangeOutcome;

    /// <summary>A field is wrong. Nothing was changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ModelChangeOutcome;

    /// <summary>There is no model with that ID.</summary>
    public sealed record NotFound : ModelChangeOutcome;

    /// <summary>The change would leave no model to offer. Nothing was changed.</summary>
    public sealed record LastOffered : ModelChangeOutcome;

    /// <summary>Versions name the model, so it cannot be renamed or deleted. Nothing was changed.</summary>
    public sealed record InUse(int VersionCount) : ModelChangeOutcome;

    /// <summary>A new order does not hold exactly the current models' IDs. Nothing was changed.</summary>
    public sealed record OrderMismatch(SunoModelCatalog Current) : ModelChangeOutcome;
}

/// <summary>
/// The Suno model list. The instance ships with <see cref="DefaultSunoModels.All"/>; the user adds,
/// renames, annotates, reorders, retires, restores, and deletes models by
/// <see cref="SunoModelRules"/>. A model a Version names is never renamed or deleted, so a Version's
/// model (frozen or not) always names a model on the list; retiring one only stops it being offered.
/// Every change names the list revision it was based on and raises it by one, checked and written in
/// one transaction, so a change made against a stale list is never applied. It is also the
/// <see cref="ISunoModelList"/> a Version's model is checked against.
/// </summary>
public sealed class ModelCatalogService(ISunoModelStore models, IExclusiveTransaction transaction, TimeProvider time) : ISunoModelList
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string NameField = "name";
    public const string NoteField = "note";
    public const string RetiredField = "retired";
    public const string IdsField = "ids";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await models.ListAsync(cancellationToken).ConfigureAwait(false)).Select(static model => model.Name)];

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> OfferedAsync(CancellationToken cancellationToken) =>
        [.. SunoModel.Offered(await models.ListAsync(cancellationToken).ConfigureAwait(false)).Select(static model => model.Name)];

    /// <summary>Every model in order with its Version count, and the list revision.</summary>
    public Task<SunoModelCatalog> ListWithUsageAsync(CancellationToken cancellationToken) => models.ListWithUsageAsync(cancellationToken);

    /// <summary>Adds a model at the end of the order, not retired, with an optional note.</summary>
    public Task<ModelChangeOutcome> AddAsync(string? name, string? note, int revision, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (SunoModelRules.NameErrors(name) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (SunoModelRules.NoteErrors(note) is { Length: > 0 } noteErrors)
        {
            errors[NoteField] = noteErrors;
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<ModelChangeOutcome>(new ModelChangeOutcome.Invalid(errors));
        }

        return ChangeAsync(
            revision,
            async (catalog, ct) =>
            {
                var all = catalog.Models.Select(static usage => usage.Model).ToList();
                if (IsTaken(all, name!, exceptId: null))
                {
                    return new ModelChangeOutcome.Invalid(NameTaken());
                }

                var model = new SunoModel(
                    Guid.CreateVersion7(time.GetUtcNow()),
                    SunoModelRules.NormaliseName(name!),
                    SunoModelRules.NormaliseNote(note),
                    all.Count == 0 ? 1 : all.Max(static existing => existing.Order) + 1,
                    Retired: false,
                    Discovered: false);
                await models.AddAsync(model, ct).ConfigureAwait(false);

                return new ModelChangeOutcome.Changed(catalog, model.Id);
            },
            cancellationToken);
    }

    /// <summary>
    /// Renames, annotates, retires, or restores a model; only the fields given change. A model a
    /// Version names cannot be renamed, and the last model not retired cannot be retired. A change
    /// that changes nothing is not written.
    /// </summary>
    public Task<ModelChangeOutcome> UpdateAsync(Guid id, SunoModelEdit edit, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (edit.Name is not null && SunoModelRules.NameErrors(edit.Name) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (edit.Note is not null && SunoModelRules.NoteErrors(edit.Note) is { Length: > 0 } noteErrors)
        {
            errors[NoteField] = noteErrors;
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<ModelChangeOutcome>(new ModelChangeOutcome.Invalid(errors));
        }

        return ChangeAsync(
            revision,
            async (catalog, ct) =>
            {
                var all = catalog.Models.Select(static usage => usage.Model).ToList();
                if (catalog.Models.FirstOrDefault(usage => usage.Model.Id == id) is not { } target)
                {
                    return new ModelChangeOutcome.NotFound();
                }

                var current = target.Model;
                if (edit.Name is not null && IsTaken(all, edit.Name, exceptId: id))
                {
                    return new ModelChangeOutcome.Invalid(NameTaken());
                }

                var changed = current with
                {
                    Name = edit.Name is null ? current.Name : SunoModelRules.NormaliseName(edit.Name),
                    Note = edit.Note is null ? current.Note : SunoModelRules.NormaliseNote(edit.Note),
                    Retired = edit.Retired ?? current.Retired,
                };
                if (changed == current)
                {
                    return null;
                }

                if (!string.Equals(changed.Name, current.Name, StringComparison.Ordinal) && target.VersionCount > 0)
                {
                    return new ModelChangeOutcome.InUse(target.VersionCount);
                }

                if (changed.Retired && !current.Retired && SunoModelRules.IsLastOffered(all, current))
                {
                    return new ModelChangeOutcome.LastOffered();
                }

                await models.UpdateAsync(changed, ct).ConfigureAwait(false);
                return new ModelChangeOutcome.Changed(catalog, id);
            },
            cancellationToken);
    }

    /// <summary>Puts the models in the order of <paramref name="ids"/>, which must hold exactly the current models' IDs, each once.</summary>
    public Task<ModelChangeOutcome> ReorderAsync(IReadOnlyList<Guid>? ids, int revision, CancellationToken cancellationToken)
    {
        if (ids is null)
        {
            return Task.FromResult<ModelChangeOutcome>(new ModelChangeOutcome.Invalid(
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [IdsField] = ["Send every model's ID, in the new order."] }));
        }

        return ChangeAsync(
            revision,
            async (catalog, ct) =>
            {
                var current = catalog.Models.Select(static usage => usage.Model.Id).ToList();
                if (ids.Count != current.Count || ids.Distinct().Count() != ids.Count || !ids.All(current.Contains))
                {
                    return new ModelChangeOutcome.OrderMismatch(catalog);
                }

                if (ids.SequenceEqual(current))
                {
                    return null;
                }

                await models.SetOrderAsync(ids, ct).ConfigureAwait(false);
                return new ModelChangeOutcome.Changed(catalog);
            },
            cancellationToken);
    }

    /// <summary>Deletes a model no Version names. The last model not retired cannot be deleted.</summary>
    public Task<ModelChangeOutcome> DeleteAsync(Guid id, int revision, CancellationToken cancellationToken) =>
        ChangeAsync(
            revision,
            async (catalog, ct) =>
            {
                if (catalog.Models.FirstOrDefault(usage => usage.Model.Id == id) is not { } target)
                {
                    return new ModelChangeOutcome.NotFound();
                }

                if (target.VersionCount > 0)
                {
                    return new ModelChangeOutcome.InUse(target.VersionCount);
                }

                if (SunoModelRules.IsLastOffered(catalog.Models.Select(static usage => usage.Model), target.Model))
                {
                    return new ModelChangeOutcome.LastOffered();
                }

                await models.DeleteAsync(id, ct).ConfigureAwait(false);
                return new ModelChangeOutcome.Changed(catalog, id);
            },
            cancellationToken);

    /// <summary>
    /// Runs <paramref name="change"/> in one transaction when the list is at <paramref name="revision"/>,
    /// then raises the revision when it changed anything and answers the list as it is now.
    /// <paramref name="change"/> answers null when there was nothing to change, a
    /// <see cref="ModelChangeOutcome.Changed"/> (its list is replaced) when it wrote, or any other
    /// outcome, which is answered as it is.
    /// </summary>
    private Task<ModelChangeOutcome> ChangeAsync(
        int revision,
        Func<SunoModelCatalog, CancellationToken, Task<ModelChangeOutcome?>> change,
        CancellationToken cancellationToken) =>
        transaction.RunAsync<ModelChangeOutcome>(
            async ct =>
            {
                var catalog = await models.ListWithUsageAsync(ct).ConfigureAwait(false);
                if (catalog.Revision != revision)
                {
                    return new ModelChangeOutcome.Conflict(catalog);
                }

                switch (await change(catalog, ct).ConfigureAwait(false))
                {
                    case null:
                        return new ModelChangeOutcome.Changed(catalog);

                    case ModelChangeOutcome.Changed changed:
                        await models.BumpRevisionAsync(ct).ConfigureAwait(false);
                        return changed with { Catalog = await models.ListWithUsageAsync(ct).ConfigureAwait(false) };

                    case var other:
                        return other;
                }
            },
            cancellationToken);

    private static bool IsTaken(IEnumerable<SunoModel> all, string name, Guid? exceptId)
    {
        var key = SunoModelRules.NameKey(name);
        return all.Any(model => model.Id != exceptId && string.Equals(SunoModelRules.NameKey(model.Name), key, StringComparison.Ordinal));
    }

    private static Dictionary<string, string[]> NameTaken() =>
        new(StringComparer.Ordinal) { [NameField] = [SunoModelRules.NameTakenMessage] };
}
