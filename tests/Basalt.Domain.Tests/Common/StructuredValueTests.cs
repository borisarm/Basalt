using Basalt.Domain.Common;
using static Basalt.Domain.Common.StructuredValue;

namespace Basalt.Domain.Tests.Common;

public sealed class StructuredValueTests
{
    [Fact]
    public void Lists_AndMaps_CompareDeeply()
    {
        new ListValue([new TextValue("a"), new NumberValue("1")]).Should().Be(new ListValue([new TextValue("a"), new NumberValue("1")]));
        new ListValue([new TextValue("a")]).Should().NotBe(new ListValue([new TextValue("b")]));
        MapValue.Of(("a", new BoolValue(true))).Should().Be(MapValue.Of(("a", new BoolValue(true))));
        MapValue.Of(("a", new BoolValue(true))).Should().NotBe(MapValue.Of(("a", new BoolValue(false))));
        MapValue.Of(("a", new BoolValue(true))).GetHashCode().Should().Be(MapValue.Of(("a", new BoolValue(true))).GetHashCode());
        new ListValue([new TextValue("a")]).GetHashCode().Should().Be(new ListValue([new TextValue("a")]).GetHashCode());
        MapValue.Of(("a", NullValue.Instance), ("b", NullValue.Instance)).Should().NotBe(MapValue.Of(("b", NullValue.Instance), ("a", NullValue.Instance)));
        new ListValue([]).Equals(null).Should().BeFalse();
        MapValue.Of().Equals(null).Should().BeFalse();
    }

    [Fact]
    public void Map_LookupAndKeys()
    {
        var map = MapValue.Of(("a", new TextValue("1")), ("b", new DateValue("2026-10-19")));

        map.TryGet("a").Should().Be(new TextValue("1"));
        map.TryGet("z").Should().BeNull();
        map.Keys.Should().Equal("a", "b");
        map.With("c", new DateTimeValue("2026-10-19T10:00:00")).Keys.Should().Equal("a", "b", "c");
        map.With("a", new TextValue("2")).TryGet("a").Should().Be(new TextValue("2"));
        map.Without("a").Keys.Should().Equal("b");
    }

    [Fact]
    public void Kinds_AreExposed()
    {
        NullValue.Instance.Kind.Should().Be(ValueKind.Null);
        new BoolValue(true).Kind.Should().Be(ValueKind.Bool);
        new NumberValue("1").Kind.Should().Be(ValueKind.Number);
        new TextValue("x").Kind.Should().Be(ValueKind.Text);
        new DateValue("2026-01-01").Kind.Should().Be(ValueKind.Date);
        new DateTimeValue("2026-01-01T00:00").Kind.Should().Be(ValueKind.DateTime);
        new ListValue([]).Kind.Should().Be(ValueKind.List);
        MapValue.Of().Kind.Should().Be(ValueKind.Map);
    }
}
