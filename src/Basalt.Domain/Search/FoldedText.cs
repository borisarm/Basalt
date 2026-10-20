using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Basalt.Domain.Search;

/// <summary>
/// Text folded for case- and diacritic-insensitive comparison (lower-case invariant, combining marks removed), with a
/// map from every folded character back to the original UTF-16 range it came from.
/// </summary>
public sealed class FoldedText
{
    private static readonly ConcurrentDictionary<int, string> FoldedRunes = new();

    private readonly int[] _starts;
    private readonly int[] _ends;

    private FoldedText(string original, string value, int[] starts, int[] ends)
    {
        Original = original;
        Value = value;
        _starts = starts;
        _ends = ends;
    }

    public string Original { get; }

    public string Value { get; }

    public static FoldedText Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var starts = new List<int>(text.Length);
        var ends = new List<int>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            var width = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            if (c < 0x80)
            {
                builder.Append(char.ToLowerInvariant(c));
                starts.Add(i);
                ends.Add(i + 1);
            }
            else
            {
                foreach (var folded in FoldRune(width == 1 ? c : char.ConvertToUtf32(c, text[i + 1])))
                {
                    builder.Append(folded);
                    starts.Add(i);
                    ends.Add(i + width);
                }
            }

            i += width;
        }

        return new FoldedText(text, builder.ToString(), [.. starts], [.. ends]);
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

    /// <summary>Maps a folded range to the original text; trailing combining marks belong to the last character.</summary>
    public (int Start, int Length) ToOriginal(int start, int length)
    {
        var originalStart = _starts[start];
        var originalEnd = _ends[start + length - 1];
        while (originalEnd < Original.Length && IsMark(Original[originalEnd]))
        {
            originalEnd++;
        }

        return (originalStart, originalEnd - originalStart);
    }

    private static string FoldRune(int rune) => FoldedRunes.GetOrAdd(rune, static r =>
    {
        var builder = new StringBuilder();
        foreach (var c in char.ConvertFromUtf32(r).Normalize(NormalizationForm.FormD))
        {
            if (!IsMark(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().ToLowerInvariant();
    });

    private static bool IsMark(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
}
