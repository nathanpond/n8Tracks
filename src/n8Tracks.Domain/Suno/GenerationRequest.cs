namespace n8Tracks.Domain.Suno;

/// <summary>
/// Where a Generate on Suno request stands (#144). <see cref="Pending"/> until the extension claims it,
/// then each step the extension reports; the last four end it.
/// </summary>
public enum GenerationRequestState
{
    /// <summary>Created and handed to the extension, which has not claimed it yet.</summary>
    Pending,

    /// <summary>The extension has it, bound to its credential.</summary>
    Claimed,

    /// <summary>The extension is opening Suno's Create page.</summary>
    Opening,

    /// <summary>The extension is choosing the Song's workspace.</summary>
    Workspace,

    /// <summary>The extension is filling the Create form.</summary>
    Filling,

    /// <summary>The form is filled: waiting for the user to click Create (and between Creates).</summary>
    Waiting,

    /// <summary>At least one Create was observed and observation has ended.</summary>
    Done,

    /// <summary>Stopped at a named step, with the reason.</summary>
    Stopped,

    /// <summary>Cancelled by the user, replaced by a newer request, or made stale by an edit of the Version.</summary>
    Cancelled,

    /// <summary>Nothing was reported for an hour.</summary>
    Expired,
}

/// <summary>
/// A Generate on Suno request (#144): a snapshot of what one Version is to be generated with, handed to
/// the extension. It is not a Generation and changes nothing in the catalog; it records only how far
/// the hand-off has gone.
/// </summary>
/// <param name="Id">The request.</param>
/// <param name="VersionId">The Version it was made from.</param>
/// <param name="SnapshotJson">What the extension fills from: the Version's effective inputs, sources, file-input notes, and workspace when it was made.</param>
/// <param name="ContentKey">The parts of the snapshot an edit of the Version changes; a Version whose key differs now makes the request stale.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Step">The step the extension last named; null before any.</param>
/// <param name="Message">What the extension or n8Tracks last said about it, in plain words; null when nothing.</param>
/// <param name="CredentialId">The credential that claimed it; null until claimed.</param>
/// <param name="CreatedUtc">When it was made.</param>
/// <param name="UpdatedUtc">When it was made, claimed, or last reported on: the hour runs from here.</param>
/// <param name="EndedUtc">When it reached a terminal state; null while active.</param>
/// <param name="VerificationJson">
/// The extension's last verification summary of the filled Create form (#146), as JSON; null until
/// one is reported. Text values in it are lengths and hashes, never the text.
/// </param>
public sealed record GenerationRequest(
    Guid Id,
    Guid VersionId,
    string SnapshotJson,
    string ContentKey,
    GenerationRequestState State,
    string? Step,
    string? Message,
    Guid? CredentialId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? EndedUtc,
    string? VerificationJson = null);

/// <summary>A change of state a request is due, with what to say about it.</summary>
public sealed record GenerationRequestTransition(GenerationRequestState State, string Message);

/// <summary>The rules of Generate on Suno requests (#144).</summary>
public static class GenerationRequestRules
{
    /// <summary>How long the extension has to claim a request before it is stopped.</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long an active request may go without a report before it expires.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(1);

    /// <summary>The longest step name a report may carry.</summary>
    public const int MaximumStepLength = 200;

    /// <summary>The longest message a report may carry.</summary>
    public const int MaximumMessageLength = 1000;

    public const string NotClaimedMessage = "The extension did not respond.";
    public const string ExpiredMessage = "Nothing was reported for an hour, so the request expired.";
    public const string StaleMessage = "The Version was edited after this request was made, so it is not what would be sent. Start Generate on Suno again.";
    public const string SupersededMessage = "A newer request for this Version replaced it.";
    public const string CancelledMessage = "You cancelled it.";
    public const string VersionGoneMessage = "The Version is no longer there.";

    private static readonly Dictionary<GenerationRequestState, string> Names = new()
    {
        [GenerationRequestState.Pending] = "pending",
        [GenerationRequestState.Claimed] = "claimed",
        [GenerationRequestState.Opening] = "opening",
        [GenerationRequestState.Workspace] = "workspace",
        [GenerationRequestState.Filling] = "filling",
        [GenerationRequestState.Waiting] = "waiting",
        [GenerationRequestState.Done] = "done",
        [GenerationRequestState.Stopped] = "stopped",
        [GenerationRequestState.Cancelled] = "cancelled",
        [GenerationRequestState.Expired] = "expired",
    };

    private static readonly Dictionary<string, GenerationRequestState> ByName =
        Names.ToDictionary(static pair => pair.Value, static pair => pair.Key, StringComparer.Ordinal);

    /// <summary>Every state's name, active first, as stored and as the API spells it.</summary>
    public static IReadOnlyList<string> StateNames { get; } = [.. Names.Values];

    /// <summary>The states a report from the extension may move a request to.</summary>
    public static IReadOnlySet<GenerationRequestState> Reportable { get; } = new HashSet<GenerationRequestState>
    {
        GenerationRequestState.Opening,
        GenerationRequestState.Workspace,
        GenerationRequestState.Filling,
        GenerationRequestState.Waiting,
        GenerationRequestState.Done,
        GenerationRequestState.Stopped,
    };

    /// <summary>The state's name as stored and as the API spells it.</summary>
    public static string NameOf(GenerationRequestState state) => Names[state];

    /// <summary>The state named <paramref name="name"/>, or null for any other text.</summary>
    public static GenerationRequestState? StateOf(string? name) =>
        name is not null && ByName.TryGetValue(name, out var state) ? state : null;

    /// <summary>Whether a request in <paramref name="state"/> is still going: one of these per Version at most.</summary>
    public static bool IsActive(GenerationRequestState state) => state <= GenerationRequestState.Waiting;

    /// <summary>
    /// What a request is due at <paramref name="now"/> by time alone: <see cref="GenerationRequestState.Stopped"/>
    /// when still unclaimed <see cref="ClaimTimeout"/> after it was made, <see cref="GenerationRequestState.Expired"/>
    /// when active with no report for <see cref="IdleLimit"/>; null otherwise.
    /// </summary>
    public static GenerationRequestTransition? DueBy(GenerationRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsActive(request.State))
        {
            return null;
        }

        if (request.State == GenerationRequestState.Pending && now - request.CreatedUtc >= ClaimTimeout)
        {
            return new(GenerationRequestState.Stopped, NotClaimedMessage);
        }

        return now - request.UpdatedUtc >= IdleLimit ? new(GenerationRequestState.Expired, ExpiredMessage) : null;
    }

    /// <summary><paramref name="request"/> moved to <paramref name="state"/> at <paramref name="now"/>, stamped ended when terminal.</summary>
    public static GenerationRequest Moved(GenerationRequest request, GenerationRequestState state, string? step, string? message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request with
        {
            State = state,
            Step = step,
            Message = message,
            UpdatedUtc = now,
            EndedUtc = IsActive(state) ? null : now,
        };
    }
}
