using Basalt.Application.Ports.Events;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>Whisper to open editors: flush pending edits before the cycle reads the disk (spec 06 §6, step 0).</summary>
public sealed record FlushPendingEditsRequested(VaultId Id) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

/// <summary>A cycle finished (spec 06 §6, step 10).</summary>
public sealed record SyncCompleted(
    VaultId Id,
    SyncOutcome Outcome,
    SyncBase Base,
    IReadOnlyList<VaultPath> Conflicts,
    bool HasDeferred) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}
