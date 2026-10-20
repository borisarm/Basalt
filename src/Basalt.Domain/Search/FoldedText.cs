using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Basalt.Domain.Search;

/// <summary>
/// Text folded for case- and diacritic-insensitive comparison (lower-case invariant, combining marks removed), with a
/// map from every folded character back to the original UTF-16 position it came from. Combining marks that fold to
/// nothing belong to the preceding character.
/// </summary>
public sealed class FoldedText
{
    private static readonly string?[] FoldedChars = new string?[char.MaxValue + 1];
    private static readonly ConcurrentDictionary<int, string> FoldedSupplementary = new();

    /// <summary>For each folded character, the start of the original character it comes from.</summary>
    private readonly int[] _starts;

    private FoldedText(string original, string value, int[] starts)
    {
        Original = original;
        Value = value;
        _starts = starts;
    }

    public string Original { get; }

    public string Value { get; }

    public static FoldedText Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = new char[text.Length];
        var starts = new int[text.Length];
        var count = 0;
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c < 0x80)
            {
                value[count] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
                starts[count++] = i++;
                continue;
            }

            var width = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var folded = width == 1 ? FoldChar(c) : FoldSupplementary(char.ConvertToUtf32(c, text[i + 1]));
            if (count + folded.Length > value.Length)
            {
                Array.Resize(ref value, Math.Max(value.Length * 2, count + folded.Length));
                Array.Resize(ref starts, value.Length);
            }

            foreach (var f in folded)
            {
                value[count] = f;
                starts[count++] = i;
            }

            i += width;
        }

        return new FoldedText(text, new string(value, 0, count), starts);
    }

    /// <summary>Folds a search operand the same way as the text.</summary>
    public static string Fold(string text) => Create(text).Value;

    /// <summary>Every non-overlapping occurrence of an already folded, non-empty <paramref name="needle"/>, as original ranges.</summary>
    public IEnumerable<(int Start, int Length)> FindAll(string needle)
    {
        ArgumentException.ThrowIfNullOrEmpty(needle);
        for (var index = Value.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = Value.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            yield return ToOriginal(index, needle.Length);
        }
    }

    public bool Contains(string needle) => Value.Contains(needle, StringComparison.Ordinal);

    /// <summary>Maps a folded range to the original text, up to where the next folded character's source starts.</summary>
    public (int Start, int Length) ToOriginal(int start, int length)
    {
        var originalStart = _starts[start];
        var last = _starts[start + length - 1];
        var next = start + length;
        while (next < Value.Length && _starts[next] == last)
        {
            next++; // the rest of a multi-character folding (e.g. "ﬁ" → "fi")
        }

        var originalEnd = next < Value.Length ? _starts[next] : Original.Length;
        return (originalStart, originalEnd - originalStart);
    }

    private static string FoldChar(char c) => FoldedChars[c] ??= char.IsSurrogate(c) ? c.ToString() : FoldRune(c);

    private static string FoldSupplementary(int rune) => FoldedSupplementary.GetOrAdd(rune, FoldRune);

    private static string FoldRune(int rune)
    {
        var builder = new StringBuilder();
        foreach (var c in char.ConvertFromUtf32(rune).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().ToLowerInvariant();
    }
}
