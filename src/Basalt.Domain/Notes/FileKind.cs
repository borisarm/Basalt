namespace Basalt.Domain.Notes;

/// <summary>Classification of a vault entry (spec 02 §3.2).</summary>
public enum FileKind
{
    Note,
    Image,
    Pdf,
    Audio,
    Video,
    Other,

    /// <summary>Anything under <c>.basalt/</c>.</summary>
    Internal,

    /// <summary>Anything under <c>.trash/</c>.</summary>
    Trash,
}
