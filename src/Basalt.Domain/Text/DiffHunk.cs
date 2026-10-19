namespace Basalt.Domain.Text;

/// <summary>
/// A change region: <see cref="OldCount"/> lines of the old sequence starting at <see cref="OldStart"/> are replaced by
/// <see cref="NewLines"/>, which start at <see cref="NewStart"/> in the new sequence. Regions between hunks are equal.
/// </summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<string> NewLines)
{
    public int OldEnd => OldStart + OldCount;

    public int NewEnd => NewStart + NewCount;
}
