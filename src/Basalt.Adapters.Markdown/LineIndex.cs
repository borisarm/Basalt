namespace Basalt.Adapters.Markdown;

/// <summary>Maps document offsets to 0-based line/column.</summary>
internal sealed class LineIndex
{
    private readonly string _text;
    private readonly List<int> _lineStarts = [0];

    public LineIndex(string text)
    {
        _text = text;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    public (int Line, int Column) PositionOf(int offset)
    {
        var index = _lineStarts.BinarySearch(offset);
        var line = index >= 0 ? index : ~index - 1;
        return (line, offset - _lineStarts[line]);
    }

    /// <summary>Text of a line without its terminator.</summary>
    public string LineText(int line)
    {
        if (line < 0 || line >= _lineStarts.Count)
        {
            return string.Empty;
        }

        var start = _lineStarts[line];
        var end = line + 1 < _lineStarts.Count ? _lineStarts[line + 1] - 1 : _text.Length;
        if (end > start && _text[end - 1] == '\r')
        {
            end--;
        }

        return _text[start..Math.Max(start, end)];
    }
}
