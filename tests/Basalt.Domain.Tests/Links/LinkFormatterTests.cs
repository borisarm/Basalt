using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Catalogs;

namespace Basalt.Domain.Tests.Links;

public sealed class LinkFormatterTests
{
    private static readonly InMemoryFileCatalog Vault = new(
        "Inicio.md",
        "Unica.md",
        "Proyectos/Proyecto X.md",
        "x/Nota.md",
        "y/Nota.md",
        "a/b/Nota.md",
        "a/c/Nota.md",
        "Adjuntos/imagen.png",
        "Otros/imagen.png",
        "Carpeta/Sub/Profunda.md",
        "Carpeta/Hermana.md",
        "Raiz.md",
        "Carpeta/Raiz.md");

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    public static TheoryData<string, string, LinkPathStyle, LinkFormat, string> Targets => new()
    {
        // Shortest × Wikilink: name when unique, otherwise shortest unique suffix.
        { "Unica.md", "Inicio.md", LinkPathStyle.Shortest, LinkFormat.Wikilink, "Unica" },
        { "Proyectos/Proyecto X.md", "Inicio.md", LinkPathStyle.Shortest, LinkFormat.Wikilink, "Proyecto X" },
        { "x/Nota.md", "Inicio.md", LinkPathStyle.Shortest, LinkFormat.Wikilink, "x/Nota" },
        { "a/b/Nota.md", "Inicio.md", LinkPathStyle.Shortest, LinkFormat.Wikilink, "b/Nota" },
        { "Adjuntos/imagen.png", "Inicio.md", LinkPathStyle.Shortest, LinkFormat.Wikilink, "Adjuntos/imagen.png" },
        { "Raiz.md", "Inicio.md", LinkPathStyle.Shortest, LinkFormat.Wikilink, "Raiz" },
        { "Carpeta/Raiz.md", "Inicio.md", LinkPathStyle.Shortest, LinkFormat.Wikilink, "Carpeta/Raiz" },
        // Shortest × Markdown: includes extension and %20.
        { "Proyectos/Proyecto X.md", "Inicio.md", LinkPathStyle.Shortest, LinkFormat.Markdown, "Proyecto%20X.md" },
        { "x/Nota.md", "Inicio.md", LinkPathStyle.Shortest, LinkFormat.Markdown, "x/Nota.md" },
        { "Unica.md", "Carpeta/Hermana.md", LinkPathStyle.Shortest, LinkFormat.Markdown, "Unica.md" },
        // Relative.
        { "Carpeta/Sub/Profunda.md", "Carpeta/Hermana.md", LinkPathStyle.Relative, LinkFormat.Wikilink, "./Sub/Profunda" },
        { "Unica.md", "Carpeta/Hermana.md", LinkPathStyle.Relative, LinkFormat.Wikilink, "../Unica" },
        { "Carpeta/Hermana.md", "Carpeta/Sub/Profunda.md", LinkPathStyle.Relative, LinkFormat.Wikilink, "../Hermana" },
        { "Carpeta/Hermana.md", "Carpeta/Raiz.md", LinkPathStyle.Relative, LinkFormat.Wikilink, "./Hermana" },
        { "Carpeta/Sub/Profunda.md", "Carpeta/Hermana.md", LinkPathStyle.Relative, LinkFormat.Markdown, "Sub/Profunda.md" },
        { "Proyectos/Proyecto X.md", "Carpeta/Hermana.md", LinkPathStyle.Relative, LinkFormat.Markdown, "../Proyectos/Proyecto%20X.md" },
        // Absolute.
        { "Carpeta/Sub/Profunda.md", "Inicio.md", LinkPathStyle.Absolute, LinkFormat.Wikilink, "Carpeta/Sub/Profunda" },
        { "Unica.md", "Carpeta/Hermana.md", LinkPathStyle.Absolute, LinkFormat.Wikilink, "Unica" },
        { "Proyectos/Proyecto X.md", "Inicio.md", LinkPathStyle.Absolute, LinkFormat.Markdown, "Proyectos/Proyecto%20X.md" },
        { "Adjuntos/imagen.png", "Inicio.md", LinkPathStyle.Absolute, LinkFormat.Wikilink, "Adjuntos/imagen.png" },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    [Trait("Spec", "LINK-3.1.1")]
    public void FormatTarget_FollowsStyleAndFormat(string target, string source, LinkPathStyle style, LinkFormat format, string expected)
    {
        LinkFormatter.FormatTarget(P(target), P(source), style, format, Vault).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    [Trait("Spec", "LINK-3.1.1")]
    public void FormatTarget_AlwaysResolvesBackToTheTarget(string target, string source, LinkPathStyle style, LinkFormat format, string expected)
    {
        var kind = format == LinkFormat.Wikilink ? LinkKind.WikiLink : LinkKind.MarkdownLink;
        var formatted = LinkFormatter.FormatTarget(P(target), P(source), style, format, Vault);

        formatted.Should().Be(expected);
        LinkResolver.Resolve(formatted, kind, P(source), Vault).TargetPath.Should().Be(P(target));
    }

    [Fact]
    [Trait("Spec", "F-004.1")]
    public void FormatLink_Wikilink_WithSubpathAliasAndEmbed()
    {
        var settings = VaultSettings.Default;
        var target = P("Proyectos/Proyecto X.md");
        var source = P("Inicio.md");

        LinkFormatter.FormatLink(target, source, settings, Vault).Should().Be("[[Proyecto X]]");
        LinkFormatter.FormatLink(target, source, settings, Vault, subpath: "Sección", alias: "alias").Should().Be("[[Proyecto X#Sección|alias]]");
        LinkFormatter.FormatLink(target, source, settings, Vault, subpath: "^abc123").Should().Be("[[Proyecto X#^abc123]]");
        LinkFormatter.FormatLink(P("Adjuntos/imagen.png"), source, settings, Vault, embed: true).Should().Be("![[Adjuntos/imagen.png]]");
    }

    [Fact]
    public void FormatLink_Markdown_UsesTitleAsTextAndEncodesSubpath()
    {
        var settings = VaultSettings.Default with { LinkFormat = LinkFormat.Markdown };
        var target = P("Proyectos/Proyecto X.md");
        var source = P("Inicio.md");

        LinkFormatter.FormatLink(target, source, settings, Vault).Should().Be("[Proyecto X](Proyecto%20X.md)");
        LinkFormatter.FormatLink(target, source, settings, Vault, subpath: "Mi Sección", alias: "ver").Should().Be("[ver](Proyecto%20X.md#Mi%20Sección)");
        LinkFormatter.FormatLink(P("Adjuntos/imagen.png"), source, settings, Vault, embed: true).Should().Be("![imagen](Adjuntos/imagen.png)");
    }

    [Fact]
    public void FormatTarget_FileOutsideCatalog_StillProducesAPath()
    {
        LinkFormatter.FormatTarget(P("Nueva/Idea.md"), P("Inicio.md"), LinkPathStyle.Shortest, LinkFormat.Wikilink, Vault).Should().Be("Idea");
    }

    [Fact]
    public void FormatTarget_RejectsNullCatalog()
    {
        FluentActions.Invoking(() => LinkFormatter.FormatTarget(P("a.md"), P("b.md"), LinkPathStyle.Shortest, LinkFormat.Wikilink, null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LinkFormatter.FormatLink(P("a.md"), P("b.md"), null!, Vault)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("a b", "a%20b")]
    [InlineData("a  b", "a%20%20b")]
    [InlineData("ab", "ab")]
    public void EncodeMarkdownPath_EncodesSpaces(string input, string expected)
    {
        LinkFormatter.EncodeMarkdownPath(input).Should().Be(expected);
    }
}
