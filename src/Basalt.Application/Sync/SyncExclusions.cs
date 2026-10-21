using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>Scope and ignore decisions shared by the local scanner and the remote snapshot (spec 06 §3).</summary>
public static class SyncExclusions
{
    /// <summary>Rules and scope; the absolute size limit is local-only (remote files above it are still downloaded, 06 §7.6).</summary>
    public static bool IsIgnored(VaultPath path, SyncScope scope, IgnoreRules rules)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.IsIgnored(path, isDirectory: false))
        {
            return true;
        }

        if (!scope.IncludeObsidianConfig && IsUnder(path, ".obsidian"))
        {
            return true;
        }

        if (!scope.IncludeTrash && IsUnder(path, FileClassifier.TrashFolder))
        {
            return true;
        }

        if (!scope.IncludeAttachments && path.Extension != "md" && !IsUnder(path, FileClassifier.ConfigFolder))
        {
            return true;
        }

        return false;
    }

    public static bool IsUnder(VaultPath path, string folder) =>
        path.Segments.Count > 0 && string.Equals(path.Segments[0], folder, StringComparison.Ordinal);
}
