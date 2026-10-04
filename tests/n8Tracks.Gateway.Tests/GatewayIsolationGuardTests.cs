using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// Guard for project invariant 5, gateway half: the MCP gateway calls only the REST API and holds no
/// business logic or catalog data. It therefore may not reference another n8Tracks project, Entity
/// Framework Core, or SQLite, directly or through another package or project. The one exemption is
/// <c>n8Tracks.ServiceDefaults</c>, whose own dependencies are held to the same rule.
/// <para>
/// Three sources are checked: the project file (what is declared), the dependency graph NuGet
/// resolved for it (<c>obj/project.assets.json</c>: every package and project, however deep), and the
/// assemblies the built gateway references, followed transitively.
/// </para>
/// <para>
/// Not covered: business logic written by hand inside the gateway (a review and audit concern), and
/// MCP scope rules (M7).
/// </para>
/// </summary>
public class GatewayIsolationGuardTests
{
    private const string Gateway = "n8Tracks.Gateway";

    /// <summary>The only other n8Tracks project the gateway may reference: telemetry wiring, no business logic.</summary>
    private static readonly string[] Exempt = ["n8Tracks.ServiceDefaults"];

    /// <summary>Matched as prefixes, in any letter case.</summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "n8Tracks.",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw",
    ];

    [Theory]
    [InlineData("n8Tracks.Application")]
    [InlineData("n8Tracks.Domain")]
    [InlineData("n8Tracks.Infrastructure")]
    [InlineData("n8Tracks.Api")]
    [InlineData("n8tracks.application")]
    [InlineData("n8Tracks.Gateway.Catalog")]
    [InlineData("n8Tracks.ServiceDefaults.Extra")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("Microsoft.EntityFrameworkCore.Abstractions")]
    [InlineData("Microsoft.Data.Sqlite")]
    [InlineData("Microsoft.Data.Sqlite.Core")]
    [InlineData("SQLitePCLRaw.core")]
    [InlineData("SQLitePCLRaw.bundle_e_sqlite3")]
    public void TheRuleForbids(string name)
    {
        Assert.True(IsForbidden(name));
    }

    [Theory]
    [InlineData("n8Tracks.Gateway")]
    [InlineData("n8Tracks.ServiceDefaults")]
    [InlineData("Microsoft.AspNetCore.App")]
    [InlineData("Microsoft.Extensions.Http")]
    [InlineData("Microsoft.Data.SqlClient")]
    [InlineData("ModelContextProtocol.AspNetCore")]
    [InlineData("System.Text.Json")]
    public void TheRuleAllows(string name)
    {
        Assert.False(IsForbidden(name));
    }

    [Fact]
    public void TheProjectFileDeclaresNoForbiddenReference()
    {
        var csproj = XDocument.Load(Path.Combine(GatewayDirectory(), Gateway + ".csproj"));

        var declared = csproj.Descendants()
            .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference" or "Reference" or "FrameworkReference")
            .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? string.Empty)
            .Select(ReferenceName)
            .ToList();

        AssertNoneForbidden(declared, "The gateway's project file declares");
    }

    [Fact]
    public void TheOnlyProjectTheGatewayReferencesIsServiceDefaults()
    {
        var csproj = XDocument.Load(Path.Combine(GatewayDirectory(), Gateway + ".csproj"));

        var projects = csproj.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => ReferenceName((string?)element.Attribute("Include") ?? string.Empty));

        Assert.Equal(Exempt, projects);
        Assert.Equal(["n8Tracks.ServiceDefaults"], Exempt);
    }

    [Fact]
    public void TheExemptProjectReferencesNoN8TracksProjectItself()
    {
        var csproj = XDocument.Load(Path.Combine(RepositoryRoot.Find(), "src", "n8Tracks.ServiceDefaults", "n8Tracks.ServiceDefaults.csproj"));

        Assert.DoesNotContain(csproj.Descendants(), element => element.Name.LocalName == "ProjectReference");

        // Complement: the file read is the project's own.
        Assert.Contains(csproj.Descendants(), element => element.Name.LocalName == "PackageReference");
    }

    [Fact]
    public void TheResolvedDependencyGraphHoldsNoForbiddenPackageOrProject()
    {
        var path = Path.Combine(GatewayDirectory(), "obj", "project.assets.json");
        Assert.True(File.Exists(path), $"'{path}' was not found: restore the solution (dotnet restore) before running this test.");

        using var assets = JsonDocument.Parse(File.ReadAllText(path));
        var root = assets.RootElement;

        // The graph is the gateway's own, and it was resolved: otherwise an empty list would prove nothing.
        Assert.Equal(Gateway, root.GetProperty("project").GetProperty("restore").GetProperty("projectName").GetString());
        Assert.NotEmpty(root.GetProperty("targets").EnumerateObject());

        // Every package and project in the graph, direct or transitive, under each target framework.
        var resolved = root.GetProperty("libraries").EnumerateObject().Select(library => library.Name)
            .Concat(root.GetProperty("targets").EnumerateObject().SelectMany(target => target.Value.EnumerateObject()).Select(library => library.Name))
            .Select(key => key.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        AssertNoneForbidden(resolved, "The gateway's resolved dependency graph holds");
    }

    [Fact]
    public void TheBuiltGatewayReferencesNoForbiddenAssemblyTransitively()
    {
        var gateway = typeof(Program).Assembly;
        Assert.Equal(Gateway, gateway.GetName().Name);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Gateway };
        var pending = new Queue<Assembly>([gateway]);

        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is not { } name || !seen.Add(name))
                {
                    continue;
                }

                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
                catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Its name is checked below; its own references cannot be followed.
                }
            }
        }

        // The walk really followed the graph into the framework.
        Assert.Contains("Microsoft.AspNetCore", seen);
        Assert.Contains("System.Private.CoreLib", seen);

        AssertNoneForbidden(seen, "The built gateway references the assemblies");
    }

    private static void AssertNoneForbidden(IEnumerable<string> names, string found)
    {
        var forbidden = names.Where(IsForbidden).Order(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(
            forbidden.Count == 0,
            $"{found} {string.Join(", ", forbidden)}. The MCP gateway may only reach n8Tracks over HTTP (project invariant 5): "
            + "it must not reference another n8Tracks project, Entity Framework Core, or SQLite.");
    }

    /// <summary>The gateway's own name is not a reference to anything; every other name is held to the rule.</summary>
    private static bool IsForbidden(string name) =>
        !string.Equals(name, Gateway, StringComparison.OrdinalIgnoreCase)
        && !Exempt.Contains(name, StringComparer.OrdinalIgnoreCase)
        && ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>A project path or an assembly display name, cut down to the bare name.</summary>
    private static string ReferenceName(string include)
    {
        var name = include.Split(',')[0].Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];

        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;
    }

    private static string GatewayDirectory() => Path.Combine(RepositoryRoot.Find(), "src", Gateway);
}
