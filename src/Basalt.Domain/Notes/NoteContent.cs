using System.Text;

namespace Basalt.Domain.Notes;

/// <summary>Line ending convention of a document.</summary>
public enum LineEnding
{
    Lf,
    CrLf,
    Mixed,
}

/// <summary>
/// Full text of a note plus the byte-level details that must survive a save: the line ending convention and the
/// UTF-8 BOM (spec 02 §4, 06 §7.9).
/// </summary>
public sealed record NoteContent
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private NoteContent(string text, bool hasBom)
    {
        Text = text;
        HasBom = hasBom;
        LineEnding = Detect(text);
    }

    public string Text { get; }

    public bool HasBom { get; }

    public LineEnding LineEnding { get; }

    public static NoteContent FromText(string text, bool hasBom = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new NoteContent(text, hasBom);
    }

    public static NoteContent FromBytes(ReadOnlySpan<byte> bytes)
    {
        var hasBom = bytes.StartsWith(Bom);
        return new NoteContent(Utf8NoBom.GetString(hasBom ? bytes[Bom.Length..] : bytes), hasBom);
    }

    public byte[] ToBytes()
    {
        var body = Utf8NoBom.GetBytes(Text);
        return HasBom ? [.. Bom, .. body] : body;
    }

    /// <summary>
    /// Replaces the text keeping the BOM and converting line breaks to this document's convention (mixed documents
    /// are left untouched, as there is no single convention to apply).
    /// </summary>
    public NoteContent WithText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = LineEnding switch
        {
            LineEnding.Lf => text.Replace("\r\n", "\n", StringComparison.Ordinal),
            LineEnding.CrLf => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal),
            _ => text,
        };
        return new NoteContent(normalized, HasBom);
    }

    public override string ToString() => Text;

    private static LineEnding Detect(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        return crlf > 0 && lf > 0 ? LineEnding.Mixed : crlf > 0 ? LineEnding.CrLf : LineEnding.Lf;
    }
}
