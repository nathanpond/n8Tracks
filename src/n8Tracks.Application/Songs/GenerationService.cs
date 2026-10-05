using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>How attaching a Generation ended.</summary>
public abstract record GenerationAttachOutcome
{
    private GenerationAttachOutcome()
    {
    }

    /// <summary>The new Generation; its Version is now frozen.</summary>
    public sealed record Attached(GenerationSummary Generation) : GenerationAttachOutcome;

    /// <summary>The reference names no Version. Nothing was stored.</summary>
    public sealed record VersionNotFound : GenerationAttachOutcome;
}

/// <summary>
/// Generations, for now only as far as the freeze needs them: attaching one to a Version (archived
/// or not) gives it the Version's next ordinal and freezes the Version's creation inputs for good
/// (<see cref="SongVersion.AttachGeneration"/>). There is no public endpoint for it yet: the
/// development-only <c>seed-generation</c> command and the tests use it; Generations proper arrive
/// in M4.
/// </summary>
public sealed class GenerationService(IVersionStore versions, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>
    /// Attaches a new Generation to the Version a reference (ID or shortcode) names, raising its
    /// revision by one. Each call adds another; ordinals start at 1 and are never reused.
    /// </summary>
    public Task<GenerationAttachOutcome> AttachAsync(string? versionReference, CancellationToken cancellationToken) =>
        transaction.RunAsync<GenerationAttachOutcome>(
            async ct =>
            {
                if (await ReferenceResolver.VersionIdAsync(versions, CatalogReference.Parse(versionReference), ct).ConfigureAwait(false) is not { } id
                    || await versions.FindAsync(id, ct).ConfigureAwait(false) is not { } version)
                {
                    return new GenerationAttachOutcome.VersionNotFound();
                }

                var now = time.GetUtcNow();
                var (frozen, generation) = version.AttachGeneration(Guid.CreateVersion7(now), now);

                // Inside the transaction nothing can change the Version between the read and the write.
                if (!await versions.TryAttachGenerationAsync(frozen, generation, version.Revision, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The Version just read changed inside the transaction.");
                }

                var attached = await versions.FindGenerationAsync(generation.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Generation just attached cannot be read back.");
                return new GenerationAttachOutcome.Attached(attached);
            },
            cancellationToken);
}
