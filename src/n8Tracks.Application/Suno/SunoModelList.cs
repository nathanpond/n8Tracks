using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>
/// The Suno models a Version's model may name, retired ones included. A Version's model is checked
/// against this list, not against the inventory, because Suno adds and retires models faster than
/// n8Tracks is released. <see cref="ModelCatalogService"/> keeps it.
/// </summary>
public interface ISunoModelList
{
    /// <summary>Every model name, retired ones included, in the list's order: what a Version's model is checked against.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The names offered for a new choice: the models not retired, in the list's order.</summary>
    Task<IReadOnlyList<string>> OfferedAsync(CancellationToken cancellationToken);
}

/// <summary>A model and how many Versions name it (as a Song's model or a Sound's).</summary>
public sealed record SunoModelUsage(SunoModel Model, int VersionCount);

/// <summary>Every model in order, with the revision of the list as a whole.</summary>
/// <param name="Revision">The revision of the one model-list record: 1 until the first change, then one more per change.</param>
/// <param name="Models">Every model, retired ones included, in order, each with its Version count.</param>
public sealed record SunoModelCatalog(int Revision, IReadOnlyList<SunoModelUsage> Models);

/// <summary>
/// Where the model list is kept, with its revision. Every write is made inside the caller's
/// transaction, which has checked the revision first.
/// </summary>
public interface ISunoModelStore
{
    /// <summary>Every model, retired ones included, in order.</summary>
    Task<IReadOnlyList<SunoModel>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Every model in order with its Version count, and the list revision.</summary>
    Task<SunoModelCatalog> ListWithUsageAsync(CancellationToken cancellationToken);

    /// <summary>Raises the list revision by one, creating the record (at revision 1) first if there is none.</summary>
    Task BumpRevisionAsync(CancellationToken cancellationToken);

    /// <summary>Stores a new model. Its name and position must be unused; the database refuses a taken one.</summary>
    Task AddAsync(SunoModel model, CancellationToken cancellationToken);

    /// <summary>Stores the name, note, and retired flag of the model with <paramref name="model"/>'s ID; its position stays.</summary>
    Task UpdateAsync(SunoModel model, CancellationToken cancellationToken);

    /// <summary>Puts the models in the order of <paramref name="ids"/>, which holds every model's ID once.</summary>
    Task SetOrderAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Removes the model with <paramref name="id"/>; the rest close up. No Version may name it.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
