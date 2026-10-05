using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>
/// The Song workflow states. The instance ships with <see cref="DefaultWorkflowStates.All"/>;
/// managing them (add, rename, reorder, recolour, hide, delete) is a later story's.
/// </summary>
public sealed class WorkflowStateService(IWorkflowStateStore states)
{
    /// <summary>Every state, hidden ones included, in order.</summary>
    public Task<IReadOnlyList<WorkflowState>> ListAsync(CancellationToken cancellationToken) => states.ListAsync(cancellationToken);
}
