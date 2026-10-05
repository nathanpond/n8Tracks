namespace n8Tracks.Application.Suno;

/// <summary>
/// The Suno models a Version's model may name, retired ones included. A Version's model is checked
/// against this list, not against the inventory, because Suno adds and retires models faster than
/// n8Tracks is released.
/// </summary>
public interface ISunoModelList
{
    /// <summary>Every model name, in the list's order.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The model list until the user can manage it (the model-list story replaces this registration): the
/// models the inventory's <c>model</c> field offers, which is what that story seeds the list with.
/// </summary>
internal sealed class InventoryModelList : ISunoModelList
{
    /// <summary>The inventory key of the Songs model field.</summary>
    public const string ModelKey = "model";

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CreateFieldInventory.Embedded.Get(ModelKey).Values ?? []);
}
