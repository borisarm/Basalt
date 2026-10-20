using Basalt.Domain.Notes;
using Basalt.Testing.Generators;
using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Domain.Tests.Notes;

public sealed class TextBlocksTests
{
    private static List<string> Describe(string text) =>
        [.. TextBlocks.Find(text).Select(b => $"{b.Kind} {b.FirstLine}-{b.LastLine} «{text.Substring(b.Range.Start, b.Range.Length)}»")];

    [Fact]
    public void ParagraphsAreSeparatedByBlankLines()
    {
        Describe("uno\ndos\n\n  \ntres").Should().Equal("Paragraph 0-1 «uno\ndos»", "Paragraph 4-4 «tres»");
    }

    [Fact]
    public void HeadingsAndListItemsAreTheirOwnBlocks()
    {
        Describe("# Título\ntexto\n- a\n  sigue\n* b\n1. c\n2) d").Should().Equal(
            "Heading 0-0 «# Título»",
            "Paragraph 1-1 «texto»",
            "ListItem 2-3 «- a\n  sigue»",
            "ListItem 4-4 «* b»",
            "ListItem 5-5 «1. c»",
            "ListItem 6-6 «2) d»");
    }

    [Fact]
    public void FencedCodeIsOneBlock_EvenWithBlankLines()
    {
        Describe("a\n```\nx\n\ny\n```\nb").Should().Equal("Paragraph 0-0 «a»", "Code 1-5 «```\nx\n\ny\n```»", "Paragraph 6-6 «b»");
        Describe("```\nnever closed\n\nstill").Should().Equal("Code 0-3 «```\nnever closed\n\nstill»");
    }

    [Fact]
    public void CrLfLinesExcludeTheTerminator()
    {
        Describe("a\r\nb\r\n\r\nc\r\n").Should().Equal("Paragraph 0-1 «a\r\nb»", "Paragraph 3-3 «c»");
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    public void EmptyTextHasNoBlocks(string text)
    {
        TextBlocks.Find(text).Should().BeEmpty();
    }

    [Theory]
    [InlineData("-a")]
    [InlineData("1.a")]
    [InlineData("#tag")]
    [InlineData("12")]
    public void NotListItemsOrHeadings(string line)
    {
        TextBlocks.Find("x\n" + line).Should().ContainSingle().Which.Kind.Should().Be(TextBlockKind.Paragraph);
    }

    [BasaltProperty]
    public Property Blocks_AreOrdered_DisjointAndNonBlank()
    {
        var lines = Gen.Elements("", "texto", "# h", "- item", "  sigue", "```", "1. n", "   ");
        return Prop.ForAll(
            Gen.ListOf(lines).Select(l => string.Join("\n", l)).ToArbitrary(),
            text =>
            {
                var blocks = TextBlocks.Find(text);
                return blocks.Zip(blocks.Skip(1)).All(p => p.First.LastLine < p.Second.FirstLine && p.First.Range.End < p.Second.Range.Start)
                    && blocks.All(b => b.Range.Length > 0 && !string.IsNullOrWhiteSpace(text.Substring(b.Range.Start, b.Range.Length)));
            });
    }
}
