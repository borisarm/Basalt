using Basalt.Application.Ports.Remote;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>A cached remote tree, keyed by its tree SHA (spec 06 §4, <c>remote-trees/</c>).</summary>
public sealed record RemoteTreeCacheEntry(TreeSha Tree, RemoteTreeSnapshot Snapshot);

/// <summary>
/// Remote side of one sync cycle (spec 06 §6, steps 2 and 3): conditional head, remote manifest and the paths that must
/// be quarantined because this device cannot materialise them.
/// </summary>
public sealed record RemoteSnapshot(
    RemoteHead? Head,
    Manifest Manifest,
    string? ETag,
    IReadOnlyList<RemotePathIssue> Quarantined,
    bool NotModified);
