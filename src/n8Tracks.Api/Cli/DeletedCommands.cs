using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.DependencyInjection;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Persistence;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Setup;

namespace n8Tracks.Api.Cli;

/// <summary>
/// The recovery commands (#105), run with <c>docker exec</c> beside the running app:
/// <c>n8tracks list-deleted [--all] [--json]</c> lists the retention groups of the last 30 days
/// (with <c>--all</c>, also older ones the daily prune has not removed yet), and
/// <c>n8tracks restore-deleted &lt;shortcode or group ID&gt; [--json]</c> restores one through
/// <see cref="DeletedItemsService"/>, the same routine the tests use, against the same database file.
/// <para>
/// Like <c>reset-password</c>, each builds a small service container of its own (the data path and
/// the time zone are the settings it reads), opens the database with the usual busy timeout, takes no
/// data-path lock, and never creates the database or applies a migration: it refuses with no database,
/// before setup is complete, during maintenance or an upgrade, and when the schema is not this build's.
/// A restored record's revision goes up, so open pages refetch it; the server needs no restart.
/// The listing and the report go to standard output, messages to standard error. Nothing retained is
/// ever written but kinds, labels, shortcodes, times, and counts: never lyrics, prompts, or other
/// content. Exit code 0 on success, 1 for every refusal (an unknown reference included).
/// </para>
/// </summary>
internal static class DeletedCommands
{
    public const string ListName = "list-deleted";
    public const string RestoreName = "restore-deleted";
    public const string AllOption = "--all";
    public const string JsonOption = "--json";

    /// <summary>What the listing prints when nothing was deleted within the retention period.</summary>
    public const string NothingDeleted = "Nothing deleted in the last 30 days.";

    /// <summary>
    /// The line after a refusal. It is a line of its own, because a refusal can end in a command to
    /// run, which should be copied without it.
    /// </summary>
    private const string NothingChanged = "Nothing was changed.";

    private const string ListUsage = "Usage: n8tracks list-deleted [--all] [--json]";
    private const string RestoreUsage = "Usage: n8tracks restore-deleted <shortcode or group ID> [--json]";

    /// <summary>
    /// The <c>--json</c> output. It goes to a terminal or a pipe, never into a web page, so it uses the
    /// relaxed encoder: a '+' in an offset and letters outside ASCII print as themselves rather than
    /// as <c>\u</c> escapes, while quotes, backslashes, and control characters are still escaped.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Whether the app binary was asked for <c>list-deleted</c>: it is the first argument.</summary>
    public static bool IsListRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Length > 0 && string.Equals(args[0], ListName, StringComparison.Ordinal);
    }

    /// <summary>Whether the app binary was asked for <c>restore-deleted</c>: it is the first argument.</summary>
    public static bool IsRestoreRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Length > 0 && string.Equals(args[0], RestoreName, StringComparison.Ordinal);
    }

    /// <summary>Runs <c>list-deleted</c>. <paramref name="args"/> are the ones after the command's name.</summary>
    public static Task<int> RunListAsync(
        string[] args,
        EnvironmentSnapshot environment,
        TextWriter output,
        CommandConsole console,
        Action<IServiceCollection>? testServices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(console);

        var options = args.Distinct(StringComparer.Ordinal).ToList();
        if (options.Count != args.Length || options.Any(static option => option is not (AllOption or JsonOption)))
        {
            console.Error.WriteLine("Unknown arguments. " + ListUsage);
            return Task.FromResult(1);
        }

        var all = options.Contains(AllOption);
        var json = options.Contains(JsonOption);
        return RunAsync(environment, console, testServices, "Nothing deleted could be listed", async (services, settings, token) =>
        {
            var items = await services.GetRequiredService<DeletedItemsService>().ListAsync(all, token).ConfigureAwait(false);
            if (json)
            {
                output.WriteLine(JsonSerializer.Serialize(items.Select(item => ListedItem.From(item, settings.TimeZone)), Json));
                return 0;
            }

            if (items.Count == 0)
            {
                output.WriteLine(all ? "Nothing deleted is kept: everything deleted has been restored or pruned." : NothingDeleted);
                return 0;
            }

            WriteTable(output, items, settings.TimeZone);
            console.Error.WriteLine(OneLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{items.Count} deleted item(s), newest first; times in {settings.TimeZone.Id}. Restore one with: n8tracks restore-deleted <shortcode or group ID>")));
            return 0;
        }, cancellationToken);
    }

    /// <summary>Runs <c>restore-deleted</c>. <paramref name="args"/> are the ones after the command's name.</summary>
    public static Task<int> RunRestoreAsync(
        string[] args,
        EnvironmentSnapshot environment,
        TextWriter output,
        CommandConsole console,
        Action<IServiceCollection>? testServices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(console);

        var json = args.Contains(JsonOption, StringComparer.Ordinal);
        var rest = args.Where(static arg => arg != JsonOption).ToList();
        if (rest is not [var reference] || reference.StartsWith('-') || args.Length - rest.Count > 1)
        {
            console.Error.WriteLine("Give one shortcode (such as n8-3) or group ID, as list-deleted shows it. " + RestoreUsage);
            return Task.FromResult(1);
        }

        var error = console.Error;
        return RunAsync(environment, console, testServices, "Nothing was restored", async (services, settings, token) =>
        {
            switch (await services.GetRequiredService<DeletedItemsService>().RestoreAsync(reference, token).ConfigureAwait(false))
            {
                case DeletedItemRestoreOutcome.Restored restored:
                    if (json)
                    {
                        output.WriteLine(JsonSerializer.Serialize(RestoredReport.From(restored, settings.TimeZone), Json));
                    }
                    else
                    {
                        WriteReport(output, restored);
                    }

                    return 0;

                case DeletedItemRestoreOutcome.NotFound notFound:
                    error.WriteLine(OneLine(notFound.Message));
                    error.WriteLine(NothingChanged);
                    return 1;

                case DeletedItemRestoreOutcome.Refused refused:
                    error.WriteLine(OneLine(refused.Message));
                    error.WriteLine(NothingChanged);
                    return 1;

                default:
                    throw new InvalidOperationException("Unknown restore outcome.");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// The part both commands share: the settings, the service container, and the checks that the
    /// database is there, of this build's schema, not in maintenance or an upgrade, and set up.
    /// </summary>
    private static async Task<int> RunAsync(
        EnvironmentSnapshot environment,
        CommandConsole console,
        Action<IServiceCollection>? testServices,
        string failure,
        Func<IServiceProvider, N8TracksOptions, CancellationToken, Task<int>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var error = console.Error;
        N8TracksOptions options;
        try
        {
            options = EnvironmentOptionsLoader.LoadDataPathAndTimeZone(environment);
        }
        catch (ConfigurationValidationException exception)
        {
            foreach (var invalid in exception.Errors)
            {
                error.WriteLine($"Invalid configuration: {invalid.Variable} {invalid.Reason}");
            }

            return 1;
        }

        var services = CommandServices.Build(options, testServices);
        await using (services.ConfigureAwait(false))
        {
            try
            {
                var scope = services.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    if (await RefusalAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false) is { } refusal)
                    {
                        error.WriteLine(refusal);
                        return 1;
                    }

                    return await run(scope.ServiceProvider, options, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The message of a database error names the failure, never a value that was read.
                error.WriteLine(OneLine($"{failure}: {exception.GetType().Name}: {exception.Message}"));
                return 1;
            }
        }
    }

    /// <summary>Why the command cannot work on the database now (the checks of <c>reset-password</c>), or null.</summary>
    private static async Task<string?> RefusalAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var database = services.GetRequiredService<IDatabaseSchemaCheck>();
        switch (await database.CheckWithoutChangingAsync(cancellationToken).ConfigureAwait(false))
        {
            case DatabaseCondition.Missing:
                return $"Setup has never been completed: there is no database at {database.DatabaseFile}. Nothing was changed.";
            case DatabaseCondition.Upgrading:
                return "The database is being upgraded, or an upgrade did not finish (the migration lock is held, or upgrade-state.json is in the data path). "
                    + "Wait until n8Tracks has started, and if it does not start, read its log; then run this command again. Nothing was changed.";
            case DatabaseCondition.Maintenance:
                return "n8Tracks is in maintenance: a restore is running, or was interrupted. Wait until it has finished, "
                    + "and if it does not finish, read its log; then run this command again. Nothing was changed.";
            case DatabaseCondition.SchemaMismatch:
                return "The database schema does not match this version of n8Tracks. Start the application first, "
                    + "so that it brings the database up to date, then run this command again. Nothing was changed.";
        }

        return await services.GetRequiredService<IAdministratorStore>().ExistsAsync(cancellationToken).ConfigureAwait(false)
            ? null
            : "Setup has never been completed: there is no administrator yet. Open n8Tracks in a browser to create one; nothing was changed.";
    }

    /// <summary>A header and one row per group, in columns: the ID as it can be given to a restore, kind, label, shortcode, and times.</summary>
    private static void WriteTable(TextWriter output, IReadOnlyList<DeletedItem> items, TimeZoneInfo timeZone)
    {
        string[] header = ["GROUP ID", "KIND", "LABEL", "SHORTCODE", "DELETED", "PRUNES"];
        var rows = items.Select(item => new[]
        {
            item.ShortId,
            item.KindName,
            OneLine(item.Label),
            item.Shortcode ?? "-",
            Shown(item.DeletedUtc, timeZone),
            Shown(item.PruneAfterUtc, timeZone),
        }).ToList();

        var widths = Enumerable.Range(0, header.Length)
            .Select(column => rows.Select(row => row[column].Length).Append(header[column].Length).Max())
            .ToArray();
        foreach (var row in rows.Prepend(header))
        {
            output.WriteLine(string.Join("  ", row.Select((cell, column) => cell.PadRight(widths[column]))).TrimEnd());
        }
    }

    /// <summary>What was restored, the kinds and counts put back, and anything left out or restored differently.</summary>
    private static void WriteReport(TextWriter output, DeletedItemRestoreOutcome.Restored restored)
    {
        var item = restored.Item;
        output.WriteLine(OneLine($"Restored {item.Label} (group {item.ShortId})."));
        output.WriteLine("Put back:");
        foreach (var count in restored.PutBack)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {count.Noun}: {count.Count}"));
        }

        if (restored.Notes.Count > 0)
        {
            output.WriteLine("Left out or changed:");
            foreach (var note in restored.Notes)
            {
                output.WriteLine("  " + OneLine(note));
            }
        }
    }

    /// <summary>A time as the listing shows it: to the minute, in the configured time zone.</summary>
    private static string Shown(DateTimeOffset utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(utc, timeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A time as the JSON gives it: ISO 8601 with the configured time zone's offset.</summary>
    private static string Iso(DateTimeOffset utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(utc, timeZone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string OneLine(string text) => RestoreCommand.OneLine(text);

    /// <summary>A listed group in <c>--json</c>: the table's columns in camelCase, the whole group ID, and the contents as counts.</summary>
    private sealed record ListedItem(
        string GroupId,
        string ShortId,
        string Kind,
        string KindName,
        string Label,
        string? Shortcode,
        string Deleted,
        string Prunes,
        IReadOnlyList<CountedRecords> Contents)
    {
        public static ListedItem From(DeletedItem item, TimeZoneInfo timeZone) => new(
            item.GroupId.ToString("D", CultureInfo.InvariantCulture),
            item.ShortId,
            item.Kind,
            item.KindName,
            item.Label,
            item.Shortcode,
            Iso(item.DeletedUtc, timeZone),
            Iso(item.PruneAfterUtc, timeZone),
            [.. item.Contents.Select(CountedRecords.From)]);
    }

    /// <summary>How many records of one type, in <c>--json</c>.</summary>
    private sealed record CountedRecords(string RecordType, string Noun, int Count)
    {
        public static CountedRecords From(DeletedCount count) => new(count.RecordType, count.Noun, count.Count);
    }

    /// <summary>A restore's report in <c>--json</c>: the group restored, the counts put back, and the notes.</summary>
    private sealed record RestoredReport(ListedItem Restored, IReadOnlyList<CountedRecords> PutBack, IReadOnlyList<string> Notes)
    {
        public static RestoredReport From(DeletedItemRestoreOutcome.Restored restored, TimeZoneInfo timeZone) => new(
            ListedItem.From(restored.Item, timeZone),
            [.. restored.PutBack.Select(CountedRecords.From)],
            restored.Notes);
    }
}
