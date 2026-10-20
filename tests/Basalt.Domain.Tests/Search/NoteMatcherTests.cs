using Basalt.Domain.Common;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;
using Basalt.Domain.Tags;

namespace Basalt.Domain.Tests.Search;

public sealed class NoteMatcherTests
{
    private const string Text =
        "---\n" +                                      // 0
        "status: done\n" +                             // 1
        "tags: [trabajo/cliente-a]\n" +                // 2
        "rating: 4\n" +                                // 3
        "empty:\n" +                                   // 4
        "list: [uno, dos]\n" +                         // 5
        "---\n" +                                      // 6
        "# Reunión semanal\n" +                        // 7
        "\n" +                                         // 8
        "Hablamos del presupuesto con María.\n" +      // 9
        "Acordamos revisar el diseño #urgente\n" +     // 10
        "\n" +                                         // 11
        "- [ ] Llamar a Pedro mañana\n" +              // 12
        "- [x] Enviar acta\n" +                        // 13
        "\n" +                                         // 14
        "## Notas\n" +                                 // 15
        "\n" +                                         // 16
        "El café estaba frío. Código: ABC-123\n";      // 17

    private static readonly SearchableNote Note = new(
        VaultPath.Create("Proyectos/Cliente A/Reunión semanal.md").Value,
        Text,
        new NoteMetadata(
            new Frontmatter(
                new StructuredValue.MapValue(
                [
                    new("status", new StructuredValue.TextValue("done")),
                    new("tags", new StructuredValue.ListValue([new StructuredValue.TextValue("trabajo/cliente-a")])),
                    new("rating", new StructuredValue.NumberValue("4")),
                    new("empty", StructuredValue.NullValue.Instance),
                    new("list", new StructuredValue.ListValue([new StructuredValue.TextValue("uno"), new StructuredValue.TextValue("dos")])),
                ]),
                new SourceSpan(0, 77, 0, 0)),
            [Heading.Create(1, "Reunión semanal", 7).Value, Heading.Create(2, "Notas", 15).Value],
            [],
            [],
            [new TagOccurrence(Tag.Create("trabajo/cliente-a").Value, TagSource.Frontmatter, 2), new TagOccurrence(Tag.Create("urgente").Value, TagSource.Text, 10)],
            [],
            [new TaskItem(12, ' ', "Llamar a Pedro mañana"), new TaskItem(13, 'x', "Enviar acta")],
            [],
            30));

    private static SearchMatch? Match(string query) => NoteMatcher.Create(SearchQueryParser.Parse(query).Value).Match(Note);

    [Theory]
    [Trait("Spec", "PERS-4.2")]
    // term: content or name, case- and diacritic-insensitive
    [InlineData("presupuesto", true)]
    [InlineData("PRESUPUESTO", true)]
    [InlineData("reunion", true)]
    [InlineData("maria", true)]
    [InlineData("MARÍA", true)]
    [InlineData("cafe", true)]
    [InlineData("semanal", true)]
    [InlineData("inexistente", false)]
    [InlineData("presu puesto", true)]
    [InlineData("presupuesto puestos", false)]
    [InlineData("presu", true)]
    // implicit AND, OR, NOT
    [InlineData("presupuesto diseño", true)]
    [InlineData("presupuesto inexistente", false)]
    [InlineData("inexistente OR diseño", true)]
    [InlineData("inexistente OR nada", false)]
    [InlineData("-inexistente", true)]
    [InlineData("-presupuesto", false)]
    [InlineData("presupuesto -(nada OR diseño)", false)]
    [InlineData("nada OR (presupuesto acta)", true)]
    // phrase
    [InlineData("\"del presupuesto\"", true)]
    [InlineData("\"presupuesto del\"", false)]
    [InlineData("\"reunion semanal\"", true)]
    // regex: case-insensitive, not diacritic-insensitive
    [InlineData("/ABC-\\d{3}/", true)]
    [InlineData("/abc-\\d{4}/", false)]
    [InlineData("/caf[eé]/", true)]
    [InlineData("/^## Notas$/", false)]
    [InlineData("/(?m)^## notas$/", true)]
    // file: name only
    [InlineData("file:reunión", true)]
    [InlineData("file:semanal", true)]
    [InlineData("file:presupuesto", false)]
    [InlineData("file:proyectos", false)]
    [InlineData("file:\"reunion semanal\"", true)]
    [InlineData("file:/^reuni.n/", true)]
    // path: full path
    [InlineData("path:proyectos", true)]
    [InlineData("path:\"cliente a/\"", true)]
    [InlineData("path:presupuesto", false)]
    // content: content only
    [InlineData("content:presupuesto", true)]
    [InlineData("content:\"cliente a\"", false)]
    // tag: tag or descendant
    [InlineData("tag:#urgente", true)]
    [InlineData("tag:urgente", true)]
    [InlineData("tag:#URGENTE", true)]
    [InlineData("tag:#trabajo", true)]
    [InlineData("tag:#trabajo/cliente-a", true)]
    [InlineData("tag:#trab", false)]
    [InlineData("tag:#cliente-a", false)]
    [InlineData("tag:(#nada OR #urgente)", true)]
    // line: same line
    [InlineData("line:(presupuesto maría)", true)]
    [InlineData("line:(presupuesto diseño)", false)]
    [InlineData("line:(presupuesto OR diseño)", true)]
    [InlineData("line:(presupuesto -maría)", false)]
    // block: same paragraph or list item
    [InlineData("block:(presupuesto diseño)", true)]
    [InlineData("block:(presupuesto pedro)", false)]
    [InlineData("block:(llamar pedro)", true)]
    [InlineData("block:(llamar acta)", false)]
    // section: same section (between headings)
    [InlineData("section:(presupuesto pedro)", true)]
    [InlineData("section:(presupuesto café)", false)]
    [InlineData("section:(notas café)", true)]
    // tasks
    [InlineData("task:pedro", true)]
    [InlineData("task:acta", true)]
    [InlineData("task:presupuesto", false)]
    [InlineData("task-todo:pedro", true)]
    [InlineData("task-todo:acta", false)]
    [InlineData("task-done:acta", true)]
    [InlineData("task-done:pedro", false)]
    [InlineData("task:(llamar mañana)", true)]
    [InlineData("task:(llamar acta)", false)]
    // properties
    [InlineData("[status]", true)]
    [InlineData("[STATUS]", true)]
    [InlineData("[owner]", false)]
    [InlineData("[status:done]", true)]
    [InlineData("[status:DON]", true)]
    [InlineData("[status:draft]", false)]
    [InlineData("[rating:4]", true)]
    [InlineData("[list:dos]", true)]
    [InlineData("[list:tres]", false)]
    [InlineData("[empty:null]", true)]
    [InlineData("[status:null]", false)]
    [InlineData("[owner:null]", false)]
    [InlineData("[tags:cliente]", true)]
    [InlineData("-[owner]", true)]
    // combinations
    [InlineData("tag:#urgente file:reunión -[status:draft]", true)]
    [InlineData("line:(\"del presupuesto\" -diseño) path:proyectos", true)]
    public void Operators(string query, bool matches)
    {
        (Match(query) is not null).Should().Be(matches, query);
    }

    [Fact]
    public void EmptyQuery_MatchesNothing()
    {
        NoteMatcher.Create(SearchQuery.Empty).Match(Note).Should().BeNull();
    }

    [Fact]
    public void Ranges_PointAtTheOriginalText()
    {
        var match = Match("maria cafe")!;

        match.Ranges.Should().Equal(
            new MatchRange(MatchTarget.Content, Text.IndexOf("María", StringComparison.Ordinal), 5),
            new MatchRange(MatchTarget.Content, Text.IndexOf("café", StringComparison.Ordinal), 4));
    }

    [Fact]
    public void Ranges_IncludeEveryOccurrence_AndTheFileName()
    {
        var match = Match("reunion")!;

        match.Ranges.Should().Equal(
            new MatchRange(MatchTarget.Content, Text.IndexOf("Reunión", StringComparison.Ordinal), 7),
            new MatchRange(MatchTarget.FileName, 0, 7));
    }

    [Fact]
    public void Ranges_ForPathRegexAndScopedOperators()
    {
        Match("path:cliente")!.Ranges.Should().Equal(new MatchRange(MatchTarget.Path, 10, 7));
        Match("/ABC-\\d+/")!.Ranges.Should().Equal(new MatchRange(MatchTarget.Content, Text.IndexOf("ABC-123", StringComparison.Ordinal), 7));
        Match("line:(presupuesto maría)")!.Ranges.Should().HaveCount(2);
        Match("task-done:acta")!.Ranges.Should().Equal(new MatchRange(MatchTarget.Content, Text.LastIndexOf("acta", StringComparison.Ordinal), 4));
        Match("tag:#urgente")!.Ranges.Should().Equal(new MatchRange(MatchTarget.Content, Text.IndexOf("#urgente", StringComparison.Ordinal), 8));
    }

    [Fact]
    public void NegatedParts_DoNotProduceRanges()
    {
        Match("presupuesto -nada")!.Ranges.Should().ContainSingle();
        Match("[status]")!.Ranges.Should().BeEmpty();
    }

    [Fact]
    public void Attachments_MatchByName()
    {
        var attachment = new SearchableNote(VaultPath.Create("img/Diagrama café.png").Value, string.Empty, NoteMetadata.Empty);
        var matcher = NoteMatcher.Create(SearchQueryParser.Parse("cafe").Value);

        matcher.Match(attachment)!.Ranges.Should().Equal(new MatchRange(MatchTarget.FileName, 9, 4));
        NoteMatcher.Create(SearchQueryParser.Parse("[status]").Value).Match(attachment).Should().BeNull();
    }

    [Fact]
    public void DecomposedText_IsFoldedLikeComposed()
    {
        var decomposed = new SearchableNote(VaultPath.Create("n.md").Value, "Café solo", NoteMetadata.Empty);

        NoteMatcher.Create(SearchQueryParser.Parse("café").Value).Match(decomposed)!.Ranges.Should().Equal(new MatchRange(MatchTarget.Content, 0, 5));
    }

    [Fact]
    public void EmptyTermsAndPhrases_MatchEverything()
    {
        Match("\"\"").Should().NotBeNull();
    }
}
