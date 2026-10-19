namespace Basalt.Adapters.FileSystem.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.Adapters.FileSystem");

        assembly.GetName().Name.Should().Be("Basalt.Adapters.FileSystem");
    }
}
