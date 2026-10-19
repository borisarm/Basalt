using Basalt.Domain.Tags;

namespace Basalt.Domain.Tests.Tags;

public sealed class TagHierarchyTests
{
    private static Tag T(string name) => Tag.Create(name).Value;

    [Fact]
    [Trait("Spec", "F-006.1")]
    public void Build_AggregatesNestedCountsIntoParents()
    {
        var hierarchy = TagHierarchy.Build([T("trabajo/cliente-a"), T("trabajo")]);

        var trabajo = hierarchy.Roots.Should().ContainSingle().Subject;
        trabajo.Segment.Should().Be("trabajo");
        trabajo.Count.Should().Be(2);
        var child = trabajo.Children.Should().ContainSingle().Subject;
        child.Segment.Should().Be("cliente-a");
        child.FullName.Should().Be("trabajo/cliente-a");
        child.Count.Should().Be(1);
    }

    [Fact]
    public void Build_MergesCaseVariantsKeepingFirstSpelling()
    {
        var hierarchy = TagHierarchy.Build([T("Proyecto"), T("proyecto"), T("PROYECTO/x")]);

        var root = hierarchy.Roots.Should().ContainSingle().Subject;
        root.Segment.Should().Be("Proyecto");
        root.Count.Should().Be(3);
        root.Children.Single().FullName.Should().Be("Proyecto/x");
    }

    [Fact]
    public void Build_SortsRootsAndChildrenCaseInsensitively()
    {
        var hierarchy = TagHierarchy.Build([T("b"), T("A"), T("c/z"), T("c/y")]);

        hierarchy.Roots.Select(r => r.Segment).Should().Equal("A", "b", "c");
        hierarchy.Roots[2].Children.Select(c => c.Segment).Should().Equal("y", "z");
    }

    [Fact]
    public void Find_ReturnsNodeByFullName()
    {
        var hierarchy = TagHierarchy.Build([T("a/b/c")]);

        hierarchy.Find(T("A/B"))!.Count.Should().Be(1);
        hierarchy.Find(T("x")).Should().BeNull();
        hierarchy.Find(T("a/x")).Should().BeNull();
        TagHierarchy.Build([]).Roots.Should().BeEmpty();
        FluentActions.Invoking(() => TagHierarchy.Build(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => hierarchy.Find(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_OrdersCaseVariantsOfDifferentTagsDeterministically()
    {
        var hierarchy = TagHierarchy.Build([T("x/b"), T("x/B2"), T("x/a")]);

        hierarchy.Roots[0].FullName.Should().Be("x");
        hierarchy.Roots[0].Children.Select(c => c.Segment).Should().Equal("a", "b", "B2");
    }
}
