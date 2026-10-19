namespace Basalt.Adapters.Index.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.Adapters.Index");

        assembly.GetName().Name.Should().Be("Basalt.Adapters.Index");
    }
}
