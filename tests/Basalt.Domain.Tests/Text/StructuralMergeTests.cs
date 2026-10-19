using Basalt.Domain.Common;
using Basalt.Domain.Text;
using static Basalt.Domain.Common.StructuredValue;

namespace Basalt.Domain.Tests.Text;

public sealed class StructuralMergeTests
{
    private static MapValue M(params (string Key, StructuredValue Value)[] entries) => MapValue.Of(entries);

    private static StructuredValue S(string text) => new TextValue(text);

    private static StructuredValue L(params StructuredValue[] items) => new ListValue(items);

    [Fact]
    [Trait("Spec", "DOM-10")]
    public void Frontmatter_DisjointKeyChanges_AreMerged()
    {
        var @base = M(("title", S("Idea")), ("status", S("draft")), ("tags", L(S("a"))));
        var ours = M(("title", S("Idea")), ("status", S("done")), ("tags", L(S("a"))));
        var theirs = M(("title", S("Idea 2")), ("status", S("draft")), ("tags", L(S("a"))), ("rating", new NumberValue("5")));

        var result = new FrontmatterMerger().Merge(@base, ours, theirs);

        var merged = result.Should().BeOfType<StructuralMergeResult.Merged>().Subject.Value.Should().BeOfType<MapValue>().Subject;
        merged.Should().Be(M(("title", S("Idea 2")), ("status", S("done")), ("tags", L(S("a"))), ("rating", new NumberValue("5"))));
    }

    [Fact]
    public void Frontmatter_SameKeyChangedDifferently_IsConflict()
    {
        var @base = M(("status", S("draft")));

        var result = new FrontmatterMerger().Merge(@base, M(("status", S("done"))), M(("status", S("review"))));

        result.Should().BeOfType<StructuralMergeResult.Conflict>().Which.Paths.Should().Equal("status");
    }

    [Fact]
    public void Frontmatter_ListKeyChangedOnBothSides_IsConflict_KeyLevelOnly()
    {
        var @base = M(("tags", L(S("a"))));

        var result = new FrontmatterMerger().Merge(@base, M(("tags", L(S("a"), S("b")))), M(("tags", L(S("a"), S("c")))));

        result.Should().BeOfType<StructuralMergeResult.Conflict>();
    }

    [Fact]
    public void Frontmatter_SameChangeOnBothSides_And_Deletions()
    {
        var @base = M(("a", S("1")), ("b", S("2")), ("c", S("3")));
        var ours = M(("a", S("X")), ("c", S("3")));
        var theirs = M(("a", S("X")), ("b", S("2")));

        var result = (StructuralMergeResult.Merged)new FrontmatterMerger().Merge(@base, ours, theirs);

        result.Value.Should().Be(M(("a", S("X"))));
    }

    [Fact]
    public void Frontmatter_DeleteVersusModify_IsConflict()
    {
        var result = new FrontmatterMerger().Merge(M(("a", S("1"))), M(), M(("a", S("2"))));

        result.Should().BeOfType<StructuralMergeResult.Conflict>().Which.Paths.Should().Equal("a");
    }

    [Fact]
    public void Frontmatter_BothAddSameKeyDifferently_IsConflict()
    {
        var result = new FrontmatterMerger().Merge(M(), M(("a", S("1"))), M(("a", S("2"))));

        result.Should().BeOfType<StructuralMergeResult.Conflict>();
    }

    [Fact]
    public void Frontmatter_KeyOrder_FollowsOursThenNewTheirsKeys()
    {
        var @base = M(("a", S("1")), ("b", S("2")));
        var ours = M(("b", S("2")), ("a", S("1")), ("o", S("o")));
        var theirs = M(("a", S("1")), ("t", S("t")), ("b", S("2")));

        var merged = (MapValue)((StructuralMergeResult.Merged)new FrontmatterMerger().Merge(@base, ours, theirs)).Value;

        merged.Entries.Select(e => e.Key).Should().Equal("b", "a", "o", "t");
    }

    [Fact]
    [Trait("Spec", "PERS-1.1")]
    public void Json_NestedObjects_MergeRecursively()
    {
        var @base = M(("types", M(("status", S("text")), ("due", S("date")))));
        var ours = M(("types", M(("status", S("text")), ("due", S("datetime")))));
        var theirs = M(("types", M(("status", S("text")), ("due", S("date")), ("rating", S("number")))));

        var result = new JsonStructuralMerger().Merge(@base, ours, theirs);

        ((StructuralMergeResult.Merged)result).Value.Should().Be(M(("types", M(("status", S("text")), ("due", S("datetime")), ("rating", S("number"))))));
    }

    [Fact]
    [Trait("Spec", "PERS-1.1")]
    public void Json_Arrays_MergeByElement()
    {
        var x = M(("type", S("file")), ("path", S("x.md")));
        var y = M(("type", S("file")), ("path", S("y.md")));
        var z = M(("type", S("search")), ("query", S("tag:#a")));
        var w = M(("type", S("file")), ("path", S("w.md")));
        var @base = M(("items", L(x, y)));
        var ours = M(("items", L(x, y, z)));
        var theirs = M(("items", L(y, w)));

        var result = new JsonStructuralMerger().Merge(@base, ours, theirs);

        ((StructuralMergeResult.Merged)result).Value.Should().Be(M(("items", L(y, z, w))));
    }

    [Fact]
    public void Json_ScalarChangedDifferently_IsConflictWithPath()
    {
        var @base = M(("a", M(("b", new NumberValue("1")))));

        var result = new JsonStructuralMerger().Merge(@base, M(("a", M(("b", new NumberValue("2"))))), M(("a", M(("b", new NumberValue("3"))))));

        result.Should().BeOfType<StructuralMergeResult.Conflict>().Which.Paths.Should().Equal("a.b");
    }

    [Fact]
    public void Json_TypeChangedDifferently_IsConflict()
    {
        var result = new JsonStructuralMerger().Merge(M(("a", S("x"))), M(("a", L(S("x")))), M(("a", M(("x", NullValue.Instance)))));

        result.Should().BeOfType<StructuralMergeResult.Conflict>();
    }

    [Fact]
    public void Json_TopLevelArrays_AndScalars()
    {
        var merger = new JsonStructuralMerger();

        ((StructuralMergeResult.Merged)merger.Merge(L(S("a")), L(S("a"), S("b")), L(S("c"), S("a")))).Value.Should().Be(L(S("a"), S("b"), S("c")));
        ((StructuralMergeResult.Merged)merger.Merge(S("a"), S("a"), S("b"))).Value.Should().Be(S("b"));
        merger.Merge(S("a"), S("b"), S("c")).Should().BeOfType<StructuralMergeResult.Conflict>().Which.Paths.Should().Equal("$");
    }

    [Fact]
    public void Json_DuplicateElementsAreKeptOnce_WhenAddedOnBothSides()
    {
        var result = new JsonStructuralMerger().Merge(L(), L(S("n")), L(S("n")));

        ((StructuralMergeResult.Merged)result).Value.Should().Be(L(S("n")));
    }

    [Fact]
    public void Mergers_RejectNulls()
    {
        FluentActions.Invoking(() => new FrontmatterMerger().Merge(null!, M(), M())).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new JsonStructuralMerger().Merge(null!, M(), M())).Should().Throw<ArgumentNullException>();
    }
}
