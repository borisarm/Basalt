using Basalt.Architecture.Tests.Rules;

namespace Basalt.Architecture.Tests;

public sealed class ConventionTests
{
    [Fact]
    [Trait("Spec", "ARCH-CONV")]
    public void Handlers_AreSealed()
    {
        TypeRules.UnsealedHandlers(SolutionAssemblies.Application).Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "ARCH-CONV")]
    public void NoAsyncVoid_OutsideUi()
    {
        var assemblies = SolutionAssemblies.Adapters
            .Append(SolutionAssemblies.Domain)
            .Append(SolutionAssemblies.Application)
            .Append(SolutionAssemblies.Presentation)
            .Append(SolutionAssemblies.Composition);

        assemblies.SelectMany(TypeRules.AsyncVoidMethods).Should().BeEmpty();
    }
}
