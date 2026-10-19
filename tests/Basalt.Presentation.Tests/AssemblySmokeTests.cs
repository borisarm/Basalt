namespace Basalt.Presentation.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.Presentation");

        assembly.GetName().Name.Should().Be("Basalt.Presentation");
    }
}
