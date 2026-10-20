namespace Basalt.Domain.Notes;

public enum TextBlockKind
{
    Paragraph,
    ListItem,
    Heading,
    Code,
}

/// <summary>A block of a note: a paragraph, list item, heading or fenced code block (lines are 0-based, inclusive).</summary>
public sealed record TextBlock(TextBlockKind Kind, int FirstLine, int LastLine, TextRange Range);

/// <summary>
/// Splits Markdown text into the blocks that <c>^block</c> references point at (spec 05 §3.3): blank lines separate
/// blocks; ATX headings are one-line blocks; list items start a block that continues with their following lines; a
/// fenced code block (up to its closing fence or the end) is one block.
/// </summary>
public static class TextBlocks
{
    public static IReadOnlyList<TextBlock> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = Lines(text);
        var blocks = new List<TextBlock>();
        var first = -1;
        var kind = TextBlockKind.Paragraph;

        void Close(int last)
        {
            if (first >= 0)
            {
                blocks.Add(new TextBlock(kind, first, last, new TextRange(lines[first].Start, lines[last].End - lines[first].Start)));
                first = -1;
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var content = text.AsSpan(lines[i].Start, lines[i].End - lines[i].Start);
            if (content.IsWhiteSpace())
            {
                Close(i - 1);
                continue;
            }

            if (FenceOf(content) is { } fence)
            {
                Close(i - 1);
                var close = i + 1;
                while (close < lines.Count && !IsClosingFence(text.AsSpan(lines[close].Start, lines[close].End - lines[close].Start), fence))
                {
                    close++;
                }

                var last = Math.Min(close, lines.Count - 1);
                first = i;
                kind = TextBlockKind.Code;
                Close(last);
                i = last;
                continue;
            }

            if (IsHeading(content))
            {
                Close(i - 1);
                first = i;
                kind = TextBlockKind.Heading;
                Close(i);
                continue;
            }

            if (IsListItem(content))
            {
                Close(i - 1);
                first = i;
                kind = TextBlockKind.ListItem;
                continue;
            }

            if (first < 0)
            {
                first = i;
                kind = TextBlockKind.Paragraph;
            }
        }

        Close(lines.Count - 1);
        return blocks;
    }

    private static (char Char, int Length)? FenceOf(ReadOnlySpan<char> line)
    {
        var start = Indentation(line);
        if (start > 3 || start >= line.Length || line[start] is not ('`' or '~'))
        {
            return null;
        }

        var length = Run(line, start);
        return length >= 3 ? (line[start], length) : null;
    }

    private static bool IsClosingFence(ReadOnlySpan<char> line, (char Char, int Length) fence)
    {
        var start = Indentation(line);
        if (start > 3 || start >= line.Length || line[start] != fence.Char)
        {
            return false;
        }

        var length = Run(line, start);
        return length >= fence.Length && line[(start + length)..].IsWhiteSpace();
    }

    /// <summary>ATX heading: up to three spaces, one to six <c>#</c>, then a space or the end of the line.</summary>
    private static bool IsHeading(ReadOnlySpan<char> line)
    {
        var start = Indentation(line);
        if (start > 3 || start >= line.Length || line[start] != '#')
        {
            return false;
        }

        var hashes = Run(line, start);
        return hashes <= 6 && (start + hashes == line.Length || line[start + hashes] is ' ' or '\t');
    }

    /// <summary><c>-</c>, <c>*</c> or <c>+</c> followed by a space, or up to nine digits followed by <c>.</c>/<c>)</c> and a space.</summary>
    private static bool IsListItem(ReadOnlySpan<char> line)
    {
        var start = Indentation(line);
        var rest = line[start..];
        if (rest.Length >= 2 && rest[0] is '-' or '*' or '+' && rest[1] is ' ' or '\t')
        {
            return true;
        }

        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        return digits is > 0 and <= 9 && digits + 1 < rest.Length && rest[digits] is '.' or ')' && rest[digits + 1] is ' ' or '\t';
    }

    private static int Indentation(ReadOnlySpan<char> line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static int Run(ReadOnlySpan<char> line, int start)
    {
        var end = start;
        while (end < line.Length && line[end] == line[start])
        {
            end++;
        }

        return end - start;
    }

    private static List<(int Start, int End)> Lines(string text)
    {
        var lines = new List<(int Start, int End)>();
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            lines.Add((start, end > start && text[end - 1] == '\r' ? end - 1 : end));
            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return lines;
    }
}
