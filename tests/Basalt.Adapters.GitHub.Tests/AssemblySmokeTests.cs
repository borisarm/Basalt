namespace Basalt.Adapters.GitHub.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.Adapters.GitHub");

        assembly.GetName().Name.Should().Be("Basalt.Adapters.GitHub");
    }
}
