using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Tests.TestSupport;
using Basalt.Domain.Notes;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Notes;

public sealed class ReadingTests
{
    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text);

    private static async Task<VaultFixture> VaultAsync() => await VaultFixture.CreateAsync(Files(
        ("A.md", "# A\n![[B]]\n![[B#Dos]]\n![[B#^blk]]\n![[img.png]]\n![[Nada]]"),
        ("B.md", "# Uno\nprimero\n# Dos\nsegundo ^blk\n## Sub\nsub\n# Tres\ntercero"),
        ("Ciclo.md", "![[Ciclo]]"),
        ("img.png", "PNG"),
        ("doc.pdf", "%PDF")));

    private static GetEmbedHandler Embeds(VaultFixture v) => new(v.Sessions, v.Renderer);

    [Fact]
    [Trait("Spec", "UI-6")]
    public async Task RenderNote_UsesTheRenderer()
    {
        await using var v = await VaultAsync();

        var document = (await new RenderNoteHandler(v.Sessions, v.Renderer).HandleAsync(new RenderNote(Id, P("A.md"), "# Hola\ntexto"), Ct)).Value;

        document.Blocks[0].Should().BeOfType<HeadingBlock>();
        document.Blocks[1].Should().BeOfType<ParagraphBlock>();
    }

    [Fact]
    public async Task ResolveLink_FindsTheTargetOrNothing()
    {
        await using var v = await VaultAsync();
        var handler = new ResolveLinkHandler(v.Sessions);

        (await handler.HandleAsync(new ResolveLink(Id, P("A.md"), "B"), Ct)).Value.Should().Be(P("B.md"));
        (await handler.HandleAsync(new ResolveLink(Id, P("A.md"), "img.png"), Ct)).Value.Should().Be(P("img.png"));
        (await handler.HandleAsync(new ResolveLink(Id, P("A.md"), "Nada"), Ct)).Value.Should().BeNull();
    }

    [Fact]
    [Trait("Spec", "UI-6")]
    public async Task Embeds_WholeNote_Section_AndBlock()
    {
        await using var v = await VaultAsync();

        var whole = (await Embeds(v).HandleAsync(new GetEmbed(Id, P("A.md"), "B", null, []), Ct)).Value;
        var section = (await Embeds(v).HandleAsync(new GetEmbed(Id, P("A.md"), "B", "Dos", []), Ct)).Value;
        var block = (await Embeds(v).HandleAsync(new GetEmbed(Id, P("A.md"), "B", "^blk", []), Ct)).Value;

        whole.Should().BeOfType<NoteEmbed>().Which.Document.Blocks.OfType<HeadingBlock>().Should().HaveCount(4);
        var sectionBlocks = section.Should().BeOfType<NoteEmbed>().Which.Document.Blocks;
        sectionBlocks.Select(b => b.Line).Should().Equal(2, 3, 4, 5);
        block.Should().BeOfType<NoteEmbed>().Which.Document.Blocks.Should().ContainSingle().Which.BlockId.Should().Be("blk");
        ((NoteEmbed)whole).Path.Should().Be(P("B.md"));
        var last = (await Embeds(v).HandleAsync(new GetEmbed(Id, P("A.md"), "B", "Tres", []), Ct)).Value;
        last.Should().BeOfType<NoteEmbed>().Which.Document.Blocks.Select(b => b.Line).Should().Equal(6, 7);
    }

    [Fact]
    public async Task Embeds_ImagesFilesAndMissingTargets()
    {
        await using var v = await VaultAsync();

        (await Embeds(v).HandleAsync(new GetEmbed(Id, P("A.md"), "img.png", null, []), Ct)).Value.Should().Be(new ImageEmbed(P("img.png")));
        (await Embeds(v).HandleAsync(new GetEmbed(Id, P("A.md"), "doc.pdf", null, []), Ct)).Value.Should().Be(new FileEmbed(P("doc.pdf"), FileKind.Pdf));
        (await Embeds(v).HandleAsync(new GetEmbed(Id, P("A.md"), "Nada", null, []), Ct)).Value.Should().Be(new MissingEmbed("Nada"));
        (await Embeds(v).HandleAsync(new GetEmbed(Id, P("A.md"), "B", "No existe", []), Ct)).Value.Should().Be(new MissingEmbed("B#No existe"));
    }

    [Fact]
    [Trait("Spec", "UI-6")]
    public async Task Embeds_StopAtCyclesAndDepthThree()
    {
        await using var v = await VaultAsync();

        (await Embeds(v).HandleAsync(new GetEmbed(Id, P("Ciclo.md"), "Ciclo", null, [P("Ciclo.md")]), Ct)).Value.Should().Be(new CycleEmbed(P("Ciclo.md")));
        (await Embeds(v).HandleAsync(new GetEmbed(Id, P("A.md"), "B", null, [P("x.md"), P("y.md"), P("A.md")]), Ct)).Value.Should().Be(new CycleEmbed(P("B.md")));
    }

    [Fact]
    public async Task ReadAttachment_ReturnsTheBytes()
    {
        await using var v = await VaultAsync();
        var handler = new ReadAttachmentHandler(v.Sessions);

        (await handler.HandleAsync(new ReadAttachment(Id, P("img.png")), Ct)).Value.Should().Equal("PNG"u8.ToArray());
        (await handler.HandleAsync(new ReadAttachment(Id, P("none.png")), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
    }
}
