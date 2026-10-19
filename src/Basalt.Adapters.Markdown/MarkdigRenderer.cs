using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Basalt.Domain.Links;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using R = Basalt.Application.Ports.Markdown;
using S = Basalt.Adapters.Markdown.Syntax;

namespace Basalt.Adapters.Markdown;

/// <summary>Markdig-based <see cref="R.IMarkdownRenderer"/>: converts the AST into the reading-mode model (spec 07 §6).</summary>
internal sealed partial class MarkdigRenderer : R.IMarkdownRenderer
{
    private static readonly Dictionary<string, string> CalloutAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["summary"] = "abstract",
        ["tldr"] = "abstract",
        ["hint"] = "tip",
        ["important"] = "tip",
        ["check"] = "success",
        ["done"] = "success",
        ["help"] = "question",
        ["faq"] = "question",
        ["caution"] = "warning",
        ["attention"] = "warning",
        ["fail"] = "failure",
        ["missing"] = "failure",
        ["error"] = "danger",
        ["cite"] = "quote",
    };

    public R.RenderDocument Render(string content, R.RenderContext context)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        var document = Markdig.Markdown.Parse(EmptyFrontmatter.Mask(content, out _), MarkdownPipelineFactory.Pipeline);
        return new R.RenderDocument(Blocks(document, content));
    }

    private static List<R.RenderBlock> Blocks(ContainerBlock container, string content)
    {
        var result = new List<R.RenderBlock>();
        foreach (var block in container.Select(b => Block(b, content)).OfType<R.RenderBlock>())
        {
            // Spec 05 §6: "^id" on its own line names the previous block.
            if (block is R.ParagraphBlock { Inlines: [R.TextInline text] } && result.Count > 0
                && OwnLineBlockIdRegex().Match(text.Text) is { Success: true } match)
            {
                result[^1] = result[^1] with { BlockId = match.Groups["id"].Value };
                continue;
            }

            result.Add(block);
        }

        return result;
    }

    private static R.RenderBlock? Block(Block block, string content) => block switch
    {
        Markdig.Syntax.HeadingBlock heading => WithBlockId(new R.HeadingBlock(heading.Level, Inlines(heading.Inline)) { Line = heading.Line }),
        Markdig.Syntax.ParagraphBlock paragraph => Paragraph(paragraph),
        Markdig.Syntax.ListBlock list => new R.ListBlock(
            list.IsOrdered,
            int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ? start : 1,
            [.. list.OfType<Markdig.Syntax.ListItemBlock>().Select(item => ListItem(item, content))])
        { Line = list.Line },
        Markdig.Syntax.QuoteBlock quote => Quote(quote, content),
        MathBlock math => new R.CodeBlock("math", Lines(math)) { Line = math.Line },
        Markdig.Extensions.Yaml.YamlFrontMatterBlock => null,
        FencedCodeBlock fenced => new R.CodeBlock(string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info, Lines(fenced)) { Line = fenced.Line },
        Markdig.Syntax.CodeBlock code => new R.CodeBlock(null, Lines(code)) { Line = code.Line },
        HtmlBlock html => new R.CodeBlock("html", Lines(html)) { Line = html.Line },
        ThematicBreakBlock rule => new R.ThematicBreakBlock { Line = rule.Line },
        Table table => TableBlock(table),
        FootnoteGroup group => new R.FootnotesBlock([.. group.OfType<Footnote>().Select(f => new R.Footnote(f.Order.ToString(CultureInfo.InvariantCulture), Blocks(f, content)))]) { Line = group.Line },
        _ => null, // frontmatter, comments, link reference definitions
    };

    private static R.RenderBlock Paragraph(Markdig.Syntax.ParagraphBlock paragraph)
    {
        var inlines = Inlines(paragraph.Inline);
        if (inlines is [R.EmbedInline embed])
        {
            return WithBlockId(new R.EmbedBlock(embed) { Line = paragraph.Line });
        }

        if (inlines is [R.ImageInline image])
        {
            return WithBlockId(new R.ImageBlock(image.Source, image.IsExternal, image.AltText) { Line = paragraph.Line });
        }

        return WithBlockId(new R.ParagraphBlock(inlines) { Line = paragraph.Line });
    }

    private static R.ListItemBlock ListItem(Markdig.Syntax.ListItemBlock item, string content)
    {
        char? task = null;
        if (item.FirstOrDefault() is Markdig.Syntax.ParagraphBlock { Inline.FirstChild: TaskList taskList })
        {
            var status = content[taskList.Span.Start + 1];
            task = status is 'X' ? 'x' : status;
        }

        var blocks = Blocks(item, content);
        if (task is not null && blocks is [R.ParagraphBlock { Inlines: [R.TextInline first, ..] } paragraph, ..])
        {
            blocks[0] = paragraph with { Inlines = [new R.TextInline(first.Text.TrimStart()), .. paragraph.Inlines.Skip(1)] };
        }

        return new R.ListItemBlock(blocks, task) { Line = item.Line };
    }

    private static R.RenderBlock Quote(Markdig.Syntax.QuoteBlock quote, string content)
    {
        var blocks = Blocks(quote, content);
        if (blocks.FirstOrDefault() is R.ParagraphBlock { Inlines: [R.TextInline first, ..] } paragraph
            && CalloutRegex().Match(first.Text) is { Success: true } match)
        {
            var type = match.Groups["type"].Value.ToLowerInvariant();
            type = CalloutAliases.GetValueOrDefault(type, type);
            var folding = match.Groups["fold"].Value switch { "-" => R.CalloutFolding.Collapsed, "+" => R.CalloutFolding.Expanded, _ => R.CalloutFolding.None };

            // The title is the rest of the first line; following lines of that paragraph belong to the body.
            var rest = new List<R.RenderInline>();
            var title = match.Groups["title"].Value.Trim();
            var titleInlines = new List<R.RenderInline>();
            if (title.Length > 0)
            {
                titleInlines.Add(new R.TextInline(title));
            }

            var inTitle = true;
            foreach (var inline in paragraph.Inlines.Skip(1))
            {
                if (inTitle && inline is R.LineBreakInline)
                {
                    inTitle = false;
                    continue;
                }

                (inTitle ? titleInlines : rest).Add(inline);
            }

            var body = rest.Count > 0 ? [new R.ParagraphBlock(rest) { Line = paragraph.Line + 1 }, .. blocks.Skip(1)] : blocks.Skip(1).ToList();
            if (titleInlines.Count == 0)
            {
                titleInlines.Add(new R.TextInline(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(type)));
            }

            return new R.CalloutBlock(type, titleInlines, folding, body) { Line = quote.Line };
        }

        return new R.QuoteBlock(blocks) { Line = quote.Line };
    }

    private static R.TableBlock TableBlock(Table table)
    {
        var alignments = table.ColumnDefinitions.Select(c => c.Alignment switch
        {
            TableColumnAlign.Left => R.ColumnAlignment.Left,
            TableColumnAlign.Center => R.ColumnAlignment.Center,
            TableColumnAlign.Right => R.ColumnAlignment.Right,
            _ => R.ColumnAlignment.None,
        }).ToList();
        var rows = table.OfType<TableRow>().ToList();
        static IReadOnlyList<IReadOnlyList<R.RenderInline>> Cells(TableRow row) =>
            [.. row.OfType<TableCell>().Select(cell => (IReadOnlyList<R.RenderInline>)[.. cell.OfType<Markdig.Syntax.ParagraphBlock>().SelectMany(p => Inlines(p.Inline))])];
        var header = rows.FirstOrDefault(r => r.IsHeader) is { } h ? Cells(h) : [];
        return new R.TableBlock(alignments, header, [.. rows.Where(r => !r.IsHeader).Select(Cells)]) { Line = table.Line };
    }

    private static T WithBlockId<T>(T block)
        where T : R.RenderBlock
    {
        var inlines = block switch
        {
            R.ParagraphBlock p => p.Inlines,
            R.HeadingBlock h => h.Inlines,
            _ => null,
        };
        if (inlines is not [.., R.TextInline last] || BlockIdRegex().Match(last.Text) is not { Success: true } match)
        {
            return block;
        }

        var trimmed = last.Text[..match.Index];
        List<R.RenderInline> updated = [.. inlines.Take(inlines.Count - 1)];
        if (trimmed.Length > 0)
        {
            updated.Add(new R.TextInline(trimmed));
        }

        R.RenderBlock result = block switch
        {
            R.ParagraphBlock p => p with { Inlines = updated, BlockId = match.Groups["id"].Value },
            R.HeadingBlock h => h with { Inlines = updated, BlockId = match.Groups["id"].Value },
            _ => block,
        };
        return (T)result;
    }

    private static string Lines(LeafBlock block)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < block.Lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(block.Lines.Lines[i].Slice.ToString());
        }

        return builder.ToString();
    }

    private static List<R.RenderInline> Inlines(ContainerInline? container)
    {
        var result = new List<R.RenderInline>();
        if (container is null)
        {
            return result;
        }

        foreach (var inline in container)
        {
            if (inline is DelimiterInline delimiter)
            {
                // Unmatched delimiters (e.g. "[[" without "]]") are literal text followed by their content.
                Append(result, new R.TextInline(delimiter.ToLiteral()));
                foreach (var child in Inlines(delimiter))
                {
                    Append(result, child);
                }

                continue;
            }

            Append(result, Inline(inline));
        }

        return result;
    }

    private static void Append(List<R.RenderInline> result, R.RenderInline? converted)
    {
        if (converted is R.TextInline text && result.Count > 0 && result[^1] is R.TextInline previous)
        {
            result[^1] = new R.TextInline(previous.Text + text.Text);
        }
        else if (converted is not null)
        {
            result.Add(converted);
        }
    }

    private static R.RenderInline? Inline(Inline inline) => inline switch
    {
        LiteralInline literal => new R.TextInline(literal.Content.ToString()),
        HtmlEntityInline entity => new R.TextInline(entity.Transcoded.ToString()),
        Markdig.Syntax.Inlines.CodeInline code => new R.CodeInline(code.Content),
        MathInline math => new R.CodeInline(math.Content.ToString()),
        HtmlInline html => new R.TextInline(html.Tag),
        LineBreakInline lineBreak => new R.LineBreakInline(lineBreak.IsHard),
        EmphasisInline emphasis => Emphasis(emphasis),
        AutolinkInline autolink => new R.LinkInline(true, autolink.Url, null, [new R.TextInline(autolink.Url)]),
        Markdig.Syntax.Inlines.LinkInline link => Link(link),
        S.WikiLinkInline wiki => WikiLink(wiki.Link),
        S.TagInline tag => new R.TagInline(tag.Tag.Name),
        S.CommentInline => null,
        FootnoteLink { IsBackLink: false } footnote => new R.FootnoteReferenceInline(footnote.Index.ToString(CultureInfo.InvariantCulture)),
        TaskList => null,
        ContainerInline container => new R.EmphasisInline(Inlines(container)),
        _ => null,
    };

    private static R.RenderInline Emphasis(EmphasisInline emphasis)
    {
        var children = Inlines(emphasis);
        return (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
        {
            ('~', _) => new R.StrikethroughInline(children),
            ('=', _) => new R.HighlightInline(children),
            (_, >= 2) => new R.StrongInline(children),
            _ => new R.EmphasisInline(children),
        };
    }

    private static R.RenderInline Link(Markdig.Syntax.Inlines.LinkInline link)
    {
        var url = link.Url ?? string.Empty;
        var external = LinkResolver.IsExternal(url) || url.StartsWith("//", StringComparison.Ordinal);
        if (link.IsImage)
        {
            var alt = string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
            if (external)
            {
                return new R.ImageInline(url, true, alt.Length == 0 ? null : alt);
            }

            var (target, subpath) = Split(Uri.UnescapeDataString(url));
            return new R.EmbedInline(target, subpath, alt.Length == 0 ? null : alt, null, null);
        }

        if (external)
        {
            return new R.LinkInline(true, url, null, Inlines(link));
        }

        var (path, anchor) = Split(Uri.UnescapeDataString(url));
        return new R.LinkInline(false, path, anchor, Inlines(link));
    }

    private static R.RenderInline WikiLink(Link link)
    {
        if (link.IsEmbed)
        {
            var (width, height) = Size(link.Alias);
            return new R.EmbedInline(link.RawTarget, link.Subpath, width is null ? link.Alias : null, width, height);
        }

        var display = link.Alias ?? (link.Subpath is null
            ? link.RawTarget
            : link.RawTarget.Length == 0 ? link.Subpath.TrimStart('^') : $"{link.RawTarget} > {link.Subpath.TrimStart('^')}");
        return new R.LinkInline(false, link.RawTarget, link.Subpath, [new R.TextInline(display)]);
    }

    private static (int? Width, int? Height) Size(string? alias)
    {
        if (alias is null || SizeRegex().Match(alias) is not { Success: true } match)
        {
            return (null, null);
        }

        var width = int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture);
        int? height = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : null;
        return (width, height);
    }

    private static (string Target, string? Subpath) Split(string destination)
    {
        var hash = destination.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? (destination, null) : (destination[..hash], hash == destination.Length - 1 ? null : destination[(hash + 1)..]);
    }

    [GeneratedRegex(@"^\[!(?<type>[A-Za-z-]+)\](?<fold>[+-]?)(?<title>[^\n]*)$")]
    private static partial Regex CalloutRegex();

    [GeneratedRegex(@"\s\^(?<id>[A-Za-z0-9-]+)\s*$")]
    private static partial Regex BlockIdRegex();

    [GeneratedRegex(@"^\s*\^(?<id>[A-Za-z0-9-]+)\s*$")]
    private static partial Regex OwnLineBlockIdRegex();

    [GeneratedRegex(@"^(?<w>\d+)(x(?<h>\d+))?$")]
    private static partial Regex SizeRegex();
}
