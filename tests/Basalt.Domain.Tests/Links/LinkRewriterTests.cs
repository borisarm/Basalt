using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Catalogs;
using Basalt.Testing.Documents;

namespace Basalt.Domain.Tests.Links;

public sealed class LinkRewriterTests
{
    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static Link Md(string text, string destination)
    {
        var start = text.IndexOf("(" + destination + ")", StringComparison.Ordinal) + 1;
        var open = text.LastIndexOf('[', start);
        var span = new SourceSpan(open, start + destination.Length + 1 - open, 0, open);
        var hash = destination.IndexOf('#', StringComparison.Ordinal);
        var target = hash < 0 ? destination : destination[..hash];
        return new Link(LinkKind.MarkdownLink, target, hash < 0 ? null : destination[(hash + 1)..], hash < 0 ? SubpathKind.None : SubpathKind.Heading, null, span, new SourceSpan(start, target.Length, 0, start));
    }

    private static RewriteResult Rewrite(
        string text,
        string source,
        IReadOnlyDictionary<string, string> renames,
        string[] files,
        VaultSettings? settings = null,
        IReadOnlyList<Link>? extraLinks = null,
        string? newSource = null)
    {
        var before = new InMemoryFileCatalog(files);
        var map = renames.ToDictionary(kv => P(kv.Key), kv => P(kv.Value));
        var after = new InMemoryFileCatalog(files.Select(f => map.TryGetValue(P(f), out var to) ? to.ToString() : f));
        var links = WikiLinkScanner.Scan(text).Concat(extraLinks ?? []).ToList();
        var request = new RewriteRequest(P(source), P(newSource ?? source), map, before, after, settings ?? VaultSettings.Default);
        return LinkRewriter.Rewrite(text, links, request);
    }

    [Fact]
    [Trait("Spec", "F-004.4")]
    public void Rename_UpdatesWikilinksAliasesSubpathsAndMarkdownLinks()
    {
        var text = "Intro [[B]] y [[B#Sección|alias]] y [texto](B.md) fin.\nOtra línea intacta.";
        var markdown = Md(text, "B.md");

        var result = Rewrite(text, "A.md", new Dictionary<string, string> { ["B.md"] = "C.md" }, ["A.md", "B.md"], extraLinks: [markdown]);

        result.Text.Should().Be("Intro [[C]] y [[C#Sección|alias]] y [texto](C.md) fin.\nOtra línea intacta.");
        result.ChangedLinks.Should().Be(3);
    }

    [Fact]
    [Trait("Spec", "F-004.5")]
    public void RenameFolder_UpdatesExplicitPaths()
    {
        var text = "[[X/Nota]] [[X/Sub/Otra|o]] [[Nota]] ![[X/img.png]]";
        var renames = new Dictionary<string, string>
        {
            ["X/Nota.md"] = "Y/Nota.md",
            ["X/Sub/Otra.md"] = "Y/Sub/Otra.md",
            ["X/img.png"] = "Y/img.png",
        };

        var result = Rewrite(text, "A.md", renames, ["A.md", "X/Nota.md", "X/Sub/Otra.md", "X/img.png"]);

        result.Text.Should().Be("[[Y/Nota]] [[Y/Sub/Otra|o]] [[Nota]] ![[Y/img.png]]");
    }

    [Fact]
    [Trait("Spec", "F-004.7")]
    public void MoveOfOtherHomonym_LeavesExplicitLinkUnchanged()
    {
        var text = "Ver [[x/Nota]].";

        var result = Rewrite(text, "A.md", new Dictionary<string, string> { ["y/Nota.md"] = "z/Nota.md" }, ["A.md", "x/Nota.md", "y/Nota.md"]);

        result.Text.Should().Be(text);
        result.ChangedLinks.Should().Be(0);
    }

    [Fact]
    public void Rename_CreatingAmbiguity_QualifiesUnchangedLinkToKeepItsTarget()
    {
        var text = "[[Nota]]";

        var result = Rewrite(text, "Carpeta/A.md", new Dictionary<string, string> { ["Otra/Foo.md"] = "Carpeta/Nota.md" }, ["Carpeta/A.md", "Nota.md", "Otra/Foo.md"]);

        result.Text.Should().Be("[[../Nota]]");
        result.ChangedLinks.Should().Be(1);
        LinkResolver.Resolve("../Nota", LinkKind.WikiLink, P("Carpeta/A.md"), new InMemoryFileCatalog("Carpeta/A.md", "Nota.md", "Carpeta/Nota.md"))
            .TargetPath.Should().Be(P("Nota.md"));
    }

    [Fact]
    public void Rename_PreservesExplicitMdExtension()
    {
        var result = Rewrite("[[B.md]] [[B.md|b]]", "A.md", new Dictionary<string, string> { ["B.md"] = "C.md" }, ["A.md", "B.md"]);

        result.Text.Should().Be("[[C.md]] [[C.md|b]]");
    }

    [Fact]
    public void Rename_KeepsRelativeStyle()
    {
        var result = Rewrite("[[./B]] [[../Raiz]]", "D/A.md", new Dictionary<string, string> { ["D/B.md"] = "D/Sub/B2.md", ["Raiz.md"] = "R2.md" }, ["D/A.md", "D/B.md", "Raiz.md"]);

        result.Text.Should().Be("[[./Sub/B2]] [[../R2]]");
    }

    [Fact]
    public void MoveOfSource_FixesRelativeLinks()
    {
        var result = Rewrite("[[./B]] [[B]]", "D/A.md", new Dictionary<string, string> { ["D/A.md"] = "E/A.md" }, ["D/A.md", "D/B.md"], newSource: "E/A.md");

        result.Text.Should().Be("[[../D/B]] [[B]]");
    }

    [Fact]
    public void Rename_WithMarkdownSettings_KeepsWikilinkSyntaxOfExistingLinks()
    {
        var settings = VaultSettings.Default with { LinkFormat = LinkFormat.Markdown };

        var result = Rewrite("[[B]]", "A.md", new Dictionary<string, string> { ["B.md"] = "Nuevo nombre.md" }, ["A.md", "B.md"], settings);

        result.Text.Should().Be("[[Nuevo nombre]]");
    }

    [Fact]
    public void Rename_MarkdownLinkWithSpaces_IsPercentEncoded()
    {
        var text = "[t](B.md#H)";
        var link = Md(text, "B.md#H");

        var result = Rewrite(text, "A.md", new Dictionary<string, string> { ["B.md"] = "Con espacio.md" }, ["A.md", "B.md"], extraLinks: [link]);

        result.Text.Should().Be("[t](Con%20espacio.md#H)");
    }

    [Fact]
    public void UnresolvedInternalAndExternalLinks_AreUntouched()
    {
        var text = "[[Nada]] [[#Encabezado]] [web](https://x.com/B.md)";
        var external = Md(text, "https://x.com/B.md");

        var result = Rewrite(text, "A.md", new Dictionary<string, string> { ["B.md"] = "C.md" }, ["A.md", "B.md"], extraLinks: [external]);

        result.Text.Should().Be(text);
    }

    [Fact]
    public void Rename_PartialPathSuffix_UsesConfiguredStyle()
    {
        var result = Rewrite("[[b/Nota]]", "A.md", new Dictionary<string, string> { ["a/b/Nota.md"] = "a/b/Nota2.md" }, ["A.md", "a/b/Nota.md", "c/Nota.md"]);

        result.Text.Should().Be("[[Nota2]]");
    }

    [Fact]
    public void Rename_MarkdownFullPathWithEncodedSpaces_StaysAFullPath()
    {
        var text = "[t](Mi%20Dir/B.md)";
        var link = Md(text, "Mi%20Dir/B.md");

        var result = Rewrite(text, "A.md", new Dictionary<string, string> { ["Mi Dir/B.md"] = "Mi Dir/C.md" }, ["A.md", "Mi Dir/B.md"], extraLinks: [link]);

        result.Text.Should().Be("[t](Mi%20Dir/C.md)");
    }

    [Fact]
    public void UnrelatedRename_LeavesEncodedMarkdownLinksUntouched()
    {
        var text = "[t](Proyecto%20X.md)";
        var link = Md(text, "Proyecto%20X.md");

        var result = Rewrite(text, "A.md", new Dictionary<string, string> { ["B.md"] = "C.md" }, ["A.md", "B.md", "Proyecto X.md"], extraLinks: [link]);

        result.Text.Should().Be(text);
        result.ChangedLinks.Should().Be(0);
    }

    [Fact]
    public void Rewrite_IsIdempotent()
    {
        var renames = new Dictionary<string, string> { ["B.md"] = "C.md" };
        var first = Rewrite("[[B]] [[B#H]]", "A.md", renames, ["A.md", "B.md"]);

        var second = Rewrite(first.Text, "A.md", new Dictionary<string, string>(), ["A.md", "C.md"]);

        second.Text.Should().Be(first.Text);
        second.ChangedLinks.Should().Be(0);
    }

    [Fact]
    public void Rewrite_RejectsNulls()
    {
        var request = new RewriteRequest(P("a.md"), P("a.md"), new Dictionary<VaultPath, VaultPath>(), new InMemoryFileCatalog(), new InMemoryFileCatalog(), VaultSettings.Default);

        FluentActions.Invoking(() => LinkRewriter.Rewrite(null!, [], request)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LinkRewriter.Rewrite("x", null!, request)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LinkRewriter.Rewrite("x", [], null!)).Should().Throw<ArgumentNullException>();
    }
}
