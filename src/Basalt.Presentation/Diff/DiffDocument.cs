using Basalt.Domain.Text;

namespace Basalt.Presentation.Diff;

public enum DiffLineKind
{
    Equal,
    Added,
    Removed,
    Changed,
}

/// <summary>One aligned row of a side-by-side diff; a missing side is an insertion or a deletion.</summary>
public sealed record DiffLine(int? LeftNumber, string? LeftText, int? RightNumber, string? RightText, DiffLineKind Kind)
{
    public bool IsAdded => Kind == DiffLineKind.Added;

    public bool IsRemoved => Kind == DiffLineKind.Removed;

    public bool IsChanged => Kind == DiffLineKind.Changed;
}

/// <summary>What a diff window shows (spec 07 §8).</summary>
public sealed record DiffRequest(string Title, string LeftTitle, string RightTitle, string Left, string Right);

/// <summary>
/// Aligns two texts by hunks of the domain's <see cref="LineDiff"/> for the side-by-side diff view
/// (spec 07 §8: aligned by hunks, never one control per line of a naive text diff).
/// </summary>
public static class DiffDocument
{
    public static IReadOnlyList<DiffLine> Build(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var oldLines = TextLines.Split(left);
        var newLines = TextLines.Split(right);
        var hunks = LineDiff.Diff(oldLines, newLines, ignoreLineEndings: true);
        var rows = new List<DiffLine>();
        var oldIndex = 0;
        var newIndex = 0;
        foreach (var hunk in hunks)
        {
            while (oldIndex < hunk.OldStart)
            {
                rows.Add(new DiffLine(oldIndex + 1, Trim(oldLines[oldIndex]), newIndex + 1, Trim(newLines[newIndex]), DiffLineKind.Equal));
                oldIndex++;
                newIndex++;
            }

            for (var offset = 0; offset < Math.Max(hunk.OldCount, hunk.NewCount); offset++)
            {
                var hasLeft = offset < hunk.OldCount;
                var hasRight = offset < hunk.NewCount;
                var kind = hasLeft && hasRight ? DiffLineKind.Changed : hasLeft ? DiffLineKind.Removed : DiffLineKind.Added;
                rows.Add(new DiffLine(
                    hasLeft ? oldIndex + offset + 1 : null,
                    hasLeft ? Trim(oldLines[oldIndex + offset]) : null,
                    hasRight ? hunk.NewStart + offset + 1 : null,
                    hasRight ? Trim(hunk.NewLines[offset]) : null,
                    kind));
            }

            oldIndex = hunk.OldEnd;
            newIndex = hunk.NewEnd;
        }

        while (oldIndex < oldLines.Count && newIndex < newLines.Count)
        {
            rows.Add(new DiffLine(oldIndex + 1, Trim(oldLines[oldIndex]), newIndex + 1, Trim(newLines[newIndex]), DiffLineKind.Equal));
            oldIndex++;
            newIndex++;
        }

        return rows;
    }

    private static string Trim(string line) => TextLines.WithoutTerminator(line);
}
