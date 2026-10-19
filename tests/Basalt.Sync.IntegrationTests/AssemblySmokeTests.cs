namespace Basalt.Sync.IntegrationTests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.Composition");

        assembly.GetName().Name.Should().Be("Basalt.Composition");
    }
}
