using System.Text;
using Basalt.Domain.Common;

namespace Basalt.Domain.Notes;

/// <summary>A Markdown heading (spec 02 §4) with its anchor slug (spec 05 §3.2).</summary>
public sealed record Heading
{
    private Heading(int level, string text, int line)
    {
        Level = level;
        Text = text;
        Line = line;
        Slug = NormalizeAnchor(text);
    }

    public int Level { get; }

    public string Text { get; }

    /// <summary>0-based line.</summary>
    public int Line { get; }

    /// <summary>Normalised text used to match <c>#anchors</c>.</summary>
    public string Slug { get; }

    /// <returns><c>heading.invalid_level</c> when the level is not 1–6.</returns>
    public static Result<Heading> Create(int level, string text, int line)
    {
        ArgumentNullException.ThrowIfNull(text);
        return level is < 1 or > 6
            ? new Error(DomainErrors.HeadingInvalidLevel, "Heading levels go from 1 to 6.")
            : new Heading(level, text, line);
    }

    public bool MatchesAnchor(string anchor) => string.Equals(NormalizeAnchor(anchor), Slug, StringComparison.Ordinal);

    /// <summary>
    /// Spec 05 §3.2: drops <c># | ^ : % [ ] \</c>, collapses white space and lower-cases, so that anchors compare
    /// case-insensitively.
    /// </summary>
    public static string NormalizeAnchor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (c is '#' or '|' or '^' or ':' or '%' or '[' or ']' or '\\')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
