namespace Basalt.Domain.Notes;

/// <summary>A UTF-16 range of a text.</summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;

    public bool Overlaps(TextRange other) => Start < other.End && other.Start < End;
}
