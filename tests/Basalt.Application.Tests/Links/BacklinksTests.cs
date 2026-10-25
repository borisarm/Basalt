using System.Text;
using Basalt.Application.Errors;
using Basalt.Application.Links;
using Basalt.Application.Notes;
using Basalt.Application.Tests.TestSupport;
using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Links;

public sealed class BacklinksTests
{
    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text);

    private static GetBacklinksHandler Backlinks(VaultFixture v) => new(v.Sessions, v.Analyzer);

    [Fact]
    [Trait("Spec", "F-005.1")]
    public async Task Backlinks_GroupLinksBySource_WithLineContext()
    {
        await using var v = await VaultFixture.CreateAsync(Files(
            ("A.md", "intro\nver [[C]] hoy\nfin [[C#Tema|c]]"),
            ("B.md", "![[C]]"),
            ("C.md", "# Tema"),
            ("D.md", "[[Otra]]")));

        var result = (await Backlinks(v).HandleAsync(new GetBacklinks(Id, P("C.md")), Ct)).Value;

        result.Linked.Select(g => g.Source).Should().Equal(P("A.md"), P("B.md"));
        var a = result.Linked[0].Links;
        a.Select(l => l.Context.Text).Should().Equal("ver [[C]] hoy", "fin [[C#Tema|c]]");
        a[0].Context.Line.Should().Be(1);
        a[0].Context.Highlight.Should().Be(new TextRange(4, 5));
        result.Linked[1].Links.Single().Link.IsEmbed.Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "F-005.1")]
    public async Task Backlinks_WhenTheSourceChangedAfterIndexing_ReadsTheCurrentText()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "[[C]]"), ("C.md", "")));
        await v.FileSystem.WriteAllBytesAsync(P("A.md"), Encoding.UTF8.GetBytes("intro [[C]] y [[C]]"), null, Ct);

        var result = (await Backlinks(v).HandleAsync(new GetBacklinks(Id, P("C.md")), Ct)).Value;

        result.Linked.Single().Links.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Spec", "F-005.2")]
    public async Task UnlinkedMentions_FindTitleAndAliases_OutsideLinksAndCode()
    {
        await using var v = await VaultFixture.CreateAsync(Files(
            ("A.md", "hablé de proyecto x ayer"),
            ("B.md", "[[Proyecto X]] y `proyecto x` y PX"),
            ("C.md", "proyectos xx"),
            ("Proyecto X.md", "---\naliases: [PX]\n---\nproyecto x aquí no cuenta")));

        var result = (await Backlinks(v).HandleAsync(new GetBacklinks(Id, P("Proyecto X.md")), Ct)).Value;

        result.Unlinked.Select(g => g.Source).Should().Equal(P("A.md"), P("B.md"));
        result.Unlinked[0].Mentions.Single().Should().BeEquivalentTo(new { Range = new TextRange(9, 10), Name = "Proyecto X" });
        result.Unlinked[0].Mentions.Single().Context.Text.Should().Be("hablé de proyecto x ayer");
        result.Unlinked[1].Mentions.Single().Name.Should().Be("PX");
        result.Linked.Select(g => g.Source).Should().Equal(P("B.md"));
    }

    [Fact]
    [Trait("Spec", "F-005.2")]
    public async Task LinkUnlinkedMention_RewritesTheMentionAsALinkWithAlias()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "hablé de proyecto x ayer"), ("Proyecto X.md", "")));
        var mention = (await Backlinks(v).HandleAsync(new GetBacklinks(Id, P("Proyecto X.md")), Ct)).Value.Unlinked.Single().Mentions.Single();

        var result = await new LinkUnlinkedMentionHandler(v.Sessions, v.Analyzer, v.Events)
            .HandleAsync(new LinkUnlinkedMention(Id, P("A.md"), mention.Range, P("Proyecto X.md")), Ct);

        result.IsSuccess.Should().BeTrue();
        (await v.ReadAsync("A.md")).Should().Be("hablé de [[Proyecto X|proyecto x]] ayer");
    }

    [Fact]
    public async Task LinkUnlinkedMention_WithTheExactTitle_HasNoAlias_AndRespectsMarkdownFormat()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "ver Proyecto X"), ("B.md", "ver Proyecto X"), ("Proyecto X.md", "")));
        var handler = new LinkUnlinkedMentionHandler(v.Sessions, v.Analyzer, v.Events);

        await handler.HandleAsync(new LinkUnlinkedMention(Id, P("A.md"), new TextRange(4, 10), P("Proyecto X.md")), Ct);
        v.Session.UpdateSettings(v.Session.Settings with { LinkFormat = LinkFormat.Markdown });
        await handler.HandleAsync(new LinkUnlinkedMention(Id, P("B.md"), new TextRange(4, 10), P("Proyecto X.md")), Ct);

        (await v.ReadAsync("A.md")).Should().Be("ver [[Proyecto X]]");
        (await v.ReadAsync("B.md")).Should().Be("ver [Proyecto X](Proyecto%20X.md)");
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(4, 9)]
    [InlineData(20, 10)]
    [InlineData(-1, 2)]
    public async Task LinkUnlinkedMention_WhenTheRangeIsNoLongerAMention_Fails(int start, int length)
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "ver Proyecto X"), ("Proyecto X.md", "")));

        var result = await new LinkUnlinkedMentionHandler(v.Sessions, v.Analyzer, v.Events)
            .HandleAsync(new LinkUnlinkedMention(Id, P("A.md"), new TextRange(start, length), P("Proyecto X.md")), Ct);

        result.Error.Code.Should().Be(ErrorCodes.MentionNotFound);
        (await v.ReadAsync("A.md")).Should().Be("ver Proyecto X");
    }

    [Fact]
    [Trait("Spec", "F-005.3")]
    public async Task Backlinks_UpdateWithinTheIndexerDebounce()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("C.md", ""), ("D.md", "nada")));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = v.Events.Subscribe<MetadataChanged>((e, _) =>
        {
            if (e.Paths.Contains(P("D.md")))
            {
                changed.TrySetResult();
            }

            return ValueTask.CompletedTask;
        });

        await new SaveNoteHandler(v.Sessions, v.Events).HandleAsync(new SaveNote(Id, P("D.md"), "ahora [[C]]", null), Ct);
        await v.Events.WaitForIdleAsync(Ct);
        v.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        (await Backlinks(v).HandleAsync(new GetBacklinks(Id, P("C.md")), Ct)).Value.Linked.Select(g => g.Source).Should().Equal(P("D.md"));
    }

    [Fact]
    public async Task Outgoing_ListsResolvedAndUnresolved_ButNotExternal()
    {
        await using var v = await VaultFixture.CreateAsync(Files(
            ("A.md", "[[B]] [[Nada]] [web](https://x.org) ![[img.png]] [md](B.md)"),
            ("B.md", ""),
            ("img.png", "")));

        var links = (await new GetOutgoingLinksHandler(v.Sessions, v.Analyzer).HandleAsync(new GetOutgoingLinks(Id, P("A.md")), Ct)).Value;

        links.Select(l => (l.Link.RawTarget, l.Resolution.TargetPath)).Should().Equal(
            ("B", P("B.md")), ("Nada", null), ("img.png", P("img.png")), ("B.md", P("B.md")));
        links[1].Resolution.Should().BeOfType<LinkResolution.Unresolved>();
    }

    [Fact]
    public async Task MissingNotesAndClosedVaults_AreErrors()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "")));
        var closed = new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));

        (await new GetOutgoingLinksHandler(v.Sessions, v.Analyzer).HandleAsync(new GetOutgoingLinks(Id, P("none.md")), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
        (await Backlinks(v).HandleAsync(new GetBacklinks(closed, P("A.md")), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await new GetOutgoingLinksHandler(v.Sessions, v.Analyzer).HandleAsync(new GetOutgoingLinks(closed, P("A.md")), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await new LinkUnlinkedMentionHandler(v.Sessions, v.Analyzer, v.Events).HandleAsync(new LinkUnlinkedMention(closed, P("A.md"), new TextRange(0, 1), P("A.md")), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }

    [Fact]
    public async Task Backlinks_ToAnAttachment_HaveNoUnlinkedMentions()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("A.md", "![[img.png]] img"), ("img.png", "")));

        var result = (await Backlinks(v).HandleAsync(new GetBacklinks(Id, P("img.png")), Ct)).Value;

        result.Linked.Should().ContainSingle();
        result.Unlinked.Should().BeEmpty();
    }
}
