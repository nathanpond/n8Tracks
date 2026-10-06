using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Generations;

/// <summary>A rating as an edit sends it: left alone when not sent, cleared by null, otherwise set.</summary>
/// <param name="IsSent">Whether the edit names the rating at all.</param>
/// <param name="Value">The new rating (checked by <see cref="GenerationRating"/>); null to clear it.</param>
public sealed record GenerationRatingEdit(bool IsSent, int? Value)
{
    /// <summary>An edit that leaves the rating as it is.</summary>
    public static GenerationRatingEdit Unsent { get; } = new(false, null);

    /// <summary>An edit that sets the rating to <paramref name="value"/> (null clears it).</summary>
    public static GenerationRatingEdit Of(int? value) => new(true, value);
}

/// <summary>How rating a Generation ended. Only <see cref="Rated"/> may have stored anything.</summary>
public abstract record GenerationRateOutcome
{
    private GenerationRateOutcome()
    {
    }

    /// <summary>The Generation as it is now: its revision raised when its rating changed, as it was otherwise.</summary>
    public sealed record Rated(GenerationSummary Generation) : GenerationRateOutcome;

    /// <summary>The reference names no live Generation.</summary>
    public sealed record NotFound : GenerationRateOutcome;

    /// <summary>The rating is not one to five, or null; errors by field.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : GenerationRateOutcome;

    /// <summary>The Generation is not at the revision sent: it, with its comments, as it is now.</summary>
    public sealed record Conflict(GenerationSummary Current) : GenerationRateOutcome;
}

/// <summary>How writing a comment ended. Only <see cref="Saved"/> and <see cref="Deleted"/> may have stored anything.</summary>
public abstract record GenerationCommentOutcome
{
    private GenerationCommentOutcome()
    {
    }

    /// <summary>The comment as it is now: new, edited, or as it was when its text did not change.</summary>
    public sealed record Saved(GenerationComment Comment) : GenerationCommentOutcome;

    /// <summary>The comment is gone.</summary>
    public sealed record Deleted : GenerationCommentOutcome;

    /// <summary>The reference names no live Generation.</summary>
    public sealed record GenerationNotFound : GenerationCommentOutcome;

    /// <summary>The Generation has no comment with that ID (it may have been deleted elsewhere).</summary>
    public sealed record CommentNotFound : GenerationCommentOutcome;

    /// <summary>The text is empty, only whitespace, or too long; errors by field.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : GenerationCommentOutcome;

    /// <summary>The comment is not at the revision sent: it as it is now.</summary>
    public sealed record Conflict(GenerationComment Current) : GenerationCommentOutcome;
}

/// <summary>
/// The user's judgement of a Generation: a rating of one to five stars and any number of comments.
/// Both are the user's own data: only this service writes them, and nothing Suno reports (attaching
/// or updating a Generation's provider data) touches them. Either works on a Generation in any state,
/// of a frozen or an archived Version alike: neither is a creation input. A rating change raises the
/// Generation's revision (checked against the one sent) and sets the Song's updated time, never its
/// revision; a comment has its own revision, and writing one sets the Song's updated time too but
/// leaves the Generation's revision alone. Deleting a comment is final: it is not retained.
/// </summary>
public sealed class GenerationEvaluationService(
    GenerationService reader,
    IGenerationStore generations,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field a rating error is reported under.</summary>
    public const string RatingField = "rating";

    /// <summary>The field a comment's text error is reported under.</summary>
    public const string TextField = "text";

    /// <summary>
    /// Sets, changes, or clears (null) the rating of the Generation a reference (ID or shortcode)
    /// names, given the revision read. An edit that leaves the rating as it is (not sent, or the same
    /// value) stores nothing, but a stale revision is still a conflict.
    /// </summary>
    public Task<GenerationRateOutcome> RateAsync(CatalogReference reference, GenerationRatingEdit edit, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        return transaction.RunAsync<GenerationRateOutcome>(
            async ct =>
            {
                if (await FindAsync(reference, ct).ConfigureAwait(false) is not { } current)
                {
                    return new GenerationRateOutcome.NotFound();
                }

                if (edit.IsSent && GenerationRating.Errors(edit.Value) is { Count: > 0 } errors)
                {
                    return new GenerationRateOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [RatingField] = [.. errors] });
                }

                var generation = current.Generation;
                if (generation.Revision != revision)
                {
                    return new GenerationRateOutcome.Conflict(current);
                }

                if (!edit.IsSent || edit.Value == generation.Rating)
                {
                    return new GenerationRateOutcome.Rated(current);
                }

                // Inside the transaction nothing can change the Generation between the read and the write.
                if (!await generations.TryRateAsync(generation.Id, edit.Value, revision, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The Generation just read changed inside the transaction.");
                }

                await generations.TouchSongAsync(generation.SongId, time.GetUtcNow(), ct).ConfigureAwait(false);
                return new GenerationRateOutcome.Rated(await ReadBackAsync(generation.Id, ct).ConfigureAwait(false));
            },
            cancellationToken);
    }

    /// <summary>Adds a comment, its text trimmed, to the Generation a reference (ID or shortcode) names.</summary>
    public Task<GenerationCommentOutcome> AddCommentAsync(CatalogReference reference, string? text, CancellationToken cancellationToken) =>
        transaction.RunAsync<GenerationCommentOutcome>(
            async ct =>
            {
                if (await FindAsync(reference, ct).ConfigureAwait(false) is not { } generation)
                {
                    return new GenerationCommentOutcome.GenerationNotFound();
                }

                if (TextErrors(text) is { } invalid)
                {
                    return invalid;
                }

                var now = time.GetUtcNow();
                var comment = new GenerationComment(Guid.CreateVersion7(now), generation.Generation.Id, GenerationComment.Normalize(text)!, now, null, 1);
                await generations.AddCommentAsync(comment, ct).ConfigureAwait(false);
                await generations.TouchSongAsync(generation.Generation.SongId, now, ct).ConfigureAwait(false);
                return new GenerationCommentOutcome.Saved(comment);
            },
            cancellationToken);

    /// <summary>
    /// Replaces a comment's text (trimmed), given the comment's revision. Text that is the same once
    /// trimmed stores nothing, so the comment is not marked edited, but a stale revision is still a
    /// conflict.
    /// </summary>
    public Task<GenerationCommentOutcome> EditCommentAsync(CatalogReference reference, Guid commentId, string? text, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<GenerationCommentOutcome>(
            async ct =>
            {
                if (await FindAsync(reference, ct).ConfigureAwait(false) is not { } generation)
                {
                    return new GenerationCommentOutcome.GenerationNotFound();
                }

                if (await generations.FindCommentAsync(generation.Generation.Id, commentId, ct).ConfigureAwait(false) is not { } comment)
                {
                    return new GenerationCommentOutcome.CommentNotFound();
                }

                if (TextErrors(text) is { } invalid)
                {
                    return invalid;
                }

                if (comment.Revision != revision)
                {
                    return new GenerationCommentOutcome.Conflict(comment);
                }

                var trimmed = GenerationComment.Normalize(text)!;
                if (string.Equals(trimmed, comment.Text, StringComparison.Ordinal))
                {
                    return new GenerationCommentOutcome.Saved(comment);
                }

                var now = time.GetUtcNow();
                if (!await generations.TryEditCommentAsync(comment.Id, trimmed, now, revision, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The comment just read changed inside the transaction.");
                }

                await generations.TouchSongAsync(generation.Generation.SongId, now, ct).ConfigureAwait(false);
                return new GenerationCommentOutcome.Saved(
                    await generations.FindCommentAsync(generation.Generation.Id, comment.Id, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The comment just edited cannot be read back."));
            },
            cancellationToken);

    /// <summary>Deletes a comment for good, given its revision.</summary>
    public Task<GenerationCommentOutcome> DeleteCommentAsync(CatalogReference reference, Guid commentId, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<GenerationCommentOutcome>(
            async ct =>
            {
                if (await FindAsync(reference, ct).ConfigureAwait(false) is not { } generation)
                {
                    return new GenerationCommentOutcome.GenerationNotFound();
                }

                if (await generations.FindCommentAsync(generation.Generation.Id, commentId, ct).ConfigureAwait(false) is not { } comment)
                {
                    return new GenerationCommentOutcome.CommentNotFound();
                }

                if (comment.Revision != revision)
                {
                    return new GenerationCommentOutcome.Conflict(comment);
                }

                if (!await generations.TryDeleteCommentAsync(comment.Id, revision, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The comment just read changed inside the transaction.");
                }

                await generations.TouchSongAsync(generation.Generation.SongId, time.GetUtcNow(), ct).ConfigureAwait(false);
                return new GenerationCommentOutcome.Deleted();
            },
            cancellationToken);

    private static GenerationCommentOutcome.Invalid? TextErrors(string? text) =>
        GenerationComment.Errors(text) is { Count: > 0 } errors
            ? new GenerationCommentOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [TextField] = [.. errors] })
            : null;

    /// <summary>The live Generation a reference (ID or shortcode) names; null when none.</summary>
    private Task<GenerationSummary?> FindAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        reader.FindAsync(reference, cancellationToken);

    private async Task<GenerationSummary> ReadBackAsync(Guid id, CancellationToken cancellationToken) =>
        await generations.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Generation just rated cannot be read back.");
}
