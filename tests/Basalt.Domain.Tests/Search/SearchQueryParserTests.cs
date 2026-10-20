using System.Text;
using Basalt.Domain.Common;
using Basalt.Domain.Search;
using Basalt.Testing;
using Basalt.Testing.Generators;
using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Domain.Tests.Search;

public sealed class SearchQueryParserTests
{
    /// <summary>Every query and its AST, reviewed in <c>SearchQueryParserTests.Parse_Corpus.verified.txt</c>.</summary>
    private static readonly string[] Corpus =
    [
        // terms and implicit AND
        "",
        "   ",
        "reunión",
        "Reunión",
        "a b",
        "a  b   c",
        "a AND b",
        "a and b",
        "café-con-leche",
        "a:b",
        "foo:bar baz",
        "C#",
        "#tag",
        "a[b]",
        "100%",
        "-",
        "a -",
        // OR and precedence (OR binds tighter than the implicit AND)
        "a OR b",
        "a OR b OR c",
        "a b OR c",
        "a OR b c",
        "a b OR c d",
        "a OR b AND c",
        "a AND b OR c",
        "(a b) OR c",
        "a or b",
        "OR a",
        "a OR",
        "a OR OR b",
        "AND",
        "a AND",
        // negation
        "-a",
        "a -b",
        "-a -b",
        "-(a OR b)",
        "-\"a b\"",
        "-file:x",
        "-[status]",
        "a -b OR c",
        // phrases
        "\"exact phrase\"",
        "\"a \\\"quoted\\\" word\"",
        "\"unterminated phrase",
        "\"\"",
        "ab\"c d\"",
        "\"a\" OR \"b c\"",
        // regex
        "/a+b/",
        "/\\d{4}-\\d{2}/",
        "/a b/",
        "/with\\/slash/",
        "/usr/bin",
        "/notclosed",
        "x/y/",
        "/a/ b",
        // groups
        "(a b)",
        "((a))",
        "()",
        "(a",
        "a)",
        "(a OR b) (c OR d)",
        // fields
        "file:diario",
        "FILE:diario",
        "path:Proyectos/X",
        "path:\"Mis notas\"",
        "tag:#trabajo",
        "tag:trabajo/cliente",
        "line:(a b)",
        "block:(reunión acta)",
        "section:(a OR b)",
        "content:\"hola mundo\"",
        "task:llamar",
        "task-todo:llamar",
        "task-done:(a b)",
        "file:/^20\\d\\d/",
        "file:",
        "file: x",
        "unknown:x",
        // properties
        "[status]",
        "[status:done]",
        "[status:null]",
        "[status:\"in progress\"]",
        "[status:\"null\"]",
        "[ status : done ]",
        "[status:]",
        "[]",
        "[unterminated",
        "[a] OR [b:c]",
        // combinations
        "tag:#a file:b -[c] OR \"d e\"",
        "line:(\"a b\" -c) path:x",
        "reunión tag:#trabajo -task-done:x",
    ];

    [Fact]
    [Trait("Spec", "PERS-4.1")]
    public void Parse_Corpus()
    {
        Corpus.Length.Should().BeGreaterThanOrEqualTo(60);
        var builder = new StringBuilder();
        foreach (var query in Corpus)
        {
            var parsed = SearchQueryParser.Parse(query);
            builder.Append(query).Append("  =>  ").Append(parsed.Match(q => q.ToString(), e => "error " + e.Code)).Append('\n');
        }

        Snapshot.MatchText(builder.ToString());
    }

    [Fact]
    [Trait("Spec", "PERS-4.1")]
    public void Or_BindsTighterThanImplicitAnd()
    {
        var query = SearchQueryParser.Parse("a b OR c").Value;

        query.Root.Should().Be(new AndNode([new TermNode("a"), new OrNode([new TermNode("b"), new TermNode("c")])]));
    }

    [Fact]
    [Trait("Spec", "PERS-4.1")]
    public void Or_BindsTighterThanExplicitAnd()
    {
        SearchQueryParser.Parse("a AND b OR c").Value.ToString().Should().Be("(and (term \"a\") (or (term \"b\") (term \"c\")))");
    }

    [Fact]
    public void Parentheses_OverridePrecedence()
    {
        SearchQueryParser.Parse("(a b) OR c").Value.ToString().Should().Be("(or (group (and (term \"a\") (term \"b\"))) (term \"c\"))");
    }

    [Theory]
    [InlineData("/[a/")]
    [InlineData("/(a/")]
    [InlineData("/(?<=a)b/")]
    [InlineData("/(a)\\1/")]
    [InlineData("file:/(/")]
    [Trait("Spec", "NFR-032")]
    public void InvalidOrBacktrackingRegex_IsAnError(string query)
    {
        SearchQueryParser.Parse(query).Error.Code.Should().Be(DomainErrors.SearchInvalidRegex);
    }

    [Fact]
    public void DeepNesting_IsRejected()
    {
        SearchQueryParser.Parse(new string('(', 200) + "a").Error.Code.Should().Be(DomainErrors.SearchTooComplex);
        SearchQueryParser.Parse(string.Concat(Enumerable.Repeat("-", 200)) + "a").Error.Code.Should().Be(DomainErrors.SearchTooComplex);
        SearchQueryParser.Parse(new string('(', 30) + "a").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void FieldNames_RoundTrip()
    {
        foreach (var field in Enum.GetValues<SearchField>())
        {
            var parsed = SearchQueryParser.Parse(SearchQueryParser.FieldName(field) + ":x").Value;
            parsed.Root.Should().Be(new FieldNode(field, new TermNode("x")));
        }
    }

    [Fact]
    public void Nodes_HaveStructuralEquality()
    {
        new AndNode([new TermNode("a")]).Should().Be(new AndNode([new TermNode("a")]));
        new AndNode([new TermNode("a")]).GetHashCode().Should().Be(new AndNode([new TermNode("a")]).GetHashCode());
        new AndNode([new TermNode("a")]).Should().NotBe(new OrNode([new TermNode("a")]));
        new OrNode([new TermNode("a")]).Should().NotBe(new OrNode([new TermNode("b")]));
        SearchQuery.Empty.IsEmpty.Should().BeTrue();
        SearchQuery.Empty.ToString().Should().Be("(empty)");
    }

    [BasaltProperty]
    public Property Parse_NeverThrows_AndIsDeterministic()
    {
        var alphabet = Gen.Elements('a', 'b', ' ', '"', '(', ')', '[', ']', ':', '-', '/', '\\', '#', 'O', 'R', 'é');
        return Prop.ForAll(
            Gen.ArrayOf(alphabet).Select(chars => new string(chars)).ToArbitrary(),
            text =>
            {
                var first = SearchQueryParser.Parse(text);
                var second = SearchQueryParser.Parse(text);
                return first.IsSuccess == second.IsSuccess
                    && (first.IsFailure || first.Value.ToString() == second.Value.ToString());
            });
    }

    [BasaltProperty]
    public Property Canonical_TermsWithoutOperators_AreAnAndOfTerms()
    {
        return Prop.ForAll(
            Gen.ListOf(Gen.Elements("x", "nota", "café", "a1", "b-c")).Where(l => l.Count > 1).ToArbitrary(),
            words =>
            {
                var root = SearchQueryParser.Parse(string.Join(' ', words)).Value.Root;
                return root is AndNode and && and.Operands.Select(o => ((TermNode)o).Text).SequenceEqual(words);
            });
    }
}
