using Basalt.Architecture.Tests.Rules;

namespace Basalt.Architecture.Tests;

public sealed class DependencyRuleTests
{
    [Fact]
    [Trait("Spec", "ARCH-R1")]
    public void Domain_ReferencesOnlyTheBaseClassLibrary()
    {
        AssemblyReferenceRules.NonFrameworkReferences(SolutionAssemblies.Domain).Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "ARCH-R2")]
    public void Application_DoesNotReferenceAdaptersUiOrInfrastructureLibraries()
    {
        string[] forbidden =
        [
            "Basalt.Adapters", "Basalt.Presentation", "Basalt.UI", "Basalt.Composition", "Avalonia",
            "System.Net.Http", "Microsoft.Data.Sqlite", "Markdig", "YamlDotNet",
        ];

        AssemblyReferenceRules.ForbiddenReferences(SolutionAssemblies.Application, forbidden).Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "ARCH-R2")]
    public void Application_ReferencesOnlyDomainBclAndLoggingAbstractions()
    {
        var references = AssemblyReferenceRules.NonFrameworkReferences(SolutionAssemblies.Application);

        references.Should().OnlyContain(r => r == "Basalt.Domain" || r.StartsWith("Microsoft.Extensions.Logging.Abstractions", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "ARCH-R3")]
    public void Core_DoesNotUseBannedApis()
    {
        var violations = BannedApiScanner.FindViolations(SolutionAssemblies.Domain)
            .Concat(BannedApiScanner.FindViolations(SolutionAssemblies.Application));

        violations.Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "ARCH-R4")]
    public void Presentation_DoesNotReferenceAvalonia()
    {
        AssemblyReferenceRules.ForbiddenReferences(SolutionAssemblies.Presentation, ["Avalonia", "Basalt.UI"]).Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "ARCH-R5")]
    public void Adapters_DoNotReferenceOtherAdapters()
    {
        var violations = SolutionAssemblies.Adapters
            .SelectMany(a => AssemblyReferenceRules.ForbiddenReferences(a, ["Basalt.Adapters"]).Select(r => $"{a.GetName().Name} -> {r}"));

        violations.Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "ARCH-R6")]
    public void Adapters_ExposeOnlyTheirRegistrationExtensions()
    {
        var violations = SolutionAssemblies.Adapters.SelectMany(TypeRules.PublicAdapterTypes);

        violations.Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "ARCH-R7")]
    public void OutputPorts_DoNotExposeExternalLibraryTypes()
    {
        var ports = TypeRules.PortInterfaces(SolutionAssemblies.Application, "Basalt.Application");

        TypeRules.PortsExposingExternalTypes(ports).Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "ARCH-MODULES")]
    public void DomainModules_RespectTheDependencyMatrix()
    {
        var architecture = ArchitectureCache.Get(SolutionAssemblies.Domain);

        DomainModuleRules.Violations(architecture, "Basalt.Domain", DomainModuleRules.AllowedDependencies).Should().BeEmpty();
    }
}
