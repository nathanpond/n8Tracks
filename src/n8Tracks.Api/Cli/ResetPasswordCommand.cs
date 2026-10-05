using n8Tracks.Api.Configuration;
using n8Tracks.Api.DependencyInjection;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Persistence;
using n8Tracks.Application.Setup;

namespace n8Tracks.Api.Cli;

/// <summary>
/// <c>n8tracks reset-password</c>, run with <c>docker exec</c> by an owner who has forgotten the
/// administrator password: there is no email reset. It sets a new password through the same
/// application service the web UI uses, against the database of the app running in the same
/// container, which sees the change at its next request: every session ends and the sign-in
/// throttle is cleared. Credentials keep working.
/// <para>
/// It builds a small service container of its own (the data path is the only setting it validates;
/// no web host, no background service), and never creates the database or applies a migration: a
/// missing database means setup was never completed, and a schema other than this build's means the
/// app must be started first. Prompts and messages go to standard error, standard output stays
/// empty, and the password is never written anywhere but as its hash. Exit code 0 on success, 1 for
/// every refusal.
/// </para>
/// </summary>
internal static class ResetPasswordCommand
{
    public const string Name = "reset-password";
    public const string PasswordStdinOption = "--password-stdin";

    public const string SourceContext = "n8Tracks.ResetPassword";

    private const string Usage = "Usage: n8tracks reset-password [--password-stdin]";

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
        CommandConsole console,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(console);

        var error = console.Error;
        bool fromStdin;
        switch (args)
        {
            case []:
                fromStdin = false;
                break;
            case [PasswordStdinOption]:
                fromStdin = true;
                break;
            default:
                // The argument itself is not shown: it may be the password, typed where it does not belong.
                error.WriteLine($"Unknown arguments. The password is never given as an argument; type it when asked, or use {PasswordStdinOption}.");
                error.WriteLine(Usage);
                return 1;
        }

        N8TracksOptions options;
        try
        {
            options = EnvironmentOptionsLoader.LoadDataPathOnly(environment);
        }
        catch (ConfigurationValidationException exception)
        {
            foreach (var invalid in exception.Errors)
            {
                error.WriteLine($"Invalid configuration: {invalid.Variable} {invalid.Reason}");
            }

            return 1;
        }

        var services = CommandServices.Build(options);
        await using (services.ConfigureAwait(false))
        {
            try
            {
                var scope = services.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    return await ResetAsync(scope.ServiceProvider, fromStdin, console, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The message of a database error names the failure, never a value that was written.
                error.WriteLine($"The password was not reset: {exception.GetType().Name}: {exception.Message}");
                return 1;
            }
        }
    }

    private static async Task<int> ResetAsync(
        IServiceProvider services,
        bool fromStdin,
        CommandConsole console,
        CancellationToken cancellationToken)
    {
        var error = console.Error;
        var database = services.GetRequiredService<IDatabaseSchemaCheck>();

        switch (await database.CheckWithoutChangingAsync(cancellationToken).ConfigureAwait(false))
        {
            case DatabaseCondition.Missing:
                error.WriteLine(
                    $"Setup has never been completed: there is no database at {database.DatabaseFile}. "
                    + "Open n8Tracks in a browser to create the administrator; nothing was changed.");
                return 1;
            case DatabaseCondition.Upgrading:
                error.WriteLine(
                    "The database is being upgraded, or an upgrade did not finish (the migration lock is held, or upgrade-state.json is in the data path). Wait until n8Tracks has started, "
                    + "and if it does not start, read its log; then run this command again. Nothing was changed.");
                return 1;
            case DatabaseCondition.Maintenance:
                error.WriteLine(
                    "n8Tracks is in maintenance: a restore is running, or was interrupted. Wait until it has finished, "
                    + "and if it does not finish, read its log; then run this command again. Nothing was changed.");
                return 1;
            case DatabaseCondition.SchemaMismatch:
                error.WriteLine(
                    "The database schema does not match this version of n8Tracks. Start the application first, "
                    + "so that it brings the database up to date, then run this command again. Nothing was changed.");
                return 1;
        }

        if (!await services.GetRequiredService<IAdministratorStore>().ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            WriteSetupNotCompleted(error);
            return 1;
        }

        if (ReadPassword(fromStdin, console) is not { } typed)
        {
            return 1;
        }

        var outcome = await services.GetRequiredService<AccountService>()
            .ResetPasswordAsync(typed.Password, typed.Confirmation, cancellationToken)
            .ConfigureAwait(false);

        switch (outcome)
        {
            case PasswordResetOutcome.Reset reset:
                using (var log = LoggingRegistration.CreateCommandLogger(error, SourceContext))
                {
                    log.Information("The administrator password was reset from the container; {SessionCount} session(s) ended", reset.SessionsEnded);
                }

                error.WriteLine(
                    $"The password of {reset.Username} was reset. Every browser session was ended and sign-in attempts were unlocked; "
                    + "sign in with the new password. API and extension credentials keep working.");
                return 0;

            case PasswordResetOutcome.Invalid invalid:
                foreach (var message in invalid.Errors)
                {
                    error.WriteLine(message);
                }

                error.WriteLine("The password was not changed.");
                return 1;

            case PasswordResetOutcome.NoAdministrator:
                WriteSetupNotCompleted(error);
                return 1;

            default:
                throw new InvalidOperationException("Unknown password reset outcome.");
        }
    }

    /// <summary>
    /// The new password and its repetition, or null after telling why there are none. From standard
    /// input it is the first line without its line ending, and counts as repeated; at a terminal it
    /// is typed twice, unseen. Anywhere else the command refuses: it would wait for typing no one sees.
    /// </summary>
    private static (string Password, string Confirmation)? ReadPassword(bool fromStdin, CommandConsole console)
    {
        var error = console.Error;
        if (fromStdin)
        {
            var line = console.Input.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                error.WriteLine($"No password was read: with {PasswordStdinOption}, the first line of standard input is the new password. Nothing was changed.");
                return null;
            }

            return (line, line);
        }

        if (!console.IsTerminal)
        {
            error.WriteLine(
                $"Standard input is not a terminal, so the password cannot be typed. Run the command with docker exec -it, "
                + $"or pass the password on standard input with {PasswordStdinOption}. Nothing was changed.");
            return null;
        }

        error.Write("New password: ");
        var password = console.ReadSecretLine();
        if (password is null)
        {
            error.WriteLine("Cancelled. Nothing was changed.");
            return null;
        }

        error.Write("Repeat the new password: ");
        var confirmation = console.ReadSecretLine();
        if (confirmation is null)
        {
            error.WriteLine("Cancelled. Nothing was changed.");
            return null;
        }

        return (password, confirmation);
    }

    private static void WriteSetupNotCompleted(TextWriter error) =>
        error.WriteLine("Setup has never been completed: there is no administrator yet. Open n8Tracks in a browser to create one; nothing was changed.");
}
