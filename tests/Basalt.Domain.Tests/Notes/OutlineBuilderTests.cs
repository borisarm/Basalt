using Basalt.Domain.Notes;

namespace Basalt.Domain.Tests.Notes;

public sealed class OutlineBuilderTests
{
    private static Heading H(int level, string text, int line) => Heading.Create(level, text, line).Value;

    private static string Render(IEnumerable<OutlineNode> nodes) =>
        string.Join(" ", nodes.Select(n => n.Children.Count == 0 ? n.Heading.Text : $"{n.Heading.Text}({Render(n.Children)})"));

    [Fact]
    public void NestsByLevel()
    {
        var outline = OutlineBuilder.Build([H(1, "A", 0), H(2, "A1", 1), H(3, "A1a", 2), H(2, "A2", 3), H(1, "B", 4), H(2, "B1", 5)]);

        Render(outline).Should().Be("A(A1(A1a) A2) B(B1)");
    }

    [Fact]
    public void SkippedLevels_AndLeadingDeepHeadings()
    {
        Render(OutlineBuilder.Build([H(3, "x", 0), H(1, "A", 1), H(4, "deep", 2), H(2, "A1", 3), H(1, "B", 4)])).Should().Be("x A(deep A1) B");
        Render(OutlineBuilder.Build([H(2, "a", 0), H(2, "b", 1), H(1, "c", 2), H(2, "d", 3)])).Should().Be("a b c(d)");
    }

    [Fact]
    public void Empty()
    {
        OutlineBuilder.Build([]).Should().BeEmpty();
    }
}
