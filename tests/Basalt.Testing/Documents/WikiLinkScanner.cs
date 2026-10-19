using Basalt.Domain.Links;

namespace Basalt.Testing.Documents;

/// <summary>Finds wikilinks in plain text (no code exclusions) — enough for domain tests of rewriting.</summary>
public static class WikiLinkScanner
{
    public static IReadOnlyList<Link> Scan(string text)
    {
        var links = new List<Link>();
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
                continue;
            }

            var isEmbed = text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[';
            if (text[i] != '[' && !isEmbed)
            {
                continue;
            }

            if (WikiLinkSyntax.TryParse(text, i, line, i - lineStart) is { } link)
            {
                links.Add(link);
                i = link.Span.End - 1;
            }
        }

        return links;
    }
}
