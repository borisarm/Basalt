namespace Basalt.Domain.Notes;

/// <summary>A range of a document: UTF-16 offset and length plus 0-based line and column (spec 02 §4).</summary>
public readonly record struct SourceSpan
{
    public SourceSpan(int start, int length, int line, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        Start = start;
        Length = length;
        Line = line;
        Column = column;
    }

    public int Start { get; }

    public int Length { get; }

    public int Line { get; }

    public int Column { get; }

    /// <summary>Exclusive end offset.</summary>
    public int End => Start + Length;

    public bool Contains(int offset) => offset >= Start && offset < End;
}
