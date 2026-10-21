using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Ports.Storage;

/// <summary>Per-vault sync profile on this device (spec 03 §1.6 and 06 §2).</summary>
public interface ISyncProfileStore
{
    Task<SyncProfile?> GetAsync(VaultId vault, CancellationToken ct);

    Task<Result<Unit>> SaveAsync(SyncProfile profile, CancellationToken ct);

    /// <returns>Success also when the vault has no profile.</returns>
    Task<Result<Unit>> DeleteAsync(VaultId vault, CancellationToken ct);
}
