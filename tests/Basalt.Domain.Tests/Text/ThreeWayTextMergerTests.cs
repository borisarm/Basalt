using Basalt.Domain.Text;
using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Domain.Tests.Text;

public sealed class ThreeWayTextMergerTests
{
    private static readonly MergeOptions Labels = new("Laptop", "GitHub");

    // Compact notation: each character is one line terminated by "\n".
    private static string T(string compact) => string.Concat(compact.Select(c => c + "\n"));

    private static MergeResult Merge(string b, string o, string t) => ThreeWayTextMerger.Merge(T(b), T(o), T(t), Labels);

    public static TheoryData<string, string, string, string> CleanCases => new()
    {
        // identities
        { "abc", "abc", "abc", "abc" },
        { "abc", "aXc", "abc", "aXc" },
        { "abc", "abc", "aYc", "aYc" },
        { "abc", "aXc", "aXc", "aXc" },
        // edits in different places
        { "abcde", "Xbcde", "abcdY", "XbcdY" },
        { "abcdef", "aXcdef", "abcdYf", "aXcdYf" },
        // insertions in different places
        { "abc", "aXbc", "abYc", "aXbYc" },
        { "abc", "Xabc", "abcY", "XabcY" },
        // deletions in different places
        { "abcde", "bcde", "abcd", "bcd" },
        { "abcde", "acde", "abce", "ace" },
        // identical deletion on both sides
        { "abc", "ac", "ac", "ac" },
        // one side deletes all, other unchanged
        { "abc", "", "abc", "" },
        // empty base, one side empty
        { "", "", "xyz", "xyz" },
        { "", "xyz", "", "xyz" },
        // both sides append identical text
        { "a", "ab", "ab", "ab" },
        // adjacent but non-overlapping changes
        { "abcd", "aXcd", "abYd", "aXYd" },
        // insertion just before a changed line on the other side
        { "abc", "aXbc", "aYc", "aXYc" },
        // insertion just after a changed line on the other side
        { "abc", "abXc", "aYc", "aYXc" },
        // change at start and end
        { "abcdefg", "Xbcdefg", "abcdefY", "XbcdefY" },
        // larger independent hunks
        { "abcdefghij", "aXYZefghij", "abcdefgQRj", "aXYZefgQRj" },
    };

    [Theory]
    [MemberData(nameof(CleanCases))]
    [Trait("Spec", "DOM-10")]
    public void Merge_NonOverlappingChanges_IsClean(string b, string o, string t, string expected)
    {
        var result = Merge(b, o, t);

        result.Should().BeOfType<MergeResult.Clean>();
        result.Text.Should().Be(T(expected));
        result.IsClean.Should().BeTrue();
    }

    public static TheoryData<string, string, string> ConflictCases => new()
    {
        { "abc", "aXc", "aYc" },
        { "abc", "Xbc", "Ybc" },
        { "abc", "abX", "abY" },
        { "abc", "aXYc", "aZc" },
        { "", "x", "y" },
        { "", "xy", "xz" },
        { "abc", "aXbc", "aYbc" },
        { "abc", "ac", "aYc" },
        { "abcd", "aXXd", "abYd" },
        { "abc", "", "aYc" },
    };

    [Theory]
    [MemberData(nameof(ConflictCases))]
    [Trait("Spec", "DOM-10")]
    public void Merge_OverlappingDifferentChanges_IsConflicted(string b, string o, string t)
    {
        var result = Merge(b, o, t);

        result.Should().BeOfType<MergeResult.Conflicted>().Which.Regions.Should().NotBeEmpty();
        result.IsClean.Should().BeFalse();
        result.Text.Should().Contain("<<<<<<< Laptop\n").And.Contain("=======\n").And.Contain(">>>>>>> GitHub\n");
    }

    [Fact]
    [Trait("Spec", "SYNC-5")]
    public void Merge_Conflict_WritesMarkersAroundBothSides()
    {
        var result = ThreeWayTextMerger.Merge("uno\ndos\ntres\n", "uno\nDOS local\ntres\n", "uno\nDOS remoto\ntres\n", Labels);

        result.Text.Should().Be("uno\n<<<<<<< Laptop\nDOS local\n=======\nDOS remoto\n>>>>>>> GitHub\ntres\n");
        var region = ((MergeResult.Conflicted)result).Regions.Single();
        region.BaseLines.Should().Equal("dos\n");
        region.OursLines.Should().Equal("DOS local\n");
        region.TheirsLines.Should().Equal("DOS remoto\n");
        region.BaseStart.Should().Be(1);
    }

    [Fact]
    public void Merge_ConflictAndCleanRegions_KeepCleanChanges()
    {
        var result = Merge("abcdefg", "aXcdefZ", "aYcdefg");

        result.Text.Should().Be("a\n<<<<<<< Laptop\nX\n=======\nY\n>>>>>>> GitHub\nc\nd\ne\nf\nZ\n");
    }

    [Fact]
    public void Merge_MissingFinalNewline_MarkersStayOnTheirOwnLines()
    {
        var result = ThreeWayTextMerger.Merge("a\nb", "a\nX", "a\nY", Labels);

        result.Text.Should().Be("a\n<<<<<<< Laptop\nX\n=======\nY\n>>>>>>> GitHub\n");
    }

    [Fact]
    public void Merge_FinalNewlineAddedOnOneSide_IsClean()
    {
        ThreeWayTextMerger.Merge("a\nb", "a\nb\n", "a\nb", Labels).Text.Should().Be("a\nb\n");
        ThreeWayTextMerger.Merge("a\nb", "a\nb", "X\na\nb", Labels).Text.Should().Be("X\na\nb");
    }

    [Fact]
    [Trait("Spec", "DOM-10")]
    public void Merge_PreservesPredominantLineEndingOfOurs()
    {
        var result = ThreeWayTextMerger.Merge("a\r\nb\r\nc\r\n", "a\r\nb\r\nc\r\nours\r\n", "a\nB\nc\n", Labels);

        result.Text.Should().Be("a\r\nB\r\nc\r\nours\r\n");
    }

    [Fact]
    public void Merge_ConflictMarkersUseOursLineEnding()
    {
        var result = ThreeWayTextMerger.Merge("a\r\n", "x\r\n", "y\n", Labels);

        result.Text.Should().Be("<<<<<<< Laptop\r\nx\r\n=======\r\ny\r\n>>>>>>> GitHub\r\n");
    }

    [Fact]
    public void Merge_LineEndingOnlyDifferences_AreNotConflicts()
    {
        ThreeWayTextMerger.Merge("a\nb\n", "a\r\nb\r\n", "a\nX\n", Labels).Text.Should().Be("a\r\nX\r\n");
    }

    [Fact]
    public void Merge_DefaultLabels()
    {
        ThreeWayTextMerger.Merge("a\n", "x\n", "y\n").Text.Should().StartWith("<<<<<<< local\n").And.Contain(">>>>>>> remote\n");
    }

    [Fact]
    public void Merge_RejectsNulls()
    {
        FluentActions.Invoking(() => ThreeWayTextMerger.Merge(null!, "", "")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ThreeWayTextMerger.Merge("", null!, "")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ThreeWayTextMerger.Merge("", "", null!)).Should().Throw<ArgumentNullException>();
    }

    private static Gen<string> Doc() =>
        Gen.Choose(0, 10).SelectMany(n => Gen.ArrayOf(Gen.Elements("a", "b", "c", "d"), n)).Select(lines => T(string.Concat(lines)));

    /// <summary>Applies a random set of edits to a base document (some lines replaced, deleted or followed by inserts).</summary>
    private static Gen<string> EditOf(string @base) =>
        Gen.ArrayOf(Gen.Elements(0, 1, 2, 3), TextLines.Split(@base).Count + 1).SelectMany(ops =>
            Gen.ArrayOf(Gen.Elements("X", "Y", "Z", "a"), ops.Length).Select(words =>
            {
                var lines = TextLines.Split(@base);
                var result = new List<string>();
                for (var i = 0; i <= lines.Count; i++)
                {
                    var op = ops[i];
                    if (op == 3)
                    {
                        result.Add(words[i] + "\n");
                    }

                    if (i < lines.Count)
                    {
                        if (op == 1)
                        {
                            result.Add(words[i] + i + "\n");
                        }
                        else if (op != 2)
                        {
                            result.Add(lines[i]);
                        }
                    }
                }

                return string.Concat(result);
            }));

    private static Gen<(string Base, string Ours, string Theirs)> Triple() =>
        from b in Doc()
        from o in EditOf(b)
        from t in EditOf(b)
        select (b, o, t);

    [Fact]
    [Trait("Spec", "DOM-10")]
    public void Property_Identities()
    {
        Prop.ForAll(Triple().ToArbitrary(), x =>
            ThreeWayTextMerger.Merge(x.Base, x.Ours, x.Base) == new MergeResult.Clean(x.Ours)
            && ThreeWayTextMerger.Merge(x.Base, x.Base, x.Theirs) == new MergeResult.Clean(x.Theirs)
            && ThreeWayTextMerger.Merge(x.Base, x.Ours, x.Ours) == new MergeResult.Clean(x.Ours)).QuickCheckThrowOnFailure();
    }

    [Fact]
    [Trait("Spec", "DOM-10")]
    public void Property_CleanMergeContainsEveryAddedLineOfBothSides()
    {
        Prop.ForAll(Triple().ToArbitrary(), x =>
        {
            var result = ThreeWayTextMerger.Merge(x.Base, x.Ours, x.Theirs);
            if (!result.IsClean)
            {
                return true;
            }

            var merged = Count(TextLines.Split(result.Text));
            var baseCounts = Count(TextLines.Split(x.Base));
            return Added(x.Ours, baseCounts).Concat(Added(x.Theirs, baseCounts)).All(line => merged.ContainsKey(line));
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Property_ConflictedMergeKeepsBothVersionsOfEveryRegion()
    {
        Prop.ForAll(Triple().ToArbitrary(), x =>
        {
            var result = ThreeWayTextMerger.Merge(x.Base, x.Ours, x.Theirs);
            return result is not MergeResult.Conflicted conflicted
                || conflicted.Regions.All(r => r.OursLines.Concat(r.TheirsLines).All(l => result.Text.Contains(TextLines.WithoutTerminator(l), StringComparison.Ordinal)));
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Generator_ProducesBothCleanAndConflictedMerges()
    {
        var samples = Triple().Sample(10, new FsCheck.Rnd(20261019), 300).Select(x => ThreeWayTextMerger.Merge(x.Base, x.Ours, x.Theirs)).ToList();

        samples.Should().Contain(r => r.IsClean).And.Contain(r => !r.IsClean);
    }

    private static Dictionary<string, int> Count(IEnumerable<string> lines) =>
        lines.GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static IEnumerable<string> Added(string side, Dictionary<string, int> baseCounts) =>
        Count(TextLines.Split(side)).Where(kv => kv.Value > baseCounts.GetValueOrDefault(kv.Key)).Select(kv => kv.Key);
}
