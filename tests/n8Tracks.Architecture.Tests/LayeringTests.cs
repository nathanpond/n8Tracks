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
    private static readonly Assembly ServiceDefaults = typeof(n8Tracks.ServiceDefaults.Extensions).Assembly;

    /// <summary>What mapping an endpoint, a health check, or data access would pull in.</summary>
    private static readonly string[] ForbiddenInServiceDefaults =
    [
        "n8Tracks.Domain",
        "n8Tracks.Application",
        "n8Tracks.Infrastructure",
        "n8Tracks.Api",
        "n8Tracks.Gateway",
        "Microsoft.AspNetCore.Builder",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics.HealthChecks",
        "Microsoft.Extensions.Diagnostics.HealthChecks",
        "Microsoft.EntityFrameworkCore",
    ];

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
    public void ServiceDefaultsReferencesNoOtherProductAssembly()
    {
        Assert.Empty(ProductReferences(ServiceDefaults));
    }

    [Fact]
    public void ServiceDefaultsMapsNoEndpointsAndTouchesNoLayer()
    {
        var result = Types.InAssembly(ServiceDefaults)
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenInServiceDefaults)
            .GetResult();

        Assert.Empty((result.FailingTypes ?? []).Select(type => type.FullName));

        // Complement: the rule looked at the wiring itself.
        Assert.Contains("n8Tracks.ServiceDefaults.Extensions", Types.InAssembly(ServiceDefaults).GetTypes().Select(type => type.FullName));
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

    /// <summary>
    /// What deleting a Generation (#124) must never reach: the network (so nothing in Suno can be
    /// touched) or the extension's pairing and handshake (<c>n8Tracks.Application.Credentials</c>).
    /// </summary>
    private static readonly string[] ForbiddenToGenerationDeletion =
    [
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.WebSockets",
        "n8Tracks.Application.Credentials",
    ];

    [Fact]
    public void DeletingAGenerationReachesNeitherTheNetworkNorTheExtension()
    {
        var reached = DependencyClosure(typeof(n8Tracks.Application.Generations.GenerationDeletionService));
        var offenders = Types.InAssemblies([Application, Infrastructure])
            .That()
            .HaveDependencyOnAny(ForbiddenToGenerationDeletion)
            .GetTypes()
            .Select(static type => OuterName(type.FullName))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(reached.Where(offenders.Contains).Order(StringComparer.Ordinal));

        // Complement: the walk followed the ports into their implementations, and the rule finds the
        // extension's handshake, which a deletion must not reach.
        Assert.Contains("n8Tracks.Application.Retention.RetentionService", reached);
        Assert.Contains("n8Tracks.Infrastructure.Persistence.GenerationStore", reached);
        Assert.Contains("n8Tracks.Infrastructure.Retention.RetentionStore", reached);
        Assert.Contains("n8Tracks.Application.Credentials.ExtensionHandshakeService", offenders);
    }

    /// <summary>
    /// The only types of the server (Domain, Application, Infrastructure, Api) that may make an
    /// outbound request: the container health check's self-request to the server's own address.
    /// </summary>
    private static readonly string[] AllowedOutboundClients = ["n8Tracks.Api.Endpoints.HealthCheckCommand"];

    /// <summary>
    /// The client namespaces an outbound request needs. Raw sockets are left out: the server binds its
    /// own listening port with them (<c>ListenPortProbe</c>), which reaches nowhere.
    /// </summary>
    private static readonly string[] OutboundClientNamespaces = ["System.Net.Http", "System.Net.WebSockets"];

    /// <summary>
    /// #121 AC 2 (#319): n8Tracks never fetches anything from Suno, a Generation's cover image above all:
    /// the extension uploads it. No type of the server may depend on the network client namespaces
    /// but the health check's self-request, so a Suno fetch cannot be added without failing here.
    /// </summary>
    [Fact]
    public void TheServerMakesNoOutboundRequestButItsOwnHealthCheck()
    {
        var clients = Types.InAssemblies([Domain, Application, Infrastructure, Api])
            .That()
            .HaveDependencyOnAny(OutboundClientNamespaces)
            .GetTypes()
            .Select(static type => OuterName(type.FullName))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(clients.Except(AllowedOutboundClients, StringComparer.Ordinal).Order(StringComparer.Ordinal));

        // Complement: the rule sees the one client that exists, so an empty answer above is not blindness.
        Assert.Equal(AllowedOutboundClients, clients.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every product type <paramref name="root"/> can reach through constructor parameters: a class's
    /// own, and for an interface or abstract class, those of each of its implementations in the
    /// Application and Infrastructure assemblies.
    /// </summary>
    private static HashSet<string> DependencyClosure(Type root)
    {
        var candidates = Application.GetTypes().Concat(Infrastructure.GetTypes()).Where(static type => type is { IsClass: true, IsAbstract: false }).ToList();
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([root]);
        while (queue.TryDequeue(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }

            var next = type.IsInterface || type.IsAbstract
                ? candidates.Where(type.IsAssignableFrom)
                : type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(static constructor => constructor.GetParameters())
                    .SelectMany(static parameter => parameter.ParameterType.IsGenericType ? [parameter.ParameterType, .. parameter.ParameterType.GetGenericArguments()] : new[] { parameter.ParameterType });
            foreach (var dependency in next)
            {
                if (dependency.Namespace?.StartsWith(ProductPrefix, StringComparison.Ordinal) == true)
                {
                    queue.Enqueue(dependency.IsGenericType ? dependency.GetGenericTypeDefinition() : dependency);
                }
            }
        }

        return [.. seen.Select(static type => OuterName(type.FullName!))];
    }

    /// <summary>A type's outermost declaring type, by full name (reflection nests with <c>+</c>, Cecil with <c>/</c>).</summary>
    private static string OuterName(string fullName) => fullName.Split('+', '/')[0];

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
