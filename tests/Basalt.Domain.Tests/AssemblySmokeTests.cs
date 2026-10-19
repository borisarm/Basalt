namespace Basalt.Domain.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.Domain");

        assembly.GetName().Name.Should().Be("Basalt.Domain");
    }
}
