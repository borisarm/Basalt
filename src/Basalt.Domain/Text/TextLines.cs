namespace Basalt.Domain.Text;

/// <summary>Line splitting that keeps terminators, so that <c>string.Concat(Split(t)) == t</c>.</summary>
public static class TextLines
{
    /// <summary>Splits after each '\n'; "\r\n" stays attached to its line; a lone '\r' is not a terminator.</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>The line without its "\n" or "\r\n" terminator.</summary>
    public static string WithoutTerminator(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return line[..^2];
        }

        return line.EndsWith('\n') ? line[..^1] : line;
    }
}
