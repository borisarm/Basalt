using System.Text.Json;
using System.Text.Json.Nodes;
using Basalt.Domain.Common;

namespace Basalt.Adapters.Index;

/// <summary>
/// Lossless JSON for <see cref="StructuredValue"/> in the index: scalars carry a type tag ({"t":"n","v":"4.5"}) so that
/// numbers and dates keep their raw text; maps keep key order.
/// </summary>
internal static class StructuredValueJson
{
    public static string Serialize(StructuredValue value) => ToNode(value).ToJsonString();

    public static StructuredValue Deserialize(string json) => FromNode(JsonNode.Parse(json)!);

    /// <summary>Plain text used by property search (<c>[key:value]</c>).</summary>
    public static string? Text(StructuredValue value) => value switch
    {
        StructuredValue.NullValue => null,
        StructuredValue.BoolValue b => b.Value ? "true" : "false",
        StructuredValue.NumberValue n => n.Raw,
        StructuredValue.TextValue t => t.Value,
        StructuredValue.DateValue d => d.Raw,
        StructuredValue.DateTimeValue d => d.Raw,
        StructuredValue.ListValue l => string.Join(", ", l.Items.Select(Text).OfType<string>()),
        _ => null,
    };

    private static JsonNode ToNode(StructuredValue value) => value switch
    {
        StructuredValue.NullValue => new JsonObject { ["t"] = "z" },
        StructuredValue.BoolValue b => new JsonObject { ["t"] = "b", ["v"] = b.Value },
        StructuredValue.NumberValue n => new JsonObject { ["t"] = "n", ["v"] = n.Raw },
        StructuredValue.TextValue t => new JsonObject { ["t"] = "s", ["v"] = t.Value },
        StructuredValue.DateValue d => new JsonObject { ["t"] = "d", ["v"] = d.Raw },
        StructuredValue.DateTimeValue d => new JsonObject { ["t"] = "dt", ["v"] = d.Raw },
        StructuredValue.ListValue l => new JsonObject { ["t"] = "l", ["v"] = new JsonArray([.. l.Items.Select(ToNode)]) },
        StructuredValue.MapValue m => new JsonObject
        {
            ["t"] = "m",
            ["v"] = new JsonArray([.. m.Entries.Select(e => (JsonNode)new JsonArray(e.Key, ToNode(e.Value)))]),
        },
        _ => throw new JsonException("Unknown value kind."),
    };

    private static StructuredValue FromNode(JsonNode node)
    {
        var type = node["t"]!.GetValue<string>();
        var v = node["v"];
        return type switch
        {
            "z" => StructuredValue.NullValue.Instance,
            "b" => new StructuredValue.BoolValue(v!.GetValue<bool>()),
            "n" => new StructuredValue.NumberValue(v!.GetValue<string>()),
            "s" => new StructuredValue.TextValue(v!.GetValue<string>()),
            "d" => new StructuredValue.DateValue(v!.GetValue<string>()),
            "dt" => new StructuredValue.DateTimeValue(v!.GetValue<string>()),
            "l" => new StructuredValue.ListValue([.. v!.AsArray().Select(i => FromNode(i!))]),
            "m" => new StructuredValue.MapValue([.. v!.AsArray().Select(e => KeyValuePair.Create(e![0]!.GetValue<string>(), FromNode(e[1]!)))]),
            _ => throw new JsonException($"Unknown value tag '{type}'."),
        };
    }
}
