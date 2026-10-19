using System.Runtime.CompilerServices;
using Basalt.Domain.Notes;
using Basalt.Testing;

namespace Basalt.Adapters.Markdown.Tests;

/// <summary>Spec 05 §8: golden documents with their verified metadata.</summary>
public sealed class GoldenFileTests
{
    private static readonly string GoldenDirectory = Path.Combine(SourceDirectory(), "Golden");

    public static TheoryData<string> Documents()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(GoldenDirectory, "*.md").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    [Trait("Spec", "MD-8")]
    public void Analyze_GoldenDocument_MatchesVerifiedMetadata(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(GoldenDirectory, name + ".md"));
        // The application analyzes NoteContent.Text (BOM stripped), so do the same here.
        var content = NoteContent.FromBytes(bytes).Text;

        var metadata = new MarkdigAnalyzer().Analyze(content);

        Snapshot.MatchFile(MetadataProjection.Project(metadata, content), Path.Combine(GoldenDirectory, name));
    }

    [Fact]
    public void Golden_HasAtLeastSixtyDocuments()
    {
        Directory.EnumerateFiles(GoldenDirectory, "*.md").Count().Should().BeGreaterThanOrEqualTo(60);
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
