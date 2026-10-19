using Basalt.Architecture.Tests.Canary.Ports;
using Basalt.Architecture.Tests.Rules;

namespace Basalt.Architecture.Tests;

/// <summary>Proves that each rule detects a violation (a rule that can never fail protects nothing).</summary>
public sealed class CanaryTests
{
    private static readonly System.Reflection.Assembly Self = typeof(CanaryTests).Assembly;

    [Fact]
    public void BannedApiScanner_DetectsClockIdRandomAndBlockingCalls()
    {
        var violations = BannedApiScanner.FindViolations(Self, "Basalt.Architecture.Tests.Canary");

        violations.Should().Contain(v => v.Contains("System.DateTime::get_Now", StringComparison.Ordinal))
            .And.Contain(v => v.Contains("System.Guid::NewGuid", StringComparison.Ordinal))
            .And.Contain(v => v.Contains("System.Random", StringComparison.Ordinal))
            .And.Contain(v => v.Contains("System.Threading.Tasks.Task`1::get_Result", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceRules_DetectForbiddenReferences()
    {
        AssemblyReferenceRules.ForbiddenReferences(Self, ["System.Net.Http"]).Should().NotBeEmpty();
        AssemblyReferenceRules.NonFrameworkReferences(Self).Should().Contain("AwesomeAssertions");
    }

    [Fact]
    public void ModuleMatrix_DetectsForbiddenModuleDependency()
    {
        var matrix = new Dictionary<string, string[]> { ["Sync"] = [], ["Graph"] = [] };
        var architecture = ArchitectureCache.Get(Self);

        DomainModuleRules.Violations(architecture, "Basalt.Architecture.Tests.Canary", matrix).Should().Equal("Sync");
    }

    [Fact]
    public void PortRule_DetectsExternalTypes()
    {
        TypeRules.PortsExposingExternalTypes([typeof(ILeakyPort)]).Should().ContainSingle();
    }

    [Fact]
    public void AdapterVisibilityRule_DetectsPublicTypes()
    {
        TypeRules.PublicAdapterTypes(Self).Should().Contain(typeof(ILeakyPort).FullName);
    }

    [Fact]
    public void ConventionRules_DetectUnsealedHandlersAndAsyncVoid()
    {
        TypeRules.UnsealedHandlers(Self).Should().ContainSingle(t => t.EndsWith("UnsealedHandler", StringComparison.Ordinal));
        TypeRules.AsyncVoidMethods(Self).Should().ContainSingle(m => m.EndsWith("AsyncVoidOwner.Fire", StringComparison.Ordinal));
    }
}
