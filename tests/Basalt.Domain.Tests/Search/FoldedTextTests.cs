using Basalt.Domain.Search;
using Basalt.Testing.Generators;
using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Domain.Tests.Search;

public sealed class FoldedTextTests
{
    [Theory]
    [InlineData("Reunión", "reunion")]
    [InlineData("CAFÉ", "cafe")]
    [InlineData("Café", "cafe")]
    [InlineData("ÅNGSTRÖM", "angstrom")]
    [InlineData("ﬁn", "ﬁn")]
    [InlineData("日本語", "日本語")]
    [InlineData("😀A", "😀a")]
    [InlineData("a\ud800b", "a\ud800b")]
    [InlineData("b\udc00", "b\udc00")]
    public void Fold(string text, string folded)
    {
        FoldedText.Fold(text).Should().Be(folded);
    }

    [Theory]
    [InlineData("Café solo", "cafe", 0, 5)]
    [InlineData("xÉy", "e", 1, 1)]
    [InlineData("a😀b", "b", 3, 1)]
    [InlineData("a😀b", "😀", 1, 2)]
    [InlineData("añ̃", "n", 1, 2)]
    public void FindAll_MapsToOriginalRanges(string text, string needle, int start, int length)
    {
        FoldedText.Create(text).FindAll(needle).Should().Equal((start, length));
    }

    [Fact]
    public void FindAll_ReturnsEveryNonOverlappingOccurrence()
    {
        FoldedText.Create("AaAa").FindAll("aa").Should().Equal((0, 2), (2, 2));
        FoldedText.Create("abc").Contains("bc").Should().BeTrue();
    }

    [BasaltProperty]
    public Property Ranges_AreWithinTheOriginal_AndFoldToTheNeedle()
    {
        var chars = Gen.Elements('a', 'B', 'é', 'É', '́', 'ñ', '日', ' ', 'ß');
        return Prop.ForAll(
            Gen.ArrayOf(chars).Select(c => new string(c)).ToArbitrary(),
            Gen.Elements("a", "e", "n", "b ", "日", "ss", "ß").ToArbitrary(),
            (text, needle) => FoldedText.Create(text).FindAll(needle).All(r =>
                r.Start >= 0 && r.Start + r.Length <= text.Length && FoldedText.Fold(text.Substring(r.Start, r.Length)).StartsWith(needle, StringComparison.Ordinal)));
    }
}
