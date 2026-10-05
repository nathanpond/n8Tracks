using n8Tracks.Application.Persistence;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>Holds the state <see cref="DatabaseStartup"/> captured. One per host.</summary>
internal sealed class MigrationStateHolder : IMigrationStateProvider
{
    private volatile MigrationState? current;

    public MigrationState Current =>
        current ?? throw new InvalidOperationException("The migration state is not available until database startup has completed.");

    /// <summary>The outcome captured so far, or none before database startup has completed.</summary>
    public MigrationOutcome LastOutcome => current?.LastOutcome ?? MigrationOutcome.None;

    public void Set(MigrationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        current = state;
    }
}
