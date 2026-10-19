using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Basalt.Adapters.Markdown.Syntax;

/// <summary>An Obsidian comment <c>%%...%%</c> inside a paragraph: hidden when reading, links/tags not indexed.</summary>
internal sealed class CommentInline(string content) : LeafInline
{
    public string Content { get; } = content;
}

internal sealed class CommentInlineParser : InlineParser
{
    public CommentInlineParser() => OpeningCharacters = ['%'];

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        if (slice.PeekChar() != '%')
        {
            return false;
        }

        var text = slice.Text;
        var close = text.IndexOf("%%", slice.Start + 2, StringComparison.Ordinal);
        if (close < 0 || close + 1 > slice.End)
        {
            return false;
        }

        var start = processor.GetSourcePosition(slice.Start, out var line, out var column);
        processor.Inline = new CommentInline(text[(slice.Start + 2)..close])
        {
            Span = new SourceSpan(start, start + (close + 2 - slice.Start) - 1),
            Line = line,
            Column = column,
        };
        slice.Start = close + 2;
        return true;
    }
}

/// <summary>A multi-line comment: a line starting with <c>%%</c> up to the next line containing <c>%%</c>.</summary>
internal sealed class CommentBlock(BlockParser parser) : LeafBlock(parser);

internal sealed class CommentBlockParser : BlockParser
{
    public CommentBlockParser() => OpeningCharacters = ['%'];

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
        {
            return BlockState.None;
        }

        var line = processor.Line;
        if (!line.Match("%%"))
        {
            return BlockState.None;
        }

        // A comment closed on its own line is an inline comment of a paragraph.
        var rest = line.Text.AsSpan(line.Start + 2, line.End - line.Start - 1);
        if (rest.Contains("%%", StringComparison.Ordinal))
        {
            return BlockState.None;
        }

        var block = new CommentBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(line.Start, line.End),
            Line = processor.LineIndex,
        };
        block.AppendLine(ref processor.Line, processor.Column, processor.LineIndex, processor.CurrentLineStartPosition, processor.TrackTrivia);
        processor.NewBlocks.Push(block);
        return BlockState.Continue;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var line = processor.Line;
        var leaf = (LeafBlock)block;
        leaf.AppendLine(ref processor.Line, processor.Column, processor.LineIndex, processor.CurrentLineStartPosition, processor.TrackTrivia);
        block.UpdateSpanEnd(line.End);
        return line.ToString().Contains("%%", StringComparison.Ordinal) ? BlockState.BreakDiscard : BlockState.Continue;
    }
}
