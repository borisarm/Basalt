using Basalt.Presentation.Diff;

namespace Basalt.Presentation.Tests.Diff;

public sealed class DiffDocumentTests
{
    [Fact]
    [Trait("Spec", "F-015.6")]
    public void EqualTexts_AlignEveryLine()
    {
        var rows = DiffDocument.Build("a\nb\n", "a\nb\n");

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(row => row.Kind == DiffLineKind.Equal);
        rows.Select(row => (row.LeftNumber, row.RightNumber, row.LeftText)).Should().Equal((1, 1, "a"), (2, 2, "b"));
    }

    [Fact]
    [Trait("Spec", "F-015.6")]
    public void InsertionsAndDeletions_KeepTheOtherSideEmpty()
    {
        var rows = DiffDocument.Build("a\nc\n", "a\nb\nc\n");

        rows[1].Kind.Should().Be(DiffLineKind.Added);
        rows[1].LeftNumber.Should().BeNull();
        rows[1].RightText.Should().Be("b");

        var removed = DiffDocument.Build("a\nb\nc\n", "a\nc\n");
        removed[1].Kind.Should().Be(DiffLineKind.Removed);
        removed[1].LeftText.Should().Be("b");
        removed[1].RightNumber.Should().BeNull();
    }

    [Fact]
    [Trait("Spec", "F-015.6")]
    public void ChangedLines_ArePaired()
    {
        var rows = DiffDocument.Build("a\nold\nd\n", "a\nnew\nd\n");

        var changed = rows.Single(row => row.Kind == DiffLineKind.Changed);
        changed.LeftText.Should().Be("old");
        changed.RightText.Should().Be("new");
        changed.LeftNumber.Should().Be(2);
        changed.RightNumber.Should().Be(2);
    }

    [Fact]
    [Trait("Spec", "F-015.6")]
    public void LineEndings_DoNotShowAsChanges()
    {
        DiffDocument.Build("a\r\nb\r\n", "a\nb\n").Should().OnlyContain(row => row.Kind == DiffLineKind.Equal);
    }

    [Fact]
    public void EmptySides_AreSupported()
    {
        DiffDocument.Build(string.Empty, "a\n").Should().ContainSingle().Which.Kind.Should().Be(DiffLineKind.Added);
        DiffDocument.Build("a\n", string.Empty).Should().ContainSingle().Which.Kind.Should().Be(DiffLineKind.Removed);
        DiffDocument.Build(string.Empty, string.Empty).Should().BeEmpty();
    }
}
