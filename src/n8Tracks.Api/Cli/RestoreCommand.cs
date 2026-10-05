using System.Globalization;
using System.Text;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.DependencyInjection;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Cli;

/// <summary>
/// <c>n8tracks restore &lt;archive&gt;</c>, for an instance that cannot start or is stuck in
/// maintenance: run with <c>docker run --rm</c> and the instance's volumes while its container is
/// stopped. It validates the archive exactly as the web restore does, then replaces the database
/// (settings included) and managed assets with the archive's, through the same application services
/// (<see cref="OfflineRestoreService"/>). The current files are moved aside under the data path
/// first and put back if anything fails; after a success they are kept in a dated folder the
/// message names. A stuck maintenance state and a failed upgrade's marker are cleared, so the
/// application starts normally; it applies any migration the restored database lacks at that start.
/// <para>
/// It holds the data path's lock while it runs, so it refuses to run beside a server using the same
/// data path, and no server can start meanwhile. Messages go to standard error, standard output
/// stays empty. Exit code 0 on success; 1, with one line saying why, for every refusal and failure.
/// Nothing it prints holds a secret: only paths, the archive's name, and what went wrong.
/// </para>
/// </summary>
internal static class RestoreCommand
{
    public const string Name = "restore";

    public const string SourceContext = "n8Tracks.Restore";

    private const string Usage = "Usage: n8tracks restore <backup archive>   (with the container stopped; n8tracks list-backups lists them)";

    /// <summary>Whether the app binary was asked for this command: it is the first argument.</summary>
    public static bool IsRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Length > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);
    }

    /// <summary>Runs the command. <paramref name="args"/> are the ones after the command's name.</summary>
    /// <param name="args">The archive's path, alone.</param>
    /// <param name="environment">The process environment.</param>
    /// <param name="console">Where messages go.</param>
    /// <param name="cancellationToken">Stops it; whatever was moved is put back.</param>
    /// <param name="testServices">Test-only: changes to the command's registrations.</param>
    public static async Task<int> RunAsync(
        string[] args,
        EnvironmentSnapshot environment,
        CommandConsole console,
        CancellationToken cancellationToken,
        Action<IServiceCollection>? testServices = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(console);

        var error = console.Error;
        if (args is not [var archive] || string.IsNullOrWhiteSpace(archive))
        {
            error.WriteLine("Give the path of one backup archive, such as /backup/n8tracks-backup-20261005-030000.zip. " + Usage);
            return 1;
        }

        N8TracksOptions options;
        try
        {
            options = EnvironmentOptionsLoader.LoadPathsOnly(environment);
        }
        catch (ConfigurationValidationException exception)
        {
            error.WriteLine(OneLine(string.Join(
                " ",
                exception.Errors.Select(static invalid => $"Invalid configuration: {invalid.Variable} {invalid.Reason}"))) + " Nothing was changed.");
            return 1;
        }

        var fullPath = Path.GetFullPath(archive, environment.WorkingDirectory);
        if (Directory.Exists(fullPath))
        {
            error.WriteLine(OneLine($"{fullPath} is a folder; give the path of one backup archive in it (n8tracks list-backups lists them). Nothing was changed."));
            return 1;
        }

        var services = CommandServices.Build(options, testServices);
        await using (services.ConfigureAwait(false))
        {
            try
            {
                var dataPathLock = services.GetRequiredService<IDataPathLock>();
                if (!dataPathLock.TryAcquire())
                {
                    error.WriteLine(OneLine(
                        $"n8Tracks is running against the data path {options.DataPath} (it holds {dataPathLock.LockFile}). Stop the container first, "
                        + "then run this command with docker run --rm and the same volumes. Nothing was changed."));
                    return 1;
                }

                var scope = services.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var outcome = await scope.ServiceProvider.GetRequiredService<OfflineRestoreService>()
                        .RestoreAsync(fullPath, cancellationToken)
                        .ConfigureAwait(false);
                    return Report(outcome, error);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Before anything moved: the restore service puts back whatever it moved before it returns.
                error.WriteLine(OneLine($"The restore did not run: {exception.GetType().Name}: {exception.Message}"));
                return 1;
            }
        }
    }

    private static int Report(OfflineRestoreOutcome outcome, TextWriter error)
    {
        switch (outcome)
        {
            case OfflineRestoreOutcome.Restored restored:
                var archive = restored.Archive;
                using (var log = LoggingRegistration.CreateCommandLogger(error, SourceContext))
                {
                    log.Information(
                        "The instance was restored by the container command from {ArchiveName}, made {ArchiveCreatedAt} by n8Tracks {ArchiveVersion}",
                        archive.Name,
                        archive.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        archive.ApplicationVersion);
                }

                error.WriteLine(OneLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Restored {archive.Name}, a {archive.Kind} backup made {archive.CreatedUtc:yyyy-MM-dd HH:mm:ss}Z by n8Tracks {archive.ApplicationVersion}. Every session was ended.")));
                if (restored.ClearedMaintenance)
                {
                    error.WriteLine("The maintenance state was cleared.");
                }

                if (restored.ClearedUpgradeMarker)
                {
                    error.WriteLine("The failed upgrade's marker was removed.");
                }

                error.WriteLine(restored.PreviousDataFolder is { } kept
                    ? OneLine($"The database and assets from before the restore are kept in {kept}; delete that folder once you no longer need it.")
                    : "There was no database before the restore.");
                error.WriteLine("Start the container: n8Tracks applies any database update the backup needs as it starts.");
                return 0;

            case OfflineRestoreOutcome.Refused refused:
                error.WriteLine(OneLine(refused.Reason));
                return 1;

            case OfflineRestoreOutcome.RolledBack rolledBack:
                error.WriteLine(OneLine(rolledBack.Reason));
                return 1;

            case OfflineRestoreOutcome.RollbackFailed failed:
                error.WriteLine(OneLine(failed.Reason));
                return 1;

            default:
                throw new InvalidOperationException("Unknown restore outcome.");
        }
    }

    /// <summary>The text on one line: line breaks and other control characters become spaces.</summary>
    internal static string OneLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var flat = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            flat.Append(char.IsControl(character) ? ' ' : character);
        }

        return string.Join(' ', flat.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
