using System.Text;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Tests.Notes;

public sealed class NoteValueObjectTests
{
    [Theory]
    [InlineData("Notes/Idea.md", "Idea")]
    [InlineData("Proyecto X.md", "Proyecto X")]
    [InlineData("a/b.c.md", "b.c")]
    public void NoteTitle_IsNameWithoutExtension(string path, string title)
    {
        NoteTitle.FromPath(VaultPath.Create(path).Value).ToString().Should().Be(title);
    }

    [Theory]
    [InlineData("a\nb\nc", LineEnding.Lf)]
    [InlineData("a\r\nb\r\n", LineEnding.CrLf)]
    [InlineData("a\r\nb\nc", LineEnding.Mixed)]
    [InlineData("single line", LineEnding.Lf)]
    [InlineData("", LineEnding.Lf)]
    [InlineData("a\rb", LineEnding.Lf)]
    public void NoteContent_DetectsLineEnding(string text, LineEnding expected)
    {
        NoteContent.FromText(text).LineEnding.Should().Be(expected);
    }

    [Fact]
    public void NoteContent_FromBytes_DetectsAndStripsBom()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'];

        var content = NoteContent.FromBytes(bytes);

        content.HasBom.Should().BeTrue();
        content.Text.Should().Be("hi");
        content.ToBytes().Should().Equal(bytes);
    }

    [Fact]
    public void NoteContent_FromBytes_WithoutBom_RoundTrips()
    {
        var bytes = Encoding.UTF8.GetBytes("línea 1\r\nlínea 2");

        var content = NoteContent.FromBytes(bytes);

        content.HasBom.Should().BeFalse();
        content.LineEnding.Should().Be(LineEnding.CrLf);
        content.ToBytes().Should().Equal(bytes);
        NoteContent.FromBytes([]).Text.Should().BeEmpty();
    }

    [Theory]
    [InlineData("a\r\nb", "x\ny\nz", "x\r\ny\r\nz")]
    [InlineData("a\nb", "x\r\ny", "x\ny")]
    [InlineData("a\r\nb\nc", "x\r\ny\nz", "x\r\ny\nz")]
    [InlineData("a\r\nb", "x\r\ny\nz", "x\r\ny\r\nz")]
    [Trait("Spec", "DOM-4")]
    public void NoteContent_WithText_PreservesOriginalLineEndingAndBom(string original, string edited, string expected)
    {
        var content = NoteContent.FromText(original, hasBom: true);

        var updated = content.WithText(edited);

        updated.Text.Should().Be(expected);
        updated.HasBom.Should().BeTrue();
    }

    [Fact]
    public void NoteContent_ToString_IsText()
    {
        NoteContent.FromText("x").ToString().Should().Be("x");
        FluentActions.Invoking(() => NoteContent.FromText(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => NoteContent.FromText("a").WithText(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SourceSpan_ExposesEnd()
    {
        var span = new SourceSpan(10, 5, 2, 3);

        span.End.Should().Be(15);
        span.Contains(10).Should().BeTrue();
        span.Contains(14).Should().BeTrue();
        span.Contains(15).Should().BeFalse();
        span.Contains(9).Should().BeFalse();
    }

    [Fact]
    public void SourceSpan_RejectsNegativeValues()
    {
        FluentActions.Invoking(() => new SourceSpan(-1, 0, 0, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new SourceSpan(0, -1, 0, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new SourceSpan(0, 0, -1, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new SourceSpan(0, 0, 0, -1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("Sección: uno", "sección uno")]
    [InlineData("A  |  B", "a b")]
    [InlineData("[[Link]] #tag ^id %x% \\y", "link tag id x y")]
    [InlineData("  Trim  ", "trim")]
    [Trait("Spec", "LINK-3.2")]
    public void Heading_AnchorNormalization(string text, string slug)
    {
        Heading.NormalizeAnchor(text).Should().Be(slug);
        Heading.Create(2, text, 4).Value.Slug.Should().Be(slug);
    }

    [Fact]
    public void Heading_MatchesAnchorCaseInsensitively()
    {
        var heading = Heading.Create(1, "Mi Sección", 0).Value;

        heading.MatchesAnchor("mi sección").Should().BeTrue();
        heading.MatchesAnchor("MI  SECCIÓN").Should().BeTrue();
        heading.MatchesAnchor("Otra").Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Heading_LevelOutOfRange_Fails(int level)
    {
        Heading.Create(level, "x", 0).Error.Code.Should().Be(DomainErrors.HeadingInvalidLevel);
        Heading.Create(level, "x", 0).Error.Message.Should().Be("Heading levels go from 1 to 6.");
    }

    [Fact]
    public void Heading_ExposesProperties()
    {
        var heading = Heading.Create(6, "Title", 9).Value;

        heading.Level.Should().Be(6);
        heading.Text.Should().Be("Title");
        heading.Line.Should().Be(9);
        Heading.Create(1, "t", 0).IsSuccess.Should().BeTrue();
        FluentActions.Invoking(() => Heading.Create(1, null!, 0)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => Heading.NormalizeAnchor(null!)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("abc123", "abc123")]
    [InlineData("^abc-1", "abc-1")]
    [InlineData("A-Z", "A-Z")]
    [Trait("Spec", "LINK-6")]
    public void BlockId_Valid(string input, string expected)
    {
        BlockId.Create(input).Value.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("^")]
    [InlineData("a b")]
    [InlineData("a_b")]
    [InlineData("é")]
    [InlineData("a.b")]
    public void BlockId_Invalid(string input)
    {
        BlockId.Create(input).Error.Code.Should().Be(DomainErrors.BlockInvalidId);
        BlockId.Create(input).Error.Message.Should().Be("Block ids contain only letters A-Z, digits and '-'.");
    }

    [Fact]
    public void BlockId_RejectsNull()
    {
        FluentActions.Invoking(() => BlockId.Create(null!)).Should().Throw<ArgumentNullException>();
    }
}
