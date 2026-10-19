using Basalt.Domain.Common;

namespace Basalt.Adapters.Markdown.Yaml;

/// <summary>
/// Canonical YAML rendering of <see cref="StructuredValue"/>. A scalar is written plain only when parsing it back with
/// <see cref="YamlValueParser"/> yields exactly the same value; otherwise it is quoted.
/// </summary>
internal static class YamlEmitter
{
    public static IReadOnlyList<string> Entry(string key, StructuredValue value, int indent = 0, string listPrefix = "  - ")
    {
        var pad = new string(' ', indent);
        var renderedKey = Key(key);
        switch (value)
        {
            case StructuredValue.MapValue { Entries.Count: 0 }:
                return [$"{pad}{renderedKey}: {{}}"];
            case StructuredValue.MapValue map:
                return [$"{pad}{renderedKey}:", .. map.Entries.SelectMany(e => Entry(e.Key, e.Value, indent + 2))];
            case StructuredValue.ListValue { Items.Count: 0 }:
                return [$"{pad}{renderedKey}: []"];
            case StructuredValue.ListValue list:
                return [$"{pad}{renderedKey}:", .. list.Items.SelectMany(item => Item(item, pad + listPrefix))];
            case StructuredValue.NullValue:
                return [$"{pad}{renderedKey}:"];
            default:
                return [$"{pad}{renderedKey}: {Scalar(value)}"];
        }
    }

    public static string FlowList(StructuredValue.ListValue list) =>
        "[" + string.Join(", ", list.Items.Select(i => Scalar(i, flow: true))) + "]";

    public static bool IsScalar(StructuredValue value) => value is not (StructuredValue.ListValue or StructuredValue.MapValue);

    public static string Scalar(StructuredValue value, bool flow = false) => value switch
    {
        StructuredValue.NullValue => flow ? "null" : string.Empty,
        StructuredValue.BoolValue b => b.Value ? "true" : "false",
        StructuredValue.NumberValue n => n.Raw,
        StructuredValue.DateValue d => d.Raw,
        StructuredValue.DateTimeValue d => d.Raw,
        StructuredValue.TextValue t => Text(t.Value, flow),
        StructuredValue.ListValue l => FlowList(l),
        _ => "{}",
    };

    private static IEnumerable<string> Item(StructuredValue item, string prefix)
    {
        if (item is StructuredValue.MapValue { Entries.Count: > 0 } map)
        {
            var continuation = new string(' ', prefix.Length);
            var lines = map.Entries.SelectMany(e => Entry(e.Key, e.Value)).ToList();
            yield return prefix + lines[0];
            foreach (var line in lines.Skip(1))
            {
                yield return continuation + line;
            }

            yield break;
        }

        yield return prefix + Scalar(item, flow: item is StructuredValue.ListValue);
    }

    private static string Key(string key)
    {
        var parsed = YamlValueParser.Parse($"{key}: x");
        return parsed.IsSuccess && parsed.Value.Values.Keys.SequenceEqual([key]) && !key.Contains('\n', StringComparison.Ordinal)
            ? key
            : DoubleQuoted(key);
    }

    private static string Text(string text, bool flow)
    {
        if (RoundTrips(text, text, flow))
        {
            return text;
        }

        if (text.Contains('"', StringComparison.Ordinal) && !text.Any(char.IsControl))
        {
            var single = "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
            if (RoundTrips(single, text, flow))
            {
                return single;
            }
        }

        return DoubleQuoted(text);
    }

    private static bool RoundTrips(string rendered, string expected, bool flow)
    {
        if (rendered.Length == 0 || rendered.Contains('\n', StringComparison.Ordinal) || rendered.Contains('\r', StringComparison.Ordinal))
        {
            return false;
        }

        var parsed = YamlValueParser.Parse(flow ? $"k: [{rendered}]" : $"k: {rendered}");
        if (parsed.IsFailure)
        {
            return false;
        }

        var value = parsed.Value.Values.TryGet("k");
        var expectedValue = new StructuredValue.TextValue(expected);
        return flow
            ? value is StructuredValue.ListValue { Items.Count: 1 } list && Equals(list.Items[0], expectedValue)
            : Equals(value, expectedValue);
    }

    private static string DoubleQuoted(string text)
    {
        var builder = new System.Text.StringBuilder("\"");
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }
}
