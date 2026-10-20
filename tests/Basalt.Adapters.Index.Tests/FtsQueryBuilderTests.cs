using Basalt.Domain.Search;

namespace Basalt.Adapters.Index.Tests;

public sealed class FtsQueryBuilderTests
{
    private static (string? Sql, string Parameters) Build(string query)
    {
        var builder = new FtsQueryBuilder();
        var sql = builder.Build(SearchQueryParser.Parse(query).Value);
        return (sql, string.Join(" | ", builder.Parameters.Select(p => $"{p.Name}={p.Value}")));
    }

    private const string Fts = "f.id IN (SELECT rowid FROM content_fts WHERE content_fts MATCH ";

    [Theory]
    [Trait("Spec", "PERS-4.3")]
    [InlineData("Reunión", Fts + "$p0)", "$p0={name body} : \"reunion\"")]
    [InlineData("\"fecha \\\"x\\\"\"", Fts + "$p0)", "$p0={name body} : \"fecha \"\"x\"\"\"")]
    [InlineData("file:Café", Fts + "$p0)", "$p0=name : \"cafe\"")]
    [InlineData("path:a/b", Fts + "$p0)", "$p0=path : \"a/b\"")]
    [InlineData("line:(abc def)", "(" + Fts + "$p0) AND " + Fts + "$p1))", "$p0=body : \"abc\" | $p1=body : \"def\"")]
    [InlineData("abc OR def", "(" + Fts + "$p0) OR " + Fts + "$p1))", "$p0={name body} : \"abc\" | $p1={name body} : \"def\"")]
    [InlineData("tag:#Trabajo", "f.id IN (SELECT file_id FROM tags WHERE tag_search = $p0 OR substr(tag_search, 1, length($p1)) = $p1)", "$p0=trabajo | $p1=trabajo/")]
    [InlineData("[Status:x]", "f.id IN (SELECT file_id FROM properties WHERE key_fold = $p0)", "$p0=STATUS")]
    public void Translates(string query, string sql, string parameters)
    {
        Build(query).Should().Be((sql, parameters));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("/abc/")]
    [InlineData("-abc")]
    [InlineData("abc OR -def")]
    [InlineData("abc OR /x/")]
    [InlineData("tag:#")]
    [InlineData("()")]
    public void Untranslatable_IsUnconstrained(string query)
    {
        Build(query).Sql.Should().BeNull();
    }

    [Fact]
    public void AndKeepsTheTranslatableParts()
    {
        Build("abc -def /x/ ab").Sql.Should().Be(Fts + "$p0)");
    }

    [Fact]
    public void ShortTermsCountRunesNotUtf16Units()
    {
        Build("😀😀").Sql.Should().BeNull();
        Build("😀😀😀").Sql.Should().NotBeNull();
    }
}
