using Basalt.Domain.Notes;
using Basalt.Domain.Sync;

namespace Basalt.Application.Sync;

/// <summary>Why a file could not be part of the local snapshot (spec 06 §6, step 1).</summary>
public enum SyncIncidentKind
{
    TooLarge,
    SymbolicLink,
    CaseCollision,
    Unreadable,
    ChangedWhileScanning,
}

/// <summary>A file excluded from the snapshot, with the reason. Incidents are logged; they never abort the scan.</summary>
public sealed record SyncIncident(SyncIncidentKind Kind, VaultPath Path, VaultPath? OtherPath = null, string? Detail = null);

/// <summary>
/// Result of scanning the vault before synchronising (spec 06 §6, step 1): the included files, the refreshed hash
/// cache, the incidents and every path the scan left out. Callers must remove <see cref="ExcludedPaths"/> from the base
/// and the remote manifest before planning, so ignored, too-large or quarantined files are never deleted.
/// </summary>
public sealed record LocalSnapshot(
    Manifest Manifest,
    HashCache HashCache,
    IReadOnlyList<SyncIncident> Incidents,
    IReadOnlyList<VaultPath> ExcludedPaths);
