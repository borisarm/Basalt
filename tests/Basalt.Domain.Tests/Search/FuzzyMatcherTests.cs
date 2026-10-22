using Basalt.Domain.Search;
using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Domain.Tests.Search;

public sealed class FuzzyMatcherTests
{
    [Theory]
    [InlineData("pro", "Proyecto X", new[] { 0, 1, 2 })]
    [InlineData("px", "Proyecto X", new[] { 0, 9 })]
    [InlineData("reunion", "Reunión semanal", new[] { 0, 1, 2, 3, 4, 5, 6 })]
    [InlineData("ds", "Daily/Sprint", new[] { 0, 6 })]
    [InlineData("abc", "a-b-c", new[] { 0, 2, 4 })]
    [Trait("Spec", "DOM-7")]
    public void Match_ReturnsPositions(string query, string candidate, int[] positions)
    {
        FuzzyMatcher.Match(query, candidate)!.Positions.Should().Equal(positions);
    }

    [Theory]
    [InlineData("xyz", "Proyecto")]
    [InlineData("oyp", "Proyecto")]
    [InlineData("toolong", "tool")]
    public void Match_NonSubsequence_IsNull(string query, string candidate)
    {
        FuzzyMatcher.Match(query, candidate).Should().BeNull();
    }

    [Fact]
    public void Match_EmptyQuery_MatchesEverythingWithZeroScore()
    {
        var match = FuzzyMatcher.Match("", "anything")!;

        match.Score.Should().Be(0);
        match.Positions.Should().BeEmpty();
    }

    [Fact]
    public void Match_ExactCaseInsensitive_IsMaximum()
    {
        FuzzyMatcher.Match("proyecto x", "Proyecto X")!.Score.Should().Be(double.PositiveInfinity);
    }

    [Fact]
    [Trait("Spec", "F-004.1")]
    public void Rank_OrdersByScore_WordStartsAndConsecutiveMatchesFirst()
    {
        string[] candidates = ["Improvement", "p r o", "prototype notes", "Proyecto X", "Otra cosa"];

        var ranked = FuzzyMatcher.Rank("pro", candidates, c => c).Select(r => r.Item).ToList();

        // "prototype notes" wins over "Proyecto X" by the exact-case bonus of its lower-case "p".
        ranked.Should().Equal("prototype notes", "Proyecto X", "p r o", "Improvement");
    }

    [Fact]
    public void Rank_TiesBreakByLengthThenOrdinal()
    {
        var ranked = FuzzyMatcher.Rank("ab", ["ab cd", "ab", "AB"], c => c).Select(r => r.Item).ToList();

        ranked.Should().Equal("AB", "ab", "ab cd");
    }

    [Fact]
    public void Match_PrefersCaseExactMatches()
    {
        var exactCase = FuzzyMatcher.Match("Pro", "Pro notes")!.Score;
        var otherCase = FuzzyMatcher.Match("Pro", "pro notes")!.Score;

        exactCase.Should().BeGreaterThan(otherCase);
    }

    [Fact]
    public void Match_WordStartBonuses()
    {
        FuzzyMatcher.Match("n", "a note")!.Score.Should().BeGreaterThan(FuzzyMatcher.Match("n", "anote")!.Score);
        FuzzyMatcher.Match("n", "a/note")!.Score.Should().BeGreaterThan(FuzzyMatcher.Match("n", "a note")!.Score);
        FuzzyMatcher.Match("n", "aNote")!.Score.Should().BeGreaterThan(FuzzyMatcher.Match("n", "anote")!.Score);
        FuzzyMatcher.Match("n", "a.note")!.Score.Should().BeGreaterThan(FuzzyMatcher.Match("n", "anote")!.Score);
        FuzzyMatcher.Match("n", "a_note")!.Score.Should().Be(FuzzyMatcher.Match("n", "a-note")!.Score);
    }

    [Fact]
    public void RejectsNulls()
    {
        FluentActions.Invoking(() => FuzzyMatcher.Match(null!, "x")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => FuzzyMatcher.Match("x", null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => FuzzyMatcher.Rank<string>("x", null!, c => c)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => FuzzyMatcher.Rank("x", ["a"], null!)).Should().Throw<ArgumentNullException>();
    }

    private static Gen<string> Word() =>
        Gen.Choose(1, 10).SelectMany(n => Gen.ArrayOf(Gen.Elements('a', 'b', 'A', ' ', '/', '-', 'é', '.'), n)).Select(cs => new string(cs));

    [Fact]
    [Trait("Spec", "DOM-7")]
    public void Property_ExactMatchScoresAtLeastAnyPartialMatch()
    {
        Prop.ForAll(Word().ToArbitrary(), Word().ToArbitrary(), (query, other) =>
        {
            var exact = FuzzyMatcher.Match(query, query)!.Score;
            var partial = FuzzyMatcher.Match(query, other);
            return partial is null || exact >= partial.Score;
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Property_PositionsAreIncreasingAndMatchQuery()
    {
        Prop.ForAll(Word().ToArbitrary(), Word().ToArbitrary(), (query, candidate) =>
        {
            var match = FuzzyMatcher.Match(query, candidate);
            if (match is null)
            {
                return true;
            }

            return match.Positions.Count == query.Length
                && match.Positions.Zip(match.Positions.Skip(1)).All(p => p.First < p.Second)
                && match.Positions.Select((p, i) => FuzzyMatcher.Fold(candidate[p]) == FuzzyMatcher.Fold(query[i])).All(x => x);
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Property_MatchesIffQueryIsFoldedSubsequence()
    {
        Prop.ForAll(Word().ToArbitrary(), Word().ToArbitrary(), (query, candidate) =>
        {
            var j = 0;
            foreach (var c in candidate)
            {
                if (j < query.Length && FuzzyMatcher.Fold(c) == FuzzyMatcher.Fold(query[j]))
                {
                    j++;
                }
            }

            return (FuzzyMatcher.Match(query, candidate) is not null) == (j == query.Length);
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Spec", "NFR-005")]
    public void RankingTenThousandTitles_IsUnderFiftyMilliseconds()
    {
        var titles = Enumerable.Range(0, 10_000).Select(index => $"Nota {index:00000} del vault").ToList();
        FuzzyMatcher.Rank("nota1234", titles, title => title);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ranked = FuzzyMatcher.Rank("nota1234", titles, title => title);
        watch.Stop();

        ranked.Should().NotBeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(50), "spec NFR-005: quick switcher per keystroke");
    }
}
