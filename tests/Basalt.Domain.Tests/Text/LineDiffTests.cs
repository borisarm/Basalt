using Basalt.Domain.Text;
using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Domain.Tests.Text;

public sealed class LineDiffTests
{
    private static string[] L(string compact) => compact.Length == 0 ? [] : [.. compact.Select(c => c + "\n")];

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("abc", "abd", 2)]
    [InlineData("abcabba", "cbabac", 5)]
    [InlineData("abc", "xabcx", 2)]
    [InlineData("abcdef", "abxdef", 2)]
    [InlineData("aaaa", "aa", 2)]
    [InlineData("abab", "baba", 2)]
    [InlineData("abcdefghij", "jihgfedcba", 18)]
    [Trait("Spec", "DOM-10")]
    public void Diff_ProducesMinimalEditScript(string a, string b, int expectedEdits)
    {
        var hunks = LineDiff.Diff(L(a), L(b));

        hunks.Sum(h => h.OldCount + h.NewCount).Should().Be(expectedEdits);
        LineDiff.Apply(L(a), hunks).Should().Equal(L(b));
    }

    [Fact]
    public void Diff_ClassicMyersExample_HasExpectedHunks()
    {
        var hunks = LineDiff.Diff(L("abcabba"), L("cbabac"));

        hunks.Should().AllSatisfy(h => (h.OldCount + h.NewCount).Should().BePositive());
        hunks.Select(h => h.OldStart).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Diff_SingleReplacement_IsOneHunk()
    {
        var hunks = LineDiff.Diff(["a\n", "b\n", "c\n"], ["a\n", "X\n", "c\n"]);

        var hunk = hunks.Should().ContainSingle().Subject;
        hunk.OldStart.Should().Be(1);
        hunk.OldCount.Should().Be(1);
        hunk.NewStart.Should().Be(1);
        hunk.NewCount.Should().Be(1);
        hunk.NewLines.Should().Equal("X\n");
        hunk.OldEnd.Should().Be(2);
        hunk.NewEnd.Should().Be(2);
    }

    [Fact]
    public void Diff_PureInsertionAndDeletion()
    {
        var insert = LineDiff.Diff(["a\n", "c\n"], ["a\n", "b\n", "c\n"]).Single();
        var delete = LineDiff.Diff(["a\n", "b\n", "c\n"], ["a\n", "c\n"]).Single();

        insert.Should().BeEquivalentTo(new { OldStart = 1, OldCount = 0, NewStart = 1, NewCount = 1 });
        delete.Should().BeEquivalentTo(new { OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 0 });
    }

    [Fact]
    public void Diff_IgnoringLineEndings_TreatsCrLfAndLfAsEqual()
    {
        string[] a = ["a\r\n", "b\r\n"];
        string[] b = ["a\n", "b\n"];

        LineDiff.Diff(a, b).Should().ContainSingle();
        LineDiff.Diff(a, b, ignoreLineEndings: true).Should().BeEmpty();
        LineDiff.Diff(["a"], ["a\n"], ignoreLineEndings: true).Should().BeEmpty();
    }

    [Fact]
    public void Diff_LargeCompletelyDifferentInputs_CompletesQuickly()
    {
        var a = Enumerable.Range(0, 20_000).Select(i => $"a{i}\n").ToArray();
        var b = Enumerable.Range(0, 20_000).Select(i => $"b{i}\n").ToArray();

        var hunks = LineDiff.Diff(a, b);

        LineDiff.Apply(a, hunks).Should().Equal(b);
    }

    [Fact]
    public void Diff_LargeSimilarInputs_FindsLocalChanges()
    {
        var a = Enumerable.Range(0, 50_000).Select(i => $"line {i}\n").ToList();
        var b = a.ToList();
        b[100] = "changed\n";
        b.Insert(30_000, "inserted\n");
        b.RemoveAt(45_000);

        var hunks = LineDiff.Diff(a, b);

        hunks.Should().HaveCount(3);
        LineDiff.Apply(a, hunks).Should().Equal(b);
    }

    [Fact]
    public void Apply_RejectsHunksThatDoNotFit()
    {
        var hunk = new DiffHunk(5, 1, 0, 0, []);

        FluentActions.Invoking(() => LineDiff.Apply(["a\n"], [hunk])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Diff_RejectsNulls()
    {
        FluentActions.Invoking(() => LineDiff.Diff(null!, [])).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LineDiff.Diff([], null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LineDiff.Apply(null!, [])).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LineDiff.Apply([], null!)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\n", new[] { "a\n" })]
    [InlineData("a\nb", new[] { "a\n", "b" })]
    [InlineData("a\r\nb\r\n", new[] { "a\r\n", "b\r\n" })]
    [InlineData("a\rb", new[] { "a\rb" })]
    [InlineData("\n\n", new[] { "\n", "\n" })]
    public void TextLines_SplitKeepsTerminators(string text, string[] expected)
    {
        var lines = TextLines.Split(text);

        lines.Should().Equal(expected);
        string.Concat(lines).Should().Be(text);
    }

    [Theory]
    [InlineData("a\r\n", "a")]
    [InlineData("a\n", "a")]
    [InlineData("a", "a")]
    [InlineData("a\r", "a\r")]
    public void TextLines_WithoutTerminator(string line, string expected)
    {
        TextLines.WithoutTerminator(line).Should().Be(expected);
    }

    private static Gen<string[]> SmallAlphabetLines() =>
        Gen.Choose(0, 12).SelectMany(n => Gen.ArrayOf(Gen.Elements("a\n", "b\n", "c\n", "a", "\n"), n));

    [Fact]
    [Trait("Spec", "DOM-10")]
    public void Property_ApplyOfDiff_ReconstructsTarget()
    {
        Prop.ForAll(SmallAlphabetLines().ToArbitrary(), SmallAlphabetLines().ToArbitrary(), (a, b) =>
            LineDiff.Apply(a, LineDiff.Diff(a, b)).SequenceEqual(b)).QuickCheckThrowOnFailure();
    }

    [Fact]
    [Trait("Spec", "DOM-10")]
    public void Property_DiffIsMinimal()
    {
        Prop.ForAll(SmallAlphabetLines().ToArbitrary(), SmallAlphabetLines().ToArbitrary(), (a, b) =>
            LineDiff.Diff(a, b).Sum(h => h.OldCount + h.NewCount) == a.Length + b.Length - (2 * Lcs(a, b))).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Property_HunksAreOrderedAndNonAdjacent()
    {
        Prop.ForAll(SmallAlphabetLines().ToArbitrary(), SmallAlphabetLines().ToArbitrary(), (a, b) =>
        {
            var hunks = LineDiff.Diff(a, b);
            return hunks.Zip(hunks.Skip(1)).All(p => p.Second.OldStart > p.First.OldEnd && p.Second.NewStart > p.First.NewEnd);
        }).QuickCheckThrowOnFailure();
    }

    private static int Lcs(string[] a, string[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        return dp[a.Length, b.Length];
    }
}
