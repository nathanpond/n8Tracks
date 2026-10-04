namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Decides whether this build can take a database from the migrations it has applied to the
/// migrations the build knows. Pure: it looks at the two lists and nothing else.
/// </summary>
internal static class SchemaVersionCheck
{
    /// <summary>Returns why the database is refused, or null when it can be used or upgraded.</summary>
    /// <param name="known">The migrations of this build, oldest first.</param>
    /// <param name="applied">The migrations recorded in the database's history, oldest first.</param>
    /// <param name="hasOtherTables">Whether the database holds any table besides EF Core's own.</param>
    public static string? Refusal(IReadOnlyList<string> known, IReadOnlyList<string> applied, bool hasOtherTables)
    {
        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            var latest = known.Count > 0 ? known[^1] : "none";

            return "The database is newer than the application: it has applied "
                + string.Join(", ", unknown)
                + $", which this build does not know. The latest migration this build knows is {latest}. "
                + "Start a version of n8Tracks at least as new as the one that last used this database.";
        }

        if (applied.Count == 0)
        {
            return hasOtherTables
                ? "The file has tables but no migration history, so it is not a database this application can upgrade."
                : null;
        }

        // Every applied migration is known. They must be the first ones, with none skipped: a
        // migration applied on top of a missing earlier one was never produced by this application.
        var missing = known.Take(known.IndexOf(applied[^1])).Except(applied, StringComparer.Ordinal).ToList();

        return missing.Count > 0
            ? "The migration history has a gap: "
                + string.Join(", ", missing)
                + $" was never applied, but the later {applied[^1]} was, so it is not a database this application can upgrade."
            : null;
    }

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var index = 0; index < list.Count; index++)
        {
            if (string.Equals(list[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
