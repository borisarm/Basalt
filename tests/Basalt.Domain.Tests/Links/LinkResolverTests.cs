using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Testing.Catalogs;

namespace Basalt.Domain.Tests.Links;

public sealed class LinkResolverTests
{
    private static readonly InMemoryFileCatalog Vault = new(
        "Nota.md",
        "Carpeta/Nota.md",
        "Carpeta/Sub/Profunda.md",
        "Otra/Profunda.md",
        "Archivo/2026/Informe.md",
        "imagen.png",
        "Adjuntos/imagen.png",
        "Adjuntos/foto.jpg",
        "Proyecto X.md",
        "Mayus/README.md",
        "Otro/readme.md",
        "Nota.v2.md",
        "Diario/2026-10-19.md",
        "x/Nota2.md",
        "y/Nota2.md",
        "a/b/Nota2.md",
        "z/Nota3.md",
        "z/w/Nota3.md",
        "q/Nota3.md",
        "Reuni\u00f3n.md",
        "Datos.csv",
        "Espacios en nombre.md",
        "Docs/Guía rápida.md",
        "Docs/sub/Guía rápida.md",
        "Solo/Unica.md",
        "b2026/Informe.md",
        "Carpeta/LEEME",
        "Carpeta/LEEME.md",
        "nota5.md",
        "A/Nota5.md");

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static LinkResolution Resolve(string target, string source = "Inicio.md", LinkKind kind = LinkKind.WikiLink) =>
        LinkResolver.Resolve(target, kind, P(source), Vault);

    public static TheoryData<string, string, string> ByName => new()
    {
        { "Nota", "Inicio.md", "Nota.md" },
        { "nota", "Inicio.md", "Nota.md" },
        { "NOTA", "Inicio.md", "Nota.md" },
        { "Nota.md", "Inicio.md", "Nota.md" },
        { "Proyecto X", "Inicio.md", "Proyecto X.md" },
        { "proyecto x", "Inicio.md", "Proyecto X.md" },
        { "  Proyecto X  ", "Inicio.md", "Proyecto X.md" },
        { "Informe", "Inicio.md", "b2026/Informe.md" },
        { "imagen.png", "Inicio.md", "imagen.png" },
        { "foto.jpg", "Inicio.md", "Adjuntos/foto.jpg" },
        { "Nota.v2", "Inicio.md", "Nota.v2.md" },
        { "2026-10-19", "Inicio.md", "Diario/2026-10-19.md" },
        { "Reunio\u0301n", "Inicio.md", "Reuni\u00f3n.md" },
        { "Datos.csv", "Inicio.md", "Datos.csv" },
        { "Espacios en nombre", "Inicio.md", "Espacios en nombre.md" },
        { "Unica", "Inicio.md", "Solo/Unica.md" },
    };

    [Theory]
    [MemberData(nameof(ByName))]
    [Trait("Spec", "LINK-3.4")]
    public void Resolve_ByName_CaseInsensitive(string target, string source, string expected)
    {
        Resolve(target, source).TargetPath.Should().Be(P(expected));
    }

    public static TheoryData<string, string, string> Tiebreaks => new()
    {
        // 5.1 exact case before case-insensitive
        { "README", "Inicio.md", "Mayus/README.md" },
        { "readme", "Inicio.md", "Otro/readme.md" },
        // 5.2 same folder as the source
        { "Nota", "Carpeta/Otra.md", "Carpeta/Nota.md" },
        { "Profunda", "Otra/Indice.md", "Otra/Profunda.md" },
        { "Profunda", "Carpeta/Sub/X.md", "Carpeta/Sub/Profunda.md" },
        // 5.3 shortest path (fewest segments)
        { "Nota", "Inicio.md", "Nota.md" },
        { "Nota", "Otra/x.md", "Nota.md" },
        { "Nota2", "Inicio.md", "x/Nota2.md" },
        { "Profunda", "Inicio.md", "Otra/Profunda.md" },
        // 5.4 ordinal order
        { "Nota3", "Inicio.md", "q/Nota3.md" },
        { "Nota2", "b/c.md", "x/Nota2.md" },
        { "Guía rápida", "Inicio.md", "Docs/Guía rápida.md" },
    };

    [Theory]
    [MemberData(nameof(Tiebreaks))]
    [Trait("Spec", "LINK-3.5")]
    public void Resolve_Ambiguous_AppliesTiebreaks(string target, string source, string expected)
    {
        Resolve(target, source).TargetPath.Should().Be(P(expected));
    }

    [Fact]
    [Trait("Spec", "LINK-3.5")]
    public void Resolve_SeveralCandidates_ReportsAmbiguity()
    {
        var result = Resolve("Nota2");

        var ambiguous = result.Should().BeOfType<LinkResolution.Ambiguous>().Subject;
        ambiguous.Path.Should().Be(P("x/Nota2.md"));
        ambiguous.Candidates.Should().Equal(P("x/Nota2.md"), P("y/Nota2.md"), P("a/b/Nota2.md"));
    }

    public static TheoryData<string, string, string> WithSlash => new()
    {
        // 3.2 absolute from root
        { "Carpeta/Nota", "Inicio.md", "Carpeta/Nota.md" },
        { "Carpeta/Nota.md", "Inicio.md", "Carpeta/Nota.md" },
        { "carpeta/nota", "Inicio.md", "Carpeta/Nota.md" },
        { "Adjuntos/imagen.png", "Inicio.md", "Adjuntos/imagen.png" },
        { "Archivo/2026/Informe", "Inicio.md", "Archivo/2026/Informe.md" },
        { "Carpeta/LEEME", "Inicio.md", "Carpeta/LEEME.md" },
        { "Carpeta\\Nota", "Inicio.md", "Carpeta/Nota.md" },
        // 3.3 path suffix
        { "2026/Informe", "Inicio.md", "Archivo/2026/Informe.md" },
        { "Sub/Profunda", "Inicio.md", "Carpeta/Sub/Profunda.md" },
        { "b/Nota2", "Inicio.md", "a/b/Nota2.md" },
        { "w/Nota3", "Inicio.md", "z/w/Nota3.md" },
        { "sub/guía rápida", "Inicio.md", "Docs/sub/Guía rápida.md" },
        // 3.1 relative
        { "./Nota", "Carpeta/Otra.md", "Carpeta/Nota.md" },
        { "../Nota", "Carpeta/Sub/X.md", "Carpeta/Nota.md" },
        { "../../Nota", "Carpeta/Sub/X.md", "Nota.md" },
        { "./Sub/Profunda.md", "Carpeta/Indice.md", "Carpeta/Sub/Profunda.md" },
    };

    [Theory]
    [MemberData(nameof(WithSlash))]
    [Trait("Spec", "LINK-3.3")]
    public void Resolve_TargetWithSlash(string target, string source, string expected)
    {
        Resolve(target, source).TargetPath.Should().Be(P(expected));
    }

    [Fact]
    [Trait("Spec", "LINK-3.3")]
    public void Resolve_AmbiguousSuffix_UsesTiebreaks()
    {
        var result = Resolve("Nota", "x/y.md");

        result.TargetPath.Should().Be(P("Nota.md"));
        Resolve("Profunda", "Inicio.md").Should().BeOfType<LinkResolution.Ambiguous>();
    }

    [Theory]
    [InlineData("Inexistente")]
    [InlineData("Carpeta/Inexistente")]
    [InlineData("./Nota")]
    [InlineData("../../../fuera")]
    [InlineData("Carpeta")]
    [InlineData("a:b")]
    [InlineData("Nota.png")]
    [InlineData("y/z/Nota2")]
    [Trait("Spec", "LINK-3.6")]
    public void Resolve_NoCandidates_IsUnresolved(string target)
    {
        var result = Resolve(target, "Otra/x.md");

        result.Should().BeOfType<LinkResolution.Unresolved>().Which.Target.Should().Be(target.Trim());
        result.TargetPath.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [Trait("Spec", "LINK-3.2")]
    public void Resolve_EmptyTarget_IsTheSourceItself(string target)
    {
        Resolve(target, "Carpeta/Nota.md").Should().Be(new LinkResolution.Resolved(P("Carpeta/Nota.md")));
    }

    [Fact]
    [Trait("Spec", "LINK-3.6")]
    public void Resolve_Unresolved_KeepsNormalizedTarget()
    {
        Resolve("  Nueva\\Idea ").Should().Be(new LinkResolution.Unresolved("Nueva/Idea"));
    }

    public static TheoryData<string, string, string> Markdown => new()
    {
        { "Nota.md", "Carpeta/Otra.md", "Carpeta/Nota.md" },
        { "Sub/Profunda.md", "Carpeta/Indice.md", "Carpeta/Sub/Profunda.md" },
        { "../Nota.md", "Carpeta/Otra.md", "Nota.md" },
        { "Carpeta/Nota.md", "Otra/x.md", "Carpeta/Nota.md" },
        { "Proyecto%20X.md", "Inicio.md", "Proyecto X.md" },
        { "Espacios%20en%20nombre.md", "Inicio.md", "Espacios en nombre.md" },
        { "Informe.md", "Inicio.md", "b2026/Informe.md" },
        { "Nota5.md", "B/x.md", "nota5.md" },
        { "Adjuntos/imagen.png", "Inicio.md", "Adjuntos/imagen.png" },
        { "Docs/Gu%C3%ADa%20r%C3%A1pida.md", "Inicio.md", "Docs/Guía rápida.md" },
        { "Nota", "Inicio.md", "Nota.md" },
    };

    [Theory]
    [MemberData(nameof(Markdown))]
    [Trait("Spec", "LINK-3.3.3")]
    public void Resolve_MarkdownLinks_RelativeThenAbsoluteThenName(string target, string source, string expected)
    {
        LinkResolver.Resolve(target, LinkKind.MarkdownLink, P(source), Vault).TargetPath.Should().Be(P(expected));
        LinkResolver.Resolve(target, LinkKind.MarkdownEmbed, P(source), Vault).TargetPath.Should().Be(P(expected));
    }

    [Fact]
    [Trait("Spec", "LINK-3.1")]
    public void Resolve_WikiLinks_DoNotPercentDecode()
    {
        Resolve("Proyecto%20X").Should().BeOfType<LinkResolution.Unresolved>();
    }

    [Theory]
    [InlineData("https://example.com/a.md")]
    [InlineData("http://x")]
    [InlineData("mailto:a@b.c")]
    [InlineData("obsidian://open?vault=x")]
    [InlineData("file:///c:/a.md")]
    [Trait("Spec", "LINK-3.3.3")]
    public void IsExternal_RecognizesSchemes(string target)
    {
        LinkResolver.IsExternal(target).Should().BeTrue();
        LinkResolver.Resolve(target, LinkKind.MarkdownLink, P("a.md"), Vault).Should().BeOfType<LinkResolution.Unresolved>();
    }

    [Theory]
    [InlineData("Nota.md")]
    [InlineData("./a.md")]
    [InlineData("C:/x")]
    public void IsExternal_FalseForInternalTargets(string target)
    {
        LinkResolver.IsExternal(target).Should().BeFalse();
    }

    [Fact]
    public void Resolve_WithoutMarkdownAbsoluteStep_NameTiebreakWouldDiffer()
    {
        Resolve("NOTA5", "A/x.md").TargetPath.Should().Be(P("A/Nota5.md"));
    }

    [Fact]
    public void Resolve_ExternalTarget_KeepsOriginalText()
    {
        LinkResolver.Resolve(" https://x%20y ", LinkKind.MarkdownLink, P("a.md"), Vault)
            .Should().Be(new LinkResolution.Unresolved("https://x%20y"));
    }

    [Fact]
    public void Resolve_CaseOnlyDuplicates_PickOrdinalFirstWhenNoExactMatch()
    {
        var catalog = new InMemoryFileCatalog("M/a.md", "M/A.md", ".x", ".x.md");

        LinkResolver.Resolve("m/a", LinkKind.WikiLink, P("r.md"), catalog).TargetPath.Should().Be(P("M/A.md"));
        LinkResolver.Resolve("M/a", LinkKind.WikiLink, P("r.md"), catalog).TargetPath.Should().Be(P("M/a.md"));
        LinkResolver.Resolve(".x", LinkKind.WikiLink, P("r.md"), catalog).TargetPath.Should().Be(P(".x.md"));
    }

    [Fact]
    public void Resolve_MarkdownAngleBrackets_AreStripped()
    {
        LinkResolver.Resolve("<Proyecto X.md>", LinkKind.MarkdownLink, P("Inicio.md"), Vault).TargetPath.Should().Be(P("Proyecto X.md"));
    }

    [Fact]
    public void Resolve_InvalidPercentEncoding_KeepsText()
    {
        LinkResolver.Resolve("Nota%zz.md", LinkKind.MarkdownLink, P("Inicio.md"), Vault).Should().BeOfType<LinkResolution.Unresolved>();
    }

    [Fact]
    public void Resolve_RejectsNulls()
    {
        FluentActions.Invoking(() => LinkResolver.Resolve(null!, LinkKind.WikiLink, P("a.md"), Vault)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LinkResolver.Resolve("a", LinkKind.WikiLink, P("a.md"), null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LinkResolver.IsExternal(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resolve_Link_UsesItsKindAndTarget()
    {
        var link = WikiLinkSyntax.TryParse("[[Proyecto X#H]]", 0, 0, 0)!;

        LinkResolver.Resolve(link, P("Inicio.md"), Vault).TargetPath.Should().Be(P("Proyecto X.md"));
        FluentActions.Invoking(() => LinkResolver.Resolve((Link)null!, P("Inicio.md"), Vault)).Should().Throw<ArgumentNullException>();
    }
}
