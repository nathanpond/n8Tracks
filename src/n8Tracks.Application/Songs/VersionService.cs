using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>How asking for a new Version's numbers ended.</summary>
public abstract record NextNumbersOutcome
{
    private NextNumbersOutcome()
    {
    }

    /// <summary>The numbers a new Version from the source may take, the proposal first.</summary>
    public sealed record Found(IReadOnlyList<VersionNumberOption> Options) : NextNumbersOutcome;

    /// <summary>There is no Version with that ID.</summary>
    public sealed record NotFound : NextNumbersOutcome;

    /// <summary>Both options would be longer than a number may be, so nothing can branch from the source.</summary>
    public sealed record TooDeep : NextNumbersOutcome;
}

/// <summary>
/// Versions: for now, the numbers a new Version may take when it branches from an existing one, by
/// <see cref="VersionNumbering"/>. The create-Version operation re-checks the chosen number against
/// <see cref="VersionNumbering.Options"/> inside its own transaction.
/// </summary>
public sealed class VersionService(IVersionStore versions)
{
    /// <summary>
    /// The valid numbers for a new Version created from the Version with <paramref name="id"/>
    /// (archived ones included): normally the next sibling and the child, the proposal first. Never a
    /// number any Version of the Song has or ever had.
    /// </summary>
    public async Task<NextNumbersOutcome> NextNumbersAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await versions.FindNumberingAsync(id, cancellationToken).ConfigureAwait(false) is not { } facts)
        {
            return new NextNumbersOutcome.NotFound();
        }

        // Stored numbers were valid when assigned, so each parses.
        var options = VersionNumbering.Options(
            VersionNumber.Parse(facts.Number),
            facts.UsedNumbers.Select(VersionNumber.Parse));

        return options.Count == 0 ? new NextNumbersOutcome.TooDeep() : new NextNumbersOutcome.Found(options);
    }
}
