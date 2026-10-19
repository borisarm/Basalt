namespace Basalt.Adapters.Markdown.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void TargetAssembly_IsLoadable()
    {
        var assembly = System.Reflection.Assembly.Load("Basalt.Adapters.Markdown");

        assembly.GetName().Name.Should().Be("Basalt.Adapters.Markdown");
    }
}
