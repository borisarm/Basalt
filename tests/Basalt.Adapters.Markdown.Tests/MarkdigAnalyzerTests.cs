using System.Diagnostics;
using System.Text;
using Basalt.Domain.Links;
using Basalt.Domain.Metadata;

namespace Basalt.Adapters.Markdown.Tests;

public sealed class MarkdigAnalyzerTests
{
    private readonly MarkdigAnalyzer _analyzer = new();

    [Fact]
    [Trait("Spec", "MD-1")]
    public void Analyze_LinkSpans_PointToTheExactSourceText()
    {
        var content = "# T\n\nIntro\r\nsee [[ Carpeta/Nota#S|alias]] and [x](<Mi nota.md>) ![[img.png|300]]\n";

        var metadata = _analyzer.Analyze(content);

        foreach (var link in metadata.AllLinks)
        {
            var text = content.Substring(link.Span.Start, link.Span.Length);
            text.Should().StartWith(link.IsEmbed && link.Kind == LinkKind.Embed ? "![[" : link.Kind == LinkKind.MarkdownLink ? "[" : "[[");
            content.Substring(link.TargetSpan.Start, link.TargetSpan.Length).Should().Be(link.RawTarget);
        }

        metadata.Links.Select(l => l.RawTarget).Should().Equal("Carpeta/Nota", "Mi nota.md");
        metadata.Embeds.Single().RawTarget.Should().Be("img.png");
        metadata.Links[0].Span.Line.Should().Be(3);
        metadata.Links[0].Span.Column.Should().Be(4);
    }

    [Fact]
    [Trait("Spec", "F-006.2")]
    public void Analyze_TagsExcludeNumbersWordInternalCodeAndHeadings()
    {
        var metadata = _analyzer.Analyze("# Heading\n\n#ok #123 C#no `#code` (#paren)\n\n```\n#fenced\n```\n");

        metadata.Tags.Select(t => t.Tag.Name).Should().Equal("ok", "paren");
    }

    [Fact]
    public void Analyze_InvalidFrontmatter_ReportsErrorAndKeepsBody()
    {
        var metadata = _analyzer.Analyze("---\na: [unclosed\n---\nBody [[L]]\n");

        metadata.Frontmatter!.IsValid.Should().BeFalse();
        metadata.Frontmatter.Error.Should().NotBeNullOrEmpty();
        metadata.Links.Should().ContainSingle();
    }

    [Fact]
    [Trait("Spec", "NFR-033")]
    public void Analyze_HugeFrontmatter_IsInvalid()
    {
        var yaml = string.Concat(Enumerable.Range(0, 5000).Select(i => $"key{i}: value number {i} padding padding\n"));

        var metadata = _analyzer.Analyze($"---\n{yaml}---\nBody\n");

        metadata.Frontmatter!.IsValid.Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "NFR-033")]
    public void Analyze_YamlTypeTags_AreNotResolved()
    {
        var metadata = _analyzer.Analyze("---\nx: !!python/object:os.system 'rm -rf'\ny: !custom value\n---\n");

        metadata.Frontmatter!.Values.Keys.Should().Equal("x", "y");
    }

    [Fact]
    public void Analyze_EmptyDocument()
    {
        _analyzer.Analyze(string.Empty).Should().BeEquivalentTo(NoteMetadata.Empty);
    }

    [Fact]
    public void Analyze_IsThreadSafe()
    {
        var content = "# H\n[[A]] #b\n- [ ] t ^id\n";
        var expected = _analyzer.Analyze(content);

        var results = Enumerable.Range(0, 64).AsParallel().Select(_ => _analyzer.Analyze(content)).ToList();

        results.Should().AllSatisfy(r => r.Links.Should().BeEquivalentTo(expected.Links));
    }

    [Fact]
    [Trait("Spec", "MD-8")]
    [Trait("Category", "Performance")]
    public void Analyze_OneMegabyteDocument_IsFast()
    {
        var builder = new StringBuilder();
        var i = 0;
        while (builder.Length < 1_048_576)
        {
            builder.Append(CultureFormat($"## Section {i}\n\nParagraph {i} with [[Note {i % 97}|alias]], #tag{i % 13} and [md](Other%20{i}.md).\n- [ ] task {i} ^b{i}\n\n"));
            i++;
        }

        var content = builder.ToString();
        _analyzer.Analyze(content);

        var stopwatch = Stopwatch.StartNew();
        var metadata = _analyzer.Analyze(content);
        stopwatch.Stop();

        metadata.Links.Count.Should().BeGreaterThan(1000);
#if DEBUG
        const int BudgetMs = 400;
#else
        const int BudgetMs = 50 * 3; // spec target 50 ms on the reference machine; CI runners are slower
#endif
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(BudgetMs);
    }

    [Fact]
    public void Analyze_RejectsNull()
    {
        FluentActions.Invoking(() => _analyzer.Analyze(null!)).Should().Throw<ArgumentNullException>();
    }

    private static string CultureFormat(FormattableString text) => FormattableString.Invariant(text);
}
