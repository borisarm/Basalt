using Basalt.Application.Errors;
using Basalt.Application.Links;
using Basalt.Application.Search;
using Basalt.Application.Tags;
using Basalt.Application.Tests.TestSupport;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Links;

public sealed class SuggestionsTests
{
    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text);

    private static async Task<IReadOnlyList<LinkSuggestion>> SuggestAsync(VaultFixture v, string partial, string source = "Nota.md") =>
        (await new SuggestLinkTargetsHandler(v.Sessions, v.Analyzer).HandleAsync(new SuggestLinkTargets(Id, P(source), partial), Ct)).Value;

    [Fact]
    [Trait("Spec", "F-004.1")]
    public async Task Notes_FuzzyByTitleAndAlias_WithTheShortestUnambiguousTarget()
    {
        await using var v = await VaultFixture.CreateAsync(Files(
            ("Nota.md", ""),
            ("Proyectos/Proyecto X.md", "---\naliases: [PX]\n---\n"),
            ("Prado.md", ""),
            ("a/Reunión.md", ""),
            ("b/Reunión.md", ""),
            ("img/Plano.png", "")));

        var suggestions = await SuggestAsync(v, "pro");

        suggestions[0].Should().BeEquivalentTo(new { Kind = LinkSuggestionKind.Note, Path = P("Proyectos/Proyecto X.md"), Display = "Proyecto X", Target = "Proyecto X" }, o => o.ComparingByValue<VaultPath>());
        suggestions.Select(s => s.Display).Should().ContainInOrder("Proyecto X", "Prado");
        suggestions.Should().OnlyContain(s => s.Score > 0 && s.Positions.Count == 3);

        (await SuggestAsync(v, "px")).Should().Contain(s => s.Kind == LinkSuggestionKind.Alias && s.Alias == "PX" && s.Target == "Proyecto X");
        (await SuggestAsync(v, "reunion")).Select(s => s.Target).Should().BeEquivalentTo("a/Reunión", "b/Reunión");
        (await SuggestAsync(v, "plano")).Single().Should().BeEquivalentTo(new { Kind = LinkSuggestionKind.Attachment, Target = "Plano.png" });
    }

    [Fact]
    [Trait("Spec", "F-004.2")]
    public async Task Headings_OfTheNamedNote_OrOfTheSourceNote()
    {
        await using var v = await VaultFixture.CreateAsync(Files(
            ("Nota.md", "# Propia"),
            ("Proyecto X.md", "# Objetivos\n## Riesgos\n## Presupuesto")));

        (await SuggestAsync(v, "Proyecto X#")).Select(s => s.Subpath).Should().Equal("Objetivos", "Riesgos", "Presupuesto");
        (await SuggestAsync(v, "Proyecto X#pre")).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Kind = LinkSuggestionKind.Heading, Target = "Proyecto X", Subpath = "Presupuesto", Line = 2 });
        (await SuggestAsync(v, "#")).Select(s => (s.Target, s.Subpath)).Should().Equal(("Nota", "Propia"));
        (await SuggestAsync(v, "Inexistente#")).Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "F-004.3")]
    public async Task Blocks_ListParagraphs_WithTheirIds()
    {
        await using var v = await VaultFixture.CreateAsync(Files(
            ("Nota.md", ""),
            ("Proyecto X.md", "---\nstatus: x\n---\n# Título\n\nPrimer párrafo ^ab12\n\n- tarea uno\n- tarea dos\n\n```\ncódigo\n```")));

        var blocks = await SuggestAsync(v, "Proyecto X#^");

        blocks.Select(b => (b.Display, b.Subpath, b.Line)).Should().Equal(
            ("Primer párrafo", "^ab12", 5), ("- tarea uno", null, 7), ("- tarea dos", null, 8), ("```\ncódigo\n```", null, 12));
        (await SuggestAsync(v, "Proyecto X#^dos")).Should().ContainSingle().Which.Line.Should().Be(8);
    }

    [Fact]
    [Trait("Spec", "F-004.3")]
    public async Task EnsureBlockId_AddsASixCharacterIdOnce()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Proyecto X.md", "uno\ndos\n\n- tarea\n\n```\nx\n```\n")));
        var handler = new EnsureBlockIdHandler(v.Sessions, v.Analyzer, new FakeRandom(), v.Events);

        var paragraph = (await handler.HandleAsync(new EnsureBlockId(Id, P("Proyecto X.md"), 0), Ct)).Value;
        var again = (await handler.HandleAsync(new EnsureBlockId(Id, P("Proyecto X.md"), 1), Ct)).Value;
        var item = (await handler.HandleAsync(new EnsureBlockId(Id, P("Proyecto X.md"), 3), Ct)).Value;
        var code = (await handler.HandleAsync(new EnsureBlockId(Id, P("Proyecto X.md"), 6), Ct)).Value;

        paragraph.Value.Should().MatchRegex("^[a-z0-9]{6}$");
        again.Should().Be(paragraph);
        item.Should().NotBe(paragraph);
        (await v.ReadAsync("Proyecto X.md")).Should().Be($"uno\ndos ^{paragraph}\n\n- tarea ^{item}\n\n```\nx\n```\n^{code}\n");
    }

    [Fact]
    public async Task EnsureBlockId_OnABlankLineOrMissingNote_Fails()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", "a\n\nb")));
        var handler = new EnsureBlockIdHandler(v.Sessions, v.Analyzer, new FakeRandom(), v.Events);

        (await handler.HandleAsync(new EnsureBlockId(Id, P("n.md"), 1), Ct)).Error.Code.Should().Be(ErrorCodes.BlockNotFound);
        (await handler.HandleAsync(new EnsureBlockId(Id, P("n.md"), 9), Ct)).Error.Code.Should().Be(ErrorCodes.BlockNotFound);
        (await handler.HandleAsync(new EnsureBlockId(Id, P("x.md"), 0), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
    }

    [Fact]
    [Trait("Spec", "F-006.1")]
    public async Task Tags_FormATreeWithPerNoteCounts()
    {
        await using var v = await VaultFixture.CreateAsync(Files(
            ("a.md", "#trabajo/cliente-a y otra vez #trabajo/cliente-a"),
            ("b.md", "#trabajo"),
            ("c.md", "---\ntags: [personal]\n---\n")));

        var tree = (await new GetTagsHandler(v.Sessions).HandleAsync(new GetTags(Id), Ct)).Value;

        tree.Roots.Select(r => (r.Segment, r.Count)).Should().BeEquivalentTo([("personal", 1), ("trabajo", 2)]);
        tree.Roots.Single(r => r.Segment == "trabajo").Children.Single().Should().BeEquivalentTo(new { Segment = "cliente-a", Count = 1 });
    }

    [Fact]
    [Trait("Spec", "F-006.2")]
    public async Task Tags_IgnoreThingsThatAreNotTags()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("a.md", "# Título\n#123 # C#sharp\n`#x`\n```\n#y\n```\n#real")));

        var tree = (await new GetTagsHandler(v.Sessions).HandleAsync(new GetTags(Id), Ct)).Value;

        tree.Roots.Select(r => r.FullName).Should().Equal("real");
    }

    [Fact]
    public async Task SuggestTags_FuzzyThenByCount()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("a.md", "#trabajo #tarea"), ("b.md", "#trabajo/cliente"), ("c.md", "#tarea")));
        var handler = new SuggestTagsHandler(v.Sessions);

        var suggestions = (await handler.HandleAsync(new SuggestTags(Id, "#ta"), Ct)).Value;

        suggestions.Select(s => (s.Name, s.Count)).Should().StartWith([("tarea", 2)]);
        suggestions.Select(s => s.Name).Should().Contain("trabajo/cliente");
        (await handler.HandleAsync(new SuggestTags(Id, ""), Ct)).Value.Select(s => s.Name).Should().Equal("tarea", "trabajo", "trabajo/cliente");
    }

    [Fact]
    [Trait("Spec", "F-008.1")]
    public async Task QuickSwitch_OffersToCreate_WhenNoNoteHasThatName()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Ideas.md", ""), ("Idea nueva vieja.md", "")));
        var handler = new QuickSwitchHandler(v.Sessions);

        var items = (await handler.HandleAsync(new QuickSwitch(Id, "Idea nueva"), Ct)).Value;

        items[0].Should().BeEquivalentTo(new { Kind = QuickSwitchKind.Create, Display = "Idea nueva", Path = (VaultPath?)null });
        items.Skip(1).Select(i => i.Display).Should().Contain("Idea nueva vieja");
        (await handler.HandleAsync(new QuickSwitch(Id, "ideas"), Ct)).Value.Should().NotContain(i => i.Kind == QuickSwitchKind.Create);
    }

    [Fact]
    public async Task QuickSwitch_MatchesAliasesAndPaths_AndListsRecentWhenEmpty()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("Proyectos/Proyecto X.md", "---\naliases: [PX]\n---\n"), ("Otra.md", "")));
        v.Clock.Advance(TimeSpan.FromMinutes(1));
        await v.FileSystem.WriteAllBytesAsync(P("Reciente.md"), Array.Empty<byte>(), null, Ct);
        await v.Events.PublishAsync(new Application.Notes.EntriesCreated(Id, [P("Reciente.md")]), Ct);
        await v.SettleAsync();
        var handler = new QuickSwitchHandler(v.Sessions);

        (await handler.HandleAsync(new QuickSwitch(Id, "px"), Ct)).Value.Should().Contain(i => i.Kind == QuickSwitchKind.Alias && i.Display == "PX" && i.Path == P("Proyectos/Proyecto X.md"));
        (await handler.HandleAsync(new QuickSwitch(Id, "proy/x"), Ct)).Value.Should().Contain(i => i.Kind == QuickSwitchKind.Note && i.Display == "Proyectos/Proyecto X");
        (await handler.HandleAsync(new QuickSwitch(Id, ""), Ct)).Value[0].Path.Should().Be(P("Reciente.md"));
    }
    [Fact]
    public async Task TagHandlers_RejectNullsAndClosedVaults()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("a.md", "#tag")));
        var closed = new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));
        var get = new GetTagsHandler(v.Sessions);
        var suggest = new SuggestTagsHandler(v.Sessions);

        await FluentActions.Awaiting(() => get.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => suggest.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        (await get.HandleAsync(new GetTags(closed), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        (await suggest.HandleAsync(new SuggestTags(closed, "t"), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }

    [Fact]
    public async Task SuggestTags_RespectsTheLimit()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("a.md", "#a #b #c")));

        var suggestions = (await new SuggestTagsHandler(v.Sessions).HandleAsync(new SuggestTags(Id, "", 2), Ct)).Value;

        suggestions.Should().HaveCount(2);
    }
    [Fact]
    public async Task SuggestLinkTargets_ReadsBlockIdsFromParagraphsAndCodeBlocks()
    {
        await using var v = await VaultFixture.CreateAsync(Files(
            ("n.md", "# T\n\n```\ncode\n```\n^abc\n\npara ^def\n")));
        var handler = new SuggestLinkTargetsHandler(v.Sessions, v.Analyzer);

        var suggestions = (await handler.HandleAsync(new SuggestLinkTargets(Id, P("n.md"), "n#^"), Ct)).Value;

        suggestions.Select(s => s.Subpath).Should().Contain(["^abc", "^def"]);
        suggestions.Should().OnlyContain(s => s.Kind == LinkSuggestionKind.Block);
    }

    [Fact]
    public async Task SuggestLinkTargets_WithAnEmptyPartialAfterTheHash_ListsHeadings()
    {
        await using var v = await VaultFixture.CreateAsync(Files(("n.md", "# Uno\n## Dos")));

        var suggestions = (await new SuggestLinkTargetsHandler(v.Sessions, v.Analyzer)
            .HandleAsync(new SuggestLinkTargets(Id, P("n.md"), "n#"), Ct)).Value;

        suggestions.Select(s => s.Display).Should().Equal("Uno", "Dos");
    }
}
