using Basalt.Testing;

namespace Basalt.Application.Tests.TestingInfrastructure;

public sealed class TestingUtilitiesTests
{
    [Fact]
    public async Task TempDirectory_CreatesAndDeletesFolder()
    {
        string path;
        await using (var temp = TempDirectory.Create("util"))
        {
            path = temp.Path;
            temp.WriteFile("a/b.md", "x");

            File.ReadAllText(temp.Combine("a", "b.md")).Should().Be("x");
        }

        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Snapshot_MatchesVerifiedFile()
    {
        Snapshot.Match(new { Name = "basalt", Values = new[] { 1, 2 } });
    }

    [Fact]
    public void Snapshot_Mismatch_WritesReceivedAndThrows()
    {
        var act = () => Snapshot.MatchText("not the verified content", caseName: "mismatch");

        act.Should().Throw<SnapshotMismatchException>();
        var received = Path.Combine(SourceDirectory(), "TestingUtilitiesTests.Snapshot_Mismatch_WritesReceivedAndThrows.mismatch.received.txt");
        File.Exists(received).Should().BeTrue();
        File.Delete(received);
    }

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path)!;
}
