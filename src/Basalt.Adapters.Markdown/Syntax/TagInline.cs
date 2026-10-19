using Basalt.Domain.Tags;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax.Inlines;

namespace Basalt.Adapters.Markdown.Syntax;

/// <summary>An inline <c>#tag</c>.</summary>
internal sealed class TagInline(Tag tag) : LeafInline
{
    public Tag Tag { get; } = tag;
}

/// <summary>
/// Inline tags (spec 05 §4): '#' at the start or after white space or one of <c>( [ { , ;</c>, followed by letters,
/// digits, '_', '-' and '/' with at least one non-digit. A trailing '/' is dropped.
/// </summary>
internal sealed class TagInlineParser : InlineParser
{
    public TagInlineParser() => OpeningCharacters = ['#'];

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var previous = slice.PeekCharExtra(-1);
        if (!(previous == '\0' || char.IsWhiteSpace(previous) || previous is '(' or '[' or '{' or ',' or ';'))
        {
            return false;
        }

        var text = slice.Text;
        var end = slice.Start + 1;
        while (end <= slice.End && IsTagChar(text, end))
        {
            end += char.IsSurrogatePair(text, end) ? 2 : 1;
        }

        var body = text[(slice.Start + 1)..end];
        if (body.Length == 0 || Tag.Create(body) is not { IsSuccess: true } tag)
        {
            return false;
        }

        var start = processor.GetSourcePosition(slice.Start, out var line, out var column);
        processor.Inline = new TagInline(tag.Value)
        {
            Span = new Markdig.Syntax.SourceSpan(start, start + (end - slice.Start) - 1),
            Line = line,
            Column = column,
        };
        slice.Start = end;
        return true;
    }

    private static bool IsTagChar(string text, int index)
    {
        var c = text[index];
        if (c is '_' or '-' or '/')
        {
            return true;
        }

        return char.IsSurrogatePair(text, index)
            ? char.IsLetterOrDigit(text, index)
            : char.IsLetterOrDigit(c);
    }
}
