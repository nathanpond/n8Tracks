using n8Tracks.Application.Auth;

namespace n8Tracks.Application.Retention;

/// <summary>
/// The deletion foundation. Deleting a record moves it, with everything deleted alongside it, out of
/// the live tables into a retention group in one transaction: the live rows are physically removed,
/// so no screen, list, count, search, or API read can see it, and no query needs a "not deleted"
/// filter. A group is kept for <see cref="RetentionPeriod"/>, during which application code can
/// restore it as a whole; then the daily prune removes it, and afterwards its managed files.
/// Retained documents hold lyrics, prompts, and the like: they are never logged or shown.
/// </summary>
public sealed class RetentionService(
    IRetentionStore store,
    IManagedFiles files,
    IEnumerable<ILiveFileReferences> liveReferences,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>How long a deleted record is kept before the prune may remove it.</summary>
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    /// <summary>The longest managed-file path a group may list.</summary>
    public const int MaximumFilePathLength = 1024;

    /// <summary>Retains <paramref name="request"/> in a transaction of its own; see <see cref="RetainWithinAsync"/>.</summary>
    public Task<RetentionGroup> RetainAsync(RetentionRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        return transaction.RunAsync(token => RetainWithinAsync(request, token), cancellationToken);
    }

    /// <summary>
    /// Inside the caller's transaction (a deletion that also changes other rows): writes the group,
    /// deleted now and prunable <see cref="RetentionPeriod"/> later, and removes the live rows.
    /// </summary>
    /// <exception cref="ArgumentException">The request names no record, or a file path n8Tracks does not manage.</exception>
    /// <exception cref="InvalidOperationException">
    /// A root does not exist, or the database would remove something with it that is not a retained type.
    /// </exception>
    public Task<RetentionGroup> RetainWithinAsync(RetentionRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var now = time.GetUtcNow();
        return store.RetainAsync(Guid.CreateVersion7(now), request, now, now + RetentionPeriod, cancellationToken);
    }

    /// <summary>The group with <paramref name="id"/>, or null when there is none (never was, restored, or pruned).</summary>
    public Task<RetentionGroup?> FindAsync(Guid id, CancellationToken cancellationToken) => store.FindAsync(id, cancellationToken);

    /// <summary>The newest unpruned group deleted under <paramref name="shortcode"/>, or null.</summary>
    public Task<RetentionGroup?> FindByShortcodeAsync(string shortcode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcode);

        return store.FindByShortcodeAsync(shortcode, cancellationToken);
    }

    /// <summary>
    /// The newest unpruned group holding the record of <paramref name="recordType"/> with
    /// <paramref name="id"/> (one of <see cref="RetainedRecordTypes"/>), whether it was the deletion's
    /// root or went with it; null when none does. How a read by ID learns that a record was deleted.
    /// </summary>
    public Task<RetentionGroup?> FindByRecordAsync(string recordType, Guid id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);

        return store.FindByRecordAsync(recordType, id, cancellationToken);
    }

    /// <summary>
    /// The identifying fields <paramref name="columns"/> of every record of <paramref name="recordType"/>
    /// in the group with <paramref name="groupId"/>, as stored text (see <see cref="IRetentionStore.RecordFieldsAsync"/>).
    /// Only internal callers, which ask for IDs, numbers, and titles, never for lyrics or prompts.
    /// </summary>
    internal Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> RecordFieldsAsync(Guid groupId, string recordType, IReadOnlyList<string> columns, CancellationToken cancellationToken) =>
        store.RecordFieldsAsync(groupId, recordType, columns, cancellationToken);

    /// <summary>Every unpruned group, newest first (the recovery listing).</summary>
    public Task<IReadOnlyList<RetentionGroup>> ListAsync(CancellationToken cancellationToken) => store.ListAsync(cancellationToken);

    /// <summary>
    /// Restores the group with <paramref name="id"/> as a whole, in a transaction of its own: every
    /// record goes back as it was, except that each one's revision is incremented so open clients
    /// refetch, and the group is removed. Refused, changing nothing, when something a record belongs to
    /// is gone or a live row now holds a record's ID or unique key; either way the message names it.
    /// </summary>
    public async Task<RetentionRestoreOutcome> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return await transaction.RunAsync<RetentionRestoreOutcome>(token => RestoreWithinAsync(id, token), cancellationToken).ConfigureAwait(false);
        }
        catch (RetentionRestoreRefusedException refused)
        {
            return refused.MissingParent
                ? new RetentionRestoreOutcome.MissingParent(refused.Message)
                : new RetentionRestoreOutcome.Clash(refused.Message);
        }
    }

    /// <summary>
    /// Inside the caller's transaction: as <see cref="RestoreAsync"/>, but a refusal is thrown as
    /// <see cref="RetentionRestoreRefusedException"/>, and the caller must let its transaction roll back.
    /// </summary>
    public async Task<RetentionRestoreOutcome> RestoreWithinAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } group)
        {
            return new RetentionRestoreOutcome.NotFound();
        }

        var notes = await store.RestoreAsync(id, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return new RetentionRestoreOutcome.Restored(group, notes);
    }

    /// <summary>
    /// Permanently removes every group whose time has come, with its records, then its managed files.
    /// Rows go first, in one transaction; each file is then deleted unless a live record or another
    /// unpruned group still uses it. A file that cannot be deleted stays queued and the next run tries
    /// again; nothing is found by scanning storage, so nothing else is ever deleted.
    /// </summary>
    public async Task<RetentionPruneSummary> PruneAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var groups = await transaction.RunAsync(token => store.RemoveDueAsync(now, token), cancellationToken).ConfigureAwait(false);

        int deleted = 0, kept = 0, failed = 0;
        foreach (var pending in await store.PendingFilesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await IsStillUsedAsync(pending.Path, cancellationToken).ConfigureAwait(false))
            {
                await store.CompletePendingAsync(pending.Path, cancellationToken).ConfigureAwait(false);
                kept++;
                continue;
            }

            if (files.Delete(pending.Path, out var error) == ManagedFileDeletion.Failed)
            {
                await store.FailPendingAsync(pending.Path, error ?? "The file could not be deleted.", now, cancellationToken).ConfigureAwait(false);
                failed++;
            }
            else
            {
                await store.CompletePendingAsync(pending.Path, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
        }

        return new RetentionPruneSummary(groups, deleted, kept, failed);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a relative path inside the managed-assets folder: forward
    /// slashes, no empty, <c>.</c>, or <c>..</c> segment, no drive or root, no control character.
    /// </summary>
    public static bool IsManagedFilePath(string? path) =>
        !string.IsNullOrEmpty(path)
        && path.Length <= MaximumFilePathLength
        && !path.Any(static character => char.IsControl(character) || character is '\\' or ':')
        && !path.StartsWith('/')
        && path.Split('/').All(static segment => segment.Length > 0 && segment is not "." and not "..");

    private async Task<bool> IsStillUsedAsync(string path, CancellationToken cancellationToken)
    {
        if (await store.IsFileRetainedAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        foreach (var references in liveReferences)
        {
            if (await references.IsReferencedAsync(path, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static void Validate(RetentionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Label);
        if (request.Roots.Count == 0)
        {
            throw new ArgumentException("A retention group holds at least one record.", nameof(request));
        }

        if (request.Files.FirstOrDefault(static path => !IsManagedFilePath(path)) is { } outside)
        {
            throw new ArgumentException($"'{outside}' is not a path inside the managed-assets folder.", nameof(request));
        }
    }
}
