namespace Basalt.Domain.Text;

/// <summary>
/// Line-based diff3 merge (spec 02 §10). Regions changed on one side take that side; regions changed identically on
/// both sides are taken once; overlapping different changes (or insertions at the same point) become conflicts with
/// markers. Unchanged regions are copied from <c>ours</c>, line endings are compared ignoring CRLF/LF and lines coming
/// from <c>theirs</c> (and markers) use the predominant line ending of <c>ours</c>.
/// </summary>
public static class ThreeWayTextMerger
{
    private static readonly MergeOptions DefaultOptions = new();

    public static MergeResult Merge(string @base, string ours, string theirs, MergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(theirs);
        options ??= DefaultOptions;

        var b = TextLines.Split(@base);
        var o = TextLines.Split(ours);
        var t = TextLines.Split(theirs);
        var eol = Predominant(o) ?? Predominant(b) ?? Predominant(t) ?? "\n";

        var changes = LineDiff.Diff(b, o, ignoreLineEndings: true).Select(h => (Hunk: h, Ours: true))
            .Concat(LineDiff.Diff(b, t, ignoreLineEndings: true).Select(h => (Hunk: h, Ours: false)))
            .OrderBy(c => c.Hunk.OldStart)
            .ThenBy(c => c.Hunk.OldCount)
            .ToList();

        var output = new List<string>(o.Count + t.Count);
        var regions = new List<ConflictRegion>();
        int basePos = 0, oursDelta = 0, theirsDelta = 0;
        var index = 0;
        while (index < changes.Count)
        {
            var chunk = new List<(DiffHunk Hunk, bool Ours)> { changes[index] };
            var start = changes[index].Hunk.OldStart;
            var end = changes[index].Hunk.OldEnd;
            index++;
            while (index < changes.Count && Overlaps(changes[index].Hunk, start, end, chunk))
            {
                chunk.Add(changes[index]);
                end = Math.Max(end, changes[index].Hunk.OldEnd);
                index++;
            }

            // Unchanged lines before the chunk, as they appear in ours.
            for (var i = basePos; i < start; i++)
            {
                output.Add(o[i + oursDelta]);
            }

            var oursChunkDelta = chunk.Where(c => c.Ours).Sum(c => c.Hunk.NewCount - c.Hunk.OldCount);
            var theirsChunkDelta = chunk.Where(c => !c.Ours).Sum(c => c.Hunk.NewCount - c.Hunk.OldCount);
            var oursSlice = Slice(o, start + oursDelta, end + oursDelta + oursChunkDelta);
            var theirsSlice = Slice(t, start + theirsDelta, end + theirsDelta + theirsChunkDelta);
            var oursChanged = chunk.Exists(c => c.Ours);
            var theirsChanged = chunk.Exists(c => !c.Ours);

            if (!theirsChanged || SameIgnoringLineEndings(oursSlice, theirsSlice))
            {
                output.AddRange(oursSlice);
            }
            else if (!oursChanged)
            {
                output.AddRange(theirsSlice.Select(l => WithLineEnding(l, eol)));
            }
            else
            {
                regions.Add(new ConflictRegion(start, Slice(b, start, end), oursSlice, theirsSlice));
                output.Add($"<<<<<<< {options.LocalLabel}{eol}");
                output.AddRange(oursSlice.Select(l => Terminated(l, eol)));
                output.Add($"======={eol}");
                output.AddRange(theirsSlice.Select(l => Terminated(WithLineEnding(l, eol), eol)));
                output.Add($">>>>>>> {options.RemoteLabel}{eol}");
            }

            oursDelta += oursChunkDelta;
            theirsDelta += theirsChunkDelta;
            basePos = end;
        }

        for (var i = basePos; i < b.Count; i++)
        {
            output.Add(o[i + oursDelta]);
        }

        var text = string.Concat(output);
        return regions.Count == 0 ? new MergeResult.Clean(text) : new MergeResult.Conflicted(text, regions);
    }

    // A hunk joins the current chunk when its base range intersects it, or when it is an insertion at the same point
    // as another insertion of the chunk (the order of both insertions would be ambiguous).
    private static bool Overlaps(DiffHunk hunk, int start, int end, List<(DiffHunk Hunk, bool Ours)> chunk) =>
        (hunk.OldStart < end && start < hunk.OldEnd)
        || (hunk.OldCount == 0 && chunk.Exists(c => c.Hunk.OldCount == 0 && c.Hunk.OldStart == hunk.OldStart));

    private static List<string> Slice(IReadOnlyList<string> lines, int from, int to) => [.. lines.Skip(from).Take(to - from)];

    private static bool SameIgnoringLineEndings(List<string> a, List<string> b) =>
        a.Count == b.Count && a.Zip(b).All(p => WithLineEnding(p.First, "\n") == WithLineEnding(p.Second, "\n"));

    private static string WithLineEnding(string line, string eol)
    {
        var bare = TextLines.WithoutTerminator(line);
        return bare.Length == line.Length ? line : bare + eol;
    }

    private static string Terminated(string line, string eol) => line.EndsWith('\n') ? line : line + eol;

    private static string? Predominant(IReadOnlyList<string> lines)
    {
        var crlf = lines.Count(l => l.EndsWith("\r\n", StringComparison.Ordinal));
        var lf = lines.Count(l => l.EndsWith('\n')) - crlf;
        return crlf + lf == 0 ? null : crlf > lf ? "\r\n" : "\n";
    }
}
