using Basalt.Application.Common;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;

namespace Basalt.Application.Sync;

/// <summary>
/// Builds the engine from the open session's file system and remote repository. The remote repository is attached to
/// the session when the vault is configured for sync (spec 01 §4.4, ADR-0012).
/// </summary>
public sealed class VaultSyncRunnerFactory(
    IVaultSessions sessions,
    ISyncStateStoreFactory states,
    IEventBus events,
    IClock clock,
    IDeviceInfo device,
    TimeProvider time,
    SyncLocks locks,
    VaultLocks vaultLocks) : IVaultSyncRunnerFactory
{
    public IVaultSyncRunner Create(SyncProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var session = sessions.Get(profile.VaultId);
        if (session.IsFailure)
        {
            throw new InvalidOperationException(session.Error.Message);
        }

        var repository = session.Value.TryGet<IRemoteRepository>()
            ?? throw new InvalidOperationException("The vault is not configured for sync.");

        return new VaultSyncEngine(
            profile.VaultId,
            session.Value.FileSystem,
            repository,
            states.Create(profile.VaultId),
            events,
            clock,
            device,
            time,
            locks,
            vaultLocks);
    }
}
