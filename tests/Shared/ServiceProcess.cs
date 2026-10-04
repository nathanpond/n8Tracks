using System.Diagnostics;
using System.Reflection;

namespace n8Tracks.TestSupport;

/// <summary>
/// One of the services running as its own process, the way an operator runs it: real command-line
/// arguments, real environment variables, and a working directory of its own (which is where the
/// host looks for <c>appsettings.json</c>). Nothing from the machine running the tests reaches its
/// settings. Disposing stops the process and removes the directory.
/// </summary>
internal sealed class ServiceProcess : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly Process process;
    private readonly Task<string> standardOutput;
    private readonly Task<string> standardError;

    private ServiceProcess(Process process, string workingDirectory)
    {
        this.process = process;
        WorkingDirectory = workingDirectory;
        standardOutput = process.StandardOutput.ReadToEndAsync();
        standardError = process.StandardError.ReadToEndAsync();
    }

    public string WorkingDirectory { get; }

    /// <param name="entryAssembly">The service's assembly, as built beside the tests.</param>
    /// <param name="settingsFile">The content of <c>appsettings.json</c> in the working directory, or null for none.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="variables">The environment variables the process gets, besides the machine's unrelated ones.</param>
    public static ServiceProcess Start(
        Assembly entryAssembly,
        string? settingsFile,
        IReadOnlyList<string> arguments,
        IReadOnlyList<(string Name, string Value)> variables)
    {
        var workingDirectory = Directory.CreateTempSubdirectory("n8tracks-test-process-").FullName;

        // The app needs its web root to exist; the gateway does not mind.
        Directory.CreateDirectory(Path.Combine(workingDirectory, "wwwroot"));
        if (settingsFile is not null)
        {
            File.WriteAllText(Path.Combine(workingDirectory, "appsettings.json"), settingsFile);
        }

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(entryAssembly.Location);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys
            .Where(name => name.StartsWith("N8TRACKS_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Logging__", StringComparison.OrdinalIgnoreCase)
                || name.Contains("OTEL_", StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in variables)
        {
            start.Environment[name] = value;
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("The service process did not start.");
        return new ServiceProcess(process, workingDirectory);
    }

    /// <summary>Asks <paramref name="url"/> until the service answers, whatever the status; fails with its output if it exits first.</summary>
    public async Task WaitUntilItAnswers(HttpClient client, Uri url)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (process.HasExited)
            {
                Assert.Fail($"The service exited with code {process.ExitCode}: {await standardOutput}{await standardError}");
            }

            try
            {
                using var response = await client.GetAsync(url);
                return;
            }
            catch (HttpRequestException) when (waited.Elapsed < StartTimeout)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>Stops the service and returns everything it wrote to standard output and standard error.</summary>
    public async Task<(string Output, string Error)> StopAndReadOutput()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();

        return (await standardOutput, await standardError);
    }

    public void Dispose()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        process.WaitForExit();
        process.Dispose();

        if (Directory.Exists(WorkingDirectory))
        {
            Directory.Delete(WorkingDirectory, recursive: true);
        }
    }
}

/// <summary>
/// The ways a setting can reach a host's configuration without being the environment variable of
/// that name: none of them may turn telemetry on, or change where it goes.
/// </summary>
internal static class OtherConfigurationSources
{
    public const string CommandLine = "command-line argument";
    public const string AspNetCorePrefix = "ASPNETCORE_ prefixed variable";
    public const string DotNetPrefix = "DOTNET_ prefixed variable";
    public const string SettingsFile = "appsettings.json";

    public static TheoryData<string> All => [CommandLine, AspNetCorePrefix, DotNetPrefix, SettingsFile];

    /// <summary>Starts the service with <paramref name="settings"/> arriving through <paramref name="source"/> and <paramref name="variables"/> as its environment.</summary>
    public static ServiceProcess Start(
        Assembly entryAssembly,
        string source,
        IReadOnlyList<(string Name, string Value)> settings,
        params (string Name, string Value)[] variables)
    {
        string[] arguments = source == CommandLine ? [.. settings.Select(setting => $"--{setting.Name}={setting.Value}")] : [];

        var settingsFile = source == SettingsFile
            ? System.Text.Json.JsonSerializer.Serialize(settings.ToDictionary(setting => setting.Name, setting => setting.Value))
            : null;

        var prefix = source switch
        {
            AspNetCorePrefix => "ASPNETCORE_",
            DotNetPrefix => "DOTNET_",
            _ => null,
        };

        var environment = prefix is null
            ? variables
            : [.. variables, .. settings.Select(setting => (prefix + setting.Name, setting.Value))];

        return ServiceProcess.Start(entryAssembly, settingsFile, arguments, environment);
    }
}
