using System.Text;
using Basalt.Application.Ports.Markdown;

namespace Basalt.Adapters.Markdown.Tests;

/// <summary>Readable text dump of a <see cref="RenderDocument"/> for snapshot review.</summary>
internal static class RenderDump
{
    public static string Dump(RenderDocument document)
    {
        var builder = new StringBuilder();
        foreach (var block in document.Blocks)
        {
            Block(builder, block, 0);
        }

        return builder.ToString();
    }

    private static void Block(StringBuilder b, RenderBlock block, int depth)
    {
        var pad = new string(' ', depth * 2);
        var meta = $"@{block.Line}{(block.BlockId is null ? string.Empty : " ^" + block.BlockId)}";
        switch (block)
        {
            case HeadingBlock h:
                b.AppendLine($"{pad}Heading{h.Level} {meta}: {Inlines(h.Inlines)}");
                break;
            case ParagraphBlock p:
                b.AppendLine($"{pad}Paragraph {meta}: {Inlines(p.Inlines)}");
                break;
            case ListBlock l:
                b.AppendLine($"{pad}List{(l.Ordered ? $" ordered from {l.Start}" : string.Empty)} {meta}");
                foreach (var item in l.Items)
                {
                    b.AppendLine($"{pad}  Item @{item.Line}{(item.TaskStatus is { } s ? $" task[{s}]" : string.Empty)}");
                    foreach (var child in item.Blocks)
                    {
                        Block(b, child, depth + 2);
                    }
                }

                break;
            case QuoteBlock q:
                b.AppendLine($"{pad}Quote {meta}");
                Children(b, q.Blocks, depth);
                break;
            case CalloutBlock c:
                b.AppendLine($"{pad}Callout[{c.Type}] {c.Folding} {meta}: {Inlines(c.Title)}");
                Children(b, c.Blocks, depth);
                break;
            case CodeBlock code:
                b.AppendLine($"{pad}Code({code.Language ?? "-"}) {meta}: {code.Code.Replace("\n", "\\n", StringComparison.Ordinal)}");
                break;
            case TableBlock t:
                b.AppendLine($"{pad}Table {meta} [{string.Join(",", t.Alignments)}]");
                b.AppendLine($"{pad}  H: {string.Join(" | ", t.Header.Select(Inlines))}");
                foreach (var row in t.Rows)
                {
                    b.AppendLine($"{pad}  R: {string.Join(" | ", row.Select(Inlines))}");
                }

                break;
            case ImageBlock i:
                b.AppendLine($"{pad}Image {meta}: {i.Source} external={i.IsExternal} alt={i.AltText}");
                break;
            case EmbedBlock e:
                b.AppendLine($"{pad}EmbedBlock {meta}: {Inline(e.Embed)}");
                break;
            case ThematicBreakBlock:
                b.AppendLine($"{pad}---- {meta}");
                break;
            case FootnotesBlock f:
                b.AppendLine($"{pad}Footnotes {meta}");
                foreach (var note in f.Footnotes)
                {
                    b.AppendLine($"{pad}  [{note.Label}]");
                    Children(b, note.Blocks, depth + 1);
                }

                break;
            default:
                b.AppendLine($"{pad}{block.GetType().Name} {meta}");
                break;
        }
    }

    private static void Children(StringBuilder b, IEnumerable<RenderBlock> blocks, int depth)
    {
        foreach (var child in blocks)
        {
            Block(b, child, depth + 1);
        }
    }

    private static string Inlines(IEnumerable<RenderInline> inlines) => string.Concat(inlines.Select(Inline));

    private static string Inline(RenderInline inline) => inline switch
    {
        TextInline t => t.Text.Replace("\n", "\\n", StringComparison.Ordinal),
        EmphasisInline e => $"<em>{Inlines(e.Children)}</em>",
        StrongInline s => $"<strong>{Inlines(s.Children)}</strong>",
        StrikethroughInline s => $"<del>{Inlines(s.Children)}</del>",
        HighlightInline h => $"<mark>{Inlines(h.Children)}</mark>",
        CodeInline c => $"<code>{c.Code}</code>",
        LinkInline l => $"<link {(l.IsExternal ? "ext" : "int")}={l.Target}{(l.Subpath is null ? string.Empty : "#" + l.Subpath)}>{Inlines(l.Children)}</link>",
        EmbedInline e => $"<embed {e.Target}{(e.Subpath is null ? string.Empty : "#" + e.Subpath)} alias={e.Alias} size={e.Width}x{e.Height}/>",
        ImageInline i => $"<img {i.Source} ext={i.IsExternal} alt={i.AltText}/>",
        TagInline t => $"<tag {t.Tag}/>",
        LineBreakInline br => br.IsHard ? "<br/>" : "<soft/>",
        FootnoteReferenceInline f => $"<fn {f.Label}/>",
        _ => $"<{inline.GetType().Name}/>",
    };
}
