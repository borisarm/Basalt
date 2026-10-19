namespace Basalt.Domain.Text;

/// <summary>
/// Minimal line diff using Myers' O(ND) algorithm in its linear-space form (bisection on the middle snake), so that
/// large, very different documents do not need O(N·D) memory (spec 02 §10). Comparison is ordinal, optionally
/// ignoring "\n" vs "\r\n".
/// </summary>
public static class LineDiff
{
    public static IReadOnlyList<DiffHunk> Diff(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, bool ignoreLineEndings = false)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);
        IEqualityComparer<string> comparer = ignoreLineEndings ? LineEndingInsensitiveComparer.Instance : StringComparer.Ordinal;

        // Map lines to integer ids so that the inner loops compare ints.
        var ids = new Dictionary<string, int>(comparer);
        var a = oldLines.Select(l => Id(ids, l)).ToArray();
        var b = newLines.Select(l => Id(ids, l)).ToArray();

        var edits = new List<(int OldIndex, int NewIndex, bool IsDelete)>();
        Compare(a, 0, a.Length, b, 0, b.Length, edits);
        return ToHunks(edits, newLines);
    }

    /// <summary>Applies hunks produced by <see cref="Diff"/> to the old lines.</summary>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> oldLines, IReadOnlyList<DiffHunk> hunks)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(hunks);
        var result = new List<string>(oldLines.Count);
        var position = 0;
        foreach (var hunk in hunks)
        {
            if (hunk.OldStart < position || hunk.OldEnd > oldLines.Count)
            {
                throw new ArgumentException("Hunks do not fit the lines they are applied to.", nameof(hunks));
            }

            for (var i = position; i < hunk.OldStart; i++)
            {
                result.Add(oldLines[i]);
            }

            result.AddRange(hunk.NewLines);
            position = hunk.OldEnd;
        }

        for (var i = position; i < oldLines.Count; i++)
        {
            result.Add(oldLines[i]);
        }

        return result;
    }

    private static int Id(Dictionary<string, int> ids, string line)
    {
        if (!ids.TryGetValue(line, out var id))
        {
            id = ids.Count;
            ids[line] = id;
        }

        return id;
    }

    // Emits edits (deletions of a[i], insertions of b[j]) in increasing order for a[aLo..aHi) vs b[bLo..bHi).
    private static void Compare(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, List<(int, int, bool)> edits)
    {
        while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
        {
            aLo++;
            bLo++;
        }

        while (aLo < aHi && bLo < bHi && a[aHi - 1] == b[bHi - 1])
        {
            aHi--;
            bHi--;
        }

        if (aLo == aHi)
        {
            for (var j = bLo; j < bHi; j++)
            {
                edits.Add((aLo, j, false));
            }

            return;
        }

        if (bLo == bHi)
        {
            for (var i = aLo; i < aHi; i++)
            {
                edits.Add((i, bLo, true));
            }

            return;
        }

        var (x, y) = Bisect(a, aLo, aHi, b, bLo, bHi);
        Compare(a, aLo, x, b, bLo, y, edits);
        Compare(a, x, aHi, b, y, bHi, edits);
    }

    // Finds the middle snake of an optimal path and returns the split point (Myers 1986, as in diff-match-patch).
    private static (int X, int Y) Bisect(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi)
    {
        var n = aHi - aLo;
        var m = bHi - bLo;
        var maxD = (n + m + 1) / 2;
        var offset = maxD;
        var length = (2 * maxD) + 2;
        var v1 = new int[length];
        var v2 = new int[length];
        Array.Fill(v1, -1);
        Array.Fill(v2, -1);
        v1[offset + 1] = 0;
        v2[offset + 1] = 0;
        var delta = n - m;
        var front = delta % 2 != 0;
        int k1Start = 0, k1End = 0, k2Start = 0, k2End = 0;

        for (var d = 0; d < maxD; d++)
        {
            for (var k1 = -d + k1Start; k1 <= d - k1End; k1 += 2)
            {
                var k1Offset = offset + k1;
                var x1 = k1 == -d || (k1 != d && v1[k1Offset - 1] < v1[k1Offset + 1]) ? v1[k1Offset + 1] : v1[k1Offset - 1] + 1;
                var y1 = x1 - k1;
                while (x1 < n && y1 < m && a[aLo + x1] == b[bLo + y1])
                {
                    x1++;
                    y1++;
                }

                v1[k1Offset] = x1;
                if (x1 > n)
                {
                    k1End += 2;
                }
                else if (y1 > m)
                {
                    k1Start += 2;
                }
                else if (front)
                {
                    var k2Offset = offset + delta - k1;
                    if (k2Offset >= 0 && k2Offset < length && v2[k2Offset] != -1 && x1 >= n - v2[k2Offset])
                    {
                        return (aLo + x1, bLo + y1);
                    }
                }
            }

            for (var k2 = -d + k2Start; k2 <= d - k2End; k2 += 2)
            {
                var k2Offset = offset + k2;
                var x2 = k2 == -d || (k2 != d && v2[k2Offset - 1] < v2[k2Offset + 1]) ? v2[k2Offset + 1] : v2[k2Offset - 1] + 1;
                var y2 = x2 - k2;
                while (x2 < n && y2 < m && a[aHi - x2 - 1] == b[bHi - y2 - 1])
                {
                    x2++;
                    y2++;
                }

                v2[k2Offset] = x2;
                if (x2 > n)
                {
                    k2End += 2;
                }
                else if (y2 > m)
                {
                    k2Start += 2;
                }
                else if (!front)
                {
                    var k1Offset = offset + delta - k2;
                    if (k1Offset >= 0 && k1Offset < length && v1[k1Offset] != -1)
                    {
                        var x1 = v1[k1Offset];
                        var y1 = offset + x1 - k1Offset;
                        if (x1 >= n - x2)
                        {
                            return (aLo + x1, bLo + y1);
                        }
                    }
                }
            }
        }

        // Unreachable for non-empty inputs: an optimal path always has a middle snake.
        return (aLo, bHi);
    }

    private static List<DiffHunk> ToHunks(List<(int OldIndex, int NewIndex, bool IsDelete)> edits, IReadOnlyList<string> newLines)
    {
        var hunks = new List<DiffHunk>();
        var i = 0;
        while (i < edits.Count)
        {
            var oldStart = edits[i].OldIndex;
            var newStart = edits[i].NewIndex;
            var oldEnd = oldStart;
            var newEnd = newStart;
            // Edits carry the current position in the other sequence, so an edit continues the hunk exactly when it
            // starts where the hunk ends on both sides.
            while (i < edits.Count && edits[i].OldIndex == oldEnd && edits[i].NewIndex == newEnd)
            {
                if (edits[i].IsDelete)
                {
                    oldEnd++;
                }
                else
                {
                    newEnd++;
                }

                i++;
            }

            hunks.Add(new DiffHunk(oldStart, oldEnd - oldStart, newStart, newEnd - newStart, [.. newLines.Skip(newStart).Take(newEnd - newStart)]));
        }

        return hunks;
    }

    private sealed class LineEndingInsensitiveComparer : IEqualityComparer<string>
    {
        public static readonly LineEndingInsensitiveComparer Instance = new();

        // CRLF and LF compare equal; a missing terminator (last line without newline) is still a difference.
        public bool Equals(string? x, string? y) => string.Equals(Key(x!), Key(y!), StringComparison.Ordinal);

        public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(Key(obj));

        private static string Key(string line) => line.EndsWith("\r\n", StringComparison.Ordinal) ? line[..^2] + "\n" : line;
    }
}
