using System.Text.RegularExpressions;
using Basalt.Adapters.Markdown.Syntax;
using Basalt.Adapters.Markdown.Yaml;
using Basalt.Domain.Common;
using Basalt.Domain.Links;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Tags;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using DomainSpan = Basalt.Domain.Notes.SourceSpan;
using IMarkdownAnalyzer = Basalt.Application.Ports.Markdown.IMarkdownAnalyzer;

namespace Basalt.Adapters.Markdown;

/// <summary>Markdig-based <see cref="IMarkdownAnalyzer"/> (spec 05).</summary>
internal sealed partial class MarkdigAnalyzer : IMarkdownAnalyzer
{
    public NoteMetadata Analyze(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var document = Markdig.Markdown.Parse(EmptyFrontmatter.Mask(content, out var emptyFrontmatterLength), MarkdownPipelineFactory.Pipeline);
        var lines = new LineIndex(content);
        var state = new State(content, lines);
        if (emptyFrontmatterLength > 0)
        {
            state.Frontmatter = new Frontmatter(StructuredValue.MapValue.Of(), new DomainSpan(0, emptyFrontmatterLength, 0, 0));
        }

        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case YamlFrontMatterBlock yaml:
                    state.Frontmatter = ReadFrontmatter(yaml, content, lines, state);
                    break;
                case HeadingBlock heading:
                    AddHeading(heading, content, state);
                    break;
                case ParagraphBlock paragraph when paragraph.Parent is not TableCell:
                    AddBlockId(paragraph, content, lines, state);
                    AddTask(paragraph, content, lines, state);
                    break;
                case Table table:
                    AddTableBlockIds(table, content, lines, state);
                    break;
                case WikiLinkInline wiki:
                    (wiki.Link.IsEmbed ? state.Embeds : state.Links).Add(wiki.Link);
                    state.Words.Add(VisibleText(wiki.Link));
                    break;
                case TagInline tag:
                    state.Tags.Add(new TagOccurrence(tag.Tag, TagSource.Text, tag.Line));
                    state.Words.Add(tag.Tag.Name);
                    break;
                case LinkInline link when !link.IsAutoLink:
                    AddMarkdownLink(link, content, lines, state);
                    break;
                case LiteralInline literal when !IsInsideIgnoredInline(literal):
                    state.Words.Add(literal.Content.ToString());
                    break;
            }
        }

        return new NoteMetadata(
            state.Frontmatter,
            state.Headings,
            state.Links,
            state.Embeds,
            state.Tags,
            state.Blocks,
            state.Tasks,
            state.Aliases,
            WordCounter.Count(state.Words));
    }

    private static bool IsInsideIgnoredInline(LiteralInline literal) =>
        literal.Parent is LinkInline { IsImage: true } || literal.FirstParentOfType<CodeInline>() is not null;

    private static string VisibleText(Link link) => link.Alias ?? link.RawTarget[(link.RawTarget.LastIndexOf('/') + 1)..];

    private static Frontmatter ReadFrontmatter(YamlFrontMatterBlock block, string content, LineIndex lines, State state)
    {
        var span = new DomainSpan(block.Span.Start, block.Span.Length, block.Line, 0);
        var yamlLines = block.Lines.Lines.Take(block.Lines.Count).ToList();
        if (yamlLines.Count == 0)
        {
            return new Frontmatter(StructuredValue.MapValue.Of(), span);
        }

        var yamlStart = yamlLines[0].Position;
        var yamlEnd = yamlLines[^1].Slice.End + 1;
        var yaml = content[yamlStart..Math.Max(yamlStart, yamlEnd)];
        var parsed = YamlValueParser.Parse(yaml);
        if (parsed.IsFailure)
        {
            return new Frontmatter(StructuredValue.MapValue.Of(), span, parsed.Error.Message);
        }

        var values = parsed.Value.Values;
        var firstLine = block.Line + 1;
        foreach (var (key, value) in values.Entries)
        {
            var (keyLine, valueIndex) = parsed.Value.Positions[key];
            var lowerKey = key.ToLowerInvariant();
            if (lowerKey is "tags" or "tag")
            {
                foreach (var name in SplitList(value))
                {
                    if (Tag.Create(name) is { IsSuccess: true } tag)
                    {
                        state.Tags.Add(new TagOccurrence(tag.Value, TagSource.Frontmatter, firstLine + keyLine));
                    }
                }
            }
            else if (lowerKey is "aliases" or "alias")
            {
                state.Aliases.AddRange(SplitList(value, splitSpaces: false));
            }

            AddFrontmatterLinks(value, yamlStart + valueIndex, content, lines, state);
        }

        return new Frontmatter(values, span);
    }

    private static IEnumerable<string> SplitList(StructuredValue value, bool splitSpaces = true) => value switch
    {
        StructuredValue.ListValue list => list.Items.OfType<StructuredValue.TextValue>().Select(t => t.Value.Trim()).Where(t => t.Length > 0),
        StructuredValue.TextValue text => text.Value
            .Split(splitSpaces ? [',', ' '] : [','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    // Spec 05 §1: a frontmatter string that is exactly "[[...]]" is a link.
    private static void AddFrontmatterLinks(StructuredValue value, int searchFrom, string content, LineIndex lines, State state)
    {
        var strings = value switch
        {
            StructuredValue.TextValue t => [t.Value],
            StructuredValue.ListValue l => l.Items.OfType<StructuredValue.TextValue>().Select(t => t.Value).ToList(),
            _ => new List<string>(),
        };
        var position = searchFrom;
        foreach (var text in strings)
        {
            var trimmed = text.Trim();
            if (!trimmed.StartsWith("[[", StringComparison.Ordinal) || !trimmed.EndsWith("]]", StringComparison.Ordinal))
            {
                continue;
            }

            var offset = content.IndexOf(trimmed, position, StringComparison.Ordinal);
            if (offset < 0)
            {
                continue;
            }

            var (line, column) = lines.PositionOf(offset);
            if (WikiLinkSyntax.TryParse(content, offset, line, column) is { } link && link.Span.Length == trimmed.Length)
            {
                state.Links.Add(link with { Kind = LinkKind.FrontmatterLink });
            }

            position = offset + trimmed.Length;
        }
    }

    private static void AddHeading(HeadingBlock heading, string content, State state)
    {
        if (heading.Lines.Count == 0 && heading.Inline is null)
        {
            return;
        }

        var raw = heading.Inline is null ? string.Empty : RawText(heading.Inline, content).Trim();
        var text = BlockIdSuffixRegex().Replace(raw, string.Empty);
        if (Heading.Create(heading.Level, text, heading.Line) is { IsSuccess: true } created)
        {
            state.Headings.Add(created.Value);
        }
    }

    private static string RawText(ContainerInline inline, string content)
    {
        var first = inline.FirstChild;
        var last = inline.LastChild;
        if (first is null || last is null)
        {
            return string.Empty;
        }

        var start = first.Span.Start;
        var end = last.Span.End;
        return start >= 0 && end >= start && end < content.Length ? content[start..(end + 1)] : string.Empty;
    }

    private static void AddBlockId(ParagraphBlock paragraph, string content, LineIndex lines, State state)
    {
        // Markdig releases paragraph lines after inline parsing: work from source positions.
        var lastLine = lines.PositionOf(Math.Clamp(paragraph.Span.End, 0, Math.Max(0, content.Length - 1))).Line;
        var text = lines.LineText(lastLine);
        var match = BlockIdSuffixRegex().Match(text);
        if (!match.Success && lastLine == paragraph.Line)
        {
            match = OwnLineBlockIdRegex().Match(text);
        }

        if (match.Success && BlockId.Create(match.Groups["id"].Value) is { IsSuccess: true } id)
        {
            state.Blocks.Add(new BlockReference(id.Value, lastLine));
        }
    }

    private static void AddTableBlockIds(Table table, string content, LineIndex lines, State state)
    {
        var first = table.Line;
        var lastLine = lines.PositionOf(Math.Min(table.Span.End, content.Length - 1)).Line;
        for (var line = first; line <= lastLine; line++)
        {
            var match = BlockIdSuffixRegex().Match(lines.LineText(line));
            if (match.Success && BlockId.Create(match.Groups["id"].Value) is { IsSuccess: true } id)
            {
                state.Blocks.Add(new BlockReference(id.Value, line));
            }
        }
    }

    private static void AddTask(ParagraphBlock paragraph, string content, LineIndex lines, State state)
    {
        if (paragraph.Inline?.FirstChild is not TaskList task || paragraph.Parent is not ListItemBlock)
        {
            return;
        }

        var status = content[task.Span.Start + 1];
        var line = lines.LineText(paragraph.Line);
        var bracket = line.IndexOf(']', StringComparison.Ordinal);
        var text = bracket >= 0 ? line[(bracket + 1)..].Trim() : line.Trim();
        state.Tasks.Add(new TaskItem(paragraph.Line, status == 'X' ? 'x' : status, text));
    }

    private static void AddMarkdownLink(LinkInline link, string content, LineIndex lines, State state)
    {
        if (link.UrlSpan.IsEmpty || link.UrlSpan.Start < 0)
        {
            return;
        }

        var rawStart = link.UrlSpan.Start;
        var rawEnd = link.UrlSpan.End;
        var raw = content[rawStart..(rawEnd + 1)];
        if (raw.StartsWith('<') && raw.EndsWith('>'))
        {
            raw = raw[1..^1];
            rawStart++;
        }

        if (raw.Length == 0 || LinkResolver.IsExternal(raw) || raw.StartsWith('#') && raw.Length == 1)
        {
            return;
        }

        var hash = raw.IndexOf('#', StringComparison.Ordinal);
        var target = hash < 0 ? raw : raw[..hash];
        var subpath = hash < 0 || hash == raw.Length - 1 ? null : raw[(hash + 1)..];
        var subpathKind = subpath is null ? SubpathKind.None : subpath.StartsWith('^') ? SubpathKind.Block : SubpathKind.Heading;
        var (line, column) = lines.PositionOf(link.Span.Start);
        var (targetLine, targetColumn) = lines.PositionOf(rawStart);
        var label = link.FirstChild is null ? string.Empty : string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
        var result = new Link(
            link.IsImage ? LinkKind.MarkdownEmbed : LinkKind.MarkdownLink,
            target,
            subpath,
            subpathKind,
            label.Length == 0 ? null : label,
            new DomainSpan(link.Span.Start, link.Span.Length, line, column),
            new DomainSpan(rawStart, target.Length, targetLine, targetColumn));
        (result.IsEmbed ? state.Embeds : state.Links).Add(result);
    }

    // Spec 05 §6: " ^id" at the end of the last line of a paragraph, list item or table row.
    [GeneratedRegex(@"\s\^(?<id>[A-Za-z0-9-]+)\s*$")]
    private static partial Regex BlockIdSuffixRegex();

    // "^id" on its own line after a block (list, quote, table).
    [GeneratedRegex(@"^\s*\^(?<id>[A-Za-z0-9-]+)\s*$")]
    private static partial Regex OwnLineBlockIdRegex();

    private sealed class State(string content, LineIndex lines)
    {
        public string Content { get; } = content;

        public LineIndex Lines { get; } = lines;

        public Frontmatter? Frontmatter { get; set; }

        public List<Heading> Headings { get; } = [];

        public List<Link> Links { get; } = [];

        public List<Link> Embeds { get; } = [];

        public List<TagOccurrence> Tags { get; } = [];

        public List<BlockReference> Blocks { get; } = [];

        public List<TaskItem> Tasks { get; } = [];

        public List<string> Aliases { get; } = [];

        public List<string> Words { get; } = [];
    }
}
