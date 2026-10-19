using Basalt.Adapters.Markdown.Yaml;
using Basalt.Application.Ports.Markdown;
using Basalt.Domain.Common;
using Basalt.Domain.Metadata;
using Basalt.Domain.Text;

namespace Basalt.Adapters.Markdown;

/// <summary>Line-based, format-preserving <see cref="IFrontmatterEditor"/> (spec 05 §5).</summary>
internal sealed class YamlFrontmatterEditor : IFrontmatterEditor
{
    public string SetProperty(string content, string key, StructuredValue value)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var document = Document.Read(content);
        if (document.Block is not { } block)
        {
            var created = Render(StructuredValue.MapValue.Of((key, value)), document.LineEnding);
            return created + content;
        }

        var parsed = YamlValueParser.Parse(block.Yaml);
        if (parsed.IsFailure)
        {
            // Never rewrite YAML we cannot understand: the user fixes it in the source editor.
            return content;
        }

        if (value is StructuredValue.MapValue { Entries.Count: > 0 } || (value is StructuredValue.ListValue list && list.Items.Any(i => !YamlEmitter.IsScalar(i))))
        {
            return document.ReplaceBlock(Render(parsed.Value.Values.With(key, value), document.LineEnding));
        }

        var range = block.FindKey(key);
        if (range is null)
        {
            return document.InsertBeforeClosing(YamlEmitter.Entry(key, value));
        }

        return document.ReplaceLines(range.Value.Start, range.Value.End, RenderEntry(key, value, block, range.Value));
    }

    public string RemoveProperty(string content, string key)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(key);
        var document = Document.Read(content);
        if (document.Block is not { } block || YamlValueParser.Parse(block.Yaml).IsFailure || block.FindKey(key) is not { } range)
        {
            return content;
        }

        return document.ReplaceLines(range.Start, range.End, []);
    }

    public Result<Frontmatter> Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return YamlValueParser.Parse(yaml).Map(p => new Frontmatter(p.Values, new Domain.Notes.SourceSpan(0, yaml.Length, 0, 0)));
    }

    public string Render(StructuredValue.MapValue values, string lineEnding = "\n")
    {
        ArgumentNullException.ThrowIfNull(values);
        var lines = values.Entries.SelectMany(e => YamlEmitter.Entry(e.Key, e.Value));
        return string.Concat(lines.Prepend("---").Append("---").Select(l => l + lineEnding));
    }

    // Keeps the list style of the existing value: flow ("[a, b]"), compact ("- a" at column 0) or indented.
    private static IReadOnlyList<string> RenderEntry(string key, StructuredValue value, Block block, (int Start, int End) range)
    {
        if (value is not StructuredValue.ListValue { Items.Count: > 0 } list)
        {
            return YamlEmitter.Entry(key, value);
        }

        var keyLine = TextLines.WithoutTerminator(block.Lines[range.Start]);
        if (keyLine.TrimEnd().EndsWith(']') && keyLine.Contains('[', StringComparison.Ordinal))
        {
            var prefix = keyLine[..(keyLine.IndexOf(':', StringComparison.Ordinal) + 1)];
            return [$"{prefix} {YamlEmitter.FlowList(list)}"];
        }

        var firstItem = block.Lines.Skip(range.Start + 1).Take(range.End - range.Start)
            .Select(TextLines.WithoutTerminator)
            .FirstOrDefault(l => l.TrimStart().StartsWith('-'));
        var listPrefix = firstItem is null ? "  - " : firstItem[..(firstItem.IndexOf('-', StringComparison.Ordinal) + 1)] + " ";
        return YamlEmitter.Entry(key, value, 0, listPrefix);
    }

    private sealed record Block(IReadOnlyList<string> Lines, int FirstYamlLine, int ClosingLine)
    {
        public string Yaml => string.Concat(Lines.Skip(FirstYamlLine).Take(ClosingLine - FirstYamlLine));

        /// <summary>Document line range (inclusive) of a top-level key and its continuation lines.</summary>
        public (int Start, int End)? FindKey(string key)
        {
            for (var i = FirstYamlLine; i < ClosingLine; i++)
            {
                if (KeyOf(TextLines.WithoutTerminator(Lines[i])) != key)
                {
                    continue;
                }

                var end = i;
                for (var j = i + 1; j < ClosingLine; j++)
                {
                    var line = TextLines.WithoutTerminator(Lines[j]);
                    if (line.Length > 0 && (char.IsWhiteSpace(line[0]) || line == "-" || line.StartsWith("- ", StringComparison.Ordinal)))
                    {
                        end = line.Trim().Length == 0 ? end : j;
                    }
                    else if (line.Trim().Length > 0 && !line.StartsWith('#'))
                    {
                        break;
                    }
                }

                return (i, end);
            }

            return null;
        }

        private static string? KeyOf(string line)
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-')
            {
                return null;
            }

            var parsed = YamlValueParser.Parse(line.EndsWith(':') ? line + " x" : line);
            return parsed.IsSuccess && parsed.Value.Values.Entries.Count == 1 ? parsed.Value.Values.Entries[0].Key : RawKey(line);
        }

        // Fallback for lines whose value only parses together with its continuation lines ("key: |").
        private static string? RawKey(string line)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            return colon > 0 ? line[..colon].Trim().Trim('"', '\'') : null;
        }
    }

    private sealed class Document
    {
        private Document(IReadOnlyList<string> lines, string lineEnding, Block? block)
        {
            Lines = lines;
            LineEnding = lineEnding;
            Block = block;
        }

        public IReadOnlyList<string> Lines { get; }

        public string LineEnding { get; }

        public Block? Block { get; }

        public static Document Read(string content)
        {
            var lines = TextLines.Split(content);
            var crlf = lines.Count(l => l.EndsWith("\r\n", StringComparison.Ordinal));
            var eol = crlf > lines.Count(l => l.EndsWith('\n')) - crlf ? "\r\n" : "\n";
            if (lines.Count == 0 || TextLines.WithoutTerminator(lines[0]).TrimEnd() != "---")
            {
                return new Document(lines, eol, null);
            }

            for (var i = 1; i < lines.Count; i++)
            {
                if (TextLines.WithoutTerminator(lines[i]).TrimEnd() is "---" or "...")
                {
                    return new Document(lines, eol, new Block(lines, 1, i));
                }
            }

            return new Document(lines, eol, null);
        }

        public string ReplaceLines(int start, int end, IReadOnlyList<string> replacement) =>
            string.Concat(Lines.Take(start).Concat(replacement.Select(l => l + LineEnding)).Concat(Lines.Skip(end + 1)));

        public string InsertBeforeClosing(IReadOnlyList<string> lines) =>
            ReplaceLines(Block!.ClosingLine, Block.ClosingLine - 1, lines);

        public string ReplaceBlock(string rendered) =>
            rendered + string.Concat(Lines.Skip(Block!.ClosingLine + 1));
    }
}
