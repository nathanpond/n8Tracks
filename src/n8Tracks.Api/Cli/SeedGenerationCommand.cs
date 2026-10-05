using n8Tracks.Api.Configuration;
using n8Tracks.Api.DependencyInjection;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Persistence;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;

namespace n8Tracks.Api.Cli;

/// <summary>
/// <c>n8tracks seed-generation &lt;version shortcode&gt;</c>: a test-only command that attaches a new
/// Generation to a Version, which freezes its lyrics and styles, through the same application
/// service the tests use (<see cref="GenerationService"/>). There is no public endpoint for it until
/// Generations arrive in M4; this is how the end-to-end tests and the Demo get a frozen Version.
/// <para>
/// It exists only where <c>ASPNETCORE_ENVIRONMENT</c> is <c>Development</c> or
/// <see cref="EnvironmentOptionsLoader.EnableTestSeeding"/> is <c>1</c> (the end-to-end containers set
/// it); anywhere else it refuses and changes nothing. It runs against the configured database (the
/// running app sees the change at its next request) and exits; like <c>reset-password</c> it never
/// creates the database or applies a migration. Each run adds another Generation, archived Versions
/// included. On success it writes the new Generation's shortcode, and nothing else, to standard
/// output; messages go to standard error. Exit code 0 on success, 1 for every refusal (an unknown
/// shortcode included).
/// </para>
/// </summary>
internal static class SeedGenerationCommand
{
    public const string Name = "seed-generation";

    private const string Usage = "Usage: n8tracks seed-generation <version shortcode>   (test instances only)";

    /// <summary>Whether the app binary was asked for this command: it is the first argument.</summary>
    public static bool IsRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Length > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether test seeding is switched on: in the Development environment, or with
    /// <see cref="EnvironmentOptionsLoader.EnableTestSeeding"/> set to exactly <c>1</c>.
    /// </summary>
    public static bool IsEnabled(EnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // In any letter case, as the host's own IsDevelopment() reads it.
        return string.Equals(EnvironmentOptionsLoader.HostEnvironmentName(environment), Environments.Development, StringComparison.OrdinalIgnoreCase)
            || (environment.Variables.TryGetValue(EnvironmentOptionsLoader.EnableTestSeeding, out var value)
                && string.Equals(value.Trim(), "1", StringComparison.Ordinal));
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
        if (!IsEnabled(environment))
        {
            error.WriteLine(
                $"{Name} is a test-only command and is not available here. It needs ASPNETCORE_ENVIRONMENT=Development "
                + $"or {EnvironmentOptionsLoader.EnableTestSeeding}=1. Nothing was changed.");
            return 1;
        }

        if (args is not [var shortcode] || CatalogReference.Parse(shortcode).Kind != ReferenceKind.Version)
        {
            error.WriteLine("Give one Version shortcode, such as n8-12-v1.1.");
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
                    return await SeedAsync(scope.ServiceProvider, shortcode, output, error, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                error.WriteLine($"No Generation was attached: {exception.GetType().Name}: {exception.Message}");
                return 1;
            }
        }
    }

    private static async Task<int> SeedAsync(IServiceProvider services, string shortcode, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var database = services.GetRequiredService<IDatabaseSchemaCheck>();
        switch (await database.CheckWithoutChangingAsync(cancellationToken).ConfigureAwait(false))
        {
            case DatabaseCondition.Missing:
                error.WriteLine($"There is no database at {database.DatabaseFile}. Nothing was changed.");
                return 1;
            case DatabaseCondition.Upgrading:
                error.WriteLine("The database is being upgraded (the migration lock is held). Run this command again once n8Tracks has started. Nothing was changed.");
                return 1;
            case DatabaseCondition.Maintenance:
                error.WriteLine("n8Tracks is in maintenance: a restore is running, or was interrupted. Nothing was changed.");
                return 1;
            case DatabaseCondition.SchemaMismatch:
                error.WriteLine("The database schema does not match this version of n8Tracks. Start the application first. Nothing was changed.");
                return 1;
        }

        switch (await services.GetRequiredService<GenerationService>().AttachAsync(shortcode, cancellationToken).ConfigureAwait(false))
        {
            case GenerationAttachOutcome.Attached attached:
                output.WriteLine(attached.Generation.Shortcode);
                error.WriteLine($"Generation {attached.Generation.Shortcode} attached; Version {attached.Generation.VersionShortcode} is frozen.");
                return 0;

            case GenerationAttachOutcome.VersionNotFound:
                error.WriteLine($"There is no Version {shortcode}. Nothing was changed.");
                return 1;

            default:
                throw new InvalidOperationException("Unknown attach outcome.");
        }
    }
}
