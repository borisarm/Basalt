namespace Basalt.Domain.Sync;

/// <summary>What part of the vault is synchronised on this device (spec 06 §2 and §3).</summary>
public sealed record SyncScope(
    bool IncludeAttachments = true,
    bool IncludeObsidianConfig = false,
    bool IncludeTrash = false,
    IReadOnlyList<string>? ExtraIgnorePatterns = null)
{
    public static SyncScope Default { get; } = new();
}
