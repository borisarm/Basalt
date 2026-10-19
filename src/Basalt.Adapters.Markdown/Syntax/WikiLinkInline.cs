using Basalt.Domain.Links;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax.Inlines;

namespace Basalt.Adapters.Markdown.Syntax;

/// <summary>A <c>[[wikilink]]</c> or <c>![[embed]]</c>; positions in <see cref="Link"/> are document offsets.</summary>
internal sealed class WikiLinkInline(Link link) : LeafInline
{
    public Link Link { get; } = link;
}

/// <summary>Parses wikilinks with the domain grammar (spec 05 §2). Registered before Markdig's link parser.</summary>
internal sealed class WikiLinkInlineParser : InlineParser
{
    public WikiLinkInlineParser() => OpeningCharacters = ['[', '!'];

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var start = slice.Start;
        var text = slice.Text;
        if (slice.CurrentChar == '!' && slice.PeekChar() != '[')
        {
            return false;
        }

        // Markdig hands us the text of the current leaf block: parse there, then translate to document coordinates.
        var local = WikiLinkSyntax.TryParse(text, start, 0, 0);
        if (local is null)
        {
            return false;
        }

        var spanStart = processor.GetSourcePosition(start, out var line, out var column);
        var targetStart = processor.GetSourcePosition(local.TargetSpan.Start, out var targetLine, out var targetColumn);
        var link = local with
        {
            Span = new Domain.Notes.SourceSpan(spanStart, local.Span.Length, line, column),
            TargetSpan = new Domain.Notes.SourceSpan(targetStart, local.TargetSpan.Length, targetLine, targetColumn),
        };

        processor.Inline = new WikiLinkInline(link)
        {
            Span = new Markdig.Syntax.SourceSpan(spanStart, spanStart + local.Span.Length - 1),
            Line = line,
            Column = column,
        };
        slice.Start = local.Span.End;
        return true;
    }
}
