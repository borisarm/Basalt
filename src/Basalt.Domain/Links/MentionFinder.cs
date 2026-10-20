using System.Globalization;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Links;

/// <summary>An unlinked mention: where it is and which of the target's names it spells.</summary>
public sealed record Mention(TextRange Range, string Name);

/// <summary>
/// Finds code where mentions and links do not count: fenced blocks (up to three spaces of indentation, three or more
/// <c>`</c> or <c>~</c>, closed by a run of the same character at least as long, or running to the end) and inline code
/// spans (a backtick run closed by a run of the same length within the paragraph).
/// </summary>
public static class CodeRegions
{
    public static IReadOnlyList<TextRange> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = Lines(text);
        var regions = new List<TextRange>();
        var proseStart = 0;
        var i = 0;
        while (i < lines.Count)
        {
            if (!IsFenceOpening(text, lines[i], out var fence, out var length))
            {
                i++;
                continue;
            }

            AddInlineSpans(text, proseStart, lines[i].Start, regions);
            var close = i + 1;
            while (close < lines.Count && !IsFenceClosing(text, lines[close], fence, length))
            {
                close++;
            }

            var end = close < lines.Count ? lines[close].End : text.Length;
            regions.Add(new TextRange(lines[i].Start, end - lines[i].Start));
            i = close + 1;
            proseStart = i < lines.Count ? lines[i].Start : text.Length;
        }

        AddInlineSpans(text, proseStart, text.Length, regions);
        return regions;
    }

    private static void AddInlineSpans(string text, int from, int to, List<TextRange> regions)
    {
        var position = from;
        while (position < to)
        {
            if (text[position] != '`')
            {
                position++;
                continue;
            }

            var run = RunLength(text, position, '`', to);
            var close = FindClosingRun(text, position + run, run, to);
            if (close < 0)
            {
                position += run;
                continue;
            }

            regions.Add(new TextRange(position, close + run - position));
            position = close + run;
        }
    }

    /// <summary>The start of the next run of exactly <paramref name="length"/> backticks before a blank line, or -1.</summary>
    private static int FindClosingRun(string text, int from, int length, int to)
    {
        var position = from;
        while (position < to)
        {
            var c = text[position];
            if (c == '\n' && NextLineIsBlank(text, position + 1, to))
            {
                return -1;
            }

            if (c != '`')
            {
                position++;
                continue;
            }

            var run = RunLength(text, position, '`', to);
            if (run == length)
            {
                return position;
            }

            position += run;
        }

        return -1;
    }

    private static bool NextLineIsBlank(string text, int start, int to)
    {
        for (var i = start; i < to; i++)
        {
            switch (text[i])
            {
                case '\n':
                    return true;
                case ' ' or '\t' or '\r':
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool IsFenceOpening(string text, Line line, out char fence, out int length)
    {
        var start = SkipIndentation(text, line);
        fence = start < line.End ? text[start] : '\0';
        length = fence is '`' or '~' ? RunLength(text, start, fence, line.End) : 0;
        if (length < 3)
        {
            return false;
        }

        // A backtick fence's info string cannot contain backticks (otherwise it is inline code).
        return fence == '~' || text.IndexOf('`', start + length, line.End - start - length) < 0;
    }

    private static bool IsFenceClosing(string text, Line line, char fence, int length)
    {
        var start = SkipIndentation(text, line);
        if (start >= line.End || text[start] != fence)
        {
            return false;
        }

        var run = RunLength(text, start, fence, line.End);
        return run >= length && text.AsSpan(start + run, line.End - start - run).IsWhiteSpace();
    }

    /// <summary>Position after at most three leading spaces, or the line end when there are four or more.</summary>
    private static int SkipIndentation(string text, Line line)
    {
        var position = line.Start;
        while (position < line.End && text[position] == ' ')
        {
            position++;
        }

        return position - line.Start <= 3 ? position : line.End;
    }

    private static int RunLength(string text, int start, char c, int end)
    {
        var position = start;
        while (position < end && text[position] == c)
        {
            position++;
        }

        return position - start;
    }

    /// <summary>Lines without their terminator (<c>\n</c> or <c>\r\n</c>).</summary>
    private static List<Line> Lines(string text)
    {
        var lines = new List<Line>();
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            lines.Add(new Line(start, end > start && text[end - 1] == '\r' ? end - 1 : end));
            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return lines;
    }

    private readonly record struct Line(int Start, int End);
}

/// <summary>
/// Unlinked mentions of a note (F-005): its title or aliases as whole words, case-insensitive, outside the excluded
/// ranges (links, frontmatter, code). Longer names win where names overlap; mentions never overlap.
/// </summary>
public static class MentionFinder
{
    public static IReadOnlyList<Mention> Find(string text, IEnumerable<string> names, IEnumerable<TextRange> excluded)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(excluded);
        var taken = excluded.ToList();
        var mentions = new List<Mention>();
        var ordered = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length);
        foreach (var name in ordered)
        {
            for (var index = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); index >= 0; index = text.IndexOf(name, index + 1, StringComparison.OrdinalIgnoreCase))
            {
                var range = new TextRange(index, name.Length);
                if (IsWordBoundary(text, index - 1) && IsWordBoundary(text, range.End) && !taken.Any(t => t.Overlaps(range)))
                {
                    mentions.Add(new Mention(range, name));
                    taken.Add(range);
                }
            }
        }

        return [.. mentions.OrderBy(m => m.Range.Start)];
    }

    /// <summary>True when <paramref name="index"/> is outside the text or not part of a word.</summary>
    private static bool IsWordBoundary(string text, int index) =>
        index < 0 || index >= text.Length
        || !(char.IsLetterOrDigit(text[index]) || text[index] == '_'
             || CharUnicodeInfo.GetUnicodeCategory(text[index]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);
}
