using System.Globalization;

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
            var enumerator = StringInfo.GetTextElementEnumerator(fragment);
            while (enumerator.MoveNext())
            {
                var element = (string)enumerator.Current;
                var rune = System.Text.Rune.GetRuneAt(element, 0);
                if (IsCjk(rune.Value))
                {
                    count++;
                    inWord = false;
                }
                else if (System.Text.Rune.IsLetterOrDigit(rune) || (inWord && element is "'" or "’"))
                {
                    if (!inWord)
                    {
                        count++;
                        inWord = true;
                    }
                }
                else
                {
                    inWord = false;
                }
            }
        }

        return count;
    }

    private static bool IsCjk(int codePoint) =>
        codePoint is (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x20000 and <= 0x2A6DF)
            or (>= 0x3040 and <= 0x30FF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF);
}
