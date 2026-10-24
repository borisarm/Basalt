namespace Basalt.Architecture.Tests;

/// <summary>NFR-034: the CI gate reviews dependencies for high or critical vulnerabilities.</summary>
public sealed class SupplyChainTests
{
    [Fact]
    [Trait("Spec", "NFR-034")]
    public void CI_FailsOnHighOrCriticalVulnerabilities()
    {
        var workflow = File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "ci.yml"));

        workflow.Should().Contain("dotnet list");
        workflow.Should().Contain("--vulnerable");
        workflow.Should().Contain("--include-transitive");
        workflow.Should().MatchRegex(@"High\|Critical", "high and critical findings must fail the job");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Basalt.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
