namespace Basalt.Domain.Vaults;

/// <summary>Link syntax used when Basalt generates links (spec 05 §3.1).</summary>
public enum LinkFormat
{
    Wikilink,
    Markdown,
}

/// <summary>Path style used when Basalt generates links (spec 05 §3.1).</summary>
public enum LinkPathStyle
{
    Shortest,
    Relative,
    Absolute,
}

/// <summary>Daily notes configuration (spec 02 §9).</summary>
public sealed record DailyNotesSettings(string Folder = "", string DateFormat = "YYYY-MM-DD", string? TemplatePath = null);

/// <summary>Settings shared by every device through the vault repository (spec 02 §2).</summary>
public sealed record VaultSettings
{
    public static VaultSettings Default { get; } = new();

    /// <summary>Folder for new attachments; empty = vault root.</summary>
    public string AttachmentFolder { get; init; } = string.Empty;

    /// <summary>Folder for new notes; empty = vault root.</summary>
    public string NewNoteFolder { get; init; } = string.Empty;

    public LinkFormat LinkFormat { get; init; } = LinkFormat.Wikilink;

    public LinkPathStyle LinkPathStyle { get; init; } = LinkPathStyle.Shortest;

    public bool AutoUpdateLinks { get; init; } = true;

    public DailyNotesSettings DailyNotes { get; init; } = new();

    public string TemplatesFolder { get; init; } = string.Empty;

    /// <summary>Extra extensions shown in the explorer besides notes and known attachments.</summary>
    public IReadOnlyList<string> VisibleExtensions { get; init; } = [];
}
