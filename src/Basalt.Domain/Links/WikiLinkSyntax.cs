using Basalt.Domain.Notes;

namespace Basalt.Domain.Links;

/// <summary>
/// Parser of the wikilink grammar (spec 05 §2): <c>[[target#subpath|alias]]</c> and <c>![[...]]</c>. Callers locate
/// candidates outside excluded regions (code, comments...) and pass the offset and position of the opening bracket.
/// </summary>
public static class WikiLinkSyntax
{
    /// <summary>Parses the link starting at <paramref name="start"/> ('[' or '!').</summary>
    /// <param name="line">0-based line of <paramref name="start"/>.</param>
    /// <param name="column">0-based column of <paramref name="start"/>.</param>
    /// <returns>The link, or null when the text at <paramref name="start"/> is not a valid wikilink.</returns>
    public static Link? TryParse(string text, int start, int line, int column)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (start < 0 || start >= text.Length)
        {
            return null;
        }

        var embed = text[start] == '!';
        var open = embed ? start + 1 : start;
        if (!At(text, open, "[["))
        {
            return null;
        }

        var innerStart = open + 2;
        var close = text.IndexOf("]]", innerStart, StringComparison.Ordinal);
        if (close < 0)
        {
            return null;
        }

        var inner = text.AsSpan(innerStart, close - innerStart);
        if (inner.ContainsAny('\n', '\r', '[') || inner.Contains(']'))
        {
            return null;
        }

        var pipe = inner.IndexOf('|');
        var head = pipe < 0 ? inner : inner[..pipe];
        string? alias = null;
        if (pipe >= 0)
        {
            alias = inner[(pipe + 1)..].Trim().ToString();
            if (head.EndsWith('\\'))
            {
                // Spec 05 §2: "\|" inside tables separates the alias.
                head = head[..^1];
            }
        }

        var hash = head.IndexOf('#');
        var targetPart = hash < 0 ? head : head[..hash];
        if (targetPart.Contains('^'))
        {
            return null;
        }

        string? subpath = null;
        var subpathKind = SubpathKind.None;
        if (hash >= 0 && head.Length > hash + 1)
        {
            subpath = head[(hash + 1)..].ToString();
            subpathKind = subpath.StartsWith('^') ? SubpathKind.Block : SubpathKind.Heading;
        }

        var trimmedTarget = targetPart.Trim();
        if (trimmedTarget.Length == 0 && subpath is null)
        {
            return null;
        }

        var leading = targetPart.Length - targetPart.TrimStart().Length;
        var targetOffset = innerStart + leading;
        var end = close + 2;
        var span = new SourceSpan(start, end - start, line, column);
        var targetSpan = new SourceSpan(targetOffset, trimmedTarget.Length, line, column + (targetOffset - start));
        return new Link(
            embed ? LinkKind.Embed : LinkKind.WikiLink,
            trimmedTarget.ToString(),
            subpath,
            subpathKind,
            alias,
            span,
            targetSpan);
    }

    private static bool At(string text, int index, string token) =>
        index + token.Length <= text.Length && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
}
