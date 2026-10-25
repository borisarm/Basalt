namespace Basalt.Adapters.Markdown;

/// <summary>
/// Word count (spec 05 §7): runs of letters/digits separated by spaces or punctuation; every CJK character counts as a
/// word. Callers pass only visible text (no frontmatter, syntax or code).
/// </summary>
internal static class WordCounter
{
    public static int Count(IEnumerable<string> fragments)
    {
        var count = 0;
        foreach (var fragment in fragments)
        {
            var inWord = false;
            foreach (var rune in fragment.EnumerateRunes())
            {
                if (IsCjk(rune.Value))
                {
                    count++;
                    inWord = false;
                }
                else if (System.Text.Rune.IsLetterOrDigit(rune))
                {
                    if (!inWord)
                    {
                        count++;
                        inWord = true;
                    }
                }
                else if (rune.Value is '\'' or '\u2019' || IsCombiningMark(rune))
                {
                    // An apostrophe inside a word and combining marks do not split it.
                }
                else
                {
                    inWord = false;
                }
            }
        }

        return count;
    }

    private static bool IsCombiningMark(System.Text.Rune rune) =>
        System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark
            or System.Globalization.UnicodeCategory.EnclosingMark;

    private static bool IsCjk(int codePoint) =>
        codePoint is (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x20000 and <= 0x2A6DF)
            or (>= 0x3040 and <= 0x30FF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF);
}
