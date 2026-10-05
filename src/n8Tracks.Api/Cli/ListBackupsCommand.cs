using System.Globalization;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.DependencyInjection;
using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Cli;

/// <summary>
/// <c>n8tracks list-backups</c>: the backups in the backup mount and in the fallback folder under
/// the data path, newest first, one per line on standard output with the date it was made, its kind,
/// the application version that made it, its size, and the path to give <c>n8tracks restore</c>.
/// It reads only the archives' manifests (the same listing the Backups page shows), never the
/// database, so it runs whether the app is running, stopped, or unable to start. Messages go to
/// standard error. Exit code 0 when the folders could be read (none found included), 1 otherwise.
/// </summary>
internal static class ListBackupsCommand
{
    public const string Name = "list-backups";

    private const string Usage = "Usage: n8tracks list-backups";

    /// <summary>Whether the app binary was asked for this command: it is the first argument.</summary>
    public static bool IsRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Length > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);
    }

    /// <summary>Runs the command. <paramref name="args"/> are the ones after the command's name.</summary>
    public static async Task<int> RunAsync(
        string[] args,
        EnvironmentSnapshot environment,
        TextWriter output,
        CommandConsole console,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(console);

        var error = console.Error;
        if (args.Length > 0)
        {
            error.WriteLine("This command takes no arguments. " + Usage);
            return 1;
        }

        N8TracksOptions options;
        try
        {
            options = EnvironmentOptionsLoader.LoadPathsOnly(environment);
        }
        catch (ConfigurationValidationException exception)
        {
            error.WriteLine(RestoreCommand.OneLine(string.Join(
                " ",
                exception.Errors.Select(static invalid => $"Invalid configuration: {invalid.Variable} {invalid.Reason}"))));
            return 1;
        }

        var services = CommandServices.Build(options);
        await using (services.ConfigureAwait(false))
        {
            try
            {
                var storage = services.GetRequiredService<IBackupStorage>();
                var archives = await storage.ListAsync(cancellationToken).ConfigureAwait(false);
                var mount = storage.FolderPath(BackupLocation.Mount);
                var fallback = storage.FolderPath(BackupLocation.Data);
                if (archives.Count == 0)
                {
                    error.WriteLine(RestoreCommand.OneLine($"No backups were found in {mount} or {fallback}."));
                    return 0;
                }

                Write(output, archives, storage);
                error.WriteLine(RestoreCommand.OneLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{archives.Count} backup(s) in {mount} and {fallback}. Restore one with the container stopped: n8tracks restore <path>")));
                return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                error.WriteLine(RestoreCommand.OneLine($"The backups could not be listed: {exception.GetType().Name}: {exception.Message}"));
                return 1;
            }
        }
    }

    /// <summary>A header and one row per archive, in columns.</summary>
    private static void Write(TextWriter output, IReadOnlyList<BackupArchive> archives, IBackupStorage storage)
    {
        string[] header = ["CREATED (UTC)", "KIND", "VERSION", "SIZE", "PATH"];
        var rows = archives.Select(archive => new[]
        {
            archive.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            archive.Validity switch
            {
                BackupValidity.Valid => archive.Kind ?? "-",
                BackupValidity.Newer => $"{archive.Kind ?? "-"} (newer)",
                _ => "unreadable",
            },
            archive.ApplicationVersion ?? "-",
            RestoreValidator.Size(archive.SizeBytes),
            RestoreCommand.OneLine(Path.Combine(storage.FolderPath(archive.Location), archive.Name)),
        }).ToList();

        var widths = Enumerable.Range(0, header.Length - 1)
            .Select(column => rows.Select(row => row[column].Length).Append(header[column].Length).Max())
            .ToArray();

        foreach (var row in rows.Prepend(header))
        {
            output.WriteLine(string.Join("  ", row.Select((cell, column) => column < widths.Length ? cell.PadRight(widths[column]) : cell)));
        }
    }
}
