namespace Basalt.UI.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.UI");

        assembly.GetName().Name.Should().Be("Basalt.UI");
    }
}
