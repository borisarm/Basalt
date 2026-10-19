namespace Basalt.Application.Ports.FileSystem;

/// <summary>File naming conventions shared by every file system adapter (spec 03 §1.1).</summary>
public static class VaultFileConventions
{
    public const string TempFilePrefix = ".~basalt-";
    public const string TempFileSuffix = ".tmp";

    /// <summary>True for Basalt's atomic-write temporary files, which must never be enumerated, watched or indexed.</summary>
    public static bool IsTemporaryFile(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.StartsWith(TempFilePrefix, StringComparison.Ordinal) && fileName.EndsWith(TempFileSuffix, StringComparison.Ordinal);
    }
}
