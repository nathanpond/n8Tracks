using System.Xml.Linq;

namespace n8Tracks.Architecture.Tests;

/// <summary>
/// Reads the csproj files under <c>src/</c>. Assembly metadata only shows references that are used,
/// so the declared project references are checked here as well.
/// </summary>
public class ProjectReferenceTests
{
    /// <summary>Every project under <c>src/</c> and exactly the projects it may reference.</summary>
    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new(StringComparer.Ordinal)
    {
        ["n8Tracks.Domain"] = [],
        ["n8Tracks.Application"] = ["n8Tracks.Domain"],
        ["n8Tracks.Infrastructure"] = ["n8Tracks.Application", "n8Tracks.Domain"],
        ["n8Tracks.Api"] = ["n8Tracks.Application", "n8Tracks.Infrastructure", "n8Tracks.ServiceDefaults"],

        // The MCP gateway reaches n8Tracks over HTTP only; see GatewayIsolationGuardTests in n8Tracks.Gateway.Tests.
        ["n8Tracks.Gateway"] = ["n8Tracks.ServiceDefaults"],

        // Telemetry wiring only: no business logic, and no way into the layers.
        ["n8Tracks.ServiceDefaults"] = [],

        // Local development only: starts the app and the gateway as processes. Nothing references it,
        // and it is in neither Docker image; see ImageIsolationGuardTests in n8Tracks.AppHost.Tests.
        ["n8Tracks.AppHost"] = ["n8Tracks.Api", "n8Tracks.Gateway"],
    };

    [Fact]
    public void EveryProjectUnderSrcIsListed()
    {
        var found = SourceProjects().Keys.Order(StringComparer.Ordinal);
        var listed = AllowedProjectReferences.Keys.Order(StringComparer.Ordinal);

        Assert.Equal(listed, found);
    }

    [Theory]
    [InlineData("n8Tracks.Domain")]
    [InlineData("n8Tracks.Application")]
    [InlineData("n8Tracks.Infrastructure")]
    [InlineData("n8Tracks.Api")]
    [InlineData("n8Tracks.Gateway")]
    [InlineData("n8Tracks.ServiceDefaults")]
    [InlineData("n8Tracks.AppHost")]
    public void ProjectReferencesAreExactlyTheAllowedOnes(string project)
    {
        var declared = ProjectReferences(Load(project)).Order(StringComparer.Ordinal);
        var allowed = AllowedProjectReferences[project].Order(StringComparer.Ordinal);

        Assert.Equal(allowed, declared);
    }

    [Fact]
    public void EveryListedProjectHasAReferenceTest()
    {
        var tested = typeof(ProjectReferenceTests)
            .GetMethod(nameof(ProjectReferencesAreExactlyTheAllowedOnes))!
            .GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == typeof(InlineDataAttribute))
            .Select(attribute => ((IEnumerable<System.Reflection.CustomAttributeTypedArgument>)attribute.ConstructorArguments[0].Value!).Single().Value as string)
            .Order(StringComparer.Ordinal);

        Assert.Equal(AllowedProjectReferences.Keys.Order(StringComparer.Ordinal), tested);
    }

    [Fact]
    public void DomainHasNoPackageReference()
    {
        var packages = Load("n8Tracks.Domain")
            .Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "FrameworkReference")
            .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? element.ToString());

        Assert.Empty(packages);
    }

    private static XDocument Load(string project)
    {
        var projects = SourceProjects();
        Assert.True(projects.TryGetValue(project, out var path), $"No project named '{project}' was found under src/.");

        return XDocument.Load(path);
    }

    private static IEnumerable<string> ProjectReferences(XDocument csproj) =>
        csproj.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));

    private static Dictionary<string, string> SourceProjects()
    {
        var src = Path.Combine(RepositoryRoot.Find(), "src");

        return Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), path => path, StringComparer.Ordinal);
    }
}
