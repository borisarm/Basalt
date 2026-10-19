namespace Basalt.Adapters.Platform.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.Adapters.Platform");

        assembly.GetName().Name.Should().Be("Basalt.Adapters.Platform");
    }
}
