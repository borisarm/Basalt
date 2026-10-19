using System.Runtime.CompilerServices;
using Basalt.Application.Ports.Markdown;
using Basalt.Domain.Notes;
using Basalt.Testing;

namespace Basalt.Adapters.Markdown.Tests;

public sealed class MarkdigRendererTests
{
    private static readonly string GoldenDirectory = Path.Combine(SourceDirectory(), "Golden");
    private static readonly RenderContext Context = new(VaultPath.Create("Nota.md").Value);
    private readonly MarkdigRenderer _renderer = new();

    [Theory]
    [MemberData(nameof(GoldenFileTests.Documents), MemberType = typeof(GoldenFileTests))]
    [Trait("Spec", "MD-8")]
    public void Render_GoldenDocument_MatchesVerifiedModel(string name)
    {
        var content = NoteContent.FromBytes(File.ReadAllBytes(Path.Combine(GoldenDirectory, name + ".md"))).Text;

        var model = _renderer.Render(content, Context);

        Snapshot.MatchTextFile(RenderDump.Dump(model), Path.Combine(GoldenDirectory, name + ".render"));
    }

    [Fact]
    public void Render_Callout_ParsesTypeFoldingTitleAndBody()
    {
        var model = _renderer.Render("> [!TIP]- My title\n> body [[Link]]\n>\n> second para\n", Context);

        var callout = model.Blocks.Should().ContainSingle().Which.Should().BeOfType<CalloutBlock>().Subject;
        callout.Type.Should().Be("tip");
        callout.Folding.Should().Be(CalloutFolding.Collapsed);
        callout.Title.Should().Equal(new TextInline("My title"));
        callout.Blocks.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("summary", "abstract")]
    [InlineData("hint", "tip")]
    [InlineData("error", "danger")]
    [InlineData("custom", "custom")]
    public void Render_CalloutAliases_AreNormalized(string written, string type)
    {
        var callout = (CalloutBlock)_renderer.Render($"> [!{written}]\n> x\n", Context).Blocks.Single();

        callout.Type.Should().Be(type);
        callout.Title.Should().ContainSingle();
    }

    [Fact]
    public void Render_TaskItems_CarryStatusAndLine()
    {
        var list = (ListBlock)_renderer.Render("- [ ] a\n- [x] b\n- c\n", Context).Blocks.Single();

        list.Items.Select(i => i.TaskStatus).Should().Equal(' ', 'x', null);
        list.Items.Select(i => i.Line).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Render_BlockIds_AreStrippedFromTextAndAttached()
    {
        var paragraph = (ParagraphBlock)_renderer.Render("Some text ^abc-1\n", Context).Blocks.Single();

        paragraph.BlockId.Should().Be("abc-1");
        paragraph.Inlines.Should().Equal(new TextInline("Some text"));
    }

    [Fact]
    [Trait("Spec", "NFR-031")]
    public void Render_Html_IsShownAsText()
    {
        var model = _renderer.Render("<script>alert(1)</script>\n\nInline <b>bold</b>\n", Context);

        model.Blocks[0].Should().BeOfType<CodeBlock>().Which.Language.Should().Be("html");
        ((ParagraphBlock)model.Blocks[1]).Inlines.Should().Equal(new TextInline("Inline <b>bold</b>"));
    }

    [Fact]
    public void Render_EmbedSizes()
    {
        var model = _renderer.Render("![[a.png|300]]\n\n![[b.png|300x200]]\n\n![[c|alias]]\n", Context);

        model.Blocks.Cast<EmbedBlock>().Select(e => (e.Embed.Width, e.Embed.Height, e.Embed.Alias))
            .Should().Equal((300, (int?)null, (string?)null), (300, 200, null), (null, null, "alias"));
    }

    [Fact]
    public void RejectsNulls()
    {
        FluentActions.Invoking(() => _renderer.Render(null!, Context)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => _renderer.Render("x", null!)).Should().Throw<ArgumentNullException>();
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
