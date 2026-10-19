using Basalt.Domain.Common;
using Basalt.Domain.Tags;

namespace Basalt.Domain.Tests.Tags;

public sealed class TagTests
{
    [Theory]
    [InlineData("trabajo", "trabajo")]
    [InlineData("#trabajo", "trabajo")]
    [InlineData("trabajo/cliente-a", "trabajo/cliente-a")]
    [InlineData("a/", "a")]
    [InlineData("y2026", "y2026")]
    [InlineData("2026y", "2026y")]
    [InlineData("_", "_")]
    [InlineData("año", "año")]
    [InlineData("日本", "日本")]
    [InlineData("a/b/c", "a/b/c")]
    [InlineData("1/a", "1/a")]
    [InlineData("café", "café")]
    [Trait("Spec", "F-006.2")]
    public void Create_Valid(string input, string expected)
    {
        Tag.Create(input).Value.Name.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("123")]
    [InlineData("#123")]
    [InlineData("1/2")]
    [InlineData("a b")]
    [InlineData("a.b")]
    [InlineData("a#b")]
    [InlineData("a//b")]
    [InlineData("/a")]
    [InlineData("a,b")]
    [Trait("Spec", "F-006.2")]
    public void Create_Invalid(string input)
    {
        Tag.Create(input).Error.Code.Should().Be(DomainErrors.TagInvalid);
    }

    [Fact]
    public void Create_RejectsNullAndHasMessage()
    {
        FluentActions.Invoking(() => Tag.Create(null!)).Should().Throw<ArgumentNullException>();
        Tag.Create("").Error.Message.Should().Be("Tags contain letters, digits, '_', '-' and '/' and at least one non-digit.");
    }

    [Fact]
    public void Equality_IsCaseInsensitive()
    {
        var a = Tag.Create("Trabajo/Cliente").Value;
        var b = Tag.Create("trabajo/cliente").Value;

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.Equals((object)b).Should().BeTrue();
        a.Equals("trabajo").Should().BeFalse();
        a.Equals(null).Should().BeFalse();
        Tag.Create("Trabajo/Cliente").Value.ToString().Should().Be("#Trabajo/Cliente");
        b.FoldKey.Should().Be("TRABAJO/CLIENTE");
    }

    [Fact]
    public void Segments_AndAncestors()
    {
        var tag = Tag.Create("a/b/c").Value;

        tag.Segments.Should().Equal("a", "b", "c");
        tag.SelfAndAncestors().Select(t => t.Name).Should().Equal("a", "a/b", "a/b/c");
        tag.IsSelfOrDescendantOf(Tag.Create("A/B").Value).Should().BeTrue();
        tag.IsSelfOrDescendantOf(tag).Should().BeTrue();
        tag.IsSelfOrDescendantOf(Tag.Create("a/bc").Value).Should().BeFalse();
        Tag.Create("a").Value.IsSelfOrDescendantOf(tag).Should().BeFalse();
        Tag.Create("ab").Value.IsSelfOrDescendantOf(Tag.Create("a").Value).Should().BeFalse();
        FluentActions.Invoking(() => tag.IsSelfOrDescendantOf(null!)).Should().Throw<ArgumentNullException>();
    }
}
