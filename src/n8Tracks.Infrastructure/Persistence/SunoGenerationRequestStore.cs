using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Suno.Generate;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Generate on Suno requests (#144) in <c>suno_generation_requests</c>. It reads <c>generations</c>
/// (a source's Suno clip ID) and <c>suno_exports</c> (when the last confirmed sync was), and writes
/// only its own table.
/// </summary>
internal sealed class SunoGenerationRequestStore(N8TracksDbContext context) : IGenerationRequestStore
{
    private static readonly string CommittedState = SunoExportRules.NameOf(SunoExportState.Committed);

    public async Task<GenerationRequest?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await context.SunoGenerationRequests.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : ToRequest(record);
    }

    public async Task<GenerationRequest?> LatestForVersionAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var record = await context.SunoGenerationRequests.AsNoTracking()
            .Where(row => row.VersionId == versionId)
            .OrderByDescending(static row => row.CreatedUtc)
            .ThenByDescending(static row => row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : ToRequest(record);
    }

    public async Task<IReadOnlyList<GenerationRequest>> ActiveForVersionAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var active = GenerationRequestRules.StateNames
            .Where(static name => GenerationRequestRules.IsActive(GenerationRequestRules.StateOf(name)!.Value))
            .ToList();
        return [.. (await context.SunoGenerationRequests.AsNoTracking()
            .Where(row => row.VersionId == versionId && active.Contains(row.State))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).Select(ToRequest)];
    }

    public async Task AddAsync(GenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var record = new SunoGenerationRequestRecord
        {
            Id = request.Id,
            VersionId = request.VersionId,
            SnapshotJson = request.SnapshotJson,
            ContentKey = request.ContentKey,
            State = string.Empty,
            CreatedUtc = UtcText.From(request.CreatedUtc),
            UpdatedUtc = string.Empty,
        };
        Write(record, request);
        context.SunoGenerationRequests.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }

    public async Task<bool> TryMoveAsync(GenerationRequest next, GenerationRequestState state, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var stateName = GenerationRequestRules.NameOf(state);
        var updated = UtcText.From(updatedUtc);
        var record = await context.SunoGenerationRequests
            .SingleOrDefaultAsync(row => row.Id == next.Id && row.State == stateName && row.UpdatedUtc == updated, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return false;
        }

        Write(record, next);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
        return true;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> SunoIdsAsync(IReadOnlyCollection<Guid> generationIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationIds);

        var ids = generationIds.ToList();
        return await context.Generations.AsNoTracking()
            .Where(generation => ids.Contains(generation.Id) && generation.SunoId != null)
            .ToDictionaryAsync(static generation => generation.Id, static generation => generation.SunoId!, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> LastConfirmedSyncAsync(CancellationToken cancellationToken)
    {
        var captured = await context.SunoExports.AsNoTracking()
            .Where(static export => export.State == CommittedState)
            .OrderByDescending(static export => export.CapturedUtc)
            .Select(static export => (string?)export.CapturedUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return captured is null ? null : UtcText.Parse(captured);
    }

    private static void Write(SunoGenerationRequestRecord record, GenerationRequest request)
    {
        record.State = GenerationRequestRules.NameOf(request.State);
        record.Step = request.Step;
        record.Message = request.Message;
        record.CredentialId = request.CredentialId;
        record.UpdatedUtc = UtcText.From(request.UpdatedUtc);
        record.EndedUtc = request.EndedUtc is { } ended ? UtcText.From(ended) : null;
        record.VerificationJson = request.VerificationJson;
        record.ObservedJson = request.ObservedJson;
    }

    private static GenerationRequest ToRequest(SunoGenerationRequestRecord record) => new(
        record.Id,
        record.VersionId,
        record.SnapshotJson,
        record.ContentKey,
        GenerationRequestRules.StateOf(record.State) ?? throw new InvalidOperationException($"Unknown generation request state '{record.State}'."),
        record.Step,
        record.Message,
        record.CredentialId,
        UtcText.Parse(record.CreatedUtc),
        UtcText.Parse(record.UpdatedUtc),
        record.EndedUtc is { } ended ? UtcText.Parse(ended) : null,
        record.VerificationJson,
        record.ObservedJson);
}
