using System.Text.RegularExpressions;

namespace n8Tracks.Architecture.Tests;

/// <summary>
/// Guard for "settings come from one place": the app and the gateway each read the process
/// environment once, into a snapshot (<c>ProcessEnvironment.Read()</c>, called by <c>Main</c>), and
/// everything else is handed that snapshot. This reads the source of every project under
/// <c>src/</c> that ends up in an image and fails when any other code reads the environment, either
/// directly or through the host's configuration, and when a host is built in a way that would put
/// the <c>ASPNETCORE_</c> and <c>DOTNET_</c> variables, the command line, or <c>appsettings.json</c>
/// into that configuration: each service starts from an empty builder.
/// <para>
/// Not covered: the AppHost, which is the developer's orchestrator, is in no image, and reads the
/// developer's environment by design; what the .NET runtime reads for itself before any project
/// code runs; and an environment read hidden behind reflection. What the services do with another
/// source is tested on the running processes (<c>ListenSourceTests</c>,
/// <c>FrameworkSettingsTests</c>, <c>LoggingSourceTests</c>, and their gateway twins).
/// </para>
/// </summary>
public partial class EnvironmentReadGuardTests
{
    /// <summary>Local development only; see <c>ImageIsolationGuardTests</c> in <c>n8Tracks.AppHost.Tests</c>.</summary>
    private const string OutOfScope = "n8Tracks.AppHost";

    /// <summary>The snapshot readers: the only lines allowed to touch the process environment.</summary>
    private static readonly string[] AllowedEnvironmentReads =
    [
        "src/n8Tracks.Api/Configuration/ProcessEnvironment.cs: foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())",
        "src/n8Tracks.Gateway/Configuration/ProcessEnvironment.cs: foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())",
    ];

    /// <summary>The only callers of the snapshot readers: each service's <c>Main</c>.</summary>
    private static readonly string[] AllowedSnapshotCalls =
    [
        "src/n8Tracks.Api/Program.cs: RunAsync(args, ProcessEnvironment.Read(), Console.Out, CancellationToken.None);",
        "src/n8Tracks.Gateway/Program.cs: RunAsync(args, ProcessEnvironment.Read(), CancellationToken.None);",
    ];

    /// <summary>The only lines that create a host: each service's composition root, from an empty builder.</summary>
    private static readonly string[] AllowedHostBuilders =
    [
        "src/n8Tracks.Api/Program.cs: var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions",
        "src/n8Tracks.Gateway/Program.cs: var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions",
    ];

    [Theory]
    [InlineData("var port = Environment.GetEnvironmentVariable(\"N8TRACKS_PORT\");")]
    [InlineData("var port = System.Environment.GetEnvironmentVariable(\"PORT\", EnvironmentVariableTarget.Process);")]
    [InlineData("foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())")]
    [InlineData("var port = GetEnvironmentVariable(\"N8TRACKS_PORT\"); // using static System.Environment")]
    [InlineData("var path = Environment.ExpandEnvironmentVariables(\"%N8TRACKS_DATA_PATH%\");")]
    [InlineData("Environment.SetEnvironmentVariable(\"N8TRACKS_PORT\", \"1\");")]
    [InlineData("builder.Configuration.AddEnvironmentVariables(\"N8TRACKS_\");")]
    [InlineData("var source = new EnvironmentVariablesConfigurationSource { Prefix = \"N8TRACKS_\" };")]
    [InlineData("var port = Process.GetCurrentProcess().StartInfo.Environment[\"N8TRACKS_PORT\"];")]
    [InlineData("var port = start.EnvironmentVariables[\"N8TRACKS_PORT\"];")]
    public void TheRuleFindsAReadOfTheProcessEnvironment(string line)
    {
        Assert.Matches(ReadsProcessEnvironment(), line);
    }

    [Theory]
    [InlineData("var port = builder.Configuration[\"N8TRACKS_PORT\"];")]
    [InlineData("if (builder.Configuration[\"urls\"] == \"x\")")]
    [InlineData("var port = app.Configuration [ \"N8TRACKS_PORT\" ] ?? \"8787\";")]
    [InlineData("var port = configuration.GetValue<int>(\"N8TRACKS_PORT\");")]
    [InlineData("var section = builder.Configuration.GetSection(\"Kestrel\");")]
    [InlineData("var database = configuration.GetConnectionString(\"Default\");")]
    [InlineData("public HealthService(IConfiguration configuration)")]
    [InlineData("var root = (IConfigurationRoot)builder.Configuration;")]
    [InlineData("private readonly ConfigurationManager configuration;")]
    [InlineData("services.AddOptions<N8TracksOptions>().BindConfiguration(\"N8Tracks\");")]
    [InlineData("services.Configure<N8TracksOptions>(builder.Configuration);")]
    [InlineData("builder.Configuration.Bind(options);")]
    public void TheRuleFindsAReadOfTheHostsConfiguration(string line)
    {
        Assert.Matches(ReadsHostConfiguration(), line);
    }

    [Theory]
    [InlineData("var builder = WebApplication.CreateBuilder(args);")]
    [InlineData("var builder = WebApplication.CreateSlimBuilder(args);")]
    [InlineData("var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = args });")]
    [InlineData("var app = WebApplication.Create(args);")]
    [InlineData("var builder = Host.CreateApplicationBuilder(args);")]
    [InlineData("var builder = Host.CreateEmptyApplicationBuilder(settings);")]
    [InlineData("Host.CreateDefaultBuilder(args).Build().Run();")]
    [InlineData("var builder = new HostApplicationBuilder(args);")]
    [InlineData("var host = new HostBuilder().ConfigureDefaults(args).Build();")]
    [InlineData("var builder = WebHost.CreateDefaultBuilder(args);")]
    [InlineData("var host = new WebHostBuilder().Build();")]
    public void TheRuleFindsAHostBeingCreated(string line)
    {
        Assert.Matches(CreatesHost(), line);
    }

    [Theory]
    [InlineData("builder.Configuration.AddJsonFile(\"appsettings.json\");")]
    [InlineData("builder.Configuration.AddJsonStream(stream);")]
    [InlineData("builder.Configuration.AddCommandLine(args);")]
    [InlineData("builder.Configuration.AddIniFile(\"settings.ini\");")]
    [InlineData("builder.Configuration.AddXmlFile(\"settings.xml\");")]
    [InlineData("builder.Configuration.AddKeyPerFile(\"/run/secrets\");")]
    [InlineData("builder.Configuration.AddUserSecrets<Program>();")]
    [InlineData("builder.Logging.AddConfiguration(section);")]
    [InlineData("builder.WebHost.UseKestrel();")]
    [InlineData("builder.WebHost.UseKestrel(static kestrel => kestrel.AddServerHeader = false);")]
    [InlineData("builder.WebHost.ConfigureKestrel((context, kestrel) => kestrel.Configure(section));")]
    [InlineData("builder.WebHost.UseConfiguration(settings);")]
    [InlineData("var options = new WebApplicationOptions { Args = args };")]
    public void TheRuleFindsAnotherSourceBeingAddedToAHost(string line)
    {
        Assert.Matches(AddsAnotherSource(), line);
    }

    [Theory]
    [InlineData("builder.Configuration[WebHostDefaults.ServerUrlsKey] = string.Empty;")]
    [InlineData("builder.WebHost.UseKestrelCore();")]
    [InlineData("var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions")]
    [InlineData("return await RunAsync(args, environment, cancellationToken).ConfigureAwait(false);")]
    [InlineData("LoggerFactory.Create(static logging =>")]
    [InlineData("builder.Configuration.Add(new EnvironmentOnlyTelemetrySettings(environment));")]
    [InlineData(": ConfigurationProvider, IConfigurationSource")]
    [InlineData("public IConfigurationProvider Build(IConfigurationBuilder builder) => this;")]
    [InlineData("environment.Variables.TryGetValue(Port, out var value)")]
    [InlineData("return new EnvironmentSnapshot(variables, Directory.GetCurrentDirectory());")]
    [InlineData("if (app.Environment.IsDevelopment())")]
    [InlineData("dualMode.Bind(new IPEndPoint(IPAddress.IPv6Any, port));")]
    [InlineData("internal static class EnvironmentOptionsLoader")]
    [InlineData("services.AddEnvironmentConfiguration(environment);")]
    [InlineData("throw new ConfigurationValidationException(errors);")]
    public void TheRuleAllows(string line)
    {
        Assert.DoesNotMatch(ReadsProcessEnvironment(), line);
        Assert.DoesNotMatch(ReadsHostConfiguration(), line);
        Assert.DoesNotMatch(AddsAnotherSource(), line);
    }

    [Fact]
    public void OnlyTheTwoSnapshotReadersReadTheProcessEnvironment()
    {
        Assert.Equal(AllowedEnvironmentReads, Find(ReadsProcessEnvironment()));
    }

    [Fact]
    public void OnlyEachServicesMainTakesTheSnapshot()
    {
        Assert.Equal(AllowedSnapshotCalls, Find(TakesSnapshot()));
    }

    [Fact]
    public void NoProjectCodeReadsASettingFromTheHostsConfiguration()
    {
        var found = Find(ReadsHostConfiguration());

        Assert.True(
            found.Count == 0,
            "Settings are read from the environment snapshot, never from the host's configuration:" + Environment.NewLine
            + string.Join(Environment.NewLine, found));
    }

    /// <summary>
    /// A default builder reads the command line, the <c>ASPNETCORE_</c>, <c>DOTNET_</c>, and plain
    /// environment variables, and <c>appsettings.json</c>, and binds framework options (Kestrel's
    /// endpoints, logging filters, allowed hosts) from them. An empty builder reads none.
    /// </summary>
    [Fact]
    public void EachServiceBuildsItsHostFromAnEmptyBuilderAndNothingElseCreatesAHost()
    {
        Assert.Equal(AllowedHostBuilders, Find(CreatesHost()));
    }

    [Fact]
    public void NoProjectCodeAddsAnotherConfigurationSourceToAHost()
    {
        var found = Find(AddsAnotherSource());

        Assert.True(
            found.Count == 0,
            "A host takes no arguments, settings files, or framework configuration sections:" + Environment.NewLine
            + string.Join(Environment.NewLine, found));
    }

    /// <summary>Complement: the scan really reads the projects it claims to, so an empty result means something.</summary>
    [Fact]
    public void TheScanCoversEveryProjectUnderSrcButTheAppHost()
    {
        string[] expected =
        [
            "n8Tracks.Api",
            "n8Tracks.Application",
            "n8Tracks.Domain",
            "n8Tracks.Gateway",
            "n8Tracks.Infrastructure",
            "n8Tracks.ServiceDefaults",
        ];

        Assert.Equal(expected, ScannedProjects().Select(Path.GetFileName).Order(StringComparer.Ordinal));

        var files = SourceFiles().Select(Relative).ToList();
        Assert.All(expected, project => Assert.Contains(files, file => file.StartsWith($"src/{project}/", StringComparison.Ordinal)));
        Assert.Contains("src/n8Tracks.Api/Program.cs", files);
        Assert.Contains("src/n8Tracks.Gateway/Program.cs", files);
        Assert.DoesNotContain(files, static file => file.Contains("/bin/", StringComparison.Ordinal) || file.Contains("/obj/", StringComparison.Ordinal));

    }

    /// <summary>The process-environment APIs of .NET, by member name, however they are qualified.</summary>
    [GeneratedRegex(
        @"\b(GetEnvironmentVariables?|SetEnvironmentVariable|ExpandEnvironmentVariables|AddEnvironmentVariables"
        + @"|EnvironmentVariablesConfiguration\w*|EnvironmentVariableTarget|EnvironmentVariables)\b"
        + @"|\bStartInfo\s*\.\s*Environment\b")]
    private static partial Regex ReadsProcessEnvironment();

    /// <summary>
    /// A setting read from <c>IConfiguration</c>: its types, its read methods, binding, and the indexer
    /// when it is not the target of an assignment.
    /// </summary>
    [GeneratedRegex(
        @"\bIConfiguration(Root|Section|Manager)?\b|\bConfigurationManager\b"
        + @"|\.\s*(GetSection|GetRequiredSection|GetValue\s*<|GetConnectionString|GetChildren|AsEnumerable|BindConfiguration)\b"
        + @"|\bConfiguration\s*\.\s*(Bind|Get)\b|\(\s*\w+\s*\.\s*Configuration\s*\)"
        + @"|\bConfiguration\s*\[[^\]]*\](?!\s*=[^=])")]
    private static partial Regex ReadsHostConfiguration();

    /// <summary>Any way of creating a host or a host builder, the empty one included (it is allowed on two lines).</summary>
    [GeneratedRegex(
        @"\bWebApplication\s*\.\s*Create\w*\s*\(|\bHost\s*\.\s*Create\w*\s*\(|\bWebHost\s*\.\s*Create\w*\s*\("
        + @"|\bnew\s+(HostApplicationBuilder|HostBuilder|WebHostBuilder)\b")]
    private static partial Regex CreatesHost();

    /// <summary>
    /// What would hand a host the sources an empty builder leaves out: arguments, configuration files,
    /// a logging configuration section, and Kestrel with its configuration loader.
    /// </summary>
    [GeneratedRegex(
        @"\.\s*Add(JsonFile|JsonStream|CommandLine|IniFile|IniStream|XmlFile|XmlStream|KeyPerFile|UserSecrets)\b"
        + @"|\bLogging\s*\.\s*AddConfiguration\b|\.\s*(UseKestrel|ConfigureKestrel|UseConfiguration|ConfigureDefaults|ConfigureWebHostDefaults)\s*\("
        + @"|\bArgs\s*=")]
    private static partial Regex AddsAnotherSource();

    [GeneratedRegex(@"\bProcessEnvironment\s*\.\s*Read\b")]
    private static partial Regex TakesSnapshot();

    /// <summary>Every matching line of code, as <c>path: line</c>, in path order. Comment lines are not code.</summary>
    private static List<string> Find(Regex rule) =>
    [
        .. SourceFiles()
            .SelectMany(file => File.ReadLines(file)
                .Select(static line => line.Trim())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal) && rule.IsMatch(line))
                .Select(line => $"{Relative(file)}: {line}")),
    ];

    private static IEnumerable<string> ScannedProjects() =>
        Directory.EnumerateDirectories(Path.Combine(RepositoryRoot.Find(), "src"))
            .Where(static directory => Path.GetFileName(directory) != OutOfScope)
            .Where(static directory => Directory.EnumerateFiles(directory, "*.csproj").Any());

    private static IEnumerable<string> SourceFiles() =>
        ScannedProjects()
            .SelectMany(static project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(static file =>
            {
                var relative = Relative(file);
                return !relative.Contains("/bin/", StringComparison.Ordinal) && !relative.Contains("/obj/", StringComparison.Ordinal);
            })
            .Order(StringComparer.Ordinal);

    private static string Relative(string file) =>
        Path.GetRelativePath(RepositoryRoot.Find(), file).Replace(Path.DirectorySeparatorChar, '/');
}
