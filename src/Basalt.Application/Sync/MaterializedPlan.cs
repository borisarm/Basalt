using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Text;

namespace Basalt.Application.Sync;

/// <summary>How a conflict was resolved while materialising a plan.</summary>
public enum SyncConflictKind
{
    /// <summary>The losing side was saved next to the path as a conflict copy (spec 06 §5, <c>KeepBoth</c>).</summary>
    ConflictCopy,

    /// <summary>The merged text carries <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> markers (spec 06 §5, <c>AutoMergeOrMarkers</c>).</summary>
    Markers,

    /// <summary>A path was both a file and a folder (spec 06 §7.3).</summary>
    FileFolder,
}

/// <summary>One conflict produced by materialisation, for the log and the conflict centre.</summary>
public sealed record SyncConflict(
    VaultPath Path,
    SyncConflictKind Kind,
    VaultPath? CopyPath = null,
    IReadOnlyList<ConflictRegion>? Regions = null);

/// <summary>
/// Result of step 5 (spec 06 §6): the target manifest, the bytes staged for download/upload/conflict copies, and the
/// conflicts. Contents are keyed by blob SHA; uploads read the local file in the publisher.
/// </summary>
public sealed record MaterializedPlan(
    Manifest Target,
    IReadOnlyDictionary<BlobSha, byte[]> Contents,
    IReadOnlyList<SyncConflict> Conflicts);
