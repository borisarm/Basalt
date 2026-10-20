using System.Text;
using System.Text.RegularExpressions;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Sync;

/// <summary>
/// Exclusion rules with the <c>.gitignore</c> syntax subset of spec 06 §3: <c>*</c>, <c>**</c>, <c>?</c>,
/// <c>[abc]</c>, <c>!negation</c>, leading <c>/</c> (anchored) and a trailing <c>/</c> (directories only). Later
/// rules win. A file inside an ignored directory stays ignored, as in Git.
/// </summary>
public sealed class IgnoreRules
{
    private readonly Rule[] _rules;

    private IgnoreRules(Rule[] rules) => _rules = rules;

    public static IgnoreRules Empty { get; } = new([]);

    /// <summary>Rules that are always applied first (spec 06 §3.1), before <c>.basalt/syncignore</c> and the profile.</summary>
    public static IReadOnlyList<string> DefaultPatterns { get; } =
    [
        ".git/",
        ".basalt/workspace.*.json",
        ".basalt/cache/",
        ".~basalt-*.tmp",
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
        "Icon\r",
        "*.swp",
    ];

    public static IgnoreRules Default { get; } = Parse(DefaultPatterns);

    /// <summary>Parses one pattern per line; blank lines and <c>#</c> comments are ignored.</summary>
    public static IgnoreRules Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
    }

    public static IgnoreRules Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return new IgnoreRules([.. lines.Select(ParseLine).OfType<Rule>()]);
    }

    /// <summary>Appends rules: later sets take precedence over earlier ones.</summary>
    public IgnoreRules Then(IgnoreRules? later) =>
        later is null || later._rules.Length == 0 ? this : new IgnoreRules([.. _rules, .. later._rules]);

    public static IgnoreRules Combine(params IgnoreRules[] sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        var rules = new List<Rule>();
        foreach (var set in sets)
        {
            ArgumentNullException.ThrowIfNull(set);
            rules.AddRange(set._rules);
        }

        return new IgnoreRules([.. rules]);
    }

    public bool IsIgnored(VaultPath path, bool isDirectory)
    {
        if (path.IsRoot)
        {
            return false;
        }

        var segments = path.Segments;
        for (var i = 1; i < segments.Count; i++)
        {
            var ancestor = VaultPath.Create(string.Join('/', segments.Take(i))).Value;
            if (Matches(ancestor, isDirectory: true))
            {
                return true;
            }
        }

        return Matches(path, isDirectory);
    }

    private bool Matches(VaultPath path, bool isDirectory)
    {
        var ignored = false;
        var value = path.ToString();
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            if (rule.Regex.IsMatch(value))
            {
                ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private static Rule? ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.EndsWith('\r'))
        {
            line = line[..^1];
        }

        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        var negated = line[0] == '!';
        if (negated)
        {
            line = line[1..];
        }

        var directoryOnly = line.EndsWith('/');
        if (directoryOnly)
        {
            line = line[..^1];
        }

        if (line.Length == 0)
        {
            return null;
        }

        var anchored = line.Contains('/');
        if (line[0] == '/')
        {
            line = line[1..];
            anchored = true;
        }

        if (line.Length == 0)
        {
            return null;
        }

        var body = GlobToRegex(line);
        var regex = anchored
            ? new Regex("^" + body + "$", RegexOptions.CultureInvariant)
            : new Regex("(?:^|/)" + body + "$", RegexOptions.CultureInvariant);
        return new Rule(regex, negated, directoryOnly);
    }

    private static string GlobToRegex(string glob)
    {
        var regex = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                var doubleStar = i + 1 < glob.Length && glob[i + 1] == '*';
                if (!doubleStar)
                {
                    regex.Append("[^/]*");
                    continue;
                }

                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    i++;
                    regex.Append("(?:.*/)?");
                }
                else
                {
                    regex.Append(".*");
                }
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else if (c == '[')
            {
                var end = glob.IndexOf(']', i + 1);
                if (end < 0)
                {
                    regex.Append("\\[");
                    continue;
                }

                var content = glob[(i + 1)..end];
                if (content.StartsWith('!'))
                {
                    content = "^" + content[1..];
                }

                regex.Append('[').Append(content.Replace("\\", "\\\\", StringComparison.Ordinal)).Append(']');
                i = end;
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        return regex.ToString();
    }

    private sealed record Rule(Regex Regex, bool Negated, bool DirectoryOnly);
}
