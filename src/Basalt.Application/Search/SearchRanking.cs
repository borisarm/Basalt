using Basalt.Application.Ports.Index;
using Basalt.Domain.Search;

namespace Basalt.Application.Search;

/// <summary>Scoring and ordering shared by every <see cref="INoteIndexReader"/> so that results are identical.</summary>
public static class SearchRanking
{
    private const double FileNameBonus = 10;

    /// <summary>One point per highlighted range, plus a bonus when the file name matches.</summary>
    public static double Score(SearchMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return match.Ranges.Count + (match.Ranges.Any(r => r.Target == MatchTarget.FileName) ? FileNameBonus : 0);
    }

    public static IReadOnlyList<SearchHit> Order(IEnumerable<SearchHit> hits, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(options);
        var byName = StringComparer.OrdinalIgnoreCase;
        var ordered = options.Sort switch
        {
            SearchSort.NameAscending => hits.OrderBy(h => h.Path.Name, byName),
            SearchSort.NameDescending => hits.OrderByDescending(h => h.Path.Name, byName),
            SearchSort.ModifiedNewest => hits.OrderByDescending(h => h.LastWriteUtc),
            SearchSort.ModifiedOldest => hits.OrderBy(h => h.LastWriteUtc),
            _ => hits.OrderByDescending(h => h.Score),
        };

        var result = ordered.ThenBy(h => h.Path.ToString(), StringComparer.Ordinal);
        return options.Limit is { } limit ? [.. result.Take(limit)] : [.. result];
    }
}
