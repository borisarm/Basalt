namespace Basalt.Domain.Common;

/// <summary>Kind of a <see cref="StructuredValue"/> (frontmatter property types, spec 05 §5).</summary>
public enum ValueKind
{
    Null,
    Bool,
    Number,
    Text,
    Date,
    DateTime,
    List,
    Map,
}

/// <summary>
/// Already-parsed YAML/JSON value used by frontmatter and structural merges. Numbers and dates keep their raw text so
/// that values round-trip exactly. Lists and maps compare deeply; maps keep key order.
/// </summary>
public abstract record StructuredValue
{
    private StructuredValue()
    {
    }

    public abstract ValueKind Kind { get; }

    public sealed record NullValue : StructuredValue
    {
        public static NullValue Instance { get; } = new();

        public override ValueKind Kind => ValueKind.Null;
    }

    public sealed record BoolValue(bool Value) : StructuredValue
    {
        public override ValueKind Kind => ValueKind.Bool;
    }

    public sealed record NumberValue(string Raw) : StructuredValue
    {
        public override ValueKind Kind => ValueKind.Number;
    }

    public sealed record TextValue(string Value) : StructuredValue
    {
        public override ValueKind Kind => ValueKind.Text;
    }

    public sealed record DateValue(string Raw) : StructuredValue
    {
        public override ValueKind Kind => ValueKind.Date;
    }

    public sealed record DateTimeValue(string Raw) : StructuredValue
    {
        public override ValueKind Kind => ValueKind.DateTime;
    }

    public sealed record ListValue(IReadOnlyList<StructuredValue> Items) : StructuredValue
    {
        public override ValueKind Kind => ValueKind.List;

        public bool Equals(ListValue? other) => other is not null && Items.SequenceEqual(other.Items);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var item in Items)
            {
                hash.Add(item);
            }

            return hash.ToHashCode();
        }
    }

    public sealed record MapValue(IReadOnlyList<KeyValuePair<string, StructuredValue>> Entries) : StructuredValue
    {
        public override ValueKind Kind => ValueKind.Map;

        public IEnumerable<string> Keys => Entries.Select(e => e.Key);

        public static MapValue Of(params (string Key, StructuredValue Value)[] entries) =>
            new([.. entries.Select(e => KeyValuePair.Create(e.Key, e.Value))]);

        public StructuredValue? TryGet(string key) =>
            Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal)).Value;

        /// <summary>Replaces the value of an existing key in place, or appends the key.</summary>
        public MapValue With(string key, StructuredValue value)
        {
            var entries = Entries.ToList();
            var index = entries.FindIndex(e => string.Equals(e.Key, key, StringComparison.Ordinal));
            if (index >= 0)
            {
                entries[index] = KeyValuePair.Create(key, value);
            }
            else
            {
                entries.Add(KeyValuePair.Create(key, value));
            }

            return new MapValue(entries);
        }

        public MapValue Without(string key) => new([.. Entries.Where(e => !string.Equals(e.Key, key, StringComparison.Ordinal))]);

        public bool Equals(MapValue? other) =>
            other is not null && Entries.Count == other.Entries.Count
            && Entries.Zip(other.Entries).All(p => p.First.Key == p.Second.Key && Equals(p.First.Value, p.Second.Value));

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var (key, value) in Entries)
            {
                hash.Add(key);
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
