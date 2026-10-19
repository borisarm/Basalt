namespace Basalt.Application.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.Application");

        assembly.GetName().Name.Should().Be("Basalt.Application");
    }
}
