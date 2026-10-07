using n8Tracks.Application.Auth;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno.Import;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>
/// An imported clip's lineage with its sources linked (#137): each source whose clip n8Tracks already
/// has as a Generation points at that Generation; every other keeps its Suno ID, with the external
/// reference to store for it.
/// </summary>
/// <param name="Lineage">The lineage to store on the imported Version.</param>
/// <param name="References">The "Not imported" external references its remaining Suno IDs need, to store first.</param>
/// <param name="Generations">The Generations sources were linked to.</param>
public sealed record LinkedLineage(VersionLineage Lineage, IReadOnlyList<ExternalSunoReference> References, IReadOnlyList<SourceGenerationFacts> Generations);

/// <summary>What resolving a Suno ID changed: the sources pointed at its Generation, and the Song relationships added.</summary>
public sealed record ReferenceResolution(int SourcesLinked, int RelationshipsAdded);

/// <summary>
/// Connects imported lineage to the Generations it names (#137), by Suno ID, so importing a source
/// after the clip made from it connects the two without the user doing anything, and never makes a
/// second Generation or a second external reference for one Suno ID.
/// <para>
/// At import, <see cref="LinkAsync"/> points each source of a clip's lineage whose clip is already a
/// Generation at that Generation. Once a Generation is attached, <see cref="ResolveAsync"/> (or, inside
/// the import commit's own transaction, <c>ResolveWithinAsync</c>) points every source that names its
/// Suno ID through an external reference at it instead: the system rewrite of a pointer, which leaves
/// the frozen source's Suno ID as it was (the database refuses any other change to a frozen source).
/// External references are never pruned: the reference stays for every other use of it. Each
/// rewritten source then relates its Version's Song to the Generation's Song under the source's type,
/// unless the two are related under it already, as an edit of the sources does.
/// </para>
/// </summary>
public sealed class ExternalReferenceResolver(
    IVersionStore versions,
    IRelationshipStore relationships,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>
    /// <paramref name="read"/> as an imported Version stores it: each source whose Suno clip is a live
    /// Generation points at it, the rest keep their Suno IDs, with the references for those.
    /// </summary>
    public async Task<LinkedLineage> LinkAsync(ImportedLineage read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        var generations = new Dictionary<string, SourceGenerationFacts?>(StringComparer.Ordinal);
        async Task<IReadOnlyList<VersionSource>> LinkSourcesAsync(IReadOnlyList<VersionSource> sources)
        {
            var linked = new List<VersionSource>(sources.Count);
            foreach (var source in sources)
            {
                if (source.Target.ExternalSunoId is not { } sunoId)
                {
                    linked.Add(source);
                    continue;
                }

                if (!generations.TryGetValue(sunoId, out var facts))
                {
                    facts = await versions.FindSourceGenerationBySunoIdAsync(sunoId, cancellationToken).ConfigureAwait(false);
                    generations[sunoId] = facts;
                }

                linked.Add(facts is null ? source : source with { Target = VersionSourceTarget.OfGeneration(facts.Id) });
            }

            return linked;
        }

        var lineage = read.Lineage;
        var audio = await LinkSourcesAsync(lineage.AudioSources).ConfigureAwait(false);
        var inspiration = await LinkSourcesAsync(lineage.InspirationSources).ConfigureAwait(false);
        return new LinkedLineage(
            new VersionLineage(audio, inspiration, lineage.Playlist, lineage.Voice, lineage.FileInputs),
            [.. read.References.Where(reference => generations.GetValueOrDefault(reference.SunoId) is null)],
            [.. generations.Values.OfType<SourceGenerationFacts>()]);
    }

    /// <summary>
    /// In its own transaction: points every source that names Suno clip <paramref name="sunoId"/>
    /// through an external reference at the live Generation with that Suno ID, and relates the Songs.
    /// Nothing changes when no live Generation has it.
    /// </summary>
    public Task<ReferenceResolution> ResolveAsync(string sunoId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sunoId);

        return transaction.RunAsync(ct => ResolveWithinAsync(sunoId, ct), cancellationToken);
    }

    /// <summary>
    /// Inside the caller's transaction (the import commit's, right after it attaches a Generation): what
    /// <see cref="ResolveAsync"/> does.
    /// </summary>
    internal async Task<ReferenceResolution> ResolveWithinAsync(string sunoId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sunoId);

        if (await versions.FindSourceGenerationBySunoIdAsync(sunoId, cancellationToken).ConfigureAwait(false) is not { } generation)
        {
            return new ReferenceResolution(0, 0);
        }

        var linked = await versions.LinkExternalSourcesAsync(sunoId, generation.Id, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var added = 0;
        foreach (var (songId, typeId) in linked.Select(static source => (source.SongId, source.TypeId)).Distinct())
        {
            if (songId == generation.SongId
                || await relationships.ExistsAsync(typeId, songId, generation.SongId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await relationships.AddAsync(new StoredRelationship(Guid.CreateVersion7(now), typeId, songId, generation.SongId), now, cancellationToken).ConfigureAwait(false);
            added++;
        }

        return new ReferenceResolution(linked.Count, added);
    }
}
