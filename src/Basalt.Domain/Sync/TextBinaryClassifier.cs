using System.Text.Unicode;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Sync;

/// <summary>
/// Decides whether a file is text for merge purposes (spec 06 §5): a known text extension and valid UTF-8 with no NUL
/// in the first 8 KB.
/// </summary>
public static class TextBinaryClassifier
{
    private const int SniffLength = 8 * 1024;

    public static IReadOnlySet<string> TextExtensions { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "md", "txt", "json", "yaml", "yml", "css", "js", "canvas", "csv", "svg", "html", "xml",
    };

    public static bool IsText(VaultPath path, ReadOnlySpan<byte> content)
    {
        if (!IsTextPath(path) || !Utf8.IsValid(content))
        {
            return false;
        }

        return !content[..Math.Min(content.Length, SniffLength)].Contains((byte)0);
    }

    /// <summary>Extension-only check used by the pure planner, which never sees the bytes (ADR-0017).</summary>
    public static bool IsTextPath(VaultPath path) => TextExtensions.Contains(path.Extension);
}
