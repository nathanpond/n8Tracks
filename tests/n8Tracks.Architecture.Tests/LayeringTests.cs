using System.Reflection;
using NetArchTest.Rules;

namespace n8Tracks.Architecture.Tests;

/// <summary>
/// Guard for invariant 5 (application half): business rules live in one layer, so the Api may reach
/// persistence only through the application layer.
/// </summary>
public class LayeringTests
{
    private const string ProductPrefix = "n8Tracks.";
    private const string ApiCompositionRootNamespace = "n8Tracks.Api.DependencyInjection";

    private static readonly Assembly Domain = typeof(n8Tracks.Domain.AssemblyMarker).Assembly;
    private static readonly Assembly Application = typeof(n8Tracks.Application.AssemblyMarker).Assembly;
    private static readonly Assembly Infrastructure = typeof(n8Tracks.Infrastructure.AssemblyMarker).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;

    private static readonly string[] ForbiddenInApi =
    [
        "n8Tracks.Infrastructure",
        "Microsoft.EntityFrameworkCore",
    ];

    [Fact]
    public void DomainReferencesNoOtherProductAssembly()
    {
        Assert.Empty(ProductReferences(Domain));
    }

    [Fact]
    public void ApplicationReferencesNeitherInfrastructureNorApi()
    {
        var references = ProductReferences(Application);

        Assert.DoesNotContain(Infrastructure.GetName().Name, references);
        Assert.DoesNotContain(Api.GetName().Name, references);
    }

    [Fact]
    public void ApplicationTypesDoNotDependOnInfrastructureOrApi()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny("n8Tracks.Infrastructure", "n8Tracks.Api")
            .GetResult();

        Assert.Empty((result.FailingTypes ?? []).Select(type => type.FullName));
    }

    [Fact]
    public void DomainTypesDoNotDependOnAnyOtherLayer()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny("n8Tracks.Application", "n8Tracks.Infrastructure", "n8Tracks.Api")
            .GetResult();

        Assert.Empty((result.FailingTypes ?? []).Select(type => type.FullName));
    }

    [Fact]
    public void ApiTypesOutsideTheCompositionRootDoNotDependOnInfrastructureOrEntityFramework()
    {
        var result = Types.InAssembly(Api)
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenInApi)
            .GetResult();

        var offenders = (result.FailingTypes ?? [])
            .Select(type => type.FullName)
            .Where(name => !IsApiCompositionRoot(name))
            .Order(StringComparer.Ordinal);

        Assert.Empty(offenders);
    }

    [Fact]
    public void ApiRuleExaminesTheHealthEndpoint()
    {
        // Complement: the rule above is not passing because it looked at nothing.
        var examined = Types.InAssembly(Api)
            .GetTypes()
            .Select(type => type.FullName)
            .Where(name => !IsApiCompositionRoot(name));

        Assert.Contains("n8Tracks.Api.Endpoints.HealthEndpoint", examined);
    }

    [Theory]
    [InlineData("Program", true)]
    [InlineData("Program/<>c", true)]
    [InlineData("Program+<>c", true)]
    [InlineData("n8Tracks.Api.DependencyInjection.PersistenceRegistration", true)]
    [InlineData("n8Tracks.Api.DependencyInjectionHelpers.Sneaky", false)]
    [InlineData("n8Tracks.Api.Endpoints.HealthEndpoint", false)]
    [InlineData("ProgramHelpers", false)]
    public void OnlyProgramAndTheDependencyInjectionNamespaceAreExempt(string typeName, bool exempt)
    {
        Assert.Equal(exempt, IsApiCompositionRoot(typeName));
    }

    /// <summary>
    /// The composition root is the global-namespace <c>Program</c> type with its compiler-generated
    /// nested types, plus the namespace <c>n8Tracks.Api.DependencyInjection</c>.
    /// </summary>
    private static bool IsApiCompositionRoot(string typeFullName) =>
        typeFullName == nameof(Program)
        || typeFullName.StartsWith(nameof(Program) + "/", StringComparison.Ordinal)
        || typeFullName.StartsWith(nameof(Program) + "+", StringComparison.Ordinal)
        || typeFullName.StartsWith(ApiCompositionRootNamespace + ".", StringComparison.Ordinal);

    private static string[] ProductReferences(Assembly assembly) =>
    [
        .. assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith(ProductPrefix, StringComparison.Ordinal)),
    ];
}
