namespace n8Tracks.Domain.Songs;

/// <summary>
/// One Create on Suno, as n8Tracks knows of it: the clips it made become Generations linked to it.
/// Internal: no screen or public response names it. One event has many Generations; a Generation has
/// at most one, and may have none.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="ProviderRequestId">Suno's ID for the Create request, when known.</param>
/// <param name="Source">How n8Tracks learned of it.</param>
/// <param name="Confidence">How sure the grouping of its clips is.</param>
/// <param name="BatchSize">How many clips the Create made, from 1.</param>
/// <param name="OccurredUtc">When the Create happened.</param>
public sealed record GenerationEvent(
    Guid Id,
    string? ProviderRequestId,
    GenerationEventSource Source,
    GenerationEventConfidence Confidence,
    int BatchSize,
    DateTimeOffset OccurredUtc)
{
    /// <summary>The longest provider request ID kept.</summary>
    public const int MaximumRequestIdLength = 200;

    /// <summary>Why the fields cannot make an event; empty when they can.</summary>
    public static IReadOnlyList<string> Errors(string? providerRequestId, int batchSize)
    {
        var errors = new List<string>();
        if (batchSize < 1)
        {
            errors.Add("The batch size is at least 1.");
        }

        if (providerRequestId is not null && (providerRequestId.Length == 0 || providerRequestId.Length > MaximumRequestIdLength))
        {
            errors.Add($"A provider request ID is 1 to {MaximumRequestIdLength} characters, or none.");
        }

        return errors;
    }

    public static string NameOf(GenerationEventSource source) => source switch
    {
        GenerationEventSource.Observed => "observed",
        GenerationEventSource.Inferred => "inferred",
        GenerationEventSource.User => "user",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static string NameOf(GenerationEventConfidence confidence) => confidence switch
    {
        GenerationEventConfidence.High => "high",
        GenerationEventConfidence.Medium => "medium",
        _ => throw new ArgumentOutOfRangeException(nameof(confidence)),
    };
}

/// <summary>How n8Tracks learned of a Generation Event.</summary>
public enum GenerationEventSource
{
    /// <summary>The extension saw the Create happen.</summary>
    Observed,

    /// <summary>Worked out on import from clips made together (spike TS-001's grouping rule).</summary>
    Inferred,

    /// <summary>The user said so.</summary>
    User,
}

/// <summary>How sure a Generation Event's grouping is.</summary>
public enum GenerationEventConfidence
{
    High,
    Medium,
}
