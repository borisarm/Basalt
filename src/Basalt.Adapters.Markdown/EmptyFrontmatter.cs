namespace Basalt.Adapters.Markdown;

/// <summary>
/// Markdig only recognises frontmatter with content; Obsidian also accepts an empty block ("---" followed by "---").
/// The delimiters are masked with spaces (offsets unchanged) so they are not parsed as thematic breaks.
/// </summary>
internal static class EmptyFrontmatter
{
    public static string Mask(string content, out int blockLength)
    {
        blockLength = 0;
        var firstEnd = content.IndexOf('\n', StringComparison.Ordinal);
        if (firstEnd < 0 || content.AsSpan(0, firstEnd).TrimEnd('\r').ToString() != "---")
        {
            return content;
        }

        var secondEnd = content.IndexOf('\n', firstEnd + 1);
        var second = (secondEnd < 0 ? content[(firstEnd + 1)..] : content[(firstEnd + 1)..secondEnd]).TrimEnd('\r');
        if (second is not ("---" or "..."))
        {
            return content;
        }

        blockLength = secondEnd < 0 ? content.Length : secondEnd + 1;
        var chars = content.ToCharArray();
        for (var i = 0; i < blockLength; i++)
        {
            if (chars[i] is not ('\r' or '\n'))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }
}
