using Basalt.Application.Ports.Index;
using Basalt.Application.Search;
using Basalt.Domain.Notes;
using Basalt.Domain.Search;

namespace Basalt.Application.Tests.Search;

public sealed class SearchRankingTests
{
    private static readonly DateTimeOffset Old = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset New = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static SearchHit Hit(string path, double score, DateTimeOffset? when = null) =>
        new(P(path), FileKind.Note, when ?? Old, [], score);

    [Fact]
    public void Score_AddsOnePerRangeAndABonusForFileNameMatches()
    {
        SearchRanking.Score(new SearchMatch([])).Should().Be(0);
        SearchRanking.Score(new SearchMatch([new MatchRange(MatchTarget.Content, 0, 1), new MatchRange(MatchTarget.Path, 2, 1)]))
            .Should().Be(2);
        SearchRanking.Score(new SearchMatch([new MatchRange(MatchTarget.FileName, 0, 1)]))
            .Should().Be(11);
    }

    [Fact]
    public void Order_ByRelevance_UsesScoreThenPath()
    {
        var hits = new[] { Hit("b.md", 5), Hit("a.md", 5), Hit("z.md", 9) };

        SearchRanking.Order(hits, SearchOptions.Default).Select(h => h.Path.ToString())
            .Should().Equal("z.md", "a.md", "b.md");
    }

    [Fact]
    public void Order_ByName_UsesTheNameIgnoringCaseThenPath()
    {
        var hits = new[] { Hit("z/b.md", 0), Hit("a/B.md", 0), Hit("a/a.md", 0) };

        SearchRanking.Order(hits, new SearchOptions(SearchSort.NameAscending)).Select(h => h.Path.ToString())
            .Should().Equal("a/a.md", "a/B.md", "z/b.md");
        SearchRanking.Order(hits, new SearchOptions(SearchSort.NameDescending)).Select(h => h.Path.ToString())
            .Should().Equal("a/B.md", "z/b.md", "a/a.md");
    }

    [Fact]
    public void Order_ByModification_UsesTheTimestampAndThenPath()
    {
        var hits = new[] { Hit("b.md", 0, Old), Hit("a.md", 0, New), Hit("c.md", 0, Old) };

        SearchRanking.Order(hits, new SearchOptions(SearchSort.ModifiedNewest)).Select(h => h.Path.ToString())
            .Should().Equal("a.md", "b.md", "c.md");
        SearchRanking.Order(hits, new SearchOptions(SearchSort.ModifiedOldest)).Select(h => h.Path.ToString())
            .Should().Equal("b.md", "c.md", "a.md");
    }

    [Fact]
    public void Order_AppliesTheLimitAfterOrdering()
    {
        var hits = new[] { Hit("a.md", 1), Hit("b.md", 3), Hit("c.md", 2) };

        SearchRanking.Order(hits, new SearchOptions(Limit: 2)).Select(h => h.Path.ToString())
            .Should().Equal("b.md", "c.md");
    }

    [Fact]
    public void RankingAndOrdering_RejectNulls()
    {
        FluentActions.Invoking(() => SearchRanking.Score(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SearchRanking.Order(null!, SearchOptions.Default)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SearchRanking.Order([], null!)).Should().Throw<ArgumentNullException>();
    }
}

