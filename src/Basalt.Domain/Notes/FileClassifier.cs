namespace Basalt.Domain.Notes;

/// <summary>Classifies vault entries by location and extension (spec 02 §3.2).</summary>
public static class FileClassifier
{
    public const string ConfigFolder = ".basalt";
    public const string TrashFolder = ".trash";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.Ordinal) { "png", "jpg", "jpeg", "gif", "webp", "svg", "bmp" };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.Ordinal) { "mp3", "wav", "m4a", "ogg", "flac", "3gp" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.Ordinal) { "mp4", "webm", "mov", "mkv", "ogv" };

    public static FileKind Classify(VaultPath path)
    {
        var segments = path.Segments;
        if (segments.Count > 0 && segments[0] == ConfigFolder)
        {
            return FileKind.Internal;
        }

        if (segments.Count > 0 && segments[0] == TrashFolder)
        {
            return FileKind.Trash;
        }

        var extension = path.Extension;
        return extension switch
        {
            "md" => FileKind.Note,
            "pdf" => FileKind.Pdf,
            _ when ImageExtensions.Contains(extension) => FileKind.Image,
            _ when AudioExtensions.Contains(extension) => FileKind.Audio,
            _ when VideoExtensions.Contains(extension) => FileKind.Video,
            _ => FileKind.Other,
        };
    }
}
