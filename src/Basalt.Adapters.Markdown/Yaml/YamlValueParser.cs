using System.Globalization;
using System.Text.RegularExpressions;
using Basalt.Application.Errors;
using Basalt.Domain.Common;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Basalt.Adapters.Markdown.Yaml;

/// <summary>A parsed YAML mapping plus, for each top-level key, the char offset (within the YAML text) of its value.</summary>
internal sealed record ParsedYaml(StructuredValue.MapValue Values, IReadOnlyDictionary<string, (int KeyLine, int ValueIndex)> Positions);

/// <summary>
/// Safe YAML → <see cref="StructuredValue"/> conversion (spec 05 §5, NFR-033): representation model only (no type
/// resolution), bounded size and depth, and type inference for null, bool, number, date and datetime.
/// </summary>
internal static partial class YamlValueParser
{
    public const int MaxBytes = 64 * 1024;
    private const int MaxDepth = 32;

    public static Result<ParsedYaml> Parse(string yaml)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(yaml) > MaxBytes)
        {
            return new Error(ErrorCodes.FrontmatterTooLarge, "Frontmatter larger than 64 KB.");
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
            {
                return new ParsedYaml(StructuredValue.MapValue.Of(), new Dictionary<string, (int, int)>());
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return new Error(ErrorCodes.FrontmatterNotAMapping, "Frontmatter must be a YAML mapping.");
            }

            var entries = new List<KeyValuePair<string, StructuredValue>>();
            var positions = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            foreach (var (keyNode, valueNode) in root.Children)
            {
                var key = keyNode is YamlScalarNode scalar ? scalar.Value ?? string.Empty : keyNode.ToString();
                entries.Add(KeyValuePair.Create(key, Convert(valueNode, 1)));
                positions[key] = ((int)keyNode.Start.Line - 1, (int)valueNode.Start.Index);
            }

            return new ParsedYaml(new StructuredValue.MapValue(entries), positions);
        }
        catch (YamlException ex)
        {
            return new Error(ErrorCodes.FrontmatterInvalidYaml, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new Error(ErrorCodes.FrontmatterInvalidYaml, ex.Message);
        }
    }

    private static StructuredValue Convert(YamlNode node, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new YamlException("Frontmatter nesting is too deep.");
        }

        return node switch
        {
            YamlSequenceNode sequence => new StructuredValue.ListValue([.. sequence.Children.Select(c => Convert(c, depth + 1))]),
            YamlMappingNode mapping => new StructuredValue.MapValue([.. mapping.Children.Select(kv =>
                KeyValuePair.Create(kv.Key is YamlScalarNode k ? k.Value ?? string.Empty : kv.Key.ToString(), Convert(kv.Value, depth + 1)))]),
            YamlScalarNode scalar => Scalar(scalar),
            _ => StructuredValue.NullValue.Instance,
        };
    }

    private static StructuredValue Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return new StructuredValue.TextValue(value);
        }

        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL")
        {
            return StructuredValue.NullValue.Instance;
        }

        if (value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            return new StructuredValue.BoolValue(value.StartsWith('t') || value.StartsWith('T'));
        }

        if (NumberRegex().IsMatch(value) && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return new StructuredValue.NumberValue(value);
        }

        if (DateRegex().IsMatch(value) && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return new StructuredValue.DateValue(value);
        }

        if (DateTimeRegex().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return new StructuredValue.DateTimeValue(value);
        }

        return new StructuredValue.TextValue(value);
    }

    [GeneratedRegex(@"^[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d+)?$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex DateTimeRegex();
}
