using Basalt.Domain.Links;

namespace Basalt.Domain.Tests.Links;

public sealed class WikiLinkSyntaxTests
{
    public static TheoryData<string, LinkKind, string, string?, SubpathKind, string?> Valid => new()
    {
        { "[[Nota]]", LinkKind.WikiLink, "Nota", null, SubpathKind.None, null },
        { "[[Carpeta/Nota]]", LinkKind.WikiLink, "Carpeta/Nota", null, SubpathKind.None, null },
        { "[[Nota|alias]]", LinkKind.WikiLink, "Nota", null, SubpathKind.None, "alias" },
        { "[[Nota#Encabezado]]", LinkKind.WikiLink, "Nota", "Encabezado", SubpathKind.Heading, null },
        { "[[Nota#A#B]]", LinkKind.WikiLink, "Nota", "A#B", SubpathKind.Heading, null },
        { "[[Nota#^bloque-1]]", LinkKind.WikiLink, "Nota", "^bloque-1", SubpathKind.Block, null },
        { "[[Nota#Sección|texto]]", LinkKind.WikiLink, "Nota", "Sección", SubpathKind.Heading, "texto" },
        { "[[#Encabezado]]", LinkKind.WikiLink, "", "Encabezado", SubpathKind.Heading, null },
        { "[[#^b1]]", LinkKind.WikiLink, "", "^b1", SubpathKind.Block, null },
        { "![[imagen.png]]", LinkKind.Embed, "imagen.png", null, SubpathKind.None, null },
        { "![[imagen.png|300]]", LinkKind.Embed, "imagen.png", null, SubpathKind.None, "300" },
        { "![[imagen.png|300x200]]", LinkKind.Embed, "imagen.png", null, SubpathKind.None, "300x200" },
        { "![[nota#Sección]]", LinkKind.Embed, "nota", "Sección", SubpathKind.Heading, null },
        { "[[ Nota con espacios ]]", LinkKind.WikiLink, "Nota con espacios", null, SubpathKind.None, null },
        { "[[Nota| alias ]]", LinkKind.WikiLink, "Nota", null, SubpathKind.None, "alias" },
        { "[[Nota.md]]", LinkKind.WikiLink, "Nota.md", null, SubpathKind.None, null },
        { "[[a|b|c]]", LinkKind.WikiLink, "a", null, SubpathKind.None, "b|c" },
        { "[[Nota\\|alias]]", LinkKind.WikiLink, "Nota", null, SubpathKind.None, "alias" },
        { "[[Nota#]]", LinkKind.WikiLink, "Nota", null, SubpathKind.None, null },
        { "[[😀 emoji]]", LinkKind.WikiLink, "😀 emoji", null, SubpathKind.None, null },
        { "[[a]]]", LinkKind.WikiLink, "a", null, SubpathKind.None, null },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    [Trait("Spec", "LINK-2")]
    public void TryParse_ValidSyntax(string text, LinkKind kind, string target, string? subpath, SubpathKind subpathKind, string? alias)
    {
        var link = WikiLinkSyntax.TryParse(text, 0, 0, 0);

        link.Should().NotBeNull();
        link!.Kind.Should().Be(kind);
        link.RawTarget.Should().Be(target);
        link.Subpath.Should().Be(subpath);
        link.SubpathKind.Should().Be(subpathKind);
        link.Alias.Should().Be(alias);
    }

    [Theory]
    [InlineData("[[]]")]
    [InlineData("[[|alias]]")]
    [InlineData("[[Nota")]
    [InlineData("[[Nota]")]
    [InlineData("[[Nota\n]]")]
    [InlineData("[[a[b]]")]
    [InlineData("[[a^b]]")]
    [InlineData("[Nota]]")]
    [InlineData("Nota")]
    [InlineData("!Nota")]
    [InlineData("[[   ]]")]
    [Trait("Spec", "LINK-2")]
    public void TryParse_InvalidSyntax_ReturnsNull(string text)
    {
        WikiLinkSyntax.TryParse(text, 0, 0, 0).Should().BeNull();
    }

    [Fact]
    public void TryParse_ComputesSpansRelativeToDocument()
    {
        var text = "Ver [[ Carpeta/Nota#S|alias]] y más";

        var link = WikiLinkSyntax.TryParse(text, 4, 3, 4)!;

        link.Span.Start.Should().Be(4);
        link.Span.Length.Should().Be("[[ Carpeta/Nota#S|alias]]".Length);
        link.Span.Line.Should().Be(3);
        link.Span.Column.Should().Be(4);
        text.Substring(link.TargetSpan.Start, link.TargetSpan.Length).Should().Be("Carpeta/Nota");
        link.TargetSpan.Line.Should().Be(3);
        link.TargetSpan.Column.Should().Be(7);
    }

    [Fact]
    public void TryParse_EmbedSpanIncludesBang()
    {
        var text = "x ![[img.png]]";

        var link = WikiLinkSyntax.TryParse(text, 2, 0, 2)!;

        text.Substring(link.Span.Start, link.Span.Length).Should().Be("![[img.png]]");
        text.Substring(link.TargetSpan.Start, link.TargetSpan.Length).Should().Be("img.png");
    }

    [Fact]
    public void TryParse_InternalLink_TargetSpanIsEmptyAtTargetPosition()
    {
        var link = WikiLinkSyntax.TryParse("[[#H]]", 0, 0, 0)!;

        link.TargetSpan.Start.Should().Be(2);
        link.TargetSpan.Length.Should().Be(0);
        link.IsInternalToSource.Should().BeTrue();
    }

    [Fact]
    public void TryParse_RejectsBadArguments()
    {
        FluentActions.Invoking(() => WikiLinkSyntax.TryParse(null!, 0, 0, 0)).Should().Throw<ArgumentNullException>();
        WikiLinkSyntax.TryParse("[[a]]", 10, 0, 0).Should().BeNull();
        WikiLinkSyntax.TryParse("[[a]]", -1, 0, 0).Should().BeNull();
    }

    [Fact]
    public void Link_Properties()
    {
        var wiki = WikiLinkSyntax.TryParse("[[a]]", 0, 0, 0)!;
        var embed = WikiLinkSyntax.TryParse("![[a]]", 0, 0, 0)!;

        wiki.IsEmbed.Should().BeFalse();
        embed.IsEmbed.Should().BeTrue();
        wiki.IsInternalToSource.Should().BeFalse();
        (wiki with { Kind = LinkKind.MarkdownEmbed }).IsEmbed.Should().BeTrue();
        (wiki with { Kind = LinkKind.MarkdownLink }).IsEmbed.Should().BeFalse();
    }
}
