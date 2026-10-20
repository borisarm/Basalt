using Basalt.Domain.Links;
using Basalt.Domain.Notes;
using Basalt.Testing.Generators;
using FsCheck;
using FsCheck.Fluent;

namespace Basalt.Domain.Tests.Links;

public sealed class MentionFinderTests
{
    private static List<string> Find(string text, params string[] names) =>
        [.. MentionFinder.Find(text, names, CodeRegions.Find(text)).Select(m => text.Substring(m.Range.Start, m.Range.Length))];

    [Fact]
    [Trait("Spec", "F-005.2")]
    public void FindsTitle_CaseInsensitive_AsWholeWords()
    {
        Find("hablé de proyecto x ayer", "Proyecto X").Should().Equal("proyecto x");
        Find("PROYECTO X. Y proyecto x", "Proyecto X").Should().Equal("PROYECTO X", "proyecto x");
    }

    [Theory]
    [InlineData("proyectos x")]
    [InlineData("subproyecto x")]
    [InlineData("proyecto xy")]
    [InlineData("proyecto_x")]
    [InlineData("proyecto x1")]
    [InlineData("proyectox")]
    public void IgnoresPartialWords(string text)
    {
        Find(text, "Proyecto X", "Proyecto").Should().NotContain(m => m.Equals("proyecto x", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WordBoundaries_UseLettersAndDigitsOfAnyScript()
    {
        Find("añoproyecto", "proyecto").Should().BeEmpty();
        Find("(proyecto), «proyecto» #proyecto", "proyecto").Should().HaveCount(3);
        Find("proyectoñ", "proyecto").Should().BeEmpty();
    }

    [Fact]
    public void PrefersTheLongestName_WithoutOverlaps()
    {
        Find("el proyecto x y el proyecto", "Proyecto", "Proyecto X").Should().Equal("proyecto x", "proyecto");
    }

    [Fact]
    public void ReportsWhichNameMatched()
    {
        var mentions = MentionFinder.Find("ver PX hoy", ["Proyecto X", "PX"], []);

        mentions.Should().ContainSingle().Which.Should().Be(new Mention(new TextRange(4, 2), "PX"));
    }

    [Fact]
    public void SkipsExcludedRanges()
    {
        const string Text = "[[Proyecto X]] y proyecto x";

        MentionFinder.Find(Text, ["Proyecto X"], [new TextRange(0, 14)]).Select(m => m.Range.Start).Should().Equal(17);
    }

    [Fact]
    public void SkipsCode()
    {
        Find("`proyecto` y proyecto", "proyecto").Should().ContainSingle();
        Find("```\nproyecto\n```\nproyecto", "proyecto").Should().ContainSingle();
        Find("~~~js\nproyecto\n~~~\n", "proyecto").Should().BeEmpty();
    }

    [Fact]
    public void IgnoresEmptyNames()
    {
        Find("algo", "", "  ").Should().BeEmpty();
    }

    [Theory]
    [InlineData("a `code` b", new[] { 2, 6 })]
    [InlineData("a ``co`de`` b", new[] { 2, 9 })]
    [InlineData("unclosed `code", new int[0])]
    [InlineData("`a`\n\n`b`", new[] { 0, 3, 5, 3 })]
    [InlineData("`a\n\nb`", new int[0])]
    [InlineData("`a\nb`", new[] { 0, 5 })]
    [InlineData("```\ncode\n```\nafter", new[] { 0, 12 })]
    [InlineData("   ```\ncode\n   ```", new[] { 0, 18 })]
    [InlineData("    ```\nnot a fence", new int[0])]
    [InlineData("````\n```\n````\nx", new[] { 0, 13 })]
    [InlineData("~~~\n```\nstill code", new[] { 0, 18 })]
    [InlineData("```\nunclosed", new[] { 0, 12 })]
    [InlineData("``` a\nx\n```", new[] { 0, 11 })]
    [InlineData("text ``` not a fence", new int[0])]
    [InlineData("```\r\nx\r\n```\r\ny", new[] { 0, 11 })]
    public void CodeRegions_FencesAndInlineSpans(string text, int[] flat)
    {
        var expected = flat.Chunk(2).Select(p => new TextRange(p[0], p[1]));

        CodeRegions.Find(text).Should().Equal(expected);
    }

    [Fact]
    public void TextRange_Overlaps()
    {
        new TextRange(0, 2).Overlaps(new TextRange(1, 1)).Should().BeTrue();
        new TextRange(0, 2).Overlaps(new TextRange(2, 1)).Should().BeFalse();
        new TextRange(2, 1).Overlaps(new TextRange(0, 2)).Should().BeFalse();
        new TextRange(1, 3).End.Should().Be(4);
    }

    [BasaltProperty]
    public Property Mentions_AreWholeWordsOutsideExclusions_AndDoNotOverlap()
    {
        var pieces = Gen.Elements("proyecto", "Proyecto X", "x", " ", "`", "\n", "a", "ñ", "[[", "]]", "PX");
        return Prop.ForAll(
            Gen.ListOf(pieces).Select(p => string.Concat(p)).ToArbitrary(),
            text =>
            {
                var code = CodeRegions.Find(text);
                var mentions = MentionFinder.Find(text, ["Proyecto X", "proyecto", "PX"], code);
                return mentions.All(m =>
                        (m.Range.Start == 0 || !char.IsLetterOrDigit(text[m.Range.Start - 1]))
                        && (m.Range.End == text.Length || !char.IsLetterOrDigit(text[m.Range.End]))
                        && string.Equals(text.Substring(m.Range.Start, m.Range.Length), m.Name, StringComparison.OrdinalIgnoreCase)
                        && !code.Any(c => c.Overlaps(m.Range)))
                    && mentions.Zip(mentions.Skip(1)).All(p => p.First.Range.End <= p.Second.Range.Start);
            });
    }
}
