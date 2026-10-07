using n8Tracks.Application.Auth;
using n8Tracks.Application.Generations;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>A Generation whose clip Suno has finished since n8Tracks last knew it (#314): its clip, its Generation, and Suno's final status.</summary>
internal sealed record FinishedInSuno(string SunoId, Guid GenerationId, string Status);

/// <summary>What the commit did for one clip Suno finished (#314): the clip, Suno's final status, and the Generation that took it.</summary>
internal sealed record FinishedStatusApplied(string SunoId, string Status, Guid GenerationId, string Shortcode);

/// <summary>
/// Suno's status of a linked clip at a sync (#314). A Generation's status is Suno's own state of its clip,
/// not catalog content the user chose, so it follows Suno at the commit of a confirmed sync for every
/// export record a live Generation holds, whatever the record's choice, but only one way: from a status that
/// is not final (<c>submitted</c>, <c>streaming</c>, or none) to Suno's final one (<c>complete</c> or
/// <c>error</c>), the same "never complete" status rule as the completion of an observed Create
/// (<see cref="ProvisionalCompletionRules.MayComplete"/>, #154). A final status is never changed, and
/// nothing else of the Generation is: its other clip columns stay with the review's Changed choice
/// (#141), and its rating, state, archiver, comments, remembered hashes, raw clip, and revision are never
/// part of it. The review's summary counts these before Confirm (<see cref="CountAsync"/>), and the
/// commit's result lists each one (invariant 3: never silent).
/// </summary>
public sealed class SunoStatusService(ISunoExportStore exports, ISunoClipLookup lookup, IGenerationStore generations, IExclusiveTransaction transaction)
{
    /// <summary>How many Generations confirming the export <paramref name="exportId"/> would give Suno's final status, as the catalog is now.</summary>
    internal async Task<int> CountAsync(Guid exportId, CancellationToken cancellationToken) =>
        (await FinishedAsync(exportId, cancellationToken).ConfigureAwait(false)).Count;

    /// <summary>
    /// The commit's step: in one transaction, works the clips out again and writes Suno's final status to
    /// each Generation whose stored status is still not final. Returns each one written.
    /// </summary>
    internal Task<IReadOnlyList<FinishedStatusApplied>> ApplyAsync(Guid exportId, CancellationToken cancellationToken) =>
        transaction.RunAsync<IReadOnlyList<FinishedStatusApplied>>(
            async ct =>
            {
                var applied = new List<FinishedStatusApplied>();
                foreach (var finished in await FinishedAsync(exportId, ct).ConfigureAwait(false))
                {
                    if (await generations.TryFinishStatusAsync(finished.GenerationId, finished.SunoId, finished.Status, ct).ConfigureAwait(false)
                        && await generations.FindAsync(finished.GenerationId, ct).ConfigureAwait(false) is { } generation)
                    {
                        applied.Add(new FinishedStatusApplied(finished.SunoId, finished.Status, finished.GenerationId, generation.Shortcode));
                    }
                }

                return applied;
            },
            cancellationToken);

    /// <summary>The export's clips Suno has finished whose live Generation's stored status is not final, by Suno ID.</summary>
    private async Task<IReadOnlyList<FinishedInSuno>> FinishedAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var incoming = new Dictionary<string, ClipFields>(StringComparer.Ordinal);
        foreach (var record in await exports.ClassifiedRecordsAsync(exportId, null, cancellationToken).ConfigureAwait(false))
        {
            if (ClipReader.Read(record.RawJson) is ClipReading.Read { Fields: var fields } && ProvisionalCompletionRules.IsFinal(fields.Status))
            {
                incoming[record.SunoId] = fields;
            }
        }

        if (incoming.Count == 0)
        {
            return [];
        }

        var live = await lookup.LiveGenerationsAsync(incoming.Keys, cancellationToken).ConfigureAwait(false);
        return [.. incoming
            .Where(pair => live.TryGetValue(pair.Key, out var linked) && ProvisionalCompletionRules.MayComplete(linked.Stored, pair.Value))
            .Select(pair => new FinishedInSuno(pair.Key, live[pair.Key].GenerationId, pair.Value.Status!))
            .OrderBy(static finished => finished.SunoId, StringComparer.Ordinal)];
    }
}
