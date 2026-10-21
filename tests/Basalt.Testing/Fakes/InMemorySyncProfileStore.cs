using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Testing.Fakes;

/// <summary>Thread-safe in-memory <see cref="ISyncProfileStore"/>.</summary>
public sealed class InMemorySyncProfileStore : ISyncProfileStore
{
    private readonly Dictionary<VaultId, SyncProfile> _profiles = [];
    private readonly object _gate = new();

    public Task<SyncProfile?> GetAsync(VaultId vault, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_profiles.GetValueOrDefault(vault));
        }
    }

    public Task<Result<Unit>> SaveAsync(SyncProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            _profiles[profile.VaultId] = profile;
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result<Unit>> DeleteAsync(VaultId vault, CancellationToken ct)
    {
        lock (_gate)
        {
            _profiles.Remove(vault);
        }

        return Task.FromResult(Result.Success());
    }
}
