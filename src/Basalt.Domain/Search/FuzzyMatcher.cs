using System.Text;

namespace Basalt.Domain.Search;

/// <summary>A fuzzy match: score (higher is better, +∞ for an exact match) and matched character positions.</summary>
public sealed record FuzzyMatch(double Score, IReadOnlyList<int> Positions);

/// <summary>
/// Subsequence fuzzy matcher for the quick switcher, the command palette and link autocompletion (spec 02 §7). Scoring
/// follows fzy: an optimal alignment by dynamic programming that rewards consecutive characters and matches at word
/// starts (after '/', separators, '.', or lower→upper case changes), penalises gaps, and adds a small bonus when the
/// case matches exactly. Matching ignores case and diacritics. Deterministic.
/// </summary>
public static class FuzzyMatcher
{
    private const double GapLeading = -0.005;
    private const double GapTrailing = -0.005;
    private const double GapInner = -0.01;
    private const double MatchConsecutive = 1.0;
    private const double BonusSlash = 0.9;
    private const double BonusWord = 0.8;
    private const double BonusCapital = 0.7;
    private const double BonusDot = 0.6;
    private const double BonusExactCase = 0.05;

    public static FuzzyMatch? Match(string query, string candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);
        var n = query.Length;
        var m = candidate.Length;
        if (n == 0)
        {
            return new FuzzyMatch(0, []);
        }

        if (n > m || !IsSubsequence(query, candidate))
        {
            return null;
        }

        if (n == m)
        {
            // Same length and a folded subsequence: the strings are equal ignoring case/diacritics.
            return new FuzzyMatch(double.PositiveInfinity, [.. Enumerable.Range(0, n)]);
        }

        var bonus = Bonuses(candidate);
        var d = new double[n, m];
        var best = new double[n, m];
        for (var i = 0; i < n; i++)
        {
            var previous = double.NegativeInfinity;
            var gap = i == n - 1 ? GapTrailing : GapInner;
            var q = Fold(query[i]);
            for (var j = 0; j < m; j++)
            {
                if (q == Fold(candidate[j]))
                {
                    var caseBonus = query[i] == candidate[j] ? BonusExactCase : 0;
                    var score = double.NegativeInfinity;
                    if (i == 0)
                    {
                        score = (j * GapLeading) + bonus[j] + caseBonus;
                    }
                    else if (j > 0)
                    {
                        score = Math.Max(best[i - 1, j - 1] + bonus[j], d[i - 1, j - 1] + MatchConsecutive) + caseBonus;
                    }

                    d[i, j] = score;
                    best[i, j] = previous = Math.Max(score, previous + gap);
                }
                else
                {
                    d[i, j] = double.NegativeInfinity;
                    best[i, j] = previous += gap;
                }
            }
        }

        return new FuzzyMatch(best[n - 1, m - 1], Backtrack(d, best, n, m, query, candidate));
    }

    /// <summary>Ranks items by score (descending), then by shorter key, then ordinally; non-matches are dropped.</summary>
    public static IReadOnlyList<(T Item, FuzzyMatch Match)> Rank<T>(string query, IEnumerable<T> items, Func<T, string> key)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(key);
        return [.. items
            .Select(item => (Item: item, Key: key(item), Match: Match(query, key(item))))
            .Where(x => x.Match is not null)
            .OrderByDescending(x => x.Match!.Score)
            .ThenBy(x => x.Key.Length)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => (x.Item, x.Match!))];
    }

    /// <summary>Lower-case base character, without diacritics.</summary>
    public static char Fold(char c)
    {
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        return char.ToLowerInvariant(decomposed[0]);
    }

    private static bool IsSubsequence(string query, string candidate)
    {
        var i = 0;
        foreach (var c in candidate)
        {
            if (i < query.Length && Fold(c) == Fold(query[i]))
            {
                i++;
            }
        }

        return i == query.Length;
    }

    private static double[] Bonuses(string candidate)
    {
        var bonus = new double[candidate.Length];
        var last = '/';
        for (var j = 0; j < candidate.Length; j++)
        {
            var c = candidate[j];
            bonus[j] = last switch
            {
                '/' or '\\' => BonusSlash,
                '-' or '_' or ' ' => BonusWord,
                '.' => BonusDot,
                _ when char.IsLower(last) && char.IsUpper(c) => BonusCapital,
                _ => 0,
            };
            last = c;
        }

        return bonus;
    }

    private static int[] Backtrack(double[,] d, double[,] best, int n, int m, string query, string candidate)
    {
        var positions = new int[n];
        var matchRequired = false;
        var j = m - 1;
        for (var i = n - 1; i >= 0; i--)
        {
            for (; j >= 0; j--)
            {
                if (!double.IsNegativeInfinity(d[i, j]) && (matchRequired || d[i, j] == best[i, j]))
                {
                    var caseBonus = query[i] == candidate[j] ? BonusExactCase : 0;
                    matchRequired = i > 0 && j > 0 && best[i, j] == d[i - 1, j - 1] + MatchConsecutive + caseBonus;
                    positions[i] = j--;
                    break;
                }
            }
        }

        return positions;
    }
}
