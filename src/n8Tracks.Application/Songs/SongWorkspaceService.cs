using System.Globalization;
using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Songs;

/// <summary>
/// A bulk move as asked: the Songs to move (each an ID or a shortcode; null when not sent), or
/// <paramref name="All"/> of the workspace's Songs, and the Suno ID of the workspace to move them to.
/// </summary>
public sealed record SongWorkspaceMove(IReadOnlyList<string?>? SongIds, bool All, string? TargetWorkspaceId);

/// <summary>A Song as a bulk move finds it: its ID, its shortcode number, and the Suno ID of its workspace (null for none).</summary>
public sealed record SongInWorkspace(Guid Id, long ShortcodeNumber, string? SunoWorkspaceId);

/// <summary>Where a bulk move reads and writes the Songs' workspace.</summary>
public interface ISongWorkspaceStore
{
    /// <summary>The IDs of the live Songs in the workspace with Suno ID <paramref name="sunoWorkspaceId"/>.</summary>
    Task<IReadOnlyList<Guid>> SongIdsInAsync(string sunoWorkspaceId, CancellationToken cancellationToken);

    /// <summary>The live Songs with any of <paramref name="ids"/> or <paramref name="shortcodeNumbers"/>; one missing is left out.</summary>
    Task<IReadOnlyList<SongInWorkspace>> FindAsync(IReadOnlyCollection<Guid> ids, IReadOnlyCollection<long> shortcodeNumbers, CancellationToken cancellationToken);

    /// <summary>
    /// Puts every Song in <paramref name="songIds"/> in the workspace <paramref name="sunoWorkspaceId"/>,
    /// raising each one's revision by one and setting its updated time; no other column changes.
    /// Answers how many it changed. Inside the caller's transaction.
    /// </summary>
    Task<int> MoveAsync(IReadOnlyCollection<Guid> songIds, string sunoWorkspaceId, DateTimeOffset updatedUtc, CancellationToken cancellationToken);
}

/// <summary>How a bulk move ended.</summary>
public abstract record SongWorkspaceMoveOutcome
{
    private SongWorkspaceMoveOutcome()
    {
    }

    /// <summary>Every Song named was moved (<paramref name="Count"/> of them), each at its next revision.</summary>
    public sealed record Moved(int Count, SunoWorkspace From, SunoWorkspace To) : SongWorkspaceMoveOutcome;

    /// <summary>There is no workspace with that Suno ID.</summary>
    public sealed record NotFound : SongWorkspaceMoveOutcome;

    /// <summary>The request is wrong (errors keyed by field). Nothing was moved.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SongWorkspaceMoveOutcome;

    /// <summary>More Songs than <see cref="SongWorkspaceService.MaximumSongs"/> would move (<paramref name="Count"/>). Nothing was moved.</summary>
    public sealed record TooManySongs(int Count) : SongWorkspaceMoveOutcome;

    /// <summary>These Songs, as sent, are not in the workspace (or are no Song at all). Nothing was moved.</summary>
    public sealed record SongsNotInWorkspace(IReadOnlyList<string> Songs) : SongWorkspaceMoveOutcome;
}

/// <summary>
/// Moving Songs from one Suno workspace to another in one command (#129): some or all of the
/// workspace's Songs, at most <see cref="MaximumSongs"/>, all or nothing, without per-Song revisions;
/// each Song moved is at its next revision. The workspace moved from may be Unavailable (that is how
/// Songs leave one that has gone); the one moved to must be Available and another one. Only a Song's
/// workspace changes: never a Version.
/// </summary>
public sealed class SongWorkspaceService(ISongWorkspaceStore songs, ISunoWorkspaceStore workspaces, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The most Songs one move takes.</summary>
    public const int MaximumSongs = 5_000;

    /// <summary>The fields a move's errors are keyed by, as the API spells them.</summary>
    public const string SongIdsField = "songIds";
    public const string AllField = "all";
    public const string TargetWorkspaceIdField = "targetWorkspaceId";

    /// <summary>
    /// Moves the Songs <paramref name="move"/> names out of the workspace with Suno ID
    /// <paramref name="sunoWorkspaceId"/>. Exactly one of <see cref="SongWorkspaceMove.SongIds"/> (not
    /// empty; duplicates are ignored) and <see cref="SongWorkspaceMove.All"/> is sent. A Song named
    /// that is not in the workspace refuses the whole move.
    /// </summary>
    public Task<SongWorkspaceMoveOutcome> MoveSongsAsync(string sunoWorkspaceId, SongWorkspaceMove move, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sunoWorkspaceId);
        ArgumentNullException.ThrowIfNull(move);

        return transaction.RunAsync<SongWorkspaceMoveOutcome>(
            async ct =>
            {
                if (await workspaces.FindAsync(sunoWorkspaceId, ct).ConfigureAwait(false) is not { } from)
                {
                    return new SongWorkspaceMoveOutcome.NotFound();
                }

                var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
                if (move.All == (move.SongIds is not null))
                {
                    errors[SongIdsField] = ["Send either the Songs to move or all: true."];
                }
                else if (move.SongIds is { Count: 0 })
                {
                    errors[SongIdsField] = ["Name at least one Song to move."];
                }

                SunoWorkspace? to = null;
                if (move.TargetWorkspaceId is not { Length: > 0 } targetId)
                {
                    errors[TargetWorkspaceIdField] = ["Send the Suno ID of the workspace to move the Songs to."];
                }
                else if ((to = await workspaces.FindAsync(targetId, ct).ConfigureAwait(false)) is null)
                {
                    errors[TargetWorkspaceIdField] = ["There is no Suno workspace with this ID."];
                }
                else if (to.SunoId == from.SunoId)
                {
                    errors[TargetWorkspaceIdField] = ["Choose another workspace than the one the Songs are in."];
                }
                else if (to.State != SunoWorkspaceState.Available)
                {
                    errors[TargetWorkspaceIdField] = ["This Suno workspace is unavailable: choose an available one."];
                }

                if (errors.Count > 0)
                {
                    return new SongWorkspaceMoveOutcome.Invalid(errors);
                }

                IReadOnlyCollection<Guid> moving;
                if (move.All)
                {
                    moving = await songs.SongIdsInAsync(from.SunoId, ct).ConfigureAwait(false);
                }
                else
                {
                    var named = move.SongIds!.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (named.Count > MaximumSongs)
                    {
                        return new SongWorkspaceMoveOutcome.TooManySongs(named.Count);
                    }

                    var (found, missing) = await ResolveAsync(named, from.SunoId, ct).ConfigureAwait(false);
                    if (missing.Count > 0)
                    {
                        return new SongWorkspaceMoveOutcome.SongsNotInWorkspace(missing);
                    }

                    moving = found;
                }

                if (moving.Count > MaximumSongs)
                {
                    return new SongWorkspaceMoveOutcome.TooManySongs(moving.Count);
                }

                var count = moving.Count == 0 ? 0 : await songs.MoveAsync(moving, to!.SunoId, time.GetUtcNow(), ct).ConfigureAwait(false);
                return count == moving.Count
                    ? new SongWorkspaceMoveOutcome.Moved(count, from, to!)
                    : throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{count} of {moving.Count} Songs moved inside the transaction."));
            },
            cancellationToken);
    }

    /// <summary>The Songs <paramref name="named"/> names that are in the workspace, and those (as sent) that are not.</summary>
    private async Task<(HashSet<Guid> Found, List<string> Missing)> ResolveAsync(IReadOnlyList<string?> named, string sunoWorkspaceId, CancellationToken cancellationToken)
    {
        var parsed = named.Select(static text => (Text: text ?? string.Empty, Reference: CatalogReference.Parse(text))).ToList();
        var ids = parsed.Where(static item => item.Reference.Kind == ReferenceKind.Id).Select(static item => item.Reference.Id).ToHashSet();
        var numbers = parsed.Where(static item => item.Reference.Kind == ReferenceKind.Song).Select(static item => item.Reference.SongShortcodeNumber).ToHashSet();
        var songsFound = await songs.FindAsync(ids, numbers, cancellationToken).ConfigureAwait(false);
        var byId = songsFound.ToDictionary(static song => song.Id);
        var byNumber = songsFound.ToDictionary(static song => song.ShortcodeNumber);

        var found = new HashSet<Guid>();
        var missing = new List<string>();
        foreach (var (text, reference) in parsed)
        {
            var song = reference.Kind switch
            {
                ReferenceKind.Id => byId.GetValueOrDefault(reference.Id),
                ReferenceKind.Song => byNumber.GetValueOrDefault(reference.SongShortcodeNumber),
                _ => null,
            };
            if (song is { } inWorkspace && inWorkspace.SunoWorkspaceId == sunoWorkspaceId)
            {
                found.Add(inWorkspace.Id);
            }
            else
            {
                missing.Add(text);
            }
        }

        return (found, missing);
    }
}
