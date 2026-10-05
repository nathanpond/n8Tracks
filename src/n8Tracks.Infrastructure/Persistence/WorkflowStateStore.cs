using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The workflow states in <c>workflow_states</c>, and the revision of the workflow as a whole in the
/// one <c>settings</c> row with key <see cref="RevisionKey"/>, whose value is
/// <c>{"revision": n}</c>. There is no such row until the first change: the workflow is then at
/// revision 1.
/// </summary>
internal sealed class WorkflowStateStore(N8TracksDbContext context) : IWorkflowStateStore
{
    public const string RevisionKey = "workflow";

    public async Task<IReadOnlyList<WorkflowState>> ListAsync(CancellationToken cancellationToken)
    {
        var records = await context.WorkflowStates.AsNoTracking()
            .OrderBy(static state => state.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. records.Select(ToState)];
    }

    public async Task<WorkflowStateList> ListWithUsageAsync(CancellationToken cancellationToken)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        var counts = await context.Songs.AsNoTracking()
            .GroupBy(static song => song.WorkflowStateId)
            .Select(static group => new { StateId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static group => group.StateId, static group => group.Count, cancellationToken)
            .ConfigureAwait(false);

        return new WorkflowStateList(
            await RevisionAsync(cancellationToken).ConfigureAwait(false),
            [.. all.Select(state => new WorkflowStateUsage(state, counts.GetValueOrDefault(state.Id)))]);
    }

    public async Task BumpRevisionAsync(CancellationToken cancellationToken)
    {
        var next = JsonSerializer.Serialize(new RevisionValue(await RevisionAsync(cancellationToken).ConfigureAwait(false) + 1));

        var updated = await context.Settings
            .Where(static setting => setting.Key == RevisionKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(static setting => setting.Value, next), cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            context.Settings.Add(new SettingRecord { Key = RevisionKey, Value = next });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
        }
    }

    public async Task AddAsync(WorkflowState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        context.WorkflowStates.Add(new WorkflowStateRecord
        {
            Id = state.Id,
            Name = state.Name,
            NameKey = WorkflowStateRules.NameKey(state.Name),
            Colour = state.Colour,
            Position = state.Order,
            Hidden = state.Hidden,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    public async Task UpdateAsync(WorkflowState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        var name = state.Name;
        var nameKey = WorkflowStateRules.NameKey(name);
        var colour = state.Colour;
        var hidden = state.Hidden;
        await context.WorkflowStates
            .Where(record => record.Id == state.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static record => record.Name, name)
                    .SetProperty(static record => record.NameKey, nameKey)
                    .SetProperty(static record => record.Colour, colour)
                    .SetProperty(static record => record.Hidden, hidden),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetOrderAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // Positions are unique, so every state first moves past the highest position there is, out
        // of the way, and then to its place.
        var offset = await context.WorkflowStates.MaxAsync(static state => (int?)state.Position, cancellationToken).ConfigureAwait(false) ?? 0;
        await context.WorkflowStates
            .ExecuteUpdateAsync(setters => setters.SetProperty(static state => state.Position, state => state.Position + offset), cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            var position = index + 1;
            await context.WorkflowStates
                .Where(state => state.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(static state => state.Position, position), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<int> DeleteAsync(Guid id, Guid? replacementId, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        var moved = 0;
        if (replacementId is { } replacement)
        {
            var updated = UtcText.From(updatedUtc);
            moved = await context.Songs
                .Where(song => song.WorkflowStateId == id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static song => song.WorkflowStateId, replacement)
                        .SetProperty(static song => song.UpdatedUtc, updated)
                        .SetProperty(static song => song.Revision, static song => song.Revision + 1),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // A state that still has Songs cannot go: the foreign key from songs refuses it.
        await context.WorkflowStates.Where(state => state.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // The rest close up, so positions stay 1 to n.
        var remaining = await context.WorkflowStates.AsNoTracking()
            .OrderBy(static state => state.Position)
            .Select(static state => state.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetOrderAsync(remaining, cancellationToken).ConfigureAwait(false);

        return moved;
    }

    private async Task<int> RevisionAsync(CancellationToken cancellationToken)
    {
        var value = await context.Settings.AsNoTracking()
            .Where(static setting => setting.Key == RevisionKey)
            .Select(static setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return value is null
            ? 1
            : JsonSerializer.Deserialize<RevisionValue>(value)?.Revision ?? throw new InvalidOperationException("The workflow record holds no revision.");
    }

    private static WorkflowState ToState(WorkflowStateRecord state) => new(state.Id, state.Name, state.Colour, state.Position, state.Hidden);

    /// <summary>The value of the <see cref="RevisionKey"/> setting.</summary>
    private sealed record RevisionValue([property: System.Text.Json.Serialization.JsonPropertyName("revision")] int Revision);
}
