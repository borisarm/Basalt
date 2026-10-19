using Basalt.Application.Errors;
using Basalt.Domain.Common;
using Basalt.Testing;
using static Basalt.Domain.Common.StructuredValue;

namespace Basalt.Adapters.Markdown.Tests;

public sealed class FrontmatterEditorTests
{
    private readonly YamlFrontmatterEditor _editor = new();

    private const string Doc = "---\ntitle: Idea\nstatus: draft # comentario\ntags:\n  - a\n  - b\nmeta:\n  author: Ana\n---\n# Body\n\nstatus: not frontmatter\n";

    [Fact]
    [Trait("Spec", "F-010.1")]
    public void SetProperty_ScalarChange_OnlyThatLineChanges()
    {
        var result = _editor.SetProperty(Doc, "status", new TextValue("done"));

        result.Should().Be(Doc.Replace("status: draft # comentario", "status: done", StringComparison.Ordinal));
    }

    [Fact]
    public void SetProperty_BlockList_KeepsIndentationStyle()
    {
        var result = _editor.SetProperty(Doc, "tags", new ListValue([new TextValue("a"), new TextValue("c d")]));

        result.Should().Be(Doc.Replace("tags:\n  - a\n  - b\n", "tags:\n  - a\n  - c d\n", StringComparison.Ordinal));
    }

    [Fact]
    public void SetProperty_CompactBlockList_KeepsCompactStyle()
    {
        var doc = "---\ntags:\n- a\n- b\nx: 1\n---\n";

        _editor.SetProperty(doc, "tags", new ListValue([new TextValue("z")])).Should().Be("---\ntags:\n- z\nx: 1\n---\n");
    }

    [Fact]
    public void SetProperty_FlowList_KeepsFlowStyle()
    {
        var doc = "---\ntags: [a, b]\n---\n";

        _editor.SetProperty(doc, "tags", new ListValue([new TextValue("a"), new TextValue("x, y")])).Should().Be("---\ntags: [a, \"x, y\"]\n---\n");
    }

    [Fact]
    public void SetProperty_NewKey_IsAppendedBeforeClosingDelimiter()
    {
        var result = _editor.SetProperty(Doc, "rating", new NumberValue("5"));

        result.Should().Be(Doc.Replace("  author: Ana\n---\n", "  author: Ana\nrating: 5\n---\n", StringComparison.Ordinal));
    }

    [Fact]
    public void SetProperty_WithoutFrontmatter_CreatesIt()
    {
        _editor.SetProperty("# Note\r\nBody\r\n", "status", new TextValue("draft")).Should().Be("---\r\nstatus: draft\r\n---\r\n# Note\r\nBody\r\n");
        _editor.SetProperty(string.Empty, "a", new BoolValue(true)).Should().Be("---\na: true\n---\n");
    }

    [Fact]
    public void SetProperty_NestedMap_RegeneratesBlockInCanonicalStyle()
    {
        var result = _editor.SetProperty(Doc, "meta", MapValue.Of(("author", new TextValue("Ana")), ("year", new NumberValue("2026"))));

        Snapshot.MatchText(result);
    }

    [Fact]
    public void SetProperty_ReplacingNestedMapWithScalar_IsSurgicalForThatKey()
    {
        var result = _editor.SetProperty(Doc, "meta", new TextValue("none"));

        result.Should().Be(Doc.Replace("meta:\n  author: Ana\n", "meta: none\n", StringComparison.Ordinal));
    }

    [Fact]
    public void SetProperty_PreservesCrLf()
    {
        var doc = "---\r\na: 1\r\nb: 2\r\n---\r\nBody\r\n";

        _editor.SetProperty(doc, "b", new ListValue([new TextValue("x")])).Should().Be("---\r\na: 1\r\nb:\r\n  - x\r\n---\r\nBody\r\n");
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with: colon", "\"with: colon\"")]
    [InlineData("#hash", "\"#hash\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("123", "\"123\"")]
    [InlineData("2026-10-19", "\"2026-10-19\"")]
    [InlineData("", "\"\"")]
    [InlineData(" padded ", "\" padded \"")]
    [InlineData("quote \" inside", "quote \" inside")]
    [InlineData("\"starts quoted", "'\"starts quoted'")]
    [InlineData("[[Link]]", "\"[[Link]]\"")]
    [InlineData("- dash", "\"- dash\"")]
    [InlineData("multi\nline", "\"multi\\nline\"")]
    [InlineData("it's", "it's")]
    [InlineData("null", "\"null\"")]
    [InlineData("comment # inside", "\"comment # inside\"")]
    public void SetProperty_QuotesTextWhenNeeded(string text, string rendered)
    {
        _editor.SetProperty("---\nk: v\n---\n", "k", new TextValue(text)).Should().Be($"---\nk: {rendered}\n---\n");
    }

    [Fact]
    public void SetProperty_TypedValues()
    {
        string Set(StructuredValue v) => _editor.SetProperty("---\nk: v\n---\n", "k", v);

        Set(new BoolValue(false)).Should().Be("---\nk: false\n---\n");
        Set(NullValue.Instance).Should().Be("---\nk:\n---\n");
        Set(new DateValue("2026-10-19")).Should().Be("---\nk: 2026-10-19\n---\n");
        Set(new DateTimeValue("2026-10-19T10:00")).Should().Be("---\nk: 2026-10-19T10:00\n---\n");
        Set(new ListValue([])).Should().Be("---\nk: []\n---\n");
    }

    [Fact]
    public void SetProperty_KeyNeedingQuotes_IsQuoted()
    {
        _editor.SetProperty("---\n---\n", "my key: x", new TextValue("v")).Should().Be("---\n\"my key: x\": v\n---\n");
    }

    [Fact]
    public void SetProperty_ResultParsesBackToTheValue()
    {
        var value = new ListValue([new TextValue("a: b"), new NumberValue("1.5"), new BoolValue(true), new TextValue("#x")]);

        var result = _editor.SetProperty(Doc, "tags", value);

        var parsed = new MarkdigAnalyzer().Analyze(result).Frontmatter!.Values.TryGet("tags");
        parsed.Should().Be(value);
    }

    [Fact]
    public void RemoveProperty_RemovesOnlyThatKey()
    {
        _editor.RemoveProperty(Doc, "tags").Should().Be(Doc.Replace("tags:\n  - a\n  - b\n", string.Empty, StringComparison.Ordinal));
        _editor.RemoveProperty(Doc, "meta").Should().Be(Doc.Replace("meta:\n  author: Ana\n", string.Empty, StringComparison.Ordinal));
        _editor.RemoveProperty(Doc, "missing").Should().Be(Doc);
        _editor.RemoveProperty("no frontmatter", "a").Should().Be("no frontmatter");
    }

    [Fact]
    public void Edits_WithInvalidYaml_LeaveTheDocumentUntouched()
    {
        var doc = "---\na: [unclosed\n---\nBody\n";

        _editor.SetProperty(doc, "a", new TextValue("x")).Should().Be(doc);
        _editor.RemoveProperty(doc, "a").Should().Be(doc);
    }

    [Fact]
    public void Parse_ReturnsValuesOrErrors()
    {
        _editor.Parse("a: 1\nb: [x]").Value.Values.Should().Be(MapValue.Of(("a", new NumberValue("1")), ("b", new ListValue([new TextValue("x")]))));
        _editor.Parse("a: [").Error.Code.Should().Be(ErrorCodes.FrontmatterInvalidYaml);
        _editor.Parse("- just\n- a list").Error.Code.Should().Be(ErrorCodes.FrontmatterNotAMapping);
    }

    [Fact]
    public void Render_ProducesCanonicalBlock()
    {
        var values = MapValue.Of(
            ("title", new TextValue("Idea: one")),
            ("tags", new ListValue([new TextValue("a"), new TextValue("b")])),
            ("meta", MapValue.Of(("year", new NumberValue("2026")), ("list", new ListValue([new TextValue("x")])))),
            ("empty", NullValue.Instance));

        Snapshot.MatchText(_editor.Render(values));
    }

    [Fact]
    public void RejectsNulls()
    {
        FluentActions.Invoking(() => _editor.SetProperty(null!, "k", NullValue.Instance)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => _editor.SetProperty("", null!, NullValue.Instance)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => _editor.SetProperty("", "k", null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => _editor.RemoveProperty(null!, "k")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => _editor.Parse(null!)).Should().Throw<ArgumentNullException>();
    }
}
